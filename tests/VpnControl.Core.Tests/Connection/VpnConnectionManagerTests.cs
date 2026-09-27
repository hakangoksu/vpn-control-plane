using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VpnControl.Core.Connection;
using VpnControl.Core.Crypto;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tests.Fakes;
using VpnControl.Core.Tunneling;
using Xunit;

namespace VpnControl.Core.Tests.Connection;

/// <summary>
/// Drives the real orchestration sequence against a fake control plane and a fake backend,
/// so the assertions are about the order of operations and what happens when one of them
/// fails, which is where a VPN client actually goes wrong.
/// </summary>
public sealed class VpnConnectionManagerTests
{
    private static readonly VpnServer Vilnius = TestServers.Create("lt-vln-01", loadPercent: 10);
    private static readonly VpnServer Kaunas = TestServers.Create("lt-kun-01", loadPercent: 20, city: "Kaunas");
    private static readonly VpnServer Frankfurt = TestServers.Create("de-fra-01", loadPercent: 80, city: "Frankfurt", country: "DE");

    private static (VpnConnectionManager Manager, SteerableCatalogClient Catalog, FakeVpnTunnel Tunnel) CreateManager(
        IEnumerable<VpnServer>? servers = null,
        IReadOnlyDictionary<string, TimeSpan?>? latencies = null,
        VpnConnectionOptions? options = null)
    {
        var catalog = new SteerableCatalogClient(servers ?? [Vilnius, Kaunas, Frankfurt]);
        var tunnel = new FakeVpnTunnel();

        var manager = new VpnConnectionManager(
            catalog,
            new DeterministicLatencyProbe(latencies),
            new ServerSelector(),
            tunnel,
            Options.Create(options ?? new VpnConnectionOptions { DeviceName = "test-device" }),
            NullLogger<VpnConnectionManager>.Instance);

        return (manager, catalog, tunnel);
    }

    [Fact]
    public async Task A_fresh_manager_is_disconnected_and_knows_nothing()
    {
        (VpnConnectionManager manager, _, _) = CreateManager();
        await using (manager)
        {
            manager.State.Should().Be(ConnectionState.Disconnected);
            manager.CurrentServer.Should().BeNull();
            manager.ConnectedSince.Should().BeNull();
            manager.Catalog.Count.Should().Be(0);
            manager.LastSelection.Should().BeNull();
        }
    }

    [Fact]
    public async Task Connecting_registers_a_peer_builds_a_config_and_brings_the_tunnel_up()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            var observed = new List<ConnectionState>();
            manager.StateChanged += (_, args) => observed.Add(args.Current);

            await manager.ConnectAsync(Vilnius);

            manager.State.Should().Be(ConnectionState.Connected);
            manager.CurrentServer.Should().Be(Vilnius);
            manager.ConnectedSince.Should().NotBeNull();
            observed.Should().Equal(ConnectionState.Connecting, ConnectionState.Connected);

            catalog.RegisterCalls.Should().Be(1);
            tunnel.UpCalls.Should().Be(1);

