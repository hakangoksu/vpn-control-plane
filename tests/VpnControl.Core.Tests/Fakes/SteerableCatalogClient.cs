using VpnControl.Core.Servers;

namespace VpnControl.Core.Tests.Fakes;

/// <summary>
/// Wraps <see cref="InMemoryServerCatalogClient"/> and adds failure injection and call
/// counting.
/// </summary>
/// <remarks>
/// A decorator rather than a replacement, so the successful paths keep the real allocation
/// behaviour and only the failure being tested is artificial.
/// </remarks>
internal sealed class SteerableCatalogClient(IEnumerable<VpnServer>? servers = null) : IServerCatalogClient
{
    private readonly InMemoryServerCatalogClient _inner = new(servers);

    public int RegisterCalls { get; private set; }

    public int UnregisterCalls { get; private set; }

    public List<string> UnregisteredPeerIds { get; } = [];

    /// <summary>Exception to throw from the next registration, or <c>null</c>.</summary>
    public Exception? FailOnRegister { get; set; }

    /// <summary>Exception to throw from every release, or <c>null</c>.</summary>
    public Exception? FailOnUnregister { get; set; }

    /// <summary>Exception to throw from every catalog fetch, or <c>null</c>.</summary>
    public Exception? FailOnGetServers { get; set; }

    /// <summary>Registrations the inner client still holds.</summary>
    public int RegisteredPeerCount => _inner.RegisteredPeerCount;

    public Task<ServerCatalog> GetServersAsync(
        string? country = null,
        string? city = null,
        CancellationToken cancellationToken = default)
    {
        if (FailOnGetServers is Exception failure)
        {
            return Task.FromException<ServerCatalog>(failure);
        }

        return _inner.GetServersAsync(country, city, cancellationToken);
    }

    public Task<VpnServer?> GetServerAsync(string serverId, CancellationToken cancellationToken = default) =>
        _inner.GetServerAsync(serverId, cancellationToken);

    public Task<PeerConfiguration> RegisterPeerAsync(
        PeerRegistrationRequest request,
        CancellationToken cancellationToken = default)
    {
        RegisterCalls++;

        if (FailOnRegister is Exception failure)
        {
            FailOnRegister = null;
            return Task.FromException<PeerConfiguration>(failure);
        }

        return _inner.RegisterPeerAsync(request, cancellationToken);
    }

    public Task<bool> UnregisterPeerAsync(string peerId, CancellationToken cancellationToken = default)
    {
        UnregisterCalls++;
        UnregisteredPeerIds.Add(peerId);

        if (FailOnUnregister is Exception failure)
        {
            return Task.FromException<bool>(failure);
        }

        return _inner.UnregisterPeerAsync(peerId, cancellationToken);
    }
}
