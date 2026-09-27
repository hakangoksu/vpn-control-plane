namespace VpnControl.Core.Tunneling;

/// <summary>
/// Counters read back from a live tunnel.
/// </summary>
/// <param name="BytesReceived">Total bytes received since the interface came up.</param>
/// <param name="BytesSent">Total bytes sent since the interface came up.</param>
/// <param name="LastHandshake">
/// When the peer last completed a handshake, or <c>null</c> when it never has.
/// This is the honest answer to "am I really connected": WireGuard has no session to
/// tear down, so a tunnel whose last handshake is minutes old is a tunnel whose peer
/// has gone away, even though the interface still exists.
/// </param>
/// <param name="Endpoint">The endpoint currently in use, as the backend reports it.</param>
public sealed record TunnelStatistics(
    long BytesReceived,
    long BytesSent,
    DateTimeOffset? LastHandshake,
    string? Endpoint)
{
    /// <summary>All counters at zero, for a tunnel that is not up.</summary>
    public static TunnelStatistics Empty { get; } = new(0, 0, null, null);

    /// <summary>
    /// Whether a handshake has happened recently enough to call the peer alive.
    /// </summary>
    /// <param name="now">Current time, passed in so this stays testable.</param>
    /// <param name="maxAge">
    /// How old the last handshake may be. WireGuard rekeys about every two minutes
    /// while traffic flows, so three minutes is a reasonable default before treating
    /// silence as a problem.
    /// </param>
    /// <returns><c>true</c> when a handshake happened within the window.</returns>
    public bool IsPeerAlive(DateTimeOffset now, TimeSpan? maxAge = null) =>
        LastHandshake is DateTimeOffset handshake && now - handshake <= (maxAge ?? TimeSpan.FromMinutes(3));
}
