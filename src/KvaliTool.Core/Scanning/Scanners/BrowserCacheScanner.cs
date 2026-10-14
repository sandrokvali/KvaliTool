using KvaliTool.Core.Models;
using KvaliTool.Core.Platform;

namespace KvaliTool.Core.Scanning.Scanners;

/// <summary>
/// Browser caches, by allow-list: only the exact cache folders below are ever entered. Cookies, history,
/// logins, sessions, local storage and IndexedDB live in other folders and are never reached. As a second
/// guard, any path containing one of the <see cref="ForbiddenNames"/> is refused and reported as a violation.
/// </summary>
internal sealed class BrowserCacheScanner : ScannerBase
{
    /// <summary>Per-profile Chromium cache folders.</summary>
    private static readonly string[] ChromiumProfileCaches = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache"];

    /// <summary>Chromium cache folders shared by all profiles (directly under "User Data").</summary>
    private static readonly string[] ChromiumSharedCaches = ["ShaderCache", "GrShaderCache", "GraphiteDawnCache"];

    internal static readonly HashSet<string> ForbiddenNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Cookies", "Cookies-journal", "Network", "History", "History-journal", "Login Data", "Login Data For Account", "Web Data",
        "Sessions", "Session Storage", "Current Session", "Last Session", "Current Tabs", "Last Tabs", "Local Storage", "IndexedDB",
        "Sync Data", "Extension State", "Extensions", "Bookmarks", "Preferences", "Secure Preferences",
        "cookies.sqlite", "places.sqlite", "logins.json", "key4.db", "sessionstore.jsonlz4", "sessionstore-backups", "storage", "formhistory.sqlite",
    };

    private readonly string _browserName;
    private readonly string[] _processNames;
    private readonly Func<IKnownPaths, IEnumerable<(string Root, string Label)>> _cacheFolders;

    private BrowserCacheScanner(CategoryId id, string browserName, string[] processNames, Func<IKnownPaths, IEnumerable<(string, string)>> cacheFolders)
        : base(id)
    {
        _browserName = browserName;
        _processNames = processNames;
        _cacheFolders = cacheFolders;
    }

    public static BrowserCacheScanner Chrome() => new(CategoryId.ChromeCache, "Chrome", ["chrome"],
        p => ChromiumFolders(Path.Combine(p.LocalAppData, "Google", "Chrome", "User Data")));

    public static BrowserCacheScanner Edge() => new(CategoryId.EdgeCache, "Edge", ["msedge"],
        p => ChromiumFolders(Path.Combine(p.LocalAppData, "Microsoft", "Edge", "User Data")));

    public static BrowserCacheScanner Brave() => new(CategoryId.BraveCache, "Brave", ["brave"],
        p => ChromiumFolders(Path.Combine(p.LocalAppData, "BraveSoftware", "Brave-Browser", "User Data")));

    public static BrowserCacheScanner Firefox() => new(CategoryId.FirefoxCache, "Firefox", ["firefox"], FirefoxFolders);

    protected override void Scan(ScanContext context, CategoryResultBuilder result)
    {
        var folders = _cacheFolders(context.Paths).Where(f => Directory.Exists(f.Root)).ToList();
        if (folders.Count == 0)
        {
            result.Status = CategoryStatus.NotFound;
            result.Note($"{_browserName} doesn't seem to be installed (no cache folders found).");
            return;
        }

        if (_processNames.Any(context.Processes.IsRunning))
        {
            result.Status = CategoryStatus.Skipped;
            result.Note($"{_browserName} is running, so its cache was not scanned. Close {_browserName} completely and scan again.");
            return;
        }

        foreach (var (root, label) in folders)
        {
            FileScan.Candidates(context, result, root, label, entry =>
            {
                var relative = Path.GetRelativePath(root, entry.FullPath);
                if (relative.Split(Path.DirectorySeparatorChar).Any(ForbiddenNames.Contains))
                {
                    result.AddViolation(entry.FullPath, "Browser path contains a protected name (cookies, history, logins, sessions or storage)");
                    return null;
                }

                return $"{_browserName} cache file; re-downloaded when a site needs it";
            });
        }
    }

    private static IEnumerable<(string, string)> ChromiumFolders(string userData)
    {
        if (!Directory.Exists(userData))
        {
            yield break;
        }

        foreach (var shared in ChromiumSharedCaches)
        {
            yield return (Path.Combine(userData, shared), shared);
        }

        // A profile folder is one that has a Preferences file ("Default", "Profile 1", "Guest Profile", ...).
        foreach (var profile in SafeSubdirectories(userData).Where(d => File.Exists(Path.Combine(d, "Preferences"))))
        {
            var profileName = Path.GetFileName(profile);
            foreach (var cache in ChromiumProfileCaches)
            {
                yield return (Path.Combine(profile, cache), $@"{profileName}\{cache}");
            }
        }
    }

    /// <summary>Firefox keeps its cache in Local AppData; the profile data (cookies, history) is in Roaming and is never looked at.</summary>
    private static IEnumerable<(string, string)> FirefoxFolders(IKnownPaths paths)
    {
        var profiles = Path.Combine(paths.LocalAppData, "Mozilla", "Firefox", "Profiles");
        foreach (var profile in SafeSubdirectories(profiles))
        {
            yield return (Path.Combine(profile, "cache2"), $@"{Path.GetFileName(profile)}\cache2");
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string path)
    {
        if (!Directory.Exists(path))
        {
            return [];
        }

        try
        {
            return new DirectoryInfo(path)
                .EnumerateDirectories("*", new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = true })
                .Where(d => !d.Attributes.HasFlag(FileAttributes.ReparsePoint))
                .Select(d => d.FullName)
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
