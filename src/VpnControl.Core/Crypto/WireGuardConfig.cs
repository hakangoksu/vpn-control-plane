using System.Globalization;
using System.Text;
using VpnControl.Core.Servers;

namespace VpnControl.Core.Crypto;

/// <summary>
/// A complete WireGuard tunnel configuration, and the code that renders it in the
/// <c>[Interface]</c> and <c>[Peer]</c> file format the tools read.
/// </summary>
/// <remarks>
/// Every tunnel backend takes one of these. Keeping the format in one place means a
/// backend that writes a file, one that pipes the text to <c>wg setconf</c> and one
/// that only logs it all agree on what was requested.
/// </remarks>
public sealed record WireGuardConfig
{
    /// <summary>Default maximum transmission unit WireGuard uses on a tunnel interface.</summary>
    /// <remarks>
    /// 1420 leaves room inside a 1500 byte path for the outer IP, UDP and WireGuard
    /// headers. Getting it wrong produces a tunnel that carries small packets and
    /// stalls on large ones, which is a memorable way to learn about MTU.
    /// </remarks>
    public const int DefaultMtu = 1420;

    /// <summary>Base64 private key of the local interface.</summary>
    public required string PrivateKey { get; init; }

    /// <summary>
    /// Addresses assigned to the local interface, in CIDR form, comma separated when there
    /// is one per address family.
    /// </summary>
    public required string Address { get; init; }

    /// <summary>Resolvers to use while the tunnel is up. Empty means leave DNS alone.</summary>
    public IReadOnlyList<string> DnsServers { get; init; } = Array.Empty<string>();

    /// <summary>Interface MTU, or <c>null</c> to let the tooling choose.</summary>
    public int? Mtu { get; init; } = DefaultMtu;

    /// <summary>Base64 public key of the remote gateway.</summary>
    public required string PeerPublicKey { get; init; }

    /// <summary>
    /// Optional symmetric key mixed into the handshake on top of the key exchange.
    /// </summary>
    /// <remarks>
    /// WireGuard offers this as post-quantum hedging: an attacker recording traffic
    /// today and breaking X25519 later still needs this key. It is optional, and this
    /// lab's control plane does not issue one.
    /// </remarks>
    public string? PresharedKey { get; init; }

    /// <summary>Prefixes routed into the tunnel.</summary>
    public required IReadOnlyList<string> AllowedIps { get; init; }

    /// <summary><c>host:port</c> of the gateway.</summary>
    public required string Endpoint { get; init; }

    /// <summary>Keepalive interval in seconds, or <c>null</c> to send none.</summary>
    public int? PersistentKeepaliveSeconds { get; init; }

    /// <summary>
    /// Whether this configuration was built with the kill switch requested.
    /// </summary>
    /// <remarks>
    /// In this project the kill switch means two things: the tunnel is a full tunnel,
    /// so no prefix is left routed outside it, and the backend is expected to refuse
    /// to leave the machine with a partial configuration. On Linux, <c>wg-quick</c>
    /// installing a default route plus its firewall mark rules is what actually keeps
    /// traffic from leaking past a tunnel in that state. A Windows client would add
    /// Windows Filtering Platform rules of its own; see the notes on
    /// <c>WindowsServiceTunnel</c>. The flag is carried here so a backend can honour it
    /// and so the user interface can show the state it was asked for.
    /// </remarks>
    public bool KillSwitchRequested { get; init; }

