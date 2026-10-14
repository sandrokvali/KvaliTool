using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning.Scanners;

/// <summary>Asks Docker how big its build cache is (docker system df: read-only).</summary>
internal sealed partial class DockerBuildCacheScanner() : ScannerBase(CategoryId.DockerBuildCache)
{
    public override async Task ScanAsync(ScanContext context, CategoryResultBuilder result)
    {
        if (!context.CanQueryRealSystem)
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("Not available in sandbox mode.");
            return;
        }

        var docker = SystemInfo.FindOnPath("docker.exe");
        if (docker is null)
        {
            result.Status = CategoryStatus.NotFound;
            result.Note("Docker isn't installed (docker.exe not found on PATH).");
            return;
        }

        var run = await CommandRunner.RunAsync(docker, ["system", "df", "--format", "{{json .}}"], TimeSpan.FromSeconds(30), context.CancellationToken);
        if (run.TimedOut || run.ExitCode != 0)
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("Docker didn't answer. Start Docker Desktop and scan again." + (run.StandardError.Length > 0 ? $" ({run.StandardError.Trim()})" : string.Empty));
            return;
        }

        foreach (var line in run.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            using var row = JsonDocument.Parse(line);
            if (row.RootElement.TryGetProperty("Type", out var type) && type.GetString() == "Build Cache")
            {
                var reclaimable = row.RootElement.TryGetProperty("Reclaimable", out var r) ? r.GetString() : null;
                var size = ParseDockerSize(reclaimable);
                if (size is null or 0)
                {
                    result.Note("Docker's build cache is empty.");
                    return;
                }

                result.TryAdd(new ScanItem
                {
                    Path = "docker:build-cache",
                    Name = "Docker build cache",
                    Kind = ItemKind.Virtual,
                    SizeBytes = size,
                    Reason = $"Reclaimable build cache reported by Docker ({reclaimable})",
                    Group = "Docker",
                });
                return;
            }
        }

        result.Note("Docker didn't report a build cache.");
    }

    /// <summary>Docker prints decimal units: "1.234GB", "512.5MB", "0B".</summary>
    internal static long? ParseDockerSize(string? text)
    {
        var match = text is null ? null : DockerSize().Match(text);
        if (match is null || !match.Success)
        {
            return null;
        }

        var value = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var multiplier = match.Groups[2].Value.ToUpperInvariant() switch
        {
            "B" => 1d,
            "KB" => 1e3,
            "MB" => 1e6,
            "GB" => 1e9,
            "TB" => 1e12,
            _ => 1d,
        };
        return (long)(value * multiplier);
    }

    [GeneratedRegex(@"^\s*([\d.]+)\s*([kKMGT]?B)")]
    private static partial Regex DockerSize();
}

