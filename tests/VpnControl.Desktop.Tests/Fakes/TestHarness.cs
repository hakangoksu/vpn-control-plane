using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using VpnControl.Core.Connection;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Threading;
using VpnControl.Desktop.ViewModels;

namespace VpnControl.Desktop.Tests.Fakes;

/// <summary>
/// Builds a full set of view models over a real connection manager, a simulated tunnel and
/// an in-memory catalog.
/// </summary>
/// <remarks>
/// The manager is the real one rather than a double, because what these tests are checking
/// is that the view models reflect a session correctly, and a fake manager would let them
/// agree with a fiction. The pieces underneath it are the same ones the application uses
/// when it runs standalone, with the clock and the dispatcher replaced so nothing waits.
/// </remarks>
internal sealed class TestHarness : IAsyncDisposable
{
    private TestHarness(
        VpnConnectionManager manager,
        MainWindowViewModel main,
        InMemoryServerCatalogClient catalog,
        FakeTunnel tunnel)
    {
        Manager = manager;
        Main = main;
        Catalog = catalog;
        Tunnel = tunnel;
    }

    public VpnConnectionManager Manager { get; }

    public MainWindowViewModel Main { get; }

    public ConnectionViewModel Connection => Main.Connection;

    public ServerListViewModel ServerList => Main.ServerList;

    public LogViewModel Log => Main.Log;

    public InMemoryServerCatalogClient Catalog { get; }

    public FakeTunnel Tunnel { get; }

    /// <summary>Gateways the harness serves, fixed so assertions can name them.</summary>
    public static IReadOnlyList<VpnServer> Servers { get; } =
    [
        TestGateways.Create("lt-vln-01", city: "Vilnius", loadPercent: 10),
        TestGateways.Create("lt-kun-01", city: "Kaunas", loadPercent: 40),
        TestGateways.Create("de-fra-01", city: "Frankfurt", country: "DE", loadPercent: 20),
        TestGateways.Create("us-nyc-01", city: "New York", country: "US", loadPercent: 5, isEnabled: false),
    ];

    /// <summary>Creates the harness.</summary>
    /// <param name="killSwitchEnabled">Starting kill switch setting.</param>
    /// <param name="latencies">Latency per gateway identifier, null meaning unreachable.</param>
    public static TestHarness Create(
        bool killSwitchEnabled = false,
        IReadOnlyDictionary<string, TimeSpan?>? latencies = null)
    {
        var catalog = new InMemoryServerCatalogClient(Servers);
        var tunnel = new FakeTunnel();
        var probe = new DeterministicLatencyProbe(latencies);
        var dispatcher = new ImmediateUiDispatcher();

        var manager = new VpnConnectionManager(
            catalog,
            probe,
            new ServerSelector(),
            tunnel,
            Options.Create(new VpnConnectionOptions
            {
                DeviceName = "test-device",
                KillSwitchEnabled = killSwitchEnabled,
                TeardownTimeout = TimeSpan.FromSeconds(5),
            }),
            NullLogger<VpnConnectionManager>.Instance);

        var log = new LogViewModel(dispatcher);
        var connection = new ConnectionViewModel(manager, dispatcher, NullLogger<ConnectionViewModel>.Instance);
        var serverList = new ServerListViewModel(manager, dispatcher, NullLogger<ServerListViewModel>.Instance);
        var main = new MainWindowViewModel(
            manager,
            connection,
            serverList,
            log,
            dispatcher,
            NullLogger<MainWindowViewModel>.Instance);

        return new TestHarness(manager, main, catalog, tunnel);
    }

    /// <summary>Loads the gateway list without starting the polling loop.</summary>
    public async Task LoadAsync()
    {
        await ServerList.RefreshAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>Selects a gateway by identifier.</summary>
    public void Select(string serverId) =>
        ServerList.SelectedServer = ServerList.Servers.Single(row => row.Id == serverId);

    public async ValueTask DisposeAsync()
    {
        await Main.DisposeAsync().ConfigureAwait(false);
        await Manager.DisposeAsync().ConfigureAwait(false);
    }
}
