using System.Diagnostics;

namespace KvaliTool.Core.Platform;

public sealed record CommandResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>
/// Runs a read-only command (dism /AnalyzeComponentStore, docker system df, Get-ComputerRestorePoint)
/// with a timeout. Scanners only ever pass commands that report; none of them change anything.
/// </summary>
public static class CommandRunner
{
    public static async Task<CommandResult> RunAsync(
        string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {executable}.");

        var stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            cancellationToken.ThrowIfCancellationRequested();
            return new CommandResult(-1, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: true);
        }

        return new CommandResult(process.ExitCode, await stdout.ConfigureAwait(false), await stderr.ConfigureAwait(false), TimedOut: false);
    }

    private static void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }
}
