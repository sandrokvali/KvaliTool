using System.Security.Principal;
using System.Text.Json;

namespace KvaliTool.Core.Platform;

/// <summary>
/// Every Windows location the scanner uses comes from here, already normalized (full, long-name paths).
/// Scanners never call Environment.GetFolderPath themselves, so a test can point all of them at a sandbox.
/// </summary>
public interface IKnownPaths
{
    bool IsSandbox { get; }

    string UserProfile { get; }

    string LocalAppData { get; }

    string RoamingAppData { get; }

    string LocalLowAppData { get; }

    string UserTemp { get; }

    string WindowsDir { get; }

    string ProgramData { get; }

    /// <summary>Drive that holds Windows, e.g. "C:\".</summary>
    string SystemDriveRoot { get; }

    IReadOnlyList<string> ProgramFilesDirs { get; }

    string Downloads { get; }

    /// <summary>Documents, Desktop, Pictures, Videos, Music (wherever they are redirected to).</summary>
    IReadOnlyList<string> UserContentFolders { get; }

    /// <summary>OneDrive, Dropbox, iCloud Drive, Box, Google Drive folders that exist on this PC.</summary>
    IReadOnlyList<string> CloudSyncFolders { get; }

    IReadOnlyList<string> FixedDriveRoots { get; }

    /// <summary>KvaliTool's own data folder. Never scanned.</summary>
    string AppDataDir { get; }

    string CurrentUserSid { get; }

    /// <summary>
    /// A cache-location environment variable such as npm_config_cache, normalized. Always null in sandbox
    /// mode so a test run can never wander into your real caches.
    /// </summary>
    string? EnvironmentOverride(string variable);
}

