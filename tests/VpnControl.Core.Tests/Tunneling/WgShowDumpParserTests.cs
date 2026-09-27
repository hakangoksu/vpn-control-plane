using FluentAssertions;
using VpnControl.Core.Tunneling;
using VpnControl.Tunnel;
using Xunit;

namespace VpnControl.Core.Tests.Tunneling;

/// <summary>
/// Parses captured output, which is the only way this code can be tested on a machine that
/// cannot create a tunnel. The samples follow the field order documented for
/// <c>wg show &lt;interface&gt; dump</c>.
/// </summary>
public sealed class WgShowDumpParserTests
{
    private const string InterfaceLine = "uPrivateKeyBase64=\tuPublicKeyBase64=\t51820\toff";

    private static string PeerLine(
        string publicKey = "cGVlclB1YmxpY0tleQ==",
        string endpoint = "203.0.113.7:51820",
        long handshakeSeconds = 1700000000,
        long received = 4096,
        long sent = 2048) =>
        string.Join('\t', publicKey, "(none)", endpoint, "0.0.0.0/0", handshakeSeconds, received, sent, "25");

    [Fact]
    public void A_single_peer_dump_yields_its_counters_and_handshake()
    {
        TunnelStatistics stats = WgShowDumpParser.Parse($"{InterfaceLine}\n{PeerLine()}\n");

        stats.BytesReceived.Should().Be(4096);
        stats.BytesSent.Should().Be(2048);
        stats.Endpoint.Should().Be("203.0.113.7:51820");
        stats.LastHandshake.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void Several_peers_are_summed_and_the_newest_handshake_is_kept()
    {
        string dump = string.Join('\n',
            InterfaceLine,
            PeerLine(handshakeSeconds: 1700000000, received: 100, sent: 10),
            PeerLine(publicKey: "b3RoZXJQZWVyS2V5", handshakeSeconds: 1700000500, received: 200, sent: 20));

        TunnelStatistics stats = WgShowDumpParser.Parse(dump);

        stats.BytesReceived.Should().Be(300);
        stats.BytesSent.Should().Be(30);
        stats.LastHandshake.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000500));
    }

    [Fact]
    public void A_peer_that_has_never_completed_a_handshake_reports_none()
    {
        // wg prints zero for "never", which must not be read as 1970.
        TunnelStatistics stats = WgShowDumpParser.Parse($"{InterfaceLine}\n{PeerLine(handshakeSeconds: 0)}\n");

        stats.LastHandshake.Should().BeNull();
        stats.BytesReceived.Should().Be(4096);
    }

    [Fact]
    public void A_peer_with_no_endpoint_yet_reports_none()
    {
        TunnelStatistics stats = WgShowDumpParser.Parse($"{InterfaceLine}\n{PeerLine(endpoint: "(none)")}\n");

        stats.Endpoint.Should().BeNull();
    }

    [Fact]
    public void An_interface_with_no_peers_yields_the_empty_statistics()
    {
        WgShowDumpParser.Parse($"{InterfaceLine}\n").Should().Be(TunnelStatistics.Empty);
        WgShowDumpParser.Parse(string.Empty).Should().Be(TunnelStatistics.Empty);
    }

    [Fact]
    public void A_malformed_counter_is_reported_rather_than_read_as_zero()
    {
        string dump = $"{InterfaceLine}\n" + string.Join('\t',
            "cGVlclB1YmxpY0tleQ==", "(none)", "203.0.113.7:51820", "0.0.0.0/0", "1700000000", "not-a-number", "2048", "25");

        Action act = () => WgShowDumpParser.Parse(dump);

        act.Should().Throw<TunnelException>().WithMessage("*transfer-rx*");
    }

    [Fact]
    public void Trailing_blank_lines_and_carriage_returns_are_tolerated()
    {
        TunnelStatistics stats = WgShowDumpParser.Parse($"{InterfaceLine}\r\n{PeerLine()}\r\n\r\n");

        stats.BytesReceived.Should().Be(4096);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(150, true)]
    [InlineData(400, false)]
    public void IsPeerAlive_treats_an_old_handshake_as_a_dead_peer(int handshakeAgeSeconds, bool expectedAlive)
    {
        var now = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var stats = new TunnelStatistics(0, 0, now.AddSeconds(-handshakeAgeSeconds), null);

        stats.IsPeerAlive(now).Should().Be(expectedAlive);
    }

    [Fact]
    public void A_peer_that_never_handshook_is_not_alive()
    {
        TunnelStatistics.Empty.IsPeerAlive(DateTimeOffset.UtcNow).Should().BeFalse();
    }
}
