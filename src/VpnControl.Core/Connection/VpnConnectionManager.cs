using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnControl.Core.Crypto;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tunneling;

namespace VpnControl.Core.Connection;

/// <summary>
/// Drives a VPN session: fetch the catalog, measure it, pick a gateway, register a key,
/// build a configuration, hand it to a tunnel backend, and keep the state honest.
/// </summary>
/// <remarks>
/// The orchestrator, and the only class that knows the order of those steps. Everything
/// it uses arrives through a constructor parameter, so the tests drive the real sequence
/// with a fake catalog and a fake tunnel, and the desktop app runs the same code with a
/// simulated backend. There is no static state and no service locator anywhere in here.
/// <para>
/// One operation runs at a time, enforced with a semaphore rather than a lock because
/// the work is asynchronous and a lock cannot be held across an <c>await</c>. Two
/// overlapping connects would otherwise both register a peer and one would leak.
/// </para>
/// </remarks>
public sealed class VpnConnectionManager : IAsyncDisposable
{
    private readonly IServerCatalogClient _catalogClient;
    private readonly ILatencyProbe _probe;
    private readonly ServerSelector _selector;
    private readonly IVpnTunnel _tunnel;
    private readonly ILogger<VpnConnectionManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly ConnectionStateMachine _stateMachine = new();

    /// <summary>Serialises connect, switch and disconnect against each other.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private VpnConnectionOptions _options;
    private VpnSession? _session;
    private bool _disposed;

