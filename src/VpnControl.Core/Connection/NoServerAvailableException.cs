using VpnControl.Core.Latency;

namespace VpnControl.Core.Connection;

/// <summary>
/// Raised when an automatic selection found no gateway it was willing to use.
/// </summary>
/// <remarks>
/// Carries the per-gateway rejection reasons, because "no server available" on its own
/// is the least useful error a VPN client can show. With the reasons attached, the
/// interface can say that every gateway was saturated, or that none of them answered,
/// which points at completely different problems.
/// </remarks>
public sealed class NoServerAvailableException : Exception
{
    /// <summary>Creates the exception from a selection pass that found nothing.</summary>
    /// <param name="excluded">The gateways that were rejected, with reasons.</param>
    public NoServerAvailableException(IReadOnlyList<ExcludedServer> excluded)
        : base(BuildMessage(excluded))
    {
        Excluded = excluded;
    }

    /// <summary>The gateways that were rejected, with a reason each.</summary>
    public IReadOnlyList<ExcludedServer> Excluded { get; }

    private static string BuildMessage(IReadOnlyList<ExcludedServer> excluded)
    {
        ArgumentNullException.ThrowIfNull(excluded);

        if (excluded.Count == 0)
        {
            return "No gateways were returned by the control plane.";
        }

        string detail = string.Join("; ", excluded.Select(e => $"{e.Server.Id}: {e.Reason}"));
        return $"No gateway was eligible. {detail}";
    }
}
