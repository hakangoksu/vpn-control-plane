using VpnControl.Core.Servers;

namespace VpnControl.Core.Latency;

/// <summary>
/// Returns a fixed latency per gateway instead of touching the network.
/// </summary>
/// <remarks>
/// Two jobs, both worth having in the library rather than only in the test project.
/// A test needs measurements it can assert against, and the demo mode of the desktop
/// app needs plausible numbers for gateways that do not exist. Deriving the value from
/// a hash of the gateway identifier makes the numbers differ between gateways and stay
/// the same between runs, so a screenshot taken twice looks the same.
/// <para>
/// Nothing this type returns was measured. Anything shown from it belongs behind the
/// same "simulated" label as the simulated tunnel.
/// </para>
/// </remarks>
public sealed class DeterministicLatencyProbe : ILatencyProbe
{
    private readonly Dictionary<string, TimeSpan?> _overrides;
    private readonly TimeSpan _minimum;
    private readonly TimeSpan _spread;

    /// <summary>Creates the probe.</summary>
    /// <param name="overrides">
    /// Explicit results per gateway identifier. A <c>null</c> value means the gateway
    /// is treated as unreachable, which is how a test covers the unhealthy path.
    /// </param>
    /// <param name="minimum">Lowest latency the derived values can take.</param>
    /// <param name="spread">Width of the band above <paramref name="minimum"/>.</param>
    public DeterministicLatencyProbe(
        IReadOnlyDictionary<string, TimeSpan?>? overrides = null,
        TimeSpan? minimum = null,
        TimeSpan? spread = null)
    {
        _overrides = overrides is null
            ? new Dictionary<string, TimeSpan?>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, TimeSpan?>(overrides, StringComparer.OrdinalIgnoreCase);

        _minimum = minimum ?? TimeSpan.FromMilliseconds(12);
        _spread = spread ?? TimeSpan.FromMilliseconds(140);
    }

    /// <inheritdoc />
    public string Name => "Simulated (no network traffic)";

    /// <inheritdoc />
    public Task<LatencyMeasurement> ProbeAsync(VpnServer server, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(server);
        cancellationToken.ThrowIfCancellationRequested();

        if (_overrides.TryGetValue(server.Id, out TimeSpan? configured))
        {
            return Task.FromResult(configured is TimeSpan value
                ? LatencyMeasurement.Success(server, value)
                : LatencyMeasurement.Failure(server, "Simulated as unreachable."));
        }

        return Task.FromResult(LatencyMeasurement.Success(server, DeriveLatency(server.Id)));
    }

    /// <summary>
    /// Maps a gateway identifier onto a latency inside the configured band.
    /// </summary>
    /// <param name="serverId">Identifier to derive from.</param>
    /// <returns>A stable latency for that identifier.</returns>
    /// <remarks>
    /// A plain FNV-1a hash is used rather than <c>string.GetHashCode</c>, which is
    /// randomised per process and so would give different numbers on every launch.
    /// </remarks>
    public TimeSpan DeriveLatency(string serverId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serverId);

        const uint FnvOffsetBasis = 2166136261;
        const uint FnvPrime = 16777619;

        uint hash = FnvOffsetBasis;
        foreach (char c in serverId)
        {
            hash = (hash ^ c) * FnvPrime;
        }

        double fraction = hash / (double)uint.MaxValue;
        return _minimum + TimeSpan.FromTicks((long)(_spread.Ticks * fraction));
    }
}
