using System.Text;
using KvaliTool.Cli;
using KvaliTool.Core;
using KvaliTool.Core.Configuration;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;
using KvaliTool.Core.Scanning;
using KvaliTool.Core.Serialization;

Console.OutputEncoding = Encoding.UTF8;

try
{
    return args.FirstOrDefault() switch
    {
        null or "help" or "-h" or "--help" => Help(),
        "categories" => Categories(),
        "scan" => await ScanAsync(CliOptions.Parse(args[1..])),
        var other => throw new UsageException($"Unknown command '{other}'."),
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    Console.Error.WriteLine("Run 'kvalitool help' for usage.");
    return 64;
}
catch (SettingsException ex)
{
    Console.Error.WriteLine($"settings error: {ex.Message}");
    return 78;
}

static int Help()
{
    Console.WriteLine("""
        KvaliTool - Phase 1 dry-run scanner. It only reports; this build has no code that deletes or moves files.

        Usage:
          kvalitool scan [options]     Scan and print what could be cleaned
          kvalitool categories         List categories, tiers and how each would be cleaned

        Scan options:
          --only <ids>                 Comma-separated category ids (see 'categories')
          --json <file>                Where to write the full report (default: %LOCALAPPDATA%\KvaliTool\reports\scan-<time>.json).
                                       Must not exist yet: KvaliTool never overwrites a file.
          --top <n>                    Show the n largest items per category (default 5, 0 = none)
          --show-skipped               List every skipped entry (locked, too recent, links, ...)
          --settings <file>            Use this settings file instead of %LOCALAPPDATA%\KvaliTool\settings.json
          --sandbox <dir>              Map every Windows location into <dir> (for testing); uses default settings
          --pretend-running <names>    With --sandbox: treat these processes as running (e.g. msedge,chrome)

        One-off threshold overrides:
          --temp-hours <n>  --downloads-days <n>  --log-days <n>  --large-mb <n>  --dup-min-kb <n>  --skip-dism

        Duplicate files (off by default because it reads every candidate file in full):
          --find-duplicates            Search for duplicate files in this run (same as "Find duplicates (slow)")
          --no-hash-cache              Don't use or update the remembered file hashes in %LOCALAPPDATA%\KvaliTool\hashcache.bin.
                                       Rescans skip files unchanged since last time. --sandbox scans never use it.

        Press Ctrl+C to cancel a scan.
        """);
    return 0;
}

static int Categories()
{
    foreach (var d in CategoryCatalog.All)
    {
        Console.ForegroundColor = d.Tier == RiskTier.Safe ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.Write($"{d.Tier,-7}");
        Console.ResetColor();
        Console.WriteLine($" {d.Id,-28} {d.DisplayName}");
        Console.WriteLine($"         {d.Description}");
        Console.WriteLine($"         Cleaned by: {Describe(d.CleanMethod)}{(d.RequiresElevation ? "  |  needs administrator" : string.Empty)}");
        if (d.Caveat is not null)
        {
            Console.WriteLine($"         Note: {d.Caveat}");
        }

        Console.WriteLine();
    }

    return 0;
}

static async Task<int> ScanAsync(CliOptions options)
{
    IKnownPaths paths = options.Sandbox is { } sandbox ? new SandboxKnownPaths(sandbox) : new WindowsKnownPaths();
    IProcessDetector processes = options.Sandbox is not null ? new FixedProcessDetector(options.PretendRunning) : new SystemProcessDetector();

    var settings = options.SettingsFile is { } file ? SettingsStore.Load(file)
        : options.Sandbox is null ? SettingsStore.Load(SettingsStore.DefaultPath)
        : new ScanSettings();
    options.ApplyOverrides(settings);
    settings.Validate();

    // The remembered hashes belong to the real machine, so a sandbox scan never reads or writes them.
    var hashCache = options.Sandbox is null && !options.NoHashCache && settings.FindDuplicates
        ? FileHashCache.Load(FileHashCache.DefaultPath)
        : null;

    var reportPath = options.JsonPath ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KvaliTool", "reports", $"scan-{DateTime.Now:yyyyMMdd-HHmmss}.json");
    if (File.Exists(reportPath))
    {
        throw new UsageException($"'{reportPath}' already exists. KvaliTool never overwrites files; choose another name.");
    }

    using var cancel = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) =>
    {
        e.Cancel = true;
        cancel.Cancel();
    };

    Console.WriteLine(options.Sandbox is null
        ? "Scanning (dry run: nothing will be changed). Press Ctrl+C to cancel."
        : $"Scanning sandbox {paths.SystemDriveRoot} (dry run).");

    var progress = new ConsoleProgress();
    ScanReport report;
    try
    {
        report = await ScanEngine.CreateDefault().ScanAsync(
            new ScanRequest { Settings = settings, Paths = paths, Processes = processes, Categories = options.Only, HashCache = hashCache },
            progress,
            cancel.Token);
    }
    catch (OperationCanceledException)
    {
        progress.Clear();
        Console.WriteLine("Scan cancelled. Nothing was changed and no report was written.");
        return 130;
    }

    progress.Clear();
    ReportPrinter.Print(report, options.Top, options.ShowSkipped);

    ScanReportJson.WriteNew(report, reportPath);
    Console.WriteLine($"Full report: {reportPath}");
    return report.PolicyViolations.Count > 0 ? 2 : 0;
}