            WireGuardConfig applied = tunnel.AppliedConfigs.Single();
            applied.Endpoint.Should().Be(Vilnius.Endpoint);
            applied.PeerPublicKey.Should().Be(Vilnius.PublicKey);
            WireGuardKeyPair.IsValidKey(applied.PrivateKey).Should().BeTrue();
        }
    }

    [Fact]
    public async Task Every_connection_uses_a_freshly_generated_key()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            await manager.DisconnectAsync();
            await manager.ConnectAsync(Vilnius);

            tunnel.AppliedConfigs.Should().HaveCount(2);
            tunnel.AppliedConfigs[1].PrivateKey.Should().NotBe(tunnel.AppliedConfigs[0].PrivateKey);
        }
    }

    [Fact]
    public async Task Connecting_twice_is_rejected_rather_than_leaking_a_second_session()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);

            Func<Task> act = () => manager.ConnectAsync(Kaunas);

            await act.Should().ThrowAsync<InvalidStateTransitionException>();
            manager.State.Should().Be(ConnectionState.Connected);
            manager.CurrentServer.Should().Be(Vilnius);
            catalog.RegisterCalls.Should().Be(1);
            tunnel.UpCalls.Should().Be(1);
        }
    }

    [Fact]
    public async Task A_failed_registration_faults_the_session_and_brings_nothing_up()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            catalog.FailOnRegister = new ServerCatalogException("gateway is full") { StatusCode = 409 };

            Func<Task> act = () => manager.ConnectAsync(Vilnius);

            await act.Should().ThrowAsync<ServerCatalogException>();
            manager.State.Should().Be(ConnectionState.Faulted);
            tunnel.UpCalls.Should().Be(0);
            manager.CurrentServer.Should().BeNull();
        }
    }

    [Fact]
    public async Task A_tunnel_that_will_not_come_up_releases_the_registration_it_had_already_made()
    {
        // This is the leak worth testing for: the control plane has reserved an address for a
        // peer that does not exist, and every failed attempt would reserve another one.
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            tunnel.FailOnUp = new TunnelException("wg-quick up failed");

            Func<Task> act = () => manager.ConnectAsync(Vilnius);

            await act.Should().ThrowAsync<TunnelException>();
            manager.State.Should().Be(ConnectionState.Faulted);
            catalog.RegisterCalls.Should().Be(1);
            catalog.UnregisterCalls.Should().BeGreaterThanOrEqualTo(1);
            catalog.RegisteredPeerCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task A_faulted_session_can_be_retried_without_being_disconnected_first()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            tunnel.FailOnUp = new TunnelException("transient");
            await FluentActions.Awaiting(() => manager.ConnectAsync(Vilnius)).Should().ThrowAsync<TunnelException>();

            await manager.ConnectAsync(Vilnius);

            manager.State.Should().Be(ConnectionState.Connected);
        }
    }

    [Fact]
    public async Task Cancelling_a_connection_leaves_the_session_disconnected_rather_than_faulted()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            tunnel.UpDelay = TimeSpan.FromSeconds(30);
            using var cts = new CancellationTokenSource();
            Task connect = manager.ConnectAsync(Vilnius, cts.Token);
            await cts.CancelAsync();

            await FluentActions.Awaiting(() => connect).Should().ThrowAsync<OperationCanceledException>();

            manager.State.Should().Be(ConnectionState.Disconnected);
            catalog.RegisteredPeerCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Disconnecting_takes_the_tunnel_down_and_releases_the_registration()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            string registeredPeer = catalog.RegisteredPeerCount.ToString();

            await manager.DisconnectAsync();

            registeredPeer.Should().Be("1");
            manager.State.Should().Be(ConnectionState.Disconnected);
            manager.CurrentServer.Should().BeNull();
            tunnel.DownCalls.Should().BeGreaterThanOrEqualTo(1);
            catalog.RegisteredPeerCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Disconnecting_when_already_disconnected_does_nothing()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.DisconnectAsync();

            manager.State.Should().Be(ConnectionState.Disconnected);
            tunnel.DownCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task A_tunnel_that_refuses_to_come_down_faults_rather_than_reporting_a_clean_disconnect()
    {
        // An interface still carrying traffic after the user pressed disconnect is exactly
        // what must not be reported as success.
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            tunnel.FailOnDown = new TunnelException("interface is busy");

            Func<Task> act = () => manager.DisconnectAsync();

            await act.Should().ThrowAsync<TunnelException>();
            manager.State.Should().Be(ConnectionState.Faulted);
        }
    }

    [Fact]
    public async Task Switching_brings_the_old_tunnel_down_before_the_new_one_up()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            var observed = new List<ConnectionState>();
            manager.StateChanged += (_, args) => observed.Add(args.Current);

            await manager.SwitchToAsync(Kaunas);

            observed.Should().Equal(ConnectionState.Switching, ConnectionState.Connected);
            manager.CurrentServer.Should().Be(Kaunas);
            tunnel.DownCalls.Should().BeGreaterThanOrEqualTo(1);
            tunnel.UpCalls.Should().Be(2);
            tunnel.AppliedConfigs[1].Endpoint.Should().Be(Kaunas.Endpoint);

            // The old registration is released, and only the new one is left.
            catalog.RegisteredPeerCount.Should().Be(1);
        }
    }

    [Fact]
    public async Task Switching_without_a_session_is_rejected()
    {
        (VpnConnectionManager manager, _, _) = CreateManager();
        await using (manager)
        {
            Func<Task> act = () => manager.SwitchToAsync(Kaunas);

            await act.Should().ThrowAsync<InvalidStateTransitionException>();
        }
    }

    [Fact]
    public async Task A_switch_whose_new_tunnel_fails_leaves_the_session_faulted_and_empty()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            tunnel.FailOnUp = new TunnelException("no route to host");

            Func<Task> act = () => manager.SwitchToAsync(Frankfurt);

            await act.Should().ThrowAsync<TunnelException>();
            manager.State.Should().Be(ConnectionState.Faulted);
            manager.CurrentServer.Should().BeNull();
            catalog.RegisteredPeerCount.Should().Be(0);
        }
    }

    [Fact]
    public async Task Refreshing_measures_the_catalog_and_ranks_it()
    {
        (VpnConnectionManager manager, _, _) = CreateManager(latencies: new Dictionary<string, TimeSpan?>
        {
            [Vilnius.Id] = TimeSpan.FromMilliseconds(15),
            [Kaunas.Id] = TimeSpan.FromMilliseconds(25),
            [Frankfurt.Id] = TimeSpan.FromMilliseconds(35),
        });

        await using (manager)
        {
            ServerSelectionResult selection = await manager.RefreshAsync();

            selection.Ranked.Should().HaveCount(3);
            selection.Best.Should().Be(Vilnius);
            manager.Catalog.Count.Should().Be(3);
            manager.LastSelection.Should().BeSameAs(selection);
        }
    }

    [Fact]
    public async Task Connecting_to_the_fastest_picks_the_best_ranked_gateway()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager(latencies: new Dictionary<string, TimeSpan?>
        {
            [Vilnius.Id] = TimeSpan.FromMilliseconds(90),
            [Kaunas.Id] = TimeSpan.FromMilliseconds(12),
            [Frankfurt.Id] = TimeSpan.FromMilliseconds(40),
        });

        await using (manager)
        {
            VpnServer chosen = await manager.ConnectToFastestAsync();

            chosen.Should().Be(Kaunas);
            manager.State.Should().Be(ConnectionState.Connected);
            tunnel.AppliedConfigs.Single().Endpoint.Should().Be(Kaunas.Endpoint);
        }
    }

    [Fact]
    public async Task Connecting_to_the_fastest_reports_why_when_no_gateway_qualifies()
    {
        (VpnConnectionManager manager, _, _) = CreateManager(latencies: new Dictionary<string, TimeSpan?>
        {
            [Vilnius.Id] = null,
            [Kaunas.Id] = null,
            [Frankfurt.Id] = null,
        });

        await using (manager)
        {
            Func<Task> act = () => manager.ConnectToFastestAsync();

            NoServerAvailableException exception = (await act.Should().ThrowAsync<NoServerAvailableException>()).Which;
            exception.Excluded.Should().HaveCount(3);
            exception.Message.Should().Contain(Vilnius.Id);
            manager.State.Should().Be(ConnectionState.Disconnected);
        }
    }

    [Fact]
    public async Task A_catalog_failure_during_refresh_surfaces_and_leaves_the_state_alone()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, _) = CreateManager();
        await using (manager)
        {
            catalog.FailOnGetServers = new ServerCatalogException("control plane unreachable");

            Func<Task> act = () => manager.RefreshAsync();

            await act.Should().ThrowAsync<ServerCatalogException>();
            manager.State.Should().Be(ConnectionState.Disconnected);
        }
    }

    [Fact]
    public async Task Statistics_are_empty_unless_a_tunnel_is_up()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            (await manager.GetStatisticsAsync()).Should().Be(TunnelStatistics.Empty);

            await manager.ConnectAsync(Vilnius);

            (await manager.GetStatisticsAsync()).Should().Be(tunnel.Statistics);
        }
    }

    [Fact]
    public async Task The_kill_switch_forces_a_full_tunnel_on_the_next_connection()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager(
            options: new VpnConnectionOptions { KillSwitchEnabled = true });

        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);

            WireGuardConfig applied = tunnel.AppliedConfigs.Single();
            applied.KillSwitchRequested.Should().BeTrue();
            applied.AllowedIps.Should().Equal("0.0.0.0/0", "::/0");
        }
    }

    [Fact]
    public async Task Turning_the_kill_switch_on_while_connected_re_applies_the_tunnel()
    {
        // The setting lives in the configuration already handed to the backend, so it cannot
        // take effect without building a new one. Leaving the running tunnel disagreeing with
        // the switch the user just flipped would be the wrong kind of quiet.
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            tunnel.AppliedConfigs.Single().KillSwitchRequested.Should().BeFalse();

            await manager.SetKillSwitchAsync(true);

            manager.KillSwitchEnabled.Should().BeTrue();
            manager.State.Should().Be(ConnectionState.Connected);
            tunnel.AppliedConfigs.Should().HaveCount(2);
            tunnel.AppliedConfigs[1].KillSwitchRequested.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Setting_the_kill_switch_to_its_current_value_changes_nothing()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);

            await manager.SetKillSwitchAsync(false);

            tunnel.AppliedConfigs.Should().HaveCount(1);
        }
    }

    [Fact]
    public async Task Turning_the_kill_switch_on_while_disconnected_does_not_connect_anything()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            await manager.SetKillSwitchAsync(true);

            manager.KillSwitchEnabled.Should().BeTrue();
            manager.State.Should().Be(ConnectionState.Disconnected);
            tunnel.UpCalls.Should().Be(0);
        }
    }

    [Fact]
    public async Task A_failure_to_release_a_registration_does_not_stop_the_disconnect()
    {
        // The tunnel is already down at that point, so the user's traffic is safe. A stranded
        // reservation is the lesser problem and must not turn into a thrown disconnect.
        (VpnConnectionManager manager, SteerableCatalogClient catalog, _) = CreateManager();
        await using (manager)
        {
            await manager.ConnectAsync(Vilnius);
            catalog.FailOnUnregister = new ServerCatalogException("control plane unreachable");

            await manager.DisconnectAsync();

            manager.State.Should().Be(ConnectionState.Disconnected);
        }
    }

    [Fact]
    public async Task Disposing_tears_down_the_session_and_the_backend()
    {
        (VpnConnectionManager manager, SteerableCatalogClient catalog, FakeVpnTunnel tunnel) = CreateManager();
        await manager.ConnectAsync(Vilnius);

        await manager.DisposeAsync();

        tunnel.DownCalls.Should().BeGreaterThanOrEqualTo(1);
        tunnel.IsDisposed.Should().BeTrue();
        catalog.RegisteredPeerCount.Should().Be(0);
    }

    [Fact]
    public async Task Using_a_disposed_manager_throws()
    {
        (VpnConnectionManager manager, _, _) = CreateManager();
        await manager.DisposeAsync();

        await FluentActions.Awaiting(() => manager.ConnectAsync(Vilnius)).Should().ThrowAsync<ObjectDisposedException>();
        await FluentActions.Awaiting(() => manager.RefreshAsync()).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task The_manager_reports_which_backend_it_is_using()
    {
        (VpnConnectionManager manager, _, FakeVpnTunnel tunnel) = CreateManager();
        await using (manager)
        {
            manager.IsSimulated.Should().Be(tunnel.IsSimulated);
            manager.TunnelName.Should().Be(tunnel.Name);
        }
    }
}
