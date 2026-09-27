using System.Diagnostics;
using Microsoft.Extensions.Logging;
using VpnControl.Core.Crypto;
using VpnControl.Core.Tunneling;

namespace VpnControl.Tunnel;

/// <summary>
/// A tunnel backend that changes no network state and moves no packets.
/// </summary>
/// <remarks>
/// The default backend in this repository, and the reason the whole application can be
/// built and demonstrated on a machine with no WireGuard installed, no root access and
/// no gateways to connect to. It accepts a configuration, waits a moment, reports itself
/// up, and produces counters that grow with time so the user interface has something to
/// render.
/// <para>
/// It reports <see cref="IsSimulated"/> as <c>true</c>, and the user interface shows that
/// prominently. A VPN client that leaves the user unsure whether their traffic is actually
/// protected is worse than one that refuses to connect.
/// </para>
/// </remarks>
public sealed class SimulatedTunnel : IVpnTunnel
{
    private readonly ILogger<SimulatedTunnel> _logger;
    private readonly TimeSpan _handshakeDelay;
    private readonly long _bytesPerSecond;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    private WireGuardConfig? _config;
    private long _upTimestamp;
    private DateTimeOffset? _lastHandshake;
    private TunnelState _state;
    private bool _disposed;

    /// <summary>Creates the backend.</summary>
    /// <param name="logger">Destination for the configuration summary and state changes.</param>
    /// <param name="handshakeDelay">
    /// How long to pretend a handshake takes. Non-zero by default so the user interface
    /// actually passes through its connecting state, which is where most state handling
    /// bugs show up.
    /// </param>
    /// <param name="bytesPerSecond">
    /// Rate the simulated counters grow at. This is an invented number for a progress
    /// display, not a measurement of anything.
    /// </param>
    /// <param name="timeProvider">Clock, injected so tests do not have to wait.</param>
    public SimulatedTunnel(
        ILogger<SimulatedTunnel> logger,
        TimeSpan? handshakeDelay = null,
        long bytesPerSecond = 96 * 1024,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
        _handshakeDelay = handshakeDelay ?? TimeSpan.FromMilliseconds(600);
        _bytesPerSecond = bytesPerSecond;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string Name => "Simulated tunnel";

    /// <inheritdoc />
    public bool IsSimulated => true;

    /// <inheritdoc />
    public TunnelState State
    {
        get
        {
            lock (_gate)
            {
                return _state;
            }
        }
    }

    /// <inheritdoc />
    public event EventHandler<TunnelStateChangedEventArgs>? StateChanged;

    /// <summary>The configuration currently applied, with the private key redacted.</summary>
    /// <remarks>
    /// Exposed so the desktop app can show what it would have sent to a real backend.
    /// Reading the redacted form is the only option offered, on purpose.
    /// </remarks>
    public string? AppliedConfigRedacted
    {
        get
        {
            lock (_gate)
            {
                return _config?.ToRedactedConfigText();
            }
        }
    }

    /// <inheritdoc />
    public async Task UpAsync(WireGuardConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(_disposed, this);

        // The configuration is validated even though nothing will use it, so that a
        // mistake in the generation code surfaces in simulated runs too.
        if (!WireGuardKeyPair.IsValidKey(config.PrivateKey) || !WireGuardKeyPair.IsValidKey(config.PeerPublicKey))
        {
            throw new TunnelException("The configuration does not carry a valid pair of 32 byte keys.");
        }

        await Task.Delay(_handshakeDelay, _timeProvider, cancellationToken).ConfigureAwait(false);

        lock (_gate)
        {
            _config = config;
            _upTimestamp = Stopwatch.GetTimestamp();
            _lastHandshake = _timeProvider.GetUtcNow();
        }

        _logger.LogInformation(
            "Simulated tunnel up to {Endpoint}, address {Address}, kill switch {KillSwitch}. No packets are being moved.",
            config.Endpoint,
            config.Address,
            config.KillSwitchRequested ? "on" : "off");

        SetState(TunnelState.Up, $"Simulated tunnel to {config.Endpoint}.");
    }

    /// <inheritdoc />
    public Task DownAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _config = null;
            _lastHandshake = null;
            _upTimestamp = 0;
        }

        SetState(TunnelState.Down, "Simulated tunnel down.");
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (_state != TunnelState.Up || _config is null)
            {
                return Task.FromResult(TunnelStatistics.Empty);
            }

            double seconds = Stopwatch.GetElapsedTime(_upTimestamp).TotalSeconds;
            long received = (long)(seconds * _bytesPerSecond);

            // Sending less than is received is the shape of ordinary browsing, which
            // makes the two counters distinguishable at a glance in the UI.
            long sent = received / 7;

            return Task.FromResult(new TunnelStatistics(received, sent, _lastHandshake, _config.Endpoint));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await DownAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Stores the new state and notifies listeners from outside the lock.</summary>
    private void SetState(TunnelState next, string? detail)
    {
        TunnelState previous;

        lock (_gate)
        {
            if (_state == next)
            {
                return;
            }

            previous = _state;
            _state = next;
        }

        StateChanged?.Invoke(this, new TunnelStateChangedEventArgs(previous, next, detail));
    }
}