/// <summary>Windows.old and setup leftovers, each reported as one folder row.</summary>
internal sealed class PreviousWindowsScanner() : ScannerBase(CategoryId.PreviousWindowsInstallation)
{
    private static readonly HashSet<string> PersonalFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Documents", "Desktop", "Pictures", "Videos", "Music", "Downloads", "OneDrive",
    };

    public override bool IsSlow => true;

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var drive = context.Paths.SystemDriveRoot;
        var found = false;
        foreach (var (name, reportOnly) in new[] { ("Windows.old", false), ("$Windows.~BT", true), ("$Windows.~WS", true) })
        {
            var root = Path.Combine(drive, name);
            if (!Directory.Exists(root))
            {
                continue;
            }

            found = true;
            result.DeclareRoot(root);

            long personalFiles = 0, personalBytes = 0;
            var totals = FileScan.Aggregate(context, result, root, entry =>
            {
                // Windows.old\Users\<name>\Documents\... can hold files from before the upgrade.
                var parts = Path.GetRelativePath(root, entry.FullPath).Split(Path.DirectorySeparatorChar);
                if (parts.Length > 3 && parts[0].Equals("Users", StringComparison.OrdinalIgnoreCase) && PersonalFolders.Contains(parts[2]))
                {
                    personalFiles++;
                    personalBytes += entry.Length;
                }
            });

            string? warning = null;
            if (personalFiles > 0)
            {
                warning = $"Contains {personalFiles:N0} personal file(s) ({Format.Bytes(personalBytes)}) under {name}\\Users, for example documents from before the upgrade. Copy anything you need first.";
            }

            if (totals.UnreadableFolders > 0)
            {
                warning = CategoryResultBuilder.Combine(warning, $"{totals.UnreadableFolders:N0} folder(s) couldn't be read, so the size is a lower bound.");
            }

            // Windows removes the folder as a whole, so it can't leave excluded files behind: report only.
            var excludedInside = totals.ExcludedEntries > 0;
            if (excludedInside)
            {
                warning = CategoryResultBuilder.Combine(warning, $"{totals.ExcludedEntries:N0} item(s) inside are on your exclusion list. Windows removes this folder as a whole, so KvaliTool won't offer it.");
            }

            if (reportOnly)
            {
                warning = CategoryResultBuilder.Combine(warning, "KvaliTool can't remove this safely. Use Disk Cleanup > \"Temporary Windows installation files\" instead.");
            }

            result.TryAdd(new ScanItem
            {
                Path = root,
                Name = name,
                Kind = ItemKind.Folder,
                SizeBytes = totals.Bytes,
                FileCount = totals.Files,
                Reason = name == "Windows.old"
                    ? "Files from the previous Windows installation, kept so you can roll back"
                    : "Leftover files from a Windows upgrade or setup",
                Group = "Previous Windows installation",
                GroupRoot = root,
                Warning = warning,
                ReportOnly = reportOnly || excludedInside,
            });
        }

        if (!found)
        {
            result.Status = CategoryStatus.NotFound;
        }
    }
}

/// <summary>Runs DISM /AnalyzeComponentStore (read-only) and reports whether cleanup is recommended.</summary>
internal sealed class ComponentStoreScanner() : ScannerBase(CategoryId.ComponentStore)
{
    public override bool IsSlow => true;

    public override async Task ScanAsync(ScanContext context, CategoryResultBuilder result)
    {
        if (!context.CanQueryRealSystem)
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("Not available in sandbox mode.");
            return;
        }

        if (!context.Settings.AnalyzeComponentStore)
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("Component store analysis is turned off in settings.");
            return;
        }

        var dism = Path.Combine(Environment.SystemDirectory, "dism.exe");
        var run = await CommandRunner.RunAsync(dism, ["/Online", "/Cleanup-Image", "/AnalyzeComponentStore", "/English"], TimeSpan.FromMinutes(20), context.CancellationToken);
        if (run.TimedOut || run.ExitCode != 0)
        {
            result.Status = CategoryStatus.Failed;
            result.Note($"DISM didn't finish the analysis (exit code {run.ExitCode}{(run.TimedOut ? ", timed out" : string.Empty)}).");
            return;
        }

        var recommended = Field(run.StandardOutput, "Component Store Cleanup Recommended");
        var packages = Field(run.StandardOutput, "Number of Reclaimable Packages");
        var backups = Field(run.StandardOutput, "Backups and Disabled Features");
        var actual = Field(run.StandardOutput, "Actual Size of Component Store");
        if (recommended is null)
        {
            result.Status = CategoryStatus.Failed;
            result.Note("Couldn't read DISM's answer.");
            return;
        }

        result.Note($"DISM: component store {actual ?? "size unknown"}, backups and disabled features {backups ?? "unknown"}, reclaimable packages {packages ?? "unknown"}.");
        if (!recommended.Equals("Yes", StringComparison.OrdinalIgnoreCase))
        {
            result.Note("DISM says cleanup isn't needed right now.");
            return;
        }

        result.TryAdd(new ScanItem
        {
            Path = "dism:component-store",
            Name = "Component store cleanup",
            Kind = ItemKind.Virtual,
            SizeBytes = null,
            Reason = $"DISM recommends cleanup ({packages ?? "?"} reclaimable package(s)). The space it frees can't be known in advance",
            Group = "Windows component store",
        });
    }

    internal static string? Field(string output, string label)
    {
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                var separator = line.IndexOf(" : ", StringComparison.Ordinal);
                return separator < 0 ? null : line[(separator + 3)..].Trim();
            }
        }

        return null;
    }
}

