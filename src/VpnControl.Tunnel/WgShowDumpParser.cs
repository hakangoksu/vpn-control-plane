using System.Globalization;
using VpnControl.Core.Tunneling;

namespace VpnControl.Tunnel;

/// <summary>
/// Reads the tab separated output of <c>wg show &lt;interface&gt; dump</c>.
/// </summary>
/// <remarks>
/// The plain <c>wg show</c> output is laid out for a human and its wording has changed
/// between releases. The <c>dump</c> form exists for programs: one record per line, tab
/// separated, with a stable field order. Parsing that instead is the difference between a
/// backend that survives a WireGuard upgrade and one that does not.
/// <para>
/// Kept as a static class with no dependencies so it can be tested against captured output
/// without WireGuard being installed, which is the only way this code gets tested at all
/// on a machine that cannot create a tunnel.
/// </para>
/// </remarks>
public static class WgShowDumpParser
{
    /// <summary>Number of fields in a peer record, fixed by the dump format.</summary>
    private const int PeerFieldCount = 8;

    /// <summary>
    /// Sums the counters of every peer on the interface and takes the most recent handshake.
    /// </summary>
    /// <param name="dumpOutput">Raw output of <c>wg show &lt;interface&gt; dump</c>.</param>
    /// <returns>
    /// The aggregated counters, or <see cref="TunnelStatistics.Empty"/> when the output
    /// lists no peers.
    /// </returns>
    /// <remarks>
    /// Aggregating is correct for this client because it configures exactly one peer per
    /// interface, so the sum is that peer. A client that configured several, for a split
    /// tunnel across regions, would want them separately.
    /// </remarks>
    /// <exception cref="TunnelException">A peer line could not be parsed.</exception>
    public static TunnelStatistics Parse(string dumpOutput)
    {
        ArgumentNullException.ThrowIfNull(dumpOutput);

        long received = 0;
        long sent = 0;
        DateTimeOffset? latestHandshake = null;
        string? endpoint = null;
        bool sawPeer = false;

        // The first non-empty line describes the interface itself and has four fields.
        // Peer lines have eight. Counting fields rather than skipping line one means a
        // dump of all interfaces, which has no interface-only line, parses too.
        foreach (string line in dumpOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] fields = line.Split('\t');
            if (fields.Length < PeerFieldCount)
            {
                continue;
            }

            sawPeer = true;
            endpoint ??= Normalise(fields[2]);

            if (ParseUnixSeconds(fields[4]) is DateTimeOffset handshake &&
                (latestHandshake is null || handshake > latestHandshake))
            {
                latestHandshake = handshake;
            }

            received += ParseCounter(fields[5], "transfer-rx");
            sent += ParseCounter(fields[6], "transfer-tx");
        }

        return sawPeer ? new TunnelStatistics(received, sent, latestHandshake, endpoint) : TunnelStatistics.Empty;
    }

    /// <summary>
    /// Turns the placeholders WireGuard prints for an absent value into <c>null</c>.
    /// </summary>
    /// <param name="field">Raw field text.</param>
    /// <returns>The value, or <c>null</c> when it was a placeholder.</returns>
    private static string? Normalise(string field) =>
        field is "(none)" or "off" or "" ? null : field;

    /// <summary>
    /// Reads a handshake timestamp, which the dump gives as Unix seconds, or 0 for never.
    /// </summary>
    private static DateTimeOffset? ParseUnixSeconds(string field)
    {
        if (!long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long seconds) || seconds <= 0)
        {
            return null;
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    /// <summary>Reads a byte counter, rejecting anything that is not a number.</summary>
    /// <remarks>
    /// A malformed counter is treated as an error rather than as zero. Silently showing
    /// nought bytes on a working tunnel would be a confusing way to report a parsing bug.
    /// </remarks>
    private static long ParseCounter(string field, string fieldName)
    {
        if (!long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out long value) || value < 0)
        {
            throw new TunnelException($"Could not read the {fieldName} field of the wg dump output: '{field}'.");
        }

        return value;
    }
}
