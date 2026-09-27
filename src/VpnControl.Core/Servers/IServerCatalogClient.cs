namespace VpnControl.Core.Servers;

/// <summary>
/// The client's view of the control plane: which gateways exist, and how to be
/// admitted to one.
/// </summary>
/// <remarks>
/// This is the seam that keeps the rest of the library testable. The connection
/// manager depends on this interface, never on <c>HttpClient</c>, so the tests and
/// the demo mode can substitute an implementation that answers from memory. It is
/// the repository pattern: callers ask for gateways without knowing whether the
/// answer came over the network, from a cache or from a fixture.
/// </remarks>
public interface IServerCatalogClient
{
    /// <summary>Fetches the current gateway catalog.</summary>
    /// <param name="country">Optional ISO 3166-1 alpha-2 country filter.</param>
    /// <param name="city">Optional city filter.</param>
    /// <param name="cancellationToken">Abandons the request.</param>
    /// <returns>A snapshot of the matching gateways.</returns>
    /// <exception cref="ServerCatalogException">The catalog could not be retrieved.</exception>
    Task<ServerCatalog> GetServersAsync(
        string? country = null,
        string? city = null,
        CancellationToken cancellationToken = default);

    /// <summary>Fetches one gateway by identifier.</summary>
    /// <param name="serverId">The gateway identifier.</param>
    /// <param name="cancellationToken">Abandons the request.</param>
    /// <returns>The gateway, or <c>null</c> when no such gateway exists.</returns>
    /// <exception cref="ServerCatalogException">The request failed for any other reason.</exception>
    Task<VpnServer?> GetServerAsync(string serverId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers a client public key with a gateway and receives the tunnel
    /// parameters chosen for it.
    /// </summary>
    /// <param name="request">The gateway and the public key to admit.</param>
    /// <param name="cancellationToken">Abandons the request.</param>
    /// <returns>The peer configuration to build a tunnel from.</returns>
    /// <exception cref="ServerCatalogException">
    /// The gateway is unknown, the key was rejected, or the call failed.
    /// </exception>
    Task<PeerConfiguration> RegisterPeerAsync(
        PeerRegistrationRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>Releases a registration so the gateway can reuse the address.</summary>
    /// <param name="peerId">Identifier returned when the peer was registered.</param>
    /// <param name="cancellationToken">Abandons the request.</param>
    /// <returns>
    /// <c>true</c> when a registration was removed, <c>false</c> when there was
    /// nothing to remove. A missing registration is not treated as an error, because
    /// the caller's goal, that the peer no longer be registered, already holds.
    /// </returns>
    /// <exception cref="ServerCatalogException">The call failed.</exception>
    Task<bool> UnregisterPeerAsync(string peerId, CancellationToken cancellationToken = default);
}