    /// <summary>Creates the manager.</summary>
    /// <param name="catalogClient">Source of gateways and peer registrations.</param>
    /// <param name="probe">How gateways are measured.</param>
    /// <param name="selector">Policy that ranks measured gateways.</param>
    /// <param name="tunnel">Platform backend that applies a configuration.</param>
    /// <param name="options">Device name, kill switch default, MTU and timeouts.</param>
    /// <param name="logger">Destination for session diagnostics.</param>
    /// <param name="timeProvider">Clock, injected so session timing is testable.</param>
    public VpnConnectionManager(
        IServerCatalogClient catalogClient,
        ILatencyProbe probe,
        ServerSelector selector,
        IVpnTunnel tunnel,
        IOptions<VpnConnectionOptions> options,
        ILogger<VpnConnectionManager> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(catalogClient);
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(selector);
        ArgumentNullException.ThrowIfNull(tunnel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _catalogClient = catalogClient;
        _probe = probe;
        _selector = selector;
        _tunnel = tunnel;
        _options = options.Value;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Raised after every session state change.</summary>
    /// <remarks>
    /// Forwarded straight from the state machine, which means it is raised on whichever
    /// thread performed the transition. A user interface has to marshal it.
    /// </remarks>
    public event EventHandler<ConnectionStateChangedEventArgs>? StateChanged
    {
        add => _stateMachine.StateChanged += value;
        remove => _stateMachine.StateChanged -= value;
    }

    /// <summary>Current session state.</summary>
    public ConnectionState State => _stateMachine.Current;

    /// <summary>Whether the tunnel is up.</summary>
    public bool IsConnected => _stateMachine.IsConnected;

    /// <summary>Gateway in use, or <c>null</c> when there is no session.</summary>
    public VpnServer? CurrentServer => _session?.Server;

    /// <summary>
    /// Gateway a connect or switch in progress is heading for, or <c>null</c> when nothing is
    /// in flight.
    /// </summary>
    /// <remarks>
    /// Set before the state changes to <c>Connecting</c> or <c>Switching</c>, so a listener to
    /// that change can say where the session is going. <see cref="CurrentServer"/> cannot: it
    /// is empty while connecting and still the old gateway while switching.
    /// </remarks>
    public VpnServer? TargetServer { get; private set; }

    /// <summary>When the current session came up, or <c>null</c> when there is none.</summary>
    public DateTimeOffset? ConnectedSince => _session?.StartedAt;

    /// <summary>Whether new tunnels are built with the kill switch on.</summary>
    public bool KillSwitchEnabled => _options.KillSwitchEnabled;

    /// <summary>Whether the active backend only simulates a tunnel.</summary>
    public bool IsSimulated => _tunnel.IsSimulated;

    /// <summary>Name of the latency measurement in use, for the user interface.</summary>
    /// <remarks>
    /// Exposed so the caption under the gateway list states how the figures were obtained,
    /// rather than assuming one technique and being wrong when configuration picks another.
    /// </remarks>
    public string LatencyProbeName => _probe.Name;

    /// <summary>Name of the active backend.</summary>
    public string TunnelName => _tunnel.Name;

    /// <summary>Most recent catalog snapshot, or an empty one before the first refresh.</summary>
    public ServerCatalog Catalog { get; private set; } = ServerCatalog.Empty;

    /// <summary>Most recent measurements, ranked, or <c>null</c> before the first refresh.</summary>
    public ServerSelectionResult? LastSelection { get; private set; }

    /// <summary>
    /// Fetches the catalog, probes every gateway and ranks the results.
    /// </summary>
    /// <param name="country">Optional country filter passed to the control plane.</param>
    /// <param name="city">Optional city filter passed to the control plane.</param>
    /// <param name="cancellationToken">Abandons the refresh.</param>
    /// <returns>The ranking, including the gateways that were rejected and why.</returns>
    /// <exception cref="ServerCatalogException">The catalog could not be fetched.</exception>
    /// <remarks>
    /// Does not take the operation gate. Refreshing is read only and a user should be
    /// able to re-measure the list while a tunnel is up.
    /// </remarks>
    public async Task<ServerSelectionResult> RefreshAsync(
        string? country = null,
        string? city = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        ServerCatalog catalog = await _catalogClient
            .GetServersAsync(country, city, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<LatencyMeasurement> measurements = await _probe
            .ProbeAllAsync(catalog.Servers, _options.ProbeConcurrency, cancellationToken)
            .ConfigureAwait(false);

        ServerSelectionResult selection = _selector.Select(measurements);

        Catalog = catalog;
        LastSelection = selection;

        _logger.LogInformation(
            "Refreshed catalog: {Total} gateways, {Eligible} eligible, probed with {Probe}.",
            catalog.Count,
            selection.Ranked.Count,
            _probe.Name);

        return selection;
    }

    /// <summary>Refreshes, then connects to the best ranked gateway.</summary>
    /// <param name="cancellationToken">Abandons the attempt.</param>
    /// <returns>The gateway that was connected to.</returns>
    /// <exception cref="NoServerAvailableException">No gateway was eligible.</exception>
    public async Task<VpnServer> ConnectToFastestAsync(CancellationToken cancellationToken = default)
    {
        ServerSelectionResult selection = await RefreshAsync(cancellationToken: cancellationToken).ConfigureAwait(false);

        if (selection.Best is not VpnServer best)
        {
            throw new NoServerAvailableException(selection.Excluded);
        }

        await ConnectAsync(best, cancellationToken).ConfigureAwait(false);
        return best;
    }

    /// <summary>Connects to a specific gateway.</summary>
    /// <param name="server">The gateway to use.</param>
    /// <param name="cancellationToken">Abandons the attempt and tears down any partial state.</param>
    /// <returns>A task that completes once the tunnel is up.</returns>
    /// <exception cref="InvalidStateTransitionException">
    /// There is already a session. Disconnect or switch instead.
    /// </exception>
    /// <exception cref="ServerCatalogException">The peer registration failed.</exception>
    /// <exception cref="TunnelException">The backend could not bring the tunnel up.</exception>
    public async Task ConnectAsync(VpnServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Throws when a session already exists, which is the useful behaviour: a
            // caller that meant to move gateway should say so by calling SwitchToAsync.
            TargetServer = server;
            _stateMachine.TransitionTo(ConnectionState.Connecting, $"Connecting to {server.Name}.");

            try
            {
                _session = await EstablishAsync(server, cancellationToken).ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Connected, $"Connected to {server.Name}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The user changed their mind. Nothing is faulted, but anything already
                // built has to go, so the machine walks the normal teardown path.
                _stateMachine.TransitionTo(ConnectionState.Disconnecting, "Connection cancelled.");
                await ReleaseQuietlyAsync().ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Disconnected, "Connection cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                await ReleaseQuietlyAsync().ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Faulted, ex.Message);
                _logger.LogError(ex, "Failed to connect to {ServerId}.", server.Id);
                throw;
            }
        }
        finally
        {
            TargetServer = null;
            _gate.Release();
        }
    }

    /// <summary>Moves an established session to a different gateway.</summary>
    /// <param name="server">The gateway to move to.</param>
    /// <param name="cancellationToken">Abandons the switch.</param>
    /// <returns>A task that completes once the new tunnel is up.</returns>
    /// <exception cref="InvalidStateTransitionException">There is no established session.</exception>
    /// <remarks>
    /// The old tunnel comes down before the new one goes up. WireGuard can have its peer
    /// endpoint changed in place, but a different gateway means a different peer key and
    /// a different assigned address, so there is nothing to reuse. The visible cost is a
    /// short gap in connectivity, which is why this has its own state: the interface can
    /// show a switch in progress rather than a disconnection.
    /// </remarks>
    public async Task SwitchToAsync(VpnServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TargetServer = server;
            _stateMachine.TransitionTo(ConnectionState.Switching, $"Switching to {server.Name}.");

            try
            {
                await ReleaseAsync(throwOnTunnelFailure: true).ConfigureAwait(false);
                _session = await EstablishAsync(server, cancellationToken).ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Connected, $"Switched to {server.Name}.");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _stateMachine.TransitionTo(ConnectionState.Disconnecting, "Switch cancelled.");
                await ReleaseQuietlyAsync().ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Disconnected, "Switch cancelled.");
                throw;
            }
            catch (Exception ex)
            {
                await ReleaseQuietlyAsync().ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Faulted, ex.Message);
                _logger.LogError(ex, "Failed to switch to {ServerId}.", server.Id);
                throw;
            }
        }
        finally
        {
            TargetServer = null;
            _gate.Release();
        }
    }

    /// <summary>Tears the session down.</summary>
    /// <param name="cancellationToken">
    /// Waited on only to acquire the operation gate. Teardown itself runs on its own
    /// timeout, because abandoning it halfway is how an interface gets left behind.
    /// </param>
    /// <returns>A task that completes once the tunnel is down.</returns>
    /// <exception cref="TunnelException">The interface could not be removed.</exception>
    /// <remarks>
    /// Disconnecting when already disconnected succeeds and does nothing.
    /// </remarks>
    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == ConnectionState.Disconnected)
            {
                return;
            }

            _stateMachine.TransitionTo(ConnectionState.Disconnecting, "Disconnect requested.");

            try
            {
                await ReleaseAsync(throwOnTunnelFailure: true).ConfigureAwait(false);
                _stateMachine.TransitionTo(ConnectionState.Disconnected, "Disconnected.");
            }
            catch (Exception ex)
            {
                // A tunnel that will not come down is the failure a kill switch exists to
                // catch, so it is reported as a fault rather than as a clean disconnect.
                _stateMachine.TransitionTo(ConnectionState.Faulted, ex.Message);
                _logger.LogError(ex, "Failed to tear the tunnel down cleanly.");
                throw;
            }
        }
        finally
        {
            TargetServer = null;
            _gate.Release();
        }
    }

    /// <summary>Reads the counters from the active tunnel.</summary>
    /// <param name="cancellationToken">Abandons the read.</param>
    /// <returns>
    /// The counters, or <see cref="TunnelStatistics.Empty"/> when nothing is connected.
    /// </returns>
    public async Task<TunnelStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return State is ConnectionState.Connected or ConnectionState.Switching
            ? await _tunnel.GetStatisticsAsync(cancellationToken).ConfigureAwait(false)
            : TunnelStatistics.Empty;
    }

    /// <summary>Turns the kill switch on or off.</summary>
    /// <param name="enabled">Desired setting.</param>
    /// <param name="cancellationToken">Abandons the re-apply, if one is needed.</param>
    /// <returns>A task that completes once the setting is in effect.</returns>
    /// <remarks>
    /// The setting lives in the configuration that was handed to the backend, so changing
    /// it while connected means building a new configuration. Rather than leaving the
    /// running tunnel disagreeing with the switch the user just flipped, this reconnects
    /// to the same gateway. A production client would do the same, and would install the
    /// firewall rules before dropping the old tunnel rather than after.
    /// </remarks>
    public async Task SetKillSwitchAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_options.KillSwitchEnabled == enabled)
        {
            return;
        }

        _options = CloneOptionsWith(enabled);
        _logger.LogInformation("Kill switch {State}.", enabled ? "enabled" : "disabled");

        if (State == ConnectionState.Connected && CurrentServer is VpnServer current)
        {
            await SwitchToAsync(current, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Tears down any session and releases the operation gate.</summary>
    /// <returns>A task that completes once cleanup has finished.</returns>
    /// <remarks>
    /// <see cref="IAsyncDisposable"/> rather than <see cref="IDisposable"/> because
    /// teardown talks to a backend and to the control plane, and blocking on that from a
    /// synchronous <c>Dispose</c> is how a UI thread deadlocks.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        await ReleaseQuietlyAsync().ConfigureAwait(false);
        await _tunnel.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }

    /// <summary>
    /// Performs the steps that turn a chosen gateway into a live tunnel.
    /// </summary>
    /// <remarks>
    /// A fresh key pair per session means a key found on disk later cannot be linked to
    /// a past session, and it costs nothing: X25519 generation is a single scalar
    /// multiplication.
    /// </remarks>
    private async Task<VpnSession> EstablishAsync(VpnServer server, CancellationToken cancellationToken)
    {
        WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        PeerConfiguration? peer = null;

        try
        {
            peer = await _catalogClient.RegisterPeerAsync(
                new PeerRegistrationRequest(server.Id, keys.PublicKeyBase64, _options.DeviceName),
                cancellationToken).ConfigureAwait(false);

            WireGuardConfig config = WireGuardConfig.Create(peer, keys, _options.KillSwitchEnabled, _options.Mtu);

            await _tunnel.UpAsync(config, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Tunnel up to {ServerId} at {Endpoint} as {Address} via {Backend}.",
                server.Id,
                peer.Endpoint,
                peer.AssignedAddress,
                _tunnel.Name);

            return new VpnSession(server, peer, keys, config, _timeProvider.GetUtcNow());
        }
        catch
        {
            // The registration was made but the tunnel never came up, so the address is
            // reserved for a peer that does not exist. Releasing it here keeps the
            // control plane from accumulating dead reservations on every failed attempt.
            if (peer is not null)
            {
                await UnregisterQuietlyAsync(peer.PeerId).ConfigureAwait(false);
            }

            keys.Dispose();
            throw;
        }
    }

    /// <summary>Brings the tunnel down, releases the registration and wipes the keys.</summary>
    /// <param name="throwOnTunnelFailure">
    /// Whether a backend failure propagates. It does on an explicit disconnect, where the
    /// user needs to know the interface is still there. It does not while unwinding a
    /// failed connect, where a second exception would hide the first.
    /// </param>
    private async Task ReleaseAsync(bool throwOnTunnelFailure)
    {
        VpnSession? session = _session;
        _session = null;

        // Teardown gets a fresh token with its own budget. Reusing the caller's token
        // would mean a cancelled connect skips its own cleanup.
        using var teardown = new CancellationTokenSource(_options.TeardownTimeout);

        try
        {
            await _tunnel.DownAsync(teardown.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!throwOnTunnelFailure)
        {
            _logger.LogWarning(ex, "Ignoring tunnel teardown failure while unwinding a failed attempt.");
        }

        if (session is not null)
        {
            await UnregisterQuietlyAsync(session.Peer.PeerId).ConfigureAwait(false);
            session.Dispose();
        }
    }

    /// <summary>Best effort teardown, used on paths that already have an exception in flight.</summary>
    private Task ReleaseQuietlyAsync() => ReleaseAsync(throwOnTunnelFailure: false);

    /// <summary>
    /// Releases a peer registration, logging rather than throwing on failure.
    /// </summary>
    /// <remarks>
    /// The exception is deliberately not propagated. Every caller is either unwinding a
    /// failure, where a second exception would replace the real one, or disconnecting,
    /// where the tunnel is already down and the user's traffic is safe. A registration
    /// left behind costs the control plane one address until it expires, which is the
    /// lesser problem. It is logged so it does not disappear silently.
    /// </remarks>
    private async Task UnregisterQuietlyAsync(string peerId)
    {
        using var timeout = new CancellationTokenSource(_options.TeardownTimeout);

        try
        {
            await _catalogClient.UnregisterPeerAsync(peerId, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not release peer registration {PeerId}.", peerId);
        }
    }

    private VpnConnectionOptions CloneOptionsWith(bool killSwitchEnabled) => new()
    {
        DeviceName = _options.DeviceName,
        KillSwitchEnabled = killSwitchEnabled,
        Mtu = _options.Mtu,
        ProbeConcurrency = _options.ProbeConcurrency,
        TeardownTimeout = _options.TeardownTimeout,
    };

    /// <summary>Everything that has to be undone when a session ends.</summary>
    /// <remarks>
    /// Grouping them means teardown cannot forget one. The private key is the reason this
    /// is disposable: dropping the reference would leave the bytes in memory until the
    /// garbage collector happened to reuse the page.
    /// </remarks>
    private sealed class VpnSession(
        VpnServer server,
        PeerConfiguration peer,
        WireGuardKeyPair keys,
        WireGuardConfig config,
        DateTimeOffset startedAt) : IDisposable
    {
        public VpnServer Server { get; } = server;

        public PeerConfiguration Peer { get; } = peer;

        public WireGuardConfig Config { get; } = config;

        public DateTimeOffset StartedAt { get; } = startedAt;

        public void Dispose() => keys.Dispose();
    }
}
