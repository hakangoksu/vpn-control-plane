using Microsoft.Extensions.Options;
using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// Turns a set of latency measurements into a ranked list of gateways.
/// </summary>
/// <remarks>
/// Deliberately a pure function of its input: no network, no clock, no logging. Every
/// interesting rule in a VPN client lives in a method like this one, and keeping it
/// free of side effects is what makes the awkward cases, ties, saturated gateways, an
/// entirely unreachable region, cheap to write tests for.
/// </remarks>
public sealed class ServerSelector
{
    private readonly ServerSelectionOptions _options;

    /// <summary>Creates a selector with the given policy.</summary>
    /// <param name="options">Weighting and cut-off settings.</param>
    public ServerSelector(IOptions<ServerSelectionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
    }

    /// <summary>Creates a selector with the default policy.</summary>
    /// <remarks>
    /// A convenience for tests and for the demo host, which have no configuration
    /// system to bind from.
    /// </remarks>
    public ServerSelector()
        : this(Options.Create(new ServerSelectionOptions()))
    {
    }

    /// <summary>Ranks the measured gateways and reports the rejects.</summary>
    /// <param name="measurements">One measurement per gateway.</param>
    /// <returns>The ranking, which may be empty.</returns>
    public ServerSelectionResult Select(IEnumerable<LatencyMeasurement> measurements)
    {
        ArgumentNullException.ThrowIfNull(measurements);

        var ranked = new List<ServerRanking>();
        var excluded = new List<ExcludedServer>();

        foreach (LatencyMeasurement measurement in measurements)
        {
            string? rejection = Reject(measurement);
            if (rejection is not null)
            {
                excluded.Add(new ExcludedServer(measurement.Server, rejection));
                continue;
            }

            TimeSpan roundTrip = measurement.RoundTrip!.Value;
            ranked.Add(new ServerRanking(measurement.Server, roundTrip, Score(roundTrip, measurement.Server.LoadPercent)));
        }

        return new ServerSelectionResult
        {
            // Two gateways with the same score are common once latencies are rounded by
            // the network itself, so the order is settled further: prefer the quieter
            // gateway, then the faster one, then the identifier. The last step is not a
            // meaningful preference, it is there so the same input always produces the
            // same output, which a test can rely on and a user will not find surprising.
            Ranked = ranked
                .OrderBy(r => r.Score)
                .ThenBy(r => r.Server.LoadPercent)
                .ThenBy(r => r.RoundTrip)
                .ThenBy(r => r.Server.Id, StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Excluded = excluded,
        };
    }

    /// <summary>Returns the single best gateway, or <c>null</c> when none qualifies.</summary>
    /// <param name="measurements">One measurement per gateway.</param>
    /// <returns>The best gateway, or <c>null</c>.</returns>
    public VpnServer? SelectBest(IEnumerable<LatencyMeasurement> measurements) => Select(measurements).Best;

    /// <summary>
    /// Scores a gateway. Lower is better.
    /// </summary>
    /// <param name="roundTrip">Measured round trip.</param>
    /// <param name="loadPercent">Reported load, 0 to 100.</param>
    /// <returns>Latency in milliseconds after the load penalty.</returns>
    /// <remarks>
    /// Multiplying rather than adding keeps the score in units of milliseconds, so it
    /// stays readable, and makes the load penalty proportional: 10 percent extra load
    /// costs a distant gateway more real time than a nearby one, which matches how it
    /// feels to use.
    /// </remarks>
    public double Score(TimeSpan roundTrip, int loadPercent)
    {
        double load = Math.Clamp(loadPercent, 0, 100) / 100.0;
        return roundTrip.TotalMilliseconds * (1.0 + (_options.LoadWeight * load));
    }

    /// <summary>
    /// Returns the reason a measurement is not a candidate, or <c>null</c> when it is.
    /// </summary>
    private string? Reject(LatencyMeasurement measurement)
    {
        if (!measurement.Server.IsEnabled)
        {
            return "Disabled by the operator.";
        }

        if (!measurement.IsReachable)
        {
            return measurement.Error ?? "Did not answer the probe.";
        }

        if (measurement.Server.LoadPercent > _options.MaxLoadPercent)
        {
            return $"Load {measurement.Server.LoadPercent}% is above the {_options.MaxLoadPercent}% limit.";
        }

        if (_options.MaxLatency is TimeSpan limit && measurement.RoundTrip > limit)
        {
            return $"Latency {measurement.RoundTripMilliseconds:F0} ms is above the {limit.TotalMilliseconds:F0} ms limit.";
        }

        return null;
    }
}
