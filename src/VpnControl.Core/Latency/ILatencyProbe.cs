using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// Measures how far away a gateway is.
/// </summary>
/// <remarks>
/// This is a strategy: the selection logic wants a number per gateway and does not
/// care how it was obtained. That matters more than it might look, because the honest
/// ways of measuring a WireGuard gateway all have drawbacks. A TCP connect measures a
/// different port, ICMP needs elevated privileges on some platforms, and a real
/// WireGuard handshake needs a key the client has not been issued yet. Keeping the
/// measurement behind an interface lets the deployment pick its trade-off, and lets
/// the tests pick certainty.
/// </remarks>
public interface ILatencyProbe
{
    /// <summary>Human readable name of the technique, for logs and for the UI.</summary>
    string Name { get; }

    /// <summary>Probes one gateway.</summary>
    /// <param name="server">The gateway to measure.</param>
    /// <param name="cancellationToken">Abandons the probe.</param>
    /// <returns>
    /// A measurement, successful or not. Implementations report a failure as a
    /// <see cref="LatencyMeasurement"/> and do not throw for an unreachable host.
    /// </returns>
    Task<LatencyMeasurement> ProbeAsync(VpnServer server, CancellationToken cancellationToken = default);
}
