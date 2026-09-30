using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using VpnControl.Api.Endpoints;
using VpnControl.Core.Crypto;
using VpnControl.Core.Servers;
using Xunit;

namespace VpnControl.Api.Tests;

/// <summary>
/// Covers the long poll and the acknowledgement that let a registration answer only once
/// the gateway has admitted the key.
/// </summary>
public sealed class PeerActivationTests : IClassFixture<PeerActivationTests.WaitingFactory>
{
    private readonly WaitingFactory _factory;

    public PeerActivationTests(WaitingFactory factory) => _factory = factory;

    [Fact]
    public async Task A_registration_answers_once_the_gateway_agent_has_applied_it()
    {
        string agentToken = await _factory.AddGatewayAsync("gw-ack");
        HttpClient agent = _factory.CreateBearerClient(agentToken);
        HttpClient device = await _factory.CreateDeviceClientAsync();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        // What the real agent does: catch up, then hold a poll open with the version it has.
        GatewayPeerList initial = (await agent.GetFromJsonAsync<GatewayPeerList>("/api/gateway/peers?applied=0"))!;
        Task<GatewayPeerList?> held = agent.GetFromJsonAsync<GatewayPeerList>($"/api/gateway/peers?applied={initial.Version}&wait=10");

        var clock = Stopwatch.StartNew();
        Task<HttpResponseMessage> registration = device.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("gw-ack", keys.PublicKeyBase64));

        // The held poll returns because of the registration, with the new key in it.
        GatewayPeerList changed = (await held.WaitAsync(TimeSpan.FromSeconds(5)))!;
        changed.Version.Should().BeGreaterThan(initial.Version);
        changed.Peers.Should().ContainSingle(p => p.PublicKey == keys.PublicKeyBase64);

        registration.IsCompleted.Should().BeFalse("the gateway has not confirmed yet");

        // The agent applies it and polls again, which is the acknowledgement.
        Task<GatewayPeerList?> next = agent.GetFromJsonAsync<GatewayPeerList>($"/api/gateway/peers?applied={changed.Version}&wait=10");

        HttpResponseMessage response = await registration.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Stop();

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<PeerConfiguration>())!.ActiveOnGateway.Should().BeTrue();
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(3));

        _ = next;
    }

    [Fact]
    public async Task Without_an_agent_a_registration_still_succeeds_after_the_wait()
    {
        await _factory.AddGatewayAsync("gw-silent");
        HttpClient device = await _factory.CreateDeviceClientAsync();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        HttpResponseMessage response = await device.PostAsJsonAsync(
            "/api/peers",
            new PeerRegistrationRequest("gw-silent", keys.PublicKeyBase64));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        (await response.Content.ReadFromJsonAsync<PeerConfiguration>())!.ActiveOnGateway.Should().BeFalse();
    }

    [Fact]
    public async Task A_poll_with_nothing_new_is_held_for_the_requested_time()
    {
        string agentToken = await _factory.AddGatewayAsync("gw-idle");
        HttpClient agent = _factory.CreateBearerClient(agentToken);

        GatewayPeerList initial = (await agent.GetFromJsonAsync<GatewayPeerList>("/api/gateway/peers"))!;

        var clock = Stopwatch.StartNew();
        GatewayPeerList again = (await agent.GetFromJsonAsync<GatewayPeerList>($"/api/gateway/peers?applied={initial.Version}&wait=1"))!;

        clock.Elapsed.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
        again.Version.Should().Be(initial.Version);
    }

    /// <summary>The standard factory with a short but non-zero activation wait.</summary>
    public sealed class WaitingFactory : ControlPlaneApiFactory
    {
        /// <inheritdoc />
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ControlPlane:ActivationWaitSeconds"] = "2",
                }));
        }
    }
}
