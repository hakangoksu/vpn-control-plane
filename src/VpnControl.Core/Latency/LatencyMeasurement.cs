using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// The outcome of probing one gateway: how long it took, or why it did not answer.
/// </summary>
/// <remarks>
/// A failed probe is represented as a value rather than an exception because the
/// caller probes every gateway and expects some of them to be unreachable. Throwing
/// would force a try/catch per gateway and lose the distinction between "this one is
/// down" and "the whole probe run failed".
/// </remarks>
public sealed record LatencyMeasurement
{
    /// <summary>The gateway that was probed.</summary>
    public required VpnServer Server { get; init; }

    /// <summary>Measured round trip, or <c>null</c> when the probe did not succeed.</summary>
    public TimeSpan? RoundTrip { get; init; }

    /// <summary>Why the probe failed, or <c>null</c> when it succeeded.</summary>
    public string? Error { get; init; }

    /// <summary>Whether the gateway answered.</summary>
    public bool IsReachable => RoundTrip.HasValue;

    /// <summary>
    /// Whether this gateway is a candidate at all: the operator advertises it and it
    /// answered a probe.
    /// </summary>
    public bool IsHealthy => IsReachable && Server.IsEnabled;

    /// <summary>Round trip in milliseconds, for display and for scoring.</summary>
    public double? RoundTripMilliseconds => RoundTrip?.TotalMilliseconds;

    /// <summary>Records a successful probe.</summary>
    /// <param name="server">The gateway probed.</param>
    /// <param name="roundTrip">Measured round trip.</param>
    /// <returns>A reachable measurement.</returns>
    public static LatencyMeasurement Success(VpnServer server, TimeSpan roundTrip) =>
        new() { Server = server, RoundTrip = roundTrip };

    /// <summary>Records a failed probe.</summary>
    /// <param name="server">The gateway probed.</param>
    /// <param name="error">Why it failed, in a form worth showing a user.</param>
    /// <returns>An unreachable measurement.</returns>
    public static LatencyMeasurement Failure(VpnServer server, string error) =>
        new() { Server = server, Error = error };
}
