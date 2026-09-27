using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using VpnControl.Core.Crypto;
using VpnControl.Core.Tests.Fakes;
using VpnControl.Core.Tunneling;
using VpnControl.Tunnel;
using Xunit;

namespace VpnControl.Core.Tests.Tunneling;

public sealed class SimulatedTunnelTests
{
    private static SimulatedTunnel CreateTunnel() =>
        new(NullLogger<SimulatedTunnel>.Instance, handshakeDelay: TimeSpan.Zero);

    private static WireGuardConfig CreateConfig(WireGuardKeyPair keys) =>
        WireGuardConfig.Create(TestServers.CreatePeerConfiguration(), keys);

    [Fact]
    public async Task Bringing_the_tunnel_up_reports_it_up_and_raises_the_event()
    {
        await using SimulatedTunnel tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        var observed = new List<TunnelState>();
        tunnel.StateChanged += (_, args) => observed.Add(args.Current);

        tunnel.State.Should().Be(TunnelState.Down);
        await tunnel.UpAsync(CreateConfig(keys));

        tunnel.State.Should().Be(TunnelState.Up);
        tunnel.IsSimulated.Should().BeTrue();
        observed.Should().Equal(TunnelState.Up);
    }

    [Fact]
    public async Task Taking_the_tunnel_down_clears_the_statistics()
    {
        await using SimulatedTunnel tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        await tunnel.UpAsync(CreateConfig(keys));
        await tunnel.DownAsync();

        tunnel.State.Should().Be(TunnelState.Down);
        (await tunnel.GetStatisticsAsync()).Should().Be(TunnelStatistics.Empty);
        tunnel.AppliedConfigRedacted.Should().BeNull();
    }

    [Fact]
    public async Task Taking_a_tunnel_down_that_is_already_down_is_harmless()
    {
        await using SimulatedTunnel tunnel = CreateTunnel();

        Func<Task> act = () => tunnel.DownAsync();

        await act.Should().NotThrowAsync();
        tunnel.State.Should().Be(TunnelState.Down);
    }

    [Fact]
    public async Task While_up_the_counters_report_more_received_than_sent_and_a_recent_handshake()
    {
        await using SimulatedTunnel tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        await tunnel.UpAsync(CreateConfig(keys));
        TunnelStatistics stats = await tunnel.GetStatisticsAsync();

        stats.Endpoint.Should().Be("lt-vln-01.invalid:51820");
        stats.BytesReceived.Should().BeGreaterThanOrEqualTo(stats.BytesSent);
        stats.LastHandshake.Should().NotBeNull();
        stats.IsPeerAlive(DateTimeOffset.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task The_applied_configuration_is_only_available_with_the_private_key_removed()
    {
        await using SimulatedTunnel tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        await tunnel.UpAsync(CreateConfig(keys));

        tunnel.AppliedConfigRedacted.Should().NotBeNull();
        tunnel.AppliedConfigRedacted.Should().NotContain(keys.PrivateKeyBase64);
        tunnel.AppliedConfigRedacted.Should().Contain("<redacted>");
    }

    [Fact]
    public async Task A_configuration_without_valid_keys_is_refused_even_though_nothing_will_use_it()
    {
        // Validating in the simulated path too is what makes the simulation useful: a mistake
        // in the generation code fails here rather than only on a machine with wg installed.
        await using SimulatedTunnel tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();

        WireGuardConfig broken = CreateConfig(keys) with { PrivateKey = "too-short" };

        Func<Task> act = () => tunnel.UpAsync(broken);

        await act.Should().ThrowAsync<TunnelException>().WithMessage("*32 byte keys*");
        tunnel.State.Should().Be(TunnelState.Down);
    }

    [Fact]
    public async Task Cancelling_the_handshake_leaves_the_tunnel_down()
    {
        await using var tunnel = new SimulatedTunnel(
            NullLogger<SimulatedTunnel>.Instance,
            handshakeDelay: TimeSpan.FromSeconds(30));
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        Func<Task> act = () => tunnel.UpAsync(CreateConfig(keys), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        tunnel.State.Should().Be(TunnelState.Down);
    }

    [Fact]
    public async Task Disposing_an_open_tunnel_takes_it_down()
    {
        var tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        await tunnel.UpAsync(CreateConfig(keys));

        await tunnel.DisposeAsync();

        tunnel.State.Should().Be(TunnelState.Down);
    }

    [Fact]
    public async Task Using_a_disposed_tunnel_throws()
    {
        var tunnel = CreateTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        await tunnel.DisposeAsync();

        Func<Task> act = () => tunnel.UpAsync(CreateConfig(keys));

        await act.Should().ThrowAsync<ObjectDisposedException>();
    }
}
