using FluentAssertions;
using Microsoft.Extensions.Time.Testing;
using VpnControl.Api.Endpoints;
using Xunit;

namespace VpnControl.Api.Tests;

public sealed class GatewaySyncCoordinatorTests
{
    private static GatewaySyncCoordinator Create() =>
        new(new FakeTimeProvider(DateTimeOffset.FromUnixTimeMilliseconds(1_000_000)));

    [Fact]
    public async Task A_poll_from_an_agent_that_is_behind_returns_at_once()
    {
        GatewaySyncCoordinator coordinator = Create();

        Task wait = coordinator.WaitForChangeAsync("gw", knownVersion: 0, TimeSpan.FromMinutes(1), CancellationToken.None);

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_held_poll_is_released_by_a_change()
    {
        GatewaySyncCoordinator coordinator = Create();
        long current = coordinator.CurrentVersion("gw");

        Task wait = coordinator.WaitForChangeAsync("gw", current, TimeSpan.FromMinutes(1), CancellationToken.None);
        wait.IsCompleted.Should().BeFalse();

        coordinator.MarkChanged("gw");

        await wait.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_change_on_one_gateway_does_not_release_another()
    {
        GatewaySyncCoordinator coordinator = Create();
        Task wait = coordinator.WaitForChangeAsync("gw-a", coordinator.CurrentVersion("gw-a"), TimeSpan.FromMilliseconds(300), CancellationToken.None);

        coordinator.MarkChanged("gw-b");

        await Task.Delay(100);
        wait.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task A_registration_is_confirmed_once_the_agent_reports_the_version()
    {
        GatewaySyncCoordinator coordinator = Create();
        long version = coordinator.MarkChanged("gw");

        Task<bool> confirmed = coordinator.WaitUntilAppliedAsync("gw", version, TimeSpan.FromMinutes(1), CancellationToken.None);
        coordinator.RecordApplied("gw", version - 1);
        confirmed.IsCompleted.Should().BeFalse();

        coordinator.RecordApplied("gw", version);

        (await confirmed.WaitAsync(TimeSpan.FromSeconds(1))).Should().BeTrue();
    }

    [Fact]
    public async Task Without_an_acknowledgement_the_wait_ends_unconfirmed()
    {
        GatewaySyncCoordinator coordinator = Create();
        long version = coordinator.MarkChanged("gw");

        bool confirmed = await coordinator.WaitUntilAppliedAsync("gw", version, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        confirmed.Should().BeFalse();
    }

    [Fact]
    public async Task An_agent_cannot_confirm_a_version_that_was_never_handed_out()
    {
        // A version from before a restart, or a made-up one, must not confirm registrations
        // the gateway has not seen yet.
        GatewaySyncCoordinator coordinator = Create();
        coordinator.RecordApplied("gw", long.MaxValue);

        long version = coordinator.MarkChanged("gw");
        bool confirmed = await coordinator.WaitUntilAppliedAsync("gw", version, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        confirmed.Should().BeFalse();
    }
}
