using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnControl.Core.Crypto;
using VpnControl.Core.Tunneling;

namespace VpnControl.Tunnel;

/// <summary>
/// A real Linux tunnel backend, driving <c>wg-quick</c> and <c>wg</c> as child processes.
/// </summary>
/// <remarks>
/// Shells out rather than talking to the kernel netlink interface directly. That is the
/// honest trade: <c>wg-quick</c> already handles interface creation, address assignment,
/// route installation and the firewall mark rules that keep traffic from leaking past a
/// full tunnel, and reimplementing that over netlink would be a project of its own for no
/// gain here. The cost is a dependency on an external tool and on its output format, which
/// is why the counters are read from the machine-readable <c>dump</c> form.
/// <para>
/// This backend needs privileges. It checks for the tools and reports a clear, actionable
/// exception when they are missing or when the process is not permitted to create an
/// interface, rather than surfacing an exit code.
/// </para>
/// </remarks>
public sealed class WgQuickTunnel : IVpnTunnel
{
    private const string WgQuickExecutable = "wg-quick";
    private const string WgExecutable = "wg";

    private readonly WgQuickOptions _options;
    private readonly ILogger<WgQuickTunnel> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private string? _configPath;
    // Written inside the semaphore, read from other threads by GetStatisticsAsync, so
    // the field is volatile rather than relying on the lock alone.
    private volatile TunnelState _state;
    private bool _disposed;

    /// <summary>Creates the backend.</summary>
    /// <param name="options">Interface name, config location and privilege settings.</param>
    /// <param name="logger">Destination for command diagnostics.</param>
    public WgQuickTunnel(IOptions<WgQuickOptions> options, ILogger<WgQuickTunnel> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => $"wg-quick ({_options.InterfaceName})";

    /// <inheritdoc />
    public bool IsSimulated => false;

    /// <inheritdoc />
    public TunnelState State => _state;

    /// <inheritdoc />
    public event EventHandler<TunnelStateChangedEventArgs>? StateChanged;

    /// <inheritdoc />
    /// <exception cref="TunnelException">
    /// The tools are missing, the process lacks privileges, or <c>wg-quick</c> rejected the
    /// configuration. No interface is left behind in any of those cases.
    /// </exception>
    public async Task UpAsync(WireGuardConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        EnsureSupportedPlatform();
        EnsureToolsPresent();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string configPath = await WriteConfigFileAsync(config, cancellationToken).ConfigureAwait(false);
            _configPath = configPath;

            ProcessResult result = await RunToolAsync(
                WgQuickExecutable,
                ["up", configPath],
                cancellationToken).ConfigureAwait(false);

            if (!result.Succeeded)
            {
                // The interface may be half built, so it is torn down before the error is
                // reported. Leaving a partial interface up would be the worst outcome: it
                // looks like a working tunnel and carries nothing.
                await RunToolQuietlyAsync(WgQuickExecutable, ["down", configPath]).ConfigureAwait(false);
                DeleteConfigFile(configPath);
                _configPath = null;

                SetState(TunnelState.Faulted, result.Diagnostics);
                throw new TunnelException($"wg-quick up failed: {result.Diagnostics}")
                {
                    ExitCode = result.ExitCode,
                    IsPrivilegeProblem = LooksLikePrivilegeFailure(result),
                };
            }

            _logger.LogInformation("Interface {Interface} is up to {Endpoint}.", _options.InterfaceName, config.Endpoint);
            SetState(TunnelState.Up, $"wg-quick brought up {_options.InterfaceName}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DownAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? configPath = _configPath;
            if (configPath is null)
            {
                // Nothing was brought up by this instance, so there is nothing to take
                // down. Reported as success, because the caller's goal already holds.
                SetState(TunnelState.Down, null);
                return;
            }

            ProcessResult result = await RunToolAsync(
                WgQuickExecutable,
                ["down", configPath],
                cancellationToken).ConfigureAwait(false);

            // The config file holds a private key, so it goes whether or not the command
            // succeeded. Leaving it on disk after a failed teardown would be the worse of
            // the two problems.
            DeleteConfigFile(configPath);
            _configPath = null;

            if (!result.Succeeded)
            {
                SetState(TunnelState.Faulted, result.Diagnostics);
                throw new TunnelException($"wg-quick down failed: {result.Diagnostics}")
                {
                    ExitCode = result.ExitCode,
                    IsPrivilegeProblem = LooksLikePrivilegeFailure(result),
                };
            }

            SetState(TunnelState.Down, $"wg-quick brought down {_options.InterfaceName}.");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        if (_state != TunnelState.Up)
        {
            return TunnelStatistics.Empty;
        }

        ProcessResult result = await RunToolAsync(
            WgExecutable,
            ["show", _options.InterfaceName, "dump"],
            cancellationToken).ConfigureAwait(false);

        if (!result.Succeeded)
        {
            throw new TunnelException($"wg show failed: {result.Diagnostics}")
            {
                ExitCode = result.ExitCode,
                IsPrivilegeProblem = LooksLikePrivilegeFailure(result),
            };
        }

        return WgShowDumpParser.Parse(result.StandardOutput);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // A tunnel left up after the process exits would keep routing traffic through an
        // interface nothing is managing any more, so teardown is attempted on dispose.
        // It is best effort: dispose is the wrong place to throw.
        if (_configPath is not null)
        {
            try
            {
                await DownAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (TunnelException ex)
            {
                _logger.LogError(ex, "Could not take {Interface} down while disposing.", _options.InterfaceName);
            }
        }

        _gate.Dispose();
    }

    /// <summary>
    /// Writes the configuration to a file only the current user can read.
    /// </summary>
    /// <returns>Full path of the file written.</returns>
    /// <remarks>
    /// The file contains the tunnel's private key, so the permissions are set before
    /// anything is written to it. Creating the file and then tightening the mode would leave
    /// a window in which another local user could read it.
    /// </remarks>
    private async Task<string> WriteConfigFileAsync(WireGuardConfig config, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_options.ConfigDirectory);

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            File.SetUnixFileMode(_options.ConfigDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        string path = Path.Combine(_options.ConfigDirectory, $"{_options.InterfaceName}.conf");

        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            using var writer = new StreamWriter(stream);
            await writer.WriteAsync(config.ToConfigText().AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        _logger.LogDebug("Wrote tunnel configuration to {Path}.", path);
        return path;
    }

    private void DeleteConfigFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException ex)
        {
            // Worth a warning rather than an exception: the tunnel is already down, and a
            // leftover file is a housekeeping problem, not a connectivity one.
            _logger.LogWarning(ex, "Could not delete {Path}.", path);
        }
    }

    /// <summary>Runs one of the WireGuard tools, with privilege escalation if configured.</summary>
    private async Task<ProcessResult> RunToolAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        (string fileName, List<string> args) = BuildCommand(executable, arguments);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.CommandTimeout);

        ProcessResult result = await ProcessRunner.RunAsync(fileName, args, timeout.Token).ConfigureAwait(false);

        _logger.LogDebug(
            "{Command} {Arguments} exited with {ExitCode}.",
            fileName,
            string.Join(' ', args),
            result.ExitCode);

        return result;
    }

