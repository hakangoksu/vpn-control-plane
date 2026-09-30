using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Api.Cli;
using VpnControl.Api.Security;
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
    public async Task Registering_without_a_device_token_is_rejected()
    {
        HttpClient anonymous = _factory.CreateClient();

        HttpResponseMessage response = await anonymous.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Registering_with_a_well_formed_but_unknown_token_is_rejected()
    {
        HttpClient wrongKey = _factory.CreateBearerClient(AccessTokens.Create(AccessTokens.DevicePrefix));

        HttpResponseMessage response = await wrongKey.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_revoked_device_token_is_rejected()
    {
        (string deviceId, string token) = await _factory.EnrollDeviceAsync();
        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<AdminCommands>().RevokeDeviceAsync(deviceId, CancellationToken.None);
        }

        HttpResponseMessage response = await _factory.CreateBearerClient(token).PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_gateway_token_is_not_accepted_on_the_device_endpoints()
    {
        string gatewayToken = await _factory.AddGatewayAsync("peer-test-gw");

        HttpResponseMessage response = await _factory.CreateBearerClient(gatewayToken).PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("c2hvcnQ=")]
    public async Task A_public_key_that_is_not_thirty_two_bytes_is_rejected(string publicKey)
    {
        HttpResponseMessage response = await (await _factory.CreateDeviceClientAsync()).PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, publicKey));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("publicKey");
    }

    [Fact]
    public async Task A_request_without_a_gateway_identifier_is_rejected()
    {
        HttpResponseMessage response = await (await _factory.CreateDeviceClientAsync()).PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(string.Empty, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("serverId");
    }

    [Fact]
    public async Task Registering_against_an_unknown_gateway_gives_a_404()
    {
        HttpResponseMessage response = await (await _factory.CreateDeviceClientAsync()).PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("no-such-gateway", NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_gateway_the_operator_has_disabled_will_not_admit_a_peer()
    {
        HttpResponseMessage response = await (await _factory.CreateDeviceClientAsync()).PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(DisabledServerId, NewPublicKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task A_peer_can_be_registered_and_released_again()
    {
        HttpClient client = (await _factory.CreateDeviceClientAsync());

        HttpResponseMessage created = await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey(), "test-laptop"));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        created.Headers.Location.Should().NotBeNull();

        PeerConfiguration peer = (await created.Content.ReadFromJsonAsync<PeerConfiguration>())!;
        peer.ServerId.Should().Be(VilniusServerId);
        peer.PeerId.Should().NotBeNullOrWhiteSpace();
        peer.AssignedAddress.Should().MatchRegex(@"^10\.99\.\d+\.\d+/32$");
        peer.AssignedAddressV6.Should().StartWith(ControlPlaneApiFactory.TestIpv6Prefix + "::").And.EndWith("/128");
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
        HttpClient client = (await _factory.CreateDeviceClientAsync());
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
    public async Task A_key_another_device_holds_on_the_gateway_is_a_conflict()
    {
        HttpClient first = await _factory.CreateDeviceClientAsync("first");
        HttpClient second = await _factory.CreateDeviceClientAsync("second");
        string publicKey = NewPublicKey();

        HttpResponseMessage created = await first.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, publicKey));
        HttpResponseMessage duplicate = await second.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, publicKey));

        created.StatusCode.Should().Be(HttpStatusCode.Created);
        duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);

        PeerConfiguration peer = (await created.Content.ReadFromJsonAsync<PeerConfiguration>())!;
        await first.DeleteAsync(new Uri($"/api/peers/{peer.PeerId}", UriKind.Relative));
    }

    [Fact]
    public async Task Two_devices_on_one_gateway_are_given_different_addresses()
    {
        HttpClient firstDevice = await _factory.CreateDeviceClientAsync("first");
        HttpClient secondDevice = await _factory.CreateDeviceClientAsync("second");

        PeerConfiguration first = (await (await firstDevice.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;
        PeerConfiguration second = (await (await secondDevice.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;

        second.AssignedAddress.Should().NotBe(first.AssignedAddress);
        second.AssignedAddressV6.Should().NotBe(first.AssignedAddressV6);

        await firstDevice.DeleteAsync(new Uri($"/api/peers/{first.PeerId}", UriKind.Relative));
        await secondDevice.DeleteAsync(new Uri($"/api/peers/{second.PeerId}", UriKind.Relative));
    }

    [Fact]
    public async Task Many_devices_registering_at_once_all_get_distinct_addresses()
    {
        // The unique index is what guarantees this. Concurrent requests can read the same free
        // index; the loser gets a 409 and retries, which is what the client does too.
        HttpClient[] clients = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i => _factory.CreateDeviceClientAsync($"concurrent-{i}")));

        PeerConfiguration[] peers = await Task.WhenAll(clients.Select(async client =>
        {
            for (int attempt = 0; ; attempt++)
            {
                HttpResponseMessage response = await client.PostAsJsonAsync(
                    "/api/peers",
                    new PeerRegistrationRequest("lt-vln-02", NewPublicKey()));

                if (response.StatusCode == HttpStatusCode.Created)
                {
                    return (await response.Content.ReadFromJsonAsync<PeerConfiguration>())!;
                }

                response.StatusCode.Should().Be(HttpStatusCode.Conflict);
                attempt.Should().BeLessThan(20);
            }
        }));

        peers.Select(p => p.AssignedAddress).Should().OnlyHaveUniqueItems();
        peers.Select(p => p.AssignedAddressV6).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Registering_again_replaces_the_devices_previous_registration()
    {
        // One tunnel per device: a registration left by a crashed session must not stay
        // admitted on the gateway, and a stolen token must not be able to pile up addresses.
        HttpClient client = await _factory.CreateDeviceClientAsync();

        PeerConfiguration first = (await (await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;
        HttpResponseMessage second = await client.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("lt-kun-01", NewPublicKey()));

        second.StatusCode.Should().Be(HttpStatusCode.Created);

        HttpResponseMessage releaseOld = await client.DeleteAsync(new Uri($"/api/peers/{first.PeerId}", UriKind.Relative));
        releaseOld.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_device_cannot_release_another_devices_registration()
    {
        HttpClient owner = await _factory.CreateDeviceClientAsync("owner");
        HttpClient other = await _factory.CreateDeviceClientAsync("other");

        PeerConfiguration peer = (await (await owner.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest(VilniusServerId, NewPublicKey()))).Content.ReadFromJsonAsync<PeerConfiguration>())!;

        HttpResponseMessage attempt = await other.DeleteAsync(new Uri($"/api/peers/{peer.PeerId}", UriKind.Relative));
        attempt.StatusCode.Should().Be(HttpStatusCode.NotFound);

        HttpResponseMessage ownRelease = await owner.DeleteAsync(new Uri($"/api/peers/{peer.PeerId}", UriKind.Relative));
        ownRelease.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Releasing_a_registration_that_does_not_exist_gives_a_404()
    {
        HttpResponseMessage response = await (await _factory.CreateDeviceClientAsync())
            .DeleteAsync(new Uri("/api/peers/never-existed", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Releasing_a_registration_without_a_device_token_is_rejected()
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
        HttpClient client = (await _factory.CreateDeviceClientAsync());

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