public sealed class WindowsKnownPaths : IKnownPaths
{
    public WindowsKnownPaths()
    {
        UserProfile = N(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        LocalAppData = N(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
        RoamingAppData = N(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
        LocalLowAppData = N(SystemInfo.GetKnownFolder(KnownFolderIds.LocalAppDataLow) ?? Path.Combine(UserProfile, "AppData", "LocalLow"));
        UserTemp = N(Path.GetTempPath());
        WindowsDir = N(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        ProgramData = N(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        SystemDriveRoot = Path.GetPathRoot(WindowsDir) ?? @"C:\";
        Downloads = N(SystemInfo.GetKnownFolder(KnownFolderIds.Downloads) ?? Path.Combine(UserProfile, "Downloads"));
        AppDataDir = Path.Combine(LocalAppData, "KvaliTool");

        using (var identity = WindowsIdentity.GetCurrent())
        {
            CurrentUserSid = identity.User?.Value ?? throw new InvalidOperationException("Could not read the current user's SID.");
        }

        ProgramFilesDirs = Existing(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432"));

        UserContentFolders = Existing(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic));

        CloudSyncFolders = Existing([.. DetectCloudFolders()]);

        FixedDriveRoots = DriveInfo.GetDrives()
            .Where(d => d.DriveType == DriveType.Fixed && d.IsReady)
            .Select(d => d.RootDirectory.FullName)
            .ToList();
    }

    public bool IsSandbox => false;

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string LocalLowAppData { get; }

    public string UserTemp { get; }

    public string WindowsDir { get; }

    public string ProgramData { get; }

    public string SystemDriveRoot { get; }

    public IReadOnlyList<string> ProgramFilesDirs { get; }

    public string Downloads { get; }

    public IReadOnlyList<string> UserContentFolders { get; }

    public IReadOnlyList<string> CloudSyncFolders { get; }

    public IReadOnlyList<string> FixedDriveRoots { get; }

    public string AppDataDir { get; }

    public string CurrentUserSid { get; }

    public string? EnvironmentOverride(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? N(value) : null;

    private IEnumerable<string?> DetectCloudFolders()
    {
        foreach (var variable in new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" })
        {
            yield return Environment.GetEnvironmentVariable(variable);
        }

        string[] oneDriveDirs;
        try
        {
            oneDriveDirs = Directory.GetDirectories(UserProfile, "OneDrive*");
        }
        catch (IOException)
        {
            oneDriveDirs = [];
        }
        catch (UnauthorizedAccessException)
        {
            oneDriveDirs = [];
        }

        foreach (var directory in oneDriveDirs)
        {
            yield return directory;
        }

        foreach (var name in new[] { "Dropbox", "iCloudDrive", "Box", "Google Drive", "MEGA" })
        {
            yield return Path.Combine(UserProfile, name);
        }

        // Dropbox records its real location (it can be moved anywhere) in info.json.
        foreach (var info in new[] { Path.Combine(LocalAppData, "Dropbox", "info.json"), Path.Combine(RoamingAppData, "Dropbox", "info.json") })
        {
            foreach (var path in ReadDropboxPaths(info))
            {
                yield return path;
            }
        }
    }

    private static List<string> ReadDropboxPaths(string infoFile)
    {
        var result = new List<string>();
        if (!File.Exists(infoFile))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(infoFile));
            foreach (var account in document.RootElement.EnumerateObject())
            {
                if (account.Value.ValueKind == JsonValueKind.Object
                    && account.Value.TryGetProperty("path", out var path)
                    && path.GetString() is { Length: > 0 } value)
                {
                    result.Add(value);
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable Dropbox config: fall back to the default folder name checked above.
        }

        return result;
    }

    private static string N(string path) => PathUtil.Normalize(path);

    private static List<string> Existing(params string?[] paths) => paths
        .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
        .Select(p => N(p!))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

/// <summary>
/// Maps every Windows location into a test folder so the whole engine can be exercised against fake data.
/// Layout: &lt;root&gt;\Users\Test\..., &lt;root&gt;\Windows\..., &lt;root&gt;\$Recycle.Bin\&lt;your SID&gt;\...
/// </summary>
public sealed class SandboxKnownPaths : IKnownPaths
{
    public SandboxKnownPaths(string root)
    {
        Root = PathUtil.Normalize(root);
        if (!Directory.Exists(Root))
        {
            throw new DirectoryNotFoundException($"Sandbox folder '{Root}' does not exist.");
        }

        SystemDriveRoot = Root;
        UserProfile = Path.Combine(Root, "Users", "Test");
        LocalAppData = Path.Combine(UserProfile, "AppData", "Local");
        RoamingAppData = Path.Combine(UserProfile, "AppData", "Roaming");
        LocalLowAppData = Path.Combine(UserProfile, "AppData", "LocalLow");
        UserTemp = Path.Combine(LocalAppData, "Temp");
        WindowsDir = Path.Combine(Root, "Windows");
        ProgramData = Path.Combine(Root, "ProgramData");
        ProgramFilesDirs = [Path.Combine(Root, "Program Files"), Path.Combine(Root, "Program Files (x86)")];
        Downloads = Path.Combine(UserProfile, "Downloads");
        UserContentFolders = new[] { "Documents", "Desktop", "Pictures", "Videos", "Music" }.Select(n => Path.Combine(UserProfile, n)).ToList();
        CloudSyncFolders = [Path.Combine(UserProfile, "OneDrive")];
        FixedDriveRoots = [Root];
        AppDataDir = Path.Combine(LocalAppData, "KvaliTool");

        using var identity = WindowsIdentity.GetCurrent();
        CurrentUserSid = identity.User?.Value ?? "S-1-5-21-0-0-0-1001";
    }

    public string Root { get; }

    public bool IsSandbox => true;

    public string UserProfile { get; }

    public string LocalAppData { get; }

    public string RoamingAppData { get; }

    public string LocalLowAppData { get; }

    public string UserTemp { get; }

    public string WindowsDir { get; }

    public string ProgramData { get; }

    public string SystemDriveRoot { get; }

    public IReadOnlyList<string> ProgramFilesDirs { get; }

    public string Downloads { get; }

    public IReadOnlyList<string> UserContentFolders { get; }

    public IReadOnlyList<string> CloudSyncFolders { get; }

    public IReadOnlyList<string> FixedDriveRoots { get; }

    public string AppDataDir { get; }

    public string CurrentUserSid { get; }

    public string? EnvironmentOverride(string variable) => null;
}