static string Describe(CleanMethod method) => method switch
{
    CleanMethod.QuarantineFiles => "move to quarantine (restorable)",
    CleanMethod.RecycleBinEntries => "Recycle Bin entries (see Phase 3 decision)",
    CleanMethod.WindowsTool => "Windows' own tool (permanent)",
    CleanMethod.ExternalTool => "the program's own command (permanent)",
    _ => method.ToString(),
};

namespace KvaliTool.Cli
{
    internal sealed class UsageException(string message) : Exception(message);

    internal sealed class CliOptions
    {
        public HashSet<CategoryId>? Only { get; private set; }

        public string? JsonPath { get; private set; }

        public int Top { get; private set; } = 5;

        public bool ShowSkipped { get; private set; }

        public string? SettingsFile { get; private set; }

        public string? Sandbox { get; private set; }

        public List<string> PretendRunning { get; } = [];

        private int? TempHours { get; set; }

        private int? DownloadsDays { get; set; }

        private int? LogDays { get; set; }

        private long? LargeMb { get; set; }

        private long? DupMinKb { get; set; }

        private bool SkipDism { get; set; }

        private bool FindDuplicates { get; set; }

        public bool NoHashCache { get; private set; }

        public static CliOptions Parse(string[] args)
        {
            var o = new CliOptions();
            for (var i = 0; i < args.Length; i++)
            {
                string Next() => i + 1 < args.Length ? args[++i] : throw new UsageException($"{args[i]} needs a value.");
                int Int() => int.TryParse(Next(), out var v) && v >= 0 ? v : throw new UsageException($"{args[i - 1]} needs a whole number.");

                switch (args[i])
                {
                    case "--only":
                        o.Only = [.. Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(ParseCategory)];
                        break;
                    case "--json": o.JsonPath = Path.GetFullPath(Next()); break;
                    case "--top": o.Top = Int(); break;
                    case "--show-skipped": o.ShowSkipped = true; break;
                    case "--settings": o.SettingsFile = Path.GetFullPath(Next()); break;
                    case "--sandbox": o.Sandbox = Path.GetFullPath(Next()); break;
                    case "--pretend-running": o.PretendRunning.AddRange(Next().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)); break;
                    case "--temp-hours": o.TempHours = Int(); break;
                    case "--downloads-days": o.DownloadsDays = Int(); break;
                    case "--log-days": o.LogDays = Int(); break;
                    case "--large-mb": o.LargeMb = Int(); break;
                    case "--dup-min-kb": o.DupMinKb = Int(); break;
                    case "--skip-dism": o.SkipDism = true; break;
                    case "--find-duplicates": o.FindDuplicates = true; break;
                    case "--no-hash-cache": o.NoHashCache = true; break;
                    default: throw new UsageException($"Unknown option '{args[i]}'.");
                }
            }

            if (o.PretendRunning.Count > 0 && o.Sandbox is null)
            {
                throw new UsageException("--pretend-running only works together with --sandbox.");
            }

            return o;
        }

        public void ApplyOverrides(ScanSettings s)
        {
            if (TempHours is { } t) s.TempMinAgeHours = t;
            if (DownloadsDays is { } d) s.DownloadsMinAgeDays = d;
            if (LogDays is { } l) s.LogMinAgeDays = l;
            if (LargeMb is { } m) s.LargeFileThresholdBytes = m * ScanSettings.OneMiB;
            if (DupMinKb is { } k) s.DuplicateMinBytes = k * 1024;
            if (SkipDism) s.AnalyzeComponentStore = false;
            if (FindDuplicates) s.FindDuplicates = true;
        }

        private static CategoryId ParseCategory(string text) =>
            Enum.TryParse<CategoryId>(text, ignoreCase: true, out var id)
                ? id
                : throw new UsageException($"Unknown category '{text}'. Run 'kvalitool categories' to see the ids.");
    }

    /// <summary>One status line, rewritten in place. Called from worker threads, so it locks.</summary>
    internal sealed class ConsoleProgress : IProgress<ScanProgress>
    {
        private readonly Lock _gate = new();
        private readonly bool _interactive = !Console.IsOutputRedirected;
        private int _lastLength;

        public void Report(ScanProgress value)
        {
            if (!_interactive)
            {
                return;
            }

            var active = value.ActiveCategories.Count > 0 ? string.Join(", ", value.ActiveCategories) : "finishing";
            var line = $"[{value.CategoriesCompleted}/{value.CategoriesTotal}] {value.FilesExamined:N0} files, {Format.Bytes(value.BytesFound)} found - {active}";
            var width = Math.Max(20, Console.WindowWidth - 1);
            if (line.Length > width)
            {
                line = line[..(width - 3)] + "...";
            }

            lock (_gate)
            {
                Console.Write("\r" + line.PadRight(_lastLength));
                _lastLength = line.Length;
            }
        }

        public void Clear()
        {
            if (!_interactive)
            {
                return;
            }

            lock (_gate)
            {
                Console.Write("\r" + new string(' ', _lastLength) + "\r");
                _lastLength = 0;
            }
        }
    }
}
