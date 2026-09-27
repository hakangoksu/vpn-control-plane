using FluentAssertions;
using VpnControl.Core.Connection;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Tests.Fakes;

namespace VpnControl.Desktop.Tests;

/// <summary>
/// Covers the commands: when each is available, and what the primary button does in each
/// state.
/// </summary>
public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task With_nothing_loaded_there_is_nothing_to_connect_to()
    {
        await using TestHarness harness = TestHarness.Create();

        harness.Main.ConnectCommand.CanExecute(null).Should().BeFalse();
        harness.Main.DisconnectCommand.CanExecute(null).Should().BeFalse();

        // Connecting to the fastest gateway needs no selection, because it refreshes first.
        harness.Main.ConnectToFastestCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task A_selection_enables_the_connect_command()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();

        harness.Select("lt-kun-01");

        harness.Main.ConnectCommand.CanExecute(null).Should().BeTrue();
        harness.Main.PrimaryActionText.Should().Be("Connect");
    }

    [Fact]
    public async Task Connecting_to_the_selected_gateway_brings_the_tunnel_up()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-kun-01");

        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.Manager.IsConnected.Should().BeTrue();
        harness.Manager.CurrentServer!.Id.Should().Be("lt-kun-01");
        harness.Main.Error.Should().BeNull();
    }

    [Fact]
    public async Task The_row_for_the_gateway_in_use_is_marked_after_connecting()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-kun-01");

        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.ServerList.Servers.Where(row => row.IsCurrent).Select(row => row.Id)
            .Should().Equal("lt-kun-01");
    }

    [Fact]
    public async Task With_the_current_gateway_selected_the_primary_button_offers_nothing_to_do()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-kun-01");
        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.Main.IsSelectionTheCurrentServer.Should().BeTrue();
        harness.Main.PrimaryActionText.Should().Be("Connected");
        harness.Main.ConnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task With_a_different_gateway_selected_the_primary_button_becomes_a_switch()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-kun-01");
        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.Select("de-fra-01");

        harness.Main.PrimaryActionText.Should().Be("Switch to selected");
        harness.Main.ConnectCommand.CanExecute(null).Should().BeTrue();

        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.Manager.CurrentServer!.Id.Should().Be("de-fra-01");
        harness.Manager.State.Should().Be(ConnectionState.Connected);
    }

    [Fact]
    public async Task Connect_to_fastest_picks_the_best_ranked_gateway_and_selects_its_row()
    {
        await using TestHarness harness = TestHarness.Create(latencies: new Dictionary<string, TimeSpan?>
        {
            ["lt-vln-01"] = TimeSpan.FromMilliseconds(70),
            ["lt-kun-01"] = TimeSpan.FromMilliseconds(9),
            ["de-fra-01"] = TimeSpan.FromMilliseconds(50),
        });
        await harness.LoadAsync();

        await harness.Main.ConnectToFastestCommand.ExecuteAsync(null);

        harness.Manager.CurrentServer!.Id.Should().Be("lt-kun-01");
        harness.ServerList.SelectedServer!.Id.Should().Be("lt-kun-01");
    }

    [Fact]
    public async Task Disconnect_is_only_available_once_there_is_something_to_disconnect()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Main.DisconnectCommand.CanExecute(null).Should().BeFalse();

        harness.Select("lt-vln-01");
        await harness.Main.ConnectCommand.ExecuteAsync(null);
        harness.Main.DisconnectCommand.CanExecute(null).Should().BeTrue();

        await harness.Main.DisconnectCommand.ExecuteAsync(null);

        harness.Manager.State.Should().Be(ConnectionState.Disconnected);
        harness.Main.DisconnectCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task A_failure_becomes_a_message_rather_than_an_unhandled_exception()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-vln-01");
        harness.Tunnel.FailOnUp = new TunnelException("wg-quick exited with 1.");

        // The command swallows nothing: the manager logged it and moved to Faulted, and the
        // window is given something to show instead of the process being brought down.
        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.Main.Error.Should().Contain("wg-quick exited with 1.");
        harness.Manager.State.Should().Be(ConnectionState.Faulted);
    }

    [Fact]
    public async Task A_faulted_session_can_still_be_disconnected_and_retried()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-vln-01");
        harness.Tunnel.FailOnUp = new TunnelException("transient");
        await harness.Main.ConnectCommand.ExecuteAsync(null);
        harness.Manager.State.Should().Be(ConnectionState.Faulted);

        await harness.Main.ConnectCommand.ExecuteAsync(null);

        harness.Manager.State.Should().Be(ConnectionState.Connected);
        harness.Main.Error.Should().BeNull();
    }

    [Fact]
    public async Task The_kill_switch_toggles_and_the_panel_follows()
    {
        await using TestHarness harness = TestHarness.Create();
        harness.Connection.KillSwitchEnabled.Should().BeFalse();

        await harness.Main.ToggleKillSwitchCommand.ExecuteAsync(null);

        harness.Manager.KillSwitchEnabled.Should().BeTrue();
        harness.Connection.KillSwitchEnabled.Should().BeTrue();

        await harness.Main.ToggleKillSwitchCommand.ExecuteAsync(null);

        harness.Manager.KillSwitchEnabled.Should().BeFalse();
        harness.Connection.KillSwitchEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task Turning_the_kill_switch_on_while_connected_rebuilds_the_tunnel_as_a_full_tunnel()
    {
        await using TestHarness harness = TestHarness.Create();
        await harness.LoadAsync();
        harness.Select("lt-vln-01");
        await harness.Main.ConnectCommand.ExecuteAsync(null);

        await harness.Main.ToggleKillSwitchCommand.ExecuteAsync(null);

        // Still connected to the same gateway, now with the setting the user asked for.
        harness.Manager.State.Should().Be(ConnectionState.Connected);
        harness.Manager.CurrentServer!.Id.Should().Be("lt-vln-01");
        harness.Manager.KillSwitchEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task The_refresh_command_reloads_the_list()
    {
        await using TestHarness harness = TestHarness.Create();

        await harness.Main.RefreshCommand.ExecuteAsync(null);

        harness.ServerList.Servers.Should().HaveCount(TestHarness.Servers.Count);
    }
}
