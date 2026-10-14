using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning.Scanners;

internal sealed class DeveloperCacheScanner() : ScannerBase(CategoryId.DeveloperCaches)
{
    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var p = context.Paths;
        var groups = new List<(string Root, string Label, string Reason)>();

        void Add(string? root, string label, string reason)
        {
            if (root is not null && Directory.Exists(root) && !groups.Any(g => string.Equals(g.Root, root, StringComparison.OrdinalIgnoreCase)))
            {
                groups.Add((PathUtil.Normalize(root), label, reason));
            }
        }

        const string npm = "npm package cache; npm downloads packages again when needed";
        Add(p.EnvironmentOverride("npm_config_cache") ?? Path.Combine(p.LocalAppData, "npm-cache"), "npm cache", npm);
        Add(Path.Combine(p.RoamingAppData, "npm-cache"), "npm cache (old location)", npm);

        const string nuget = "NuGet package cache; restored again on the next build";
        Add(p.EnvironmentOverride("NUGET_PACKAGES") ?? Path.Combine(p.UserProfile, ".nuget", "packages"), "NuGet global packages", nuget);
        Add(Path.Combine(p.LocalAppData, "NuGet", "v3-cache"), "NuGet HTTP cache", nuget);
        Add(Path.Combine(p.LocalAppData, "NuGet", "plugins-cache"), "NuGet plugins cache", nuget);

        Add(p.EnvironmentOverride("PIP_CACHE_DIR") ?? Path.Combine(p.LocalAppData, "pip", "Cache"), "pip cache", "pip download cache; downloaded again when needed");

        var gradleHome = p.EnvironmentOverride("GRADLE_USER_HOME") ?? Path.Combine(p.UserProfile, ".gradle");
        Add(Path.Combine(gradleHome, "caches"), "Gradle caches", "Gradle dependency and build cache; rebuilt on the next build");

        if (context.Processes.IsRunning("devenv"))
        {
            result.Note("Visual Studio is running, so its cache folders were not scanned. Close it and scan again to include them.");
        }
        else
        {
            const string vs = "Visual Studio cache; rebuilt the next time Visual Studio starts";
            foreach (var version in Subdirectories(Path.Combine(p.LocalAppData, "Microsoft", "VisualStudio")))
            {
                Add(Path.Combine(version, "ComponentModelCache"), $@"Visual Studio {Path.GetFileName(version)}\ComponentModelCache", vs);
            }

            Add(Path.Combine(p.LocalAppData, "Microsoft", "VSApplicationInsights"), "Visual Studio telemetry cache", vs);
            Add(Path.Combine(p.LocalAppData, "Microsoft", "WebsiteCache"), "Visual Studio website cache", vs);
        }

        if (groups.Count == 0)
        {
            result.Status = CategoryStatus.NotFound;
            return;
        }

        foreach (var (root, label, reason) in groups)
        {
            FileScan.Candidates(context, result, root, label, _ => reason);
        }

        if (result.Skipped.Any(s => s.Reason == SkipReason.Locked))
        {
            result.Note("Some cache files are in use (a build, IDE or Gradle daemon is running). They were skipped.");
        }
    }

    private static IEnumerable<string> Subdirectories(string path)
    {
        try
        {
            return Directory.Exists(path)
                ? new DirectoryInfo(path).EnumerateDirectories()
                    .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    .Select(d => d.FullName)
                    .ToList()
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
