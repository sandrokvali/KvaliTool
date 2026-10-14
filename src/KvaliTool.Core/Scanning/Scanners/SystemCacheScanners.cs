using System.IO.Enumeration;
using KvaliTool.Core.Models;

namespace KvaliTool.Core.Scanning.Scanners;

internal sealed class ThumbnailCacheScanner() : ScannerBase(CategoryId.ThumbnailCache)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var root = Path.Combine(context.Paths.LocalAppData, "Microsoft", "Windows", "Explorer");
        if (!Directory.Exists(root))
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        // Only Explorer's cache databases, nothing else in that folder, and no sub-folders.
        FileScan.Candidates(context, result, root, "Explorer caches",
            e => FileSystemName.MatchesSimpleExpression("thumbcache_*.db", e.Name) ? "Thumbnail cache database; Windows rebuilds it"
               : FileSystemName.MatchesSimpleExpression("iconcache_*.db", e.Name) ? "Icon cache database; Windows rebuilds it"
               : null,
            descendInto: (_, _) => false);

        if (result.Skipped.Any(s => s.Reason == SkipReason.Locked))
        {
            result.Note("Some cache files are open in Explorer and were skipped. They can only be removed after Explorer restarts; KvaliTool won't restart it for you.");
        }
    }
}

internal sealed class ShaderCacheScanner() : ScannerBase(CategoryId.ShaderCache)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var local = context.Paths.LocalAppData;
        var localLow = context.Paths.LocalLowAppData;
        (string Root, string Label)[] roots =
        [
            (Path.Combine(local, "D3DSCache"), "DirectX shader cache"),
            (Path.Combine(local, "NVIDIA", "DXCache"), "NVIDIA DirectX cache"),
            (Path.Combine(local, "NVIDIA", "GLCache"), "NVIDIA OpenGL cache"),
            (Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "DXCache"), "NVIDIA DirectX cache (per driver)"),
            (Path.Combine(localLow, "NVIDIA", "PerDriverVersion", "GLCache"), "NVIDIA OpenGL cache (per driver)"),
            (Path.Combine(local, "AMD", "DxCache"), "AMD DirectX cache"),
            (Path.Combine(local, "AMD", "DxcCache"), "AMD DirectX 12 cache"),
            (Path.Combine(local, "AMD", "VkCache"), "AMD Vulkan cache"),
            (Path.Combine(local, "AMD", "GLCache"), "AMD OpenGL cache"),
            (Path.Combine(localLow, "Intel", "ShaderCache"), "Intel shader cache"),
        ];

        var any = false;
        foreach (var (root, label) in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            any = true;
            FileScan.Candidates(context, result, root, label, _ => "Compiled shader; rebuilt automatically when needed");
        }

        if (!any)
        {
            result.Status = CategoryStatus.NotFound;
        }
    }
}

internal sealed class UserErrorReportsScanner() : ScannerBase(CategoryId.UserErrorReports)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var wer = Path.Combine(context.Paths.LocalAppData, "Microsoft", "Windows", "WER");
        FileScan.Candidates(context, result, Path.Combine(wer, "ReportArchive"), "Sent error reports", _ => "Windows Error Reporting report that was already sent");
        FileScan.Candidates(context, result, Path.Combine(wer, "ReportQueue"), "Queued error reports", _ => "Windows Error Reporting report waiting to be sent");
        FileScan.Candidates(context, result, Path.Combine(context.Paths.LocalAppData, "CrashDumps"), "Application crash dumps", _ => "Memory dump written when a program crashed");
    }
}

internal sealed class SystemErrorReportsScanner() : ScannerBase(CategoryId.SystemErrorReportsAndLogs)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var windows = context.Paths.WindowsDir;
        var wer = Path.Combine(context.Paths.ProgramData, "Microsoft", "Windows", "WER");

        FileScan.Candidates(context, result, Path.Combine(wer, "ReportArchive"), "System error reports (sent)", _ => "Windows Error Reporting report that was already sent");
        FileScan.Candidates(context, result, Path.Combine(wer, "ReportQueue"), "System error reports (queued)", _ => "Windows Error Reporting report waiting to be sent");
        FileScan.Candidates(context, result, Path.Combine(wer, "Temp"), "Error reporting temp files", _ => "Temporary Windows Error Reporting file",
            TimeSpan.FromHours(context.Settings.TempMinAgeHours));
        FileScan.Candidates(context, result, Path.Combine(windows, "Minidump"), "Blue-screen minidumps", _ => "Small memory dump from a system crash");
        FileScan.Candidates(context, result, Path.Combine(windows, "LiveKernelReports"), "Live kernel reports",
            e => e.Name.EndsWith(".dmp", StringComparison.OrdinalIgnoreCase) ? "Kernel diagnostic dump" : null);
        FileScan.SingleFile(context, result, Path.Combine(windows, "MEMORY.DMP"), "Full memory dump", "Complete memory dump from the last system crash");

        var logAge = TimeSpan.FromDays(context.Settings.LogMinAgeDays);
        FileScan.Candidates(context, result, Path.Combine(windows, "Logs"), @"Windows\Logs", e =>
        {
            var isLog = e.Name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
                || e.Name.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)
                || FileSystemName.MatchesSimpleExpression("CbsPersist_*.cab", e.Name);
            return isLog ? $"Windows log file, unchanged for {Format.Age(context.AgeOf(e))}" : null;
        }, logAge);
    }
}
