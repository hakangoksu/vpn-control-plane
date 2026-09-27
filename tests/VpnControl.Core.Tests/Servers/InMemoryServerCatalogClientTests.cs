using FluentAssertions;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using Xunit;

namespace VpnControl.Core.Tests.Servers;

public sealed class InMemoryServerCatalogClientTests
{
    [Fact]
    public async Task Registering_and_releasing_a_peer_round_trips()
    {
        var client = new InMemoryServerCatalogClient([TestServers.Create("lt-vln-01")]);

        PeerConfiguration peer = await client.RegisterPeerAsync(
            new PeerRegistrationRequest("lt-vln-01", TestServers.SampleKey, "laptop"));

        peer.ServerId.Should().Be("lt-vln-01");
        peer.AssignedAddress.Should().Be("10.99.0.3/32");
        peer.ServerPublicKey.Should().Be(TestServers.SampleKey);
        client.RegisteredPeerCount.Should().Be(1);

        (await client.UnregisterPeerAsync(peer.PeerId)).Should().BeTrue();
        client.RegisteredPeerCount.Should().Be(0);
        (await client.UnregisterPeerAsync(peer.PeerId)).Should().BeFalse();
    }

    [Fact]
    public async Task Each_registration_gets_a_different_address()
    {
        var client = new InMemoryServerCatalogClient([TestServers.Create("lt-vln-01")]);

        PeerConfiguration first = await client.RegisterPeerAsync(new PeerRegistrationRequest("lt-vln-01", TestServers.SampleKey));
        PeerConfiguration second = await client.RegisterPeerAsync(new PeerRegistrationRequest("lt-vln-01", TestServers.GenerateKey()));

        second.AssignedAddress.Should().NotBe(first.AssignedAddress);
    }

    [Fact]
    public async Task Registering_against_an_unknown_gateway_is_rejected_as_a_not_found()
    {
        var client = new InMemoryServerCatalogClient([TestServers.Create("lt-vln-01")]);

        Func<Task> act = () => client.RegisterPeerAsync(new PeerRegistrationRequest("nope", TestServers.SampleKey));

        (await act.Should().ThrowAsync<ServerCatalogException>()).Which.StatusCode.Should().Be(404);
    }

    [Fact]
    public async Task The_catalog_can_be_filtered_by_country_and_city()
    {
        var client = new InMemoryServerCatalogClient([
            TestServers.Create("lt-vln-01", city: "Vilnius", country: "LT"),
            TestServers.Create("lt-kun-01", city: "Kaunas", country: "LT"),
            TestServers.Create("de-fra-01", city: "Frankfurt", country: "DE"),
        ]);

        (await client.GetServersAsync(country: "lt")).Count.Should().Be(2);
        (await client.GetServersAsync(city: "Frankfurt")).Count.Should().Be(1);
        (await client.GetServersAsync()).Count.Should().Be(3);
    }
}
