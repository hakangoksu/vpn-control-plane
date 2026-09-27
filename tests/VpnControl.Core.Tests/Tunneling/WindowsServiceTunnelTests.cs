using FluentAssertions;
using VpnControl.Core.Crypto;
using VpnControl.Core.Tests.Fakes;
using VpnControl.Core.Tunneling;
using VpnControl.Tunnel;
using Xunit;

namespace VpnControl.Core.Tests.Tunneling;

/// <summary>
/// Pins down that the Windows placeholder refuses rather than pretends. That is the whole
/// behaviour it has, and it is the behaviour that matters: a backend which reported success
/// without building a tunnel would mislead a user about whether their traffic is protected.
/// </summary>
public sealed class WindowsServiceTunnelTests
{
    [Fact]
    public async Task Every_operation_throws_platform_not_supported()
    {
        await using var tunnel = new WindowsServiceTunnel();
        using WireGuardKeyPair keys = WireGuardKeyPair.Generate();
        WireGuardConfig config = WireGuardConfig.Create(TestServers.CreatePeerConfiguration(), keys);

        await FluentActions.Awaiting(() => tunnel.UpAsync(config))
            .Should().ThrowAsync<PlatformNotSupportedException>();
        await FluentActions.Awaiting(() => tunnel.DownAsync())
            .Should().ThrowAsync<PlatformNotSupportedException>();
        await FluentActions.Awaiting(() => tunnel.GetStatisticsAsync())
            .Should().ThrowAsync<PlatformNotSupportedException>();
    }

    [Fact]
    public void It_does_not_claim_to_be_a_simulation()
    {
        // If this reported true, a user interface could reasonably show a simulated
        // connection over a backend that never connects at all.
        var tunnel = new WindowsServiceTunnel();

        tunnel.IsSimulated.Should().BeFalse();
        tunnel.State.Should().Be(TunnelState.Down);
    }

    [Fact]
    public async Task Disposing_it_does_not_throw()
    {
        var tunnel = new WindowsServiceTunnel();

        Func<Task> act = async () => await tunnel.DisposeAsync();

        await act.Should().NotThrowAsync();
    }
}
