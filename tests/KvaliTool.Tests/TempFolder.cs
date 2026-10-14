using System.Diagnostics;

namespace KvaliTool.Tests;

/// <summary>A new folder under the system temp path that is removed again when the test is done.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "KvaliToolTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public string Write(string relativePath, byte[] contents)
    {
        var full = Combine(relativePath);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, contents);
        return full;
    }

    /// <summary>
    /// Windows reports "someone else has this file open and won't let it be replaced" as either an IOException or an
    /// UnauthorizedAccessException depending on the call; either is a clean, rethrown failure.
    /// </summary>
    public static void AssertFileError(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        Assert.Fail("Expected an IOException or UnauthorizedAccessException.");
    }

    /// <summary>Runs a cmd.exe built-in such as mklink and fails the test if it doesn't work.</summary>
    public static void RunCmd(string arguments)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", "/c " + arguments)
        {
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException("Couldn't start cmd.exe.");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode, $"cmd /c {arguments} failed: {output}");
    }

    public void Dispose()
    {
        try
        {
            // Remove links first so a recursive delete can never wander into what they point at.
            foreach (var entry in new DirectoryInfo(Path).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    entry.Delete();
                }
            }

            foreach (var file in new DirectoryInfo(Path).EnumerateFiles("*", SearchOption.AllDirectories))
            {
                file.Attributes = FileAttributes.Normal;
            }

            Directory.Delete(Path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temp folder is harmless.
        }
    }
}
