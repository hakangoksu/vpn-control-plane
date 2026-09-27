namespace VpnControl.Core.Servers;

/// <summary>
/// Answers catalog and peer requests from memory, with no network involved.
/// </summary>
/// <remarks>
/// The second implementation of <see cref="IServerCatalogClient"/>, and the reason that
/// interface pays for itself. The desktop app points at this when no backend is running,
/// so the whole application can be launched, clicked through and demonstrated on a
/// machine with nothing else installed. It assigns addresses and issues peer identifiers
/// exactly as the API does, so the code above it cannot tell the difference.
/// <para>
/// Thread safe, because the desktop app calls it from a background task while the UI
/// thread reads the results.
/// </para>
/// </remarks>
public sealed class InMemoryServerCatalogClient : IServerCatalogClient
{
    private readonly object _gate = new();
    private readonly Dictionary<string, VpnServer> _servers;
    private readonly Dictionary<string, PeerConfiguration> _peers = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;

    private int _nextHostAddress = 2;

    /// <summary>Creates the client over a fixed gateway list.</summary>
    /// <param name="servers">Gateways to serve. Defaults to <see cref="DemoCatalog"/>.</param>
    /// <param name="timeProvider">Clock used to stamp catalog snapshots.</param>
    public InMemoryServerCatalogClient(IEnumerable<VpnServer>? servers = null, TimeProvider? timeProvider = null)
    {
        _servers = (servers ?? DemoCatalog.CreateServers())
            .ToDictionary(s => s.Id, StringComparer.OrdinalIgnoreCase);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Peer registrations currently held, for assertions and for display.</summary>
    public int RegisteredPeerCount
    {
        get
        {
            lock (_gate)
            {
                return _peers.Count;
            }
        }
    }

    /// <inheritdoc />
    public Task<ServerCatalog> GetServersAsync(
        string? country = null,
        string? city = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            IEnumerable<VpnServer> matches = _servers.Values;

            if (!string.IsNullOrWhiteSpace(country))
            {
                matches = matches.Where(s => string.Equals(s.Country, country, StringComparison.OrdinalIgnoreCase));
            }

            if (!string.IsNullOrWhiteSpace(city))
            {
                matches = matches.Where(s => string.Equals(s.City, city, StringComparison.OrdinalIgnoreCase));
            }

            return Task.FromResult(new ServerCatalog(matches.ToArray(), _timeProvider.GetUtcNow()));
        }
    }

    /// <inheritdoc />
    public Task<VpnServer?> GetServerAsync(string serverId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_servers.GetValueOrDefault(serverId));
        }
    }

    /// <inheritdoc />
    public Task<PeerConfiguration> RegisterPeerAsync(
        PeerRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            if (!_servers.TryGetValue(request.ServerId, out VpnServer? server))
            {
                throw new ServerCatalogException($"No gateway with id '{request.ServerId}'.") { StatusCode = 404 };
            }

            var peer = new PeerConfiguration
            {
                PeerId = Guid.NewGuid().ToString("n"),
                ServerId = server.Id,
                AssignedAddress = NextAddress(),
                ServerPublicKey = server.PublicKey,
                Endpoint = server.Endpoint,
                AllowedIps = ["0.0.0.0/0", "::/0"],
                DnsServers = ["10.99.0.1"],
                PersistentKeepaliveSeconds = 25,
            };

            _peers[peer.PeerId] = peer;
            return Task.FromResult(peer);
        }
    }

    /// <inheritdoc />
    public Task<bool> UnregisterPeerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            return Task.FromResult(_peers.Remove(peerId));
        }
    }

    /// <summary>
    /// Hands out the next address from the lab's 10.99.0.0/16 range.
    /// </summary>
    /// <remarks>
    /// Addresses are never reused here. A real control plane has to reclaim them, which is
    /// the interesting half of the problem and is out of scope for a simulation.
    /// </remarks>
    private string NextAddress()
    {
        int host = _nextHostAddress++;
        return $"10.99.{host / 254}.{(host % 254) + 1}/32";
    }
}