    /// <summary>
    /// Runs a command whose failure is not worth reporting, used when unwinding.
    /// </summary>
    private async Task RunToolQuietlyAsync(string executable, IReadOnlyList<string> arguments)
    {
        try
        {
            using var timeout = new CancellationTokenSource(_options.CommandTimeout);
            (string fileName, List<string> args) = BuildCommand(executable, arguments);
            await ProcessRunner.RunAsync(fileName, args, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TunnelException or OperationCanceledException)
        {
            // Already unwinding from a failure. The original error is the one the caller
            // needs, so this one is recorded and not allowed to replace it.
            _logger.LogWarning(ex, "Cleanup command {Executable} failed.", executable);
        }
    }

    private (string FileName, List<string> Arguments) BuildCommand(string executable, IReadOnlyList<string> arguments)
    {
        if (string.IsNullOrWhiteSpace(_options.PrivilegeEscalationCommand))
        {
            return (executable, [.. arguments]);
        }

        var wrapped = new List<string>(arguments.Count + 1) { executable };
        wrapped.AddRange(arguments);
        return (_options.PrivilegeEscalationCommand, wrapped);
    }

    /// <summary>
    /// Guesses whether a failure was about permissions, to steer the error message.
    /// </summary>
    /// <remarks>
    /// A guess, and marked as one: the tools report a permission problem as an ordinary
    /// non-zero exit with text on stderr, so there is nothing better to go on than the
    /// text. Getting it wrong only changes the wording of a message.
    /// </remarks>
    private static bool LooksLikePrivilegeFailure(ProcessResult result) =>
        result.Diagnostics.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        result.Diagnostics.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase) ||
        result.Diagnostics.Contains("must be run as root", StringComparison.OrdinalIgnoreCase);

    private static void EnsureSupportedPlatform()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            throw new TunnelException(
                "This backend drives the Linux wg-quick script. On Windows a privileged service is needed instead; see WindowsServiceTunnel.");
        }
    }

    private static void EnsureToolsPresent()
    {
        foreach (string tool in new[] { WgQuickExecutable, WgExecutable })
        {
            if (!ProcessRunner.ExistsOnPath(tool))
            {
                throw new TunnelException(
                    $"'{tool}' was not found on PATH. Install the wireguard-tools package, or use the simulated backend.");
            }
        }
    }

    private void SetState(TunnelState next, string? detail)
    {
        TunnelState previous = _state;
        if (previous == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(this, new TunnelStateChangedEventArgs(previous, next, detail));
    }
}
