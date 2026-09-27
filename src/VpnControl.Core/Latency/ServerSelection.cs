using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>One gateway's place in a ranking, with the numbers that put it there.</summary>
/// <param name="Server">The gateway.</param>
/// <param name="RoundTrip">Measured round trip.</param>
/// <param name="Score">
/// Latency in milliseconds after the load penalty. Lower is better. Carried so the
/// user interface and the logs can show why one gateway beat another instead of
/// presenting the choice as a black box.
/// </param>
public sealed record ServerRanking(VpnServer Server, TimeSpan RoundTrip, double Score);

/// <summary>A gateway that was not considered, and why.</summary>
/// <param name="Server">The gateway.</param>
/// <param name="Reason">Plain text reason, suitable for a log line or a tooltip.</param>
public sealed record ExcludedServer(VpnServer Server, string Reason);

/// <summary>
/// The full outcome of a selection pass: the ordered candidates and the rejects.
/// </summary>
/// <remarks>
/// Returning the rejects rather than silently dropping them is the difference between
/// "there is no server available" and a support conversation. When selection finds
/// nothing, the reasons are the answer.
/// </remarks>
public sealed record ServerSelectionResult
{
    /// <summary>Eligible gateways, best first.</summary>
    public required IReadOnlyList<ServerRanking> Ranked { get; init; }

    /// <summary>Gateways that were excluded, with a reason each.</summary>
    public required IReadOnlyList<ExcludedServer> Excluded { get; init; }

    /// <summary>The winner, or <c>null</c> when nothing was eligible.</summary>
    public VpnServer? Best => Ranked.Count > 0 ? Ranked[0].Server : null;

    /// <summary>Whether any gateway was eligible.</summary>
    public bool HasCandidate => Ranked.Count > 0;
}