    /// <summary>
    /// Builds a configuration from what the control plane assigned and the key pair
    /// the client generated.
    /// </summary>
    /// <param name="peer">Parameters returned by the control plane.</param>
    /// <param name="keys">Locally generated key pair. Only the private half is read.</param>
    /// <param name="killSwitch">
    /// When <c>true</c>, the allowed prefixes are replaced with a full tunnel so that
    /// nothing is routed around it.
    /// </param>
    /// <param name="mtu">Interface MTU, defaulting to <see cref="DefaultMtu"/>.</param>
    /// <returns>A validated configuration.</returns>
    /// <exception cref="ArgumentException">
    /// A key is not a 32 byte base64 value, or the peer configuration is missing an
    /// address, an endpoint or any allowed prefix.
    /// </exception>
    public static WireGuardConfig Create(
        PeerConfiguration peer,
        WireGuardKeyPair keys,
        bool killSwitch = false,
        int? mtu = DefaultMtu)
    {
        ArgumentNullException.ThrowIfNull(peer);
        ArgumentNullException.ThrowIfNull(keys);

        if (!WireGuardKeyPair.IsValidKey(peer.ServerPublicKey))
        {
            throw new ArgumentException("The gateway public key is not a 32 byte base64 value.", nameof(peer));
        }

        if (string.IsNullOrWhiteSpace(peer.AssignedAddress))
        {
            throw new ArgumentException("The peer configuration has no assigned address.", nameof(peer));
        }

        if (string.IsNullOrWhiteSpace(peer.Endpoint))
        {
            throw new ArgumentException("The peer configuration has no endpoint.", nameof(peer));
        }

        IReadOnlyList<string> allowedIps = killSwitch
            ? FullTunnelPrefixes
            : peer.AllowedIps;

        if (allowedIps.Count == 0)
        {
            throw new ArgumentException("A tunnel with no allowed prefixes would carry no traffic.", nameof(peer));
        }

        return new WireGuardConfig
        {
            PrivateKey = keys.PrivateKeyBase64,
            Address = string.IsNullOrWhiteSpace(peer.AssignedAddressV6)
                ? peer.AssignedAddress
                : $"{peer.AssignedAddress}, {peer.AssignedAddressV6}",
            DnsServers = peer.DnsServers,
            Mtu = mtu,
            PeerPublicKey = peer.ServerPublicKey,
            AllowedIps = allowedIps,
            Endpoint = peer.Endpoint,
            PersistentKeepaliveSeconds = peer.PersistentKeepaliveSeconds,
            KillSwitchRequested = killSwitch,
        };
    }

    /// <summary>Prefixes that together cover all IPv4 and IPv6 traffic.</summary>
    public static IReadOnlyList<string> FullTunnelPrefixes { get; } = new[] { "0.0.0.0/0", "::/0" };

    /// <summary>
    /// Renders the configuration in the format <c>wg-quick</c> and the WireGuard
    /// clients read.
    /// </summary>
    /// <returns>The file contents, with Unix line endings.</returns>
    /// <remarks>
    /// Line endings are fixed to <c>\n</c> rather than taken from the environment,
    /// because the same text may be generated on one machine and consumed on another
    /// and the parsers are happier with the Unix form.
    /// </remarks>
    public string ToConfigText()
    {
        var text = new StringBuilder();

        text.Append("[Interface]\n");
        text.Append(CultureInfo.InvariantCulture, $"PrivateKey = {PrivateKey}\n");
        text.Append(CultureInfo.InvariantCulture, $"Address = {Address}\n");

        if (DnsServers.Count > 0)
        {
            text.Append(CultureInfo.InvariantCulture, $"DNS = {string.Join(", ", DnsServers)}\n");
        }

        if (Mtu is int mtu)
        {
            text.Append(CultureInfo.InvariantCulture, $"MTU = {mtu}\n");
        }

        text.Append('\n');
        text.Append("[Peer]\n");
        text.Append(CultureInfo.InvariantCulture, $"PublicKey = {PeerPublicKey}\n");

        if (!string.IsNullOrWhiteSpace(PresharedKey))
        {
            text.Append(CultureInfo.InvariantCulture, $"PresharedKey = {PresharedKey}\n");
        }

        text.Append(CultureInfo.InvariantCulture, $"AllowedIPs = {string.Join(", ", AllowedIps)}\n");
        text.Append(CultureInfo.InvariantCulture, $"Endpoint = {Endpoint}\n");

        if (PersistentKeepaliveSeconds is int keepalive)
        {
            text.Append(CultureInfo.InvariantCulture, $"PersistentKeepalive = {keepalive}\n");
        }

        return text.ToString();
    }

    /// <summary>
    /// Renders the configuration with the private key replaced by a placeholder.
    /// </summary>
    /// <returns>Config text safe to write to a log or show in the user interface.</returns>
    /// <remarks>
    /// The obvious mistake when adding a "show me the config" button is to print the
    /// real thing. This overload exists so the safe version is the easy one to reach for.
    /// </remarks>
    public string ToRedactedConfigText() =>
        (this with { PrivateKey = "<redacted>" }).ToConfigText();
}
