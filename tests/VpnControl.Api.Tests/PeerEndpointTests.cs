using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;
using Xunit;

namespace VpnControl.Api.Tests;

/// <summary>
/// Covers the peer endpoints, including every status code the client code branches on.
/// </summary>
/// <remarks>
/// Each test registers its own freshly generated key, so no test depends on another having
/// run first and the unique index cannot trip over a shared fixture value.
/// </remarks>
public sealed class PeerEndpointTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private const string VilniusServerId = "lt-vln-01";
    private const string DisabledServerId = "us-nyc-01";

    private readonly ControlPlaneApiFactory _factory = factory;

    private static string NewPublicKey()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        return keys.PublicKeyBase64;
    }

    [Fact]
    public async Task Registering_without_an_api_key_is_rejected()
    {
        HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Registering_with_the_wrong_api_key_is_rejected()
    {
        HttpClient wrongKey = _factory.CreateClient();
        wrongKey.DefaultRequestHeaders.Add("X-Api-Key", "not-the-key");

        HttpResponseMessage response = await wrongKey.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_catalog_stays_readable_without_an_api_key()
    {
        // Only the peer endpoints are behind the filter. A client has to be able to show the
        // gateway list before it has any credential to present.
        HttpResponseMessage response = await _factory.CreateClient().GetAsync(new Uri("/api/servers", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("c2hvcnQ=")]
    public async Task A_public_key_that_is_not_thirty_two_bytes_is_rejected(string publicKey)
    {
        HttpResponseMessage response = await _factory.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, publicKey));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("publicKey");
    }

    [Fact]
    public async Task A_request_without_a_gateway_identifier_is_rejected()
    {
        HttpResponseMessage response = await _factory.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(string.Empty, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("serverId");
    }

    [Fact]
    public async Task Registering_against_an_unknown_gateway_gives_a_404()
    {
        HttpResponseMessage response = await _factory.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("no-such-gateway", NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_gateway_the_operator_has_disabled_will_not_admit_a_peer()
    {
        HttpResponseMessage response = await _factory.CreateAuthenticatedClient().PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(DisabledServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_peer_can_be_registered_and_released_again()
    {
        HttpClient client = _factory.CreateAuthenticatedClient();

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey(), "test-laptop"));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location.Should().NotBeNull();

        PeerConfiguration peer = (await created.Content.ReadFromJsonAsync<PeerConfiguration>())!;
        peer.ServerId.Should().Be(VilniusServerId);
        peer.PeerId.Should().NotBeNullOrWhiteSpace();
        peer.AssignedAddress.Should().MatchRegex(@"^10\.99\.\d+\.\d+/32$");
        peer.Endpoint.Should().Be("lab-vilnius-1.invalid:51820");
        peer.AllowedIps.Should().Equal("0.0.0.0/0", "::/0");
        peer.DnsServers.Should().Equal("10.99.0.1");
        peer.PersistentKeepaliveSeconds.Should().Be(25);
        WireGuardKeyPair.IsValidKey(peer.ServerPublicKey).Should().BeTrue();

        HttpResponseMessage deleted = await client.DeleteAsync(new Uri($"/api/peers/{peer.PeerId}", UriKind.Relative));
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task The_returned_configuration_is_enough_to_build_a_tunnel()
    {
        // The point of the whole exchange: what the API returns, plus a locally generated key,
        // has to produce a configuration the tunnel code accepts.
        HttpClient client = _factory.CreateAuthenticatedClient();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, keys.PublicKeyBase64));

        PeerConfiguration peer = (await created.Content.ReadFromJsonAsync<PeerConfiguration>())!;
        WireGuardConfig config = WireGuardConfig.Create(peer, keys);

        config.ToConfigText().Should().Contain("[Interface]").And.Contain("[Peer]");
        config.ToConfigText().Should().Contain($"Endpoint = {peer.Endpoint}");

        await client.DeleteAsync(new Uri($"/api/peers/{peer.PeerId}", UriKind.Relative));
    }

    [Fact]
    public async Task Registering_the_same_key_twice_on_one_gateway_is_a_conflict()
    {
        HttpClient client = _factory.CreateAuthenticatedClient();
        string publicKey = NewPublicKey();

        HttpResponseMessage first = await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, publicKey));
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, publicKey));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);

        PeerConfiguration peer = (await first.Content.ReadFromJsonAsync<PeerConfiguration>())!;
        await client.DeleteAsync(new Uri($"/api/peers/{peer.PeerId}", UriKind.Relative));
    }

    [Fact]
    public async Task Two_peers_on_one_gateway_are_given_different_addresses()
    {
        HttpClient client = _factory.CreateAuthenticatedClient();

        PeerConfiguration first = (await (await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;
        PeerConfiguration second = (await (await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;

        second.AssignedAddress.Should().NotBe(first.AssignedAddress);

        await client.DeleteAsync(new Uri($"/api/peers/{first.PeerId}", UriKind.Relative));
        await client.DeleteAsync(new Uri($"/api/peers/{second.PeerId}", UriKind.Relative));
    }

    [Fact]
    public async Task Releasing_a_registration_that_does_not_exist_gives_a_404()
    {
        HttpResponseMessage response = await _factory.CreateAuthenticatedClient()
            .DeleteAsync(new Uri("/api/peers/never-existed", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Releasing_a_registration_without_an_api_key_is_rejected()
    {
        HttpResponseMessage response = await _factory.CreateClient()
            .DeleteAsync(new Uri("/api/peers/anything", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_released_address_is_handed_out_again()
    {
        // The pool is finite, so an address that is given back has to become available. This is
        // the behaviour that stops a long-running gateway from exhausting its range.
        HttpClient client = _factory.CreateAuthenticatedClient();

        PeerConfiguration first = (await (await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("lt-kun-01", NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;

        await client.DeleteAsync(new Uri($"/api/peers/{first.PeerId}", UriKind.Relative));

        PeerConfiguration second = (await (await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("lt-kun-01", NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;

        second.AssignedAddress.Should().Be(first.AssignedAddress);
        second.PeerId.Should().NotBe(first.PeerId);

        await client.DeleteAsync(new Uri($"/api/peers/{second.PeerId}", UriKind.Relative));
    }
}
