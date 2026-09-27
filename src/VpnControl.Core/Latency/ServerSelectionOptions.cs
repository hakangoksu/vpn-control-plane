namespace VpnControl.Core.Latency;

/// <summary>
/// Knobs that decide what "the best gateway" means.
/// </summary>
/// <remarks>
/// These are settings rather than constants because the right answer differs by
/// deployment: a gaming profile wants the lowest latency almost regardless of load,
/// a streaming profile would rather avoid a busy gateway. Binding them from
/// configuration means changing the policy does not mean changing the selector.
/// </remarks>
public sealed class ServerSelectionOptions
{
    /// <summary>Configuration section these options are bound from.</summary>
    public const string SectionName = "ServerSelection";

    /// <summary>
    /// How strongly reported load penalises a gateway, relative to its latency.
    /// </summary>
    /// <remarks>
    /// The score is the latency multiplied by <c>1 + LoadWeight * load / 100</c>. At the
    /// default of 1.0, a fully loaded gateway has to be twice as fast as an idle one to
    /// win. Zero ignores load and ranks purely on latency.
    /// </remarks>
    public double LoadWeight { get; set; } = 1.0;

    /// <summary>
    /// Load above which a gateway is dropped from consideration entirely.
    /// </summary>
    /// <remarks>
    /// Separate from the weighting because a saturated gateway is not merely a worse
    /// choice, it is one that will drop the connection. The default of 95 leaves a
    /// little headroom rather than waiting for 100.
    /// </remarks>
    public int MaxLoadPercent { get; set; } = 95;

    /// <summary>
    /// Latency above which a gateway is dropped, or <c>null</c> for no ceiling.
    /// </summary>
    public TimeSpan? MaxLatency { get; set; }
}
