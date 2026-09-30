using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Api.Cli;
using VpnControl.Api.Endpoints;
using VpnControl.Api.Security;
using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;
using Xunit;

namespace VpnControl.Api.Tests;

/// <summary>
/// Covers the endpoint gateways poll, above all that a gateway sees its own peers and
/// nobody else's.
/// </summary>
public sealed class GatewayEndpointTests(ControlPlaneApiFactory factory) : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ControlPlaneApiFactory _factory = factory;

    private static string NewPublicKey()
    {
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        return keys.PublicKeyBase64;
    }

    [Fact]
    public async Task A_gateway_receives_exactly_its_own_peers_with_both_addresses()
    {
        string tokenA = await _factory.AddGatewayAsync("gw-a");
        await _factory.AddGatewayAsync("gw-b");

        HttpClient deviceOnA = await _factory.CreateDeviceClientAsync("on-a");
        HttpClient deviceOnB = await _factory.CreateDeviceClientAsync("on-b");
        string keyOnA = NewPublicKey();

        (await deviceOnA.PostAsJsonAsync("/api/peers", new PeerRegistrationRequest("gw-a", keyOnA)))
            .StatusCode.Should().Be(HttpStatusCode.Created);
        (await deviceOnB.PostAsJsonAsync("/api/peers", new PeerRegistrationRequest("gw-b", NewPublicKey())))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        GatewayPeerList list = (await _factory.CreateBearerClient(tokenA)
            .GetFromJsonAsync<GatewayPeerList>("/api/gateway/peers"))!;

        list.GatewayId.Should().Be("gw-a");
        list.Peers.Should().ContainSingle();
        list.Peers[0].PublicKey.Should().Be(keyOnA);
        list.Peers[0].AllowedIps.Should().HaveCount(2);
        list.Peers[0].AllowedIps[0].Should().EndWith("/32");
        list.Peers[0].AllowedIps[1].Should().StartWith(ControlPlaneApiFactory.TestIpv6Prefix).And.EndWith("/128");
    }

    [Fact]
    public async Task Revoking_a_device_removes_its_peer_from_the_gateway_list()
    {
        string token = await _factory.AddGatewayAsync("gw-revoke");
        (string deviceId, string deviceToken) = await _factory.EnrollDeviceAsync("to-revoke");

        (await _factory.CreateBearerClient(deviceToken)
            .PostAsJsonAsync("/api/peers", new PeerRegistrationRequest("gw-revoke", NewPublicKey())))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        await using (AsyncServiceScope scope = _factory.Services.CreateAsyncScope())
        {
            int? removed = await scope.ServiceProvider.GetRequiredService<AdminCommands>()
                .RevokeDeviceAsync(deviceId, CancellationToken.None);
            removed.Should().Be(1);
        }

        GatewayPeerList list = (await _factory.CreateBearerClient(token)
            .GetFromJsonAsync<GatewayPeerList>("/api/gateway/peers"))!;

        list.Peers.Should().BeEmpty();
    }

    [Fact]
    public async Task The_endpoint_rejects_a_missing_token()
    {
        HttpResponseMessage response = await _factory.CreateClient()
            .GetAsync(new Uri("/api/gateway/peers", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_endpoint_rejects_a_device_token()
    {
        HttpClient device = await _factory.CreateDeviceClientAsync();

        HttpResponseMessage response = await device.GetAsync(new Uri("/api/gateway/peers", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_endpoint_rejects_an_unknown_gateway_token()
    {
        HttpResponseMessage response = await _factory.CreateBearerClient(AccessTokens.Create(AccessTokens.GatewayPrefix))
            .GetAsync(new Uri("/api/gateway/peers", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rotating_the_agent_token_invalidates_the_old_one()
    {
        string oldToken = await _factory.AddGatewayAsync("gw-rotate");
        string newToken = await _factory.AddGatewayAsync("gw-rotate");

        (await _factory.CreateBearerClient(oldToken).GetAsync(new Uri("/api/gateway/peers", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _factory.CreateBearerClient(newToken).GetAsync(new Uri("/api/gateway/peers", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
