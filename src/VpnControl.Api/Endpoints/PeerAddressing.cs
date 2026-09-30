using System.Globalization;

namespace VpnControl.Api.Endpoints;

/// <summary>
/// Turns an address index into the IPv4 and IPv6 addresses a peer is given.
/// </summary>
/// <remarks>
/// Both families are derived from one index, so the unique index on
/// <c>(ServerId, AddressIndex)</c> guarantees uniqueness for both at once. The gateway
/// holds index 0 in each family: <c>x.y.0.1</c> and <c>prefix::1</c>.
/// </remarks>
internal static class PeerAddressing
{
    /// <summary>
    /// Renders an address index as a /32 inside the configured IPv4 prefix.
    /// </summary>
    /// <param name="prefix">First two octets, for example <c>10.99</c>.</param>
    /// <param name="index">Address index, counting from 1.</param>
    /// <returns>The address in CIDR form.</returns>
    /// <remarks>
    /// A /32 is handed out rather than the whole subnet, because a client should route only
    /// its own address onto the interface. Giving each client a /16 would have every one of
    /// them believing it owns the entire range.
    /// <para>
    /// The last octet skips 0 and 255, which are the network and broadcast addresses of the
    /// /24 they sit in, so 254 usable addresses fit in each.
    /// </para>
    /// </remarks>
    public static string FormatIpv4(string prefix, int index)
    {
        int thirdOctet = index / 254;
        int fourthOctet = (index % 254) + 1;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{prefix}.{thirdOctet}.{fourthOctet}/32");
    }

    /// <summary>
    /// Renders an address index as a /128 inside the configured IPv6 /64, or returns
    /// <c>null</c> when no IPv6 prefix is configured.
    /// </summary>
    /// <param name="prefix">First four groups of the /64, for example <c>fd4c:7a2e:91b3:0</c>.</param>
    /// <param name="index">Address index, counting from 1.</param>
    /// <returns>The address in CIDR form, or <c>null</c>.</returns>
    /// <remarks>
    /// Offset by one so the numbering matches IPv4: index 1 is <c>::2</c>, as it is
    /// <c>.0.2</c>, and the gateway is <c>::1</c> in both.
    /// </remarks>
    public static string? FormatIpv6(string prefix, int index) =>
        string.IsNullOrEmpty(prefix)
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"{prefix}::{index + 1:x}/128");
}
