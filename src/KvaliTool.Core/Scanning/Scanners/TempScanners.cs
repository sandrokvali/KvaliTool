using KvaliTool.Core.Models;
using KvaliTool.Core.Safety;

namespace KvaliTool.Core.Scanning.Scanners;

internal sealed class UserTempScanner() : ScannerBase(CategoryId.UserTemp)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var root = context.Paths.UserTemp;
        if (!Directory.Exists(root))
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        var minAge = TimeSpan.FromHours(context.Settings.TempMinAgeHours);
        FileScan.Candidates(context, result, root, "Your temp folder",
            e => $"Temporary file, unchanged for {Format.Age(context.AgeOf(e))}", minAge);
    }
}

internal sealed class WindowsTempScanner() : ScannerBase(CategoryId.WindowsTemp)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var root = Path.Combine(context.Paths.WindowsDir, "Temp");
        if (!Directory.Exists(root))
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        var minAge = TimeSpan.FromHours(context.Settings.TempMinAgeHours);
        FileScan.Candidates(context, result, root, @"Windows\Temp",
            e => $"Temporary file, unchanged for {Format.Age(context.AgeOf(e))}", minAge);
    }
}

internal sealed class WindowsUpdateCacheScanner() : ScannerBase(CategoryId.WindowsUpdateCache)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        if (context.CanQueryRealSystem && Platform.SystemInfo.IsUpdateRestartPending())
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("An update is waiting for a restart. Restart Windows first; until then these files may still be needed.");
            return;
        }

        var root = Path.Combine(context.Paths.WindowsDir, "SoftwareDistribution", "Download");
        if (!Directory.Exists(root))
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        // Files from the last day may belong to an update that is downloading or installing right now.
        var minAge = TimeSpan.FromHours(context.Settings.TempMinAgeHours);
        FileScan.Candidates(context, result, root, "Windows Update downloads",
            _ => "Downloaded update package. Windows downloads it again if it is still needed", minAge);
    }
}

internal sealed class DeliveryOptimizationScanner() : ScannerBase(CategoryId.DeliveryOptimizationCache)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var root = ProtectedPathPolicy.DeliveryOptimizationCachePath(context.Paths);
        if (!Directory.Exists(root))
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        result.DeclareRoot(root);
        var totals = FileScan.Aggregate(context, result, root);
        if (totals.Files == 0)
        {
            return;
        }

        // Shown as one row: Windows' own cmdlet clears the cache as a whole, not file by file.
        result.TryAdd(new ScanItem
        {
            Path = root,
            Name = "Delivery Optimization cache",
            Kind = ItemKind.Folder,
            SizeBytes = totals.Bytes,
            FileCount = totals.Files,
            Reason = $"{totals.Files:N0} cached update pieces. Cleared with Delete-DeliveryOptimizationCache",
            Group = "Delivery Optimization",
            GroupRoot = root,
            Warning = totals.UnreadableFolders > 0 ? $"{totals.UnreadableFolders} folder(s) couldn't be read; the real size may be larger." : null,
        });
    }
}
