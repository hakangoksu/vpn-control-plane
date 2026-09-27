using System.Diagnostics;
using VpnControl.Core.Tunneling;

namespace VpnControl.Tunnel;

/// <summary>Result of running an external command.</summary>
/// <param name="ExitCode">Exit code the command returned.</param>
/// <param name="StandardOutput">Everything the command wrote to stdout.</param>
/// <param name="StandardError">Everything the command wrote to stderr.</param>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>Whether the command reported success.</summary>
    public bool Succeeded => ExitCode == 0;

    /// <summary>
    /// Whichever stream carries the explanation, preferring stderr.
    /// </summary>
    /// <remarks>
    /// Command line tools are inconsistent about where they put an error message, so both
    /// are captured and the useful one is picked when building an exception message.
    /// </remarks>
    public string Diagnostics =>
        string.IsNullOrWhiteSpace(StandardError) ? StandardOutput.Trim() : StandardError.Trim();
}

/// <summary>
/// Runs an external command and captures its output.
/// </summary>
/// <remarks>
/// Small on purpose, because every subtle bug in this area comes from the same few
/// mistakes: reading one stream to completion before the other and deadlocking when the
/// pipe buffer fills, forgetting that <c>WaitForExit</c> without a token blocks forever,
/// and passing arguments as a single string so that a space in a path becomes two
/// arguments. This reads both streams concurrently, honours a token, and takes arguments
/// as a list.
/// </remarks>
public static class ProcessRunner
{
    /// <summary>Runs a command and waits for it to finish.</summary>
    /// <param name="fileName">Executable to run, resolved through <c>PATH</c>.</param>
    /// <param name="arguments">Arguments, each passed separately and quoted by the runtime.</param>
    /// <param name="cancellationToken">
    /// Cancels the wait and kills the process tree, so an unresponsive tool cannot hang
    /// the caller indefinitely.
    /// </param>
    /// <returns>Exit code and captured output.</returns>
    /// <exception cref="TunnelException">The executable could not be started.</exception>
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            throw new TunnelException($"Could not run '{fileName}'. Is it installed and on PATH? {ex.Message}", ex);
        }

        // Both streams are read before the wait, and concurrently with each other. Reading
        // one to the end first is the classic deadlock: the child blocks writing to the
        // full pipe of the stream nobody is draining.
        Task<string> readOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> readError = process.StandardError.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(process);
            throw;
        }

        string output = await readOutput.ConfigureAwait(false);
        string error = await readError.ConfigureAwait(false);

        return new ProcessResult(process.ExitCode, output, error);
    }

    /// <summary>Checks whether an executable can be found on <c>PATH</c>.</summary>
    /// <param name="fileName">Executable name, without a directory.</param>
    /// <returns><c>true</c> when a matching executable file exists.</returns>
    /// <remarks>
    /// Used to turn "wg-quick is not installed" into a clear message before anything is
    /// attempted, instead of a Win32 exception from deep inside a connect attempt.
    /// </remarks>
    public static bool ExistsOnPath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        string? path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (string directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(directory, fileName)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Kills a process tree, ignoring the race where it exited on its own first.
    /// </summary>
    private static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // The process finished between the check and the kill. Nothing to do, and
            // nothing worth reporting: the goal, that it not be running, already holds.
        }
    }
}