/// <summary>Lists System Restore points with Get-ComputerRestorePoint (read-only) and offers all but the newest.</summary>
internal sealed class RestorePointScanner() : ScannerBase(CategoryId.OldRestorePoints)
{
    public override async Task ScanAsync(ScanContext context, CategoryResultBuilder result)
    {
        if (!context.CanQueryRealSystem)
        {
            result.Status = CategoryStatus.Skipped;
            result.Note("Not available in sandbox mode.");
            return;
        }

        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        const string script = "Get-ComputerRestorePoint | Select-Object SequenceNumber,Description,CreationTime | ConvertTo-Json -Compress";
        var run = await CommandRunner.RunAsync(powershell, ["-NoProfile", "-NonInteractive", "-Command", script], TimeSpan.FromMinutes(2), context.CancellationToken);
        if (run.TimedOut || run.ExitCode != 0)
        {
            result.Status = CategoryStatus.Failed;
            result.Note("Couldn't list restore points." + (run.StandardError.Length > 0 ? $" ({run.StandardError.Trim()})" : string.Empty));
            return;
        }

        var points = Parse(run.StandardOutput);
        if (points.Count == 0)
        {
            result.Status = CategoryStatus.NotFound;
            result.Note("No restore points found. System Restore may be turned off for this drive.");
            return;
        }

        var newest = points.MaxBy(p => p.Sequence);
        result.Note($"Newest restore point (always kept): #{newest.Sequence} \"{newest.Description}\" from {newest.CreatedUtc?.ToLocalTime():g}.");
        foreach (var point in points.Where(p => p.Sequence != newest.Sequence).OrderBy(p => p.Sequence))
        {
            result.TryAdd(new ScanItem
            {
                Path = $"restorepoint:{point.Sequence}",
                Name = string.IsNullOrWhiteSpace(point.Description) ? $"Restore point #{point.Sequence}" : point.Description,
                Kind = ItemKind.Virtual,
                SizeBytes = null,
                LastModifiedUtc = point.CreatedUtc,
                Reason = $"Older than the newest restore point (#{newest.Sequence})",
                Group = "System Restore",
            });
        }
    }

    internal readonly record struct RestorePoint(long Sequence, string Description, DateTime? CreatedUtc);

    internal static List<RestorePoint> Parse(string json)
    {
        var list = new List<RestorePoint>();
        if (string.IsNullOrWhiteSpace(json))
        {
            return list;
        }

        using var document = JsonDocument.Parse(json);
        var rows = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray().ToList()
            : [document.RootElement];
        foreach (var row in rows)
        {
            if (!row.TryGetProperty("SequenceNumber", out var seq) || !seq.TryGetInt64(out var sequence))
            {
                continue;
            }

            var description = row.TryGetProperty("Description", out var d) ? d.GetString() ?? string.Empty : string.Empty;
            var created = row.TryGetProperty("CreationTime", out var c) ? ParseWmiDate(c.GetString()) : null;
            list.Add(new RestorePoint(sequence, description, created));
        }

        return list;
    }

    /// <summary>WMI dates look like "20261001093015.123456+240": local time, then the UTC offset in minutes.</summary>
    internal static DateTime? ParseWmiDate(string? text)
    {
        if (text is null || text.Length < 25
            || !DateTime.TryParseExact(text[..14], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local)
            || !int.TryParse(text[21..], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var offsetMinutes))
        {
            return null;
        }

        return DateTime.SpecifyKind(local.AddMinutes(-offsetMinutes), DateTimeKind.Utc);
    }
}
