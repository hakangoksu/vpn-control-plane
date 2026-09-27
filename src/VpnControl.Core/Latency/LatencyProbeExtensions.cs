using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// Runs a probe across a whole catalog.
/// </summary>
public static class LatencyProbeExtensions
{
    /// <summary>
    /// Probes every gateway, several at a time, and returns the results ordered
    /// fastest first with the unreachable ones last.
    /// </summary>
    /// <param name="probe">The probe to run.</param>
    /// <param name="servers">Gateways to measure.</param>
    /// <param name="maxConcurrency">
    /// How many probes may be in flight at once. Probing a hundred gateways at once
    /// would compete for the same uplink and make every measurement look worse than
    /// it is, so the fan-out is capped.
    /// </param>
    /// <param name="cancellationToken">Abandons the run.</param>
    /// <returns>One measurement per gateway.</returns>
    public static async Task<IReadOnlyList<LatencyMeasurement>> ProbeAllAsync(
        this ILatencyProbe probe,
        IEnumerable<VpnServer> servers,
        int maxConcurrency = 8,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentNullException.ThrowIfNull(servers);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxConcurrency, 1);

        VpnServer[] targets = servers.ToArray();
        var results = new LatencyMeasurement[targets.Length];

        // Writing into a pre-sized array by index keeps this lock free: each task owns
        // exactly one slot, so no synchronisation is needed and the order is preserved.
        await Parallel.ForEachAsync(
            Enumerable.Range(0, targets.Length),
            new ParallelOptions { MaxDegreeOfParallelism = maxConcurrency, CancellationToken = cancellationToken },
            async (index, token) => results[index] = await probe.ProbeAsync(targets[index], token).ConfigureAwait(false))
            .ConfigureAwait(false);

        return results
            .OrderBy(m => m.IsReachable ? 0 : 1)
            .ThenBy(m => m.RoundTrip ?? TimeSpan.MaxValue)
            .ToArray();
    }
}
