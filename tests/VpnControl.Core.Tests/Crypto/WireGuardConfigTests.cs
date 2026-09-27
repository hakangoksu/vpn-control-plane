using FluentAssertions;
using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using Xunit;

namespace VpnControl.Core.Tests.Crypto;

public sealed class WireGuardConfigTests
{
    [Fact]
    public void The_rendered_file_matches_the_wireguard_format_exactly()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        string serverKey = TestServers.GenerateKey();

        WireGuardConfig config = WireGuardConfig.Create(
            TestServers.CreatePeerConfiguration(serverPublicKey: serverKey),
            keys);

        // Asserted as a whole string rather than line by line. This text is consumed by a
        // tool outside the process, so the exact bytes are the contract.
        config.ToConfigText().Should().Be(
            $"""
            [Interface]
            PrivateKey = {keys.PrivateKeyBase64}
            Address = 10.99.0.2/32
            DNS = 10.99.0.1
            MTU = 1420

            [Peer]
            PublicKey = {serverKey}
            AllowedIPs = 10.99.0.0/16
            Endpoint = lt-vln-01.invalid:51820
            PersistentKeepalive = 25

            """.ReplaceLineEndings("\n"));
    }

    [Fact]
    public void Optional_sections_are_left_out_rather_than_written_empty()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        PeerConfiguration peer = TestServers.CreatePeerConfiguration() with
        {
            DnsServers = [],
            PersistentKeepaliveSeconds = null,
        };

        string text = WireGuardConfig.Create(peer, keys, mtu: null).ToConfigText();

        text.Should().NotContain("DNS =").And.NotContain("PersistentKeepalive =").And.NotContain("MTU =");
        text.Should().Contain("Endpoint = lt-vln-01.invalid:51820");
    }

    [Fact]
    public void Several_allowed_prefixes_and_resolvers_are_written_as_one_comma_separated_list()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        PeerConfiguration peer = TestServers.CreatePeerConfiguration() with
        {
            AllowedIps = ["10.0.0.0/8", "192.168.0.0/16"],
            DnsServers = ["10.99.0.1", "10.99.0.2"],
        };

        string text = WireGuardConfig.Create(peer, keys).ToConfigText();

        text.Should().Contain("AllowedIPs = 10.0.0.0/8, 192.168.0.0/16");
        text.Should().Contain("DNS = 10.99.0.1, 10.99.0.2");
    }

    [Fact]
    public void The_kill_switch_replaces_the_allowed_prefixes_with_a_full_tunnel()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        PeerConfiguration splitTunnel = TestServers.CreatePeerConfiguration() with
        {
            AllowedIps = ["10.99.0.0/16"],
        };

        WireGuardConfig config = WireGuardConfig.Create(splitTunnel, keys, killSwitch: true);

        config.KillSwitchRequested.Should().BeTrue();
        config.AllowedIps.Should().Equal("0.0.0.0/0", "::/0");
        config.ToConfigText().Should().Contain("AllowedIPs = 0.0.0.0/0, ::/0");
    }

    [Fact]
    public void Without_the_kill_switch_the_prefixes_the_control_plane_chose_are_kept()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        WireGuardConfig config = WireGuardConfig.Create(TestServers.CreatePeerConfiguration(), keys);

        config.AllowedIps.Should().Equal("10.99.0.0/16");
    }

    [Fact]
    public void The_redacted_form_hides_the_private_key_and_nothing_else()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        WireGuardConfig config = WireGuardConfig.Create(TestServers.CreatePeerConfiguration(), keys);

        string redacted = config.ToRedactedConfigText();

        redacted.Should().NotContain(keys.PrivateKeyBase64);
        redacted.Should().Contain("PrivateKey = <redacted>");
        redacted.Should().Contain($"PublicKey = {config.PeerPublicKey}");
        redacted.Should().Contain("Address = 10.99.0.2/32");
    }

    [Fact]
    public void A_gateway_key_of_the_wrong_length_is_rejected()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        PeerConfiguration peer = TestServers.CreatePeerConfiguration() with
        {
            ServerPublicKey = Convert.ToBase64String(new byte[16]),
        };

        Action act = () => WireGuardConfig.Create(peer, keys);

        act.Should().Throw<ArgumentException>().WithMessage("*32 byte*");
    }

    [Fact]
    public void A_peer_configuration_without_an_address_or_endpoint_is_rejected()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        Action noAddress = () => WireGuardConfig.Create(
            TestServers.CreatePeerConfiguration() with { AssignedAddress = "  " }, keys);
        Action noEndpoint = () => WireGuardConfig.Create(
            TestServers.CreatePeerConfiguration() with { Endpoint = "" }, keys);

        noAddress.Should().Throw<ArgumentException>().WithMessage("*assigned address*");
        noEndpoint.Should().Throw<ArgumentException>().WithMessage("*endpoint*");
    }

    [Fact]
    public void A_tunnel_with_no_allowed_prefixes_is_rejected()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        Action act = () => WireGuardConfig.Create(
            TestServers.CreatePeerConfiguration() with { AllowedIps = [] }, keys);

        act.Should().Throw<ArgumentException>().WithMessage("*carry no traffic*");
    }

    [Fact]
    public void The_rendered_file_uses_unix_line_endings_whatever_the_host_does()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        string text = WireGuardConfig.Create(TestServers.CreatePeerConfiguration(), keys).ToConfigText();

        text.Should().NotContain("\r");
    }
}
