using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VpnControl.Core.Connection;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Composition;
using VpnControl.Desktop.ViewModels;
using VpnControl.Tunnel;

namespace VpnControl.Desktop.Tests;

/// <summary>
/// Checks that the composition root actually composes.
/// </summary>
/// <remarks>
/// A dependency injection mistake is invisible at build time and shows up as an exception
/// on launch, which on a desktop application means a window that never appears. Resolving
/// the graph in a test moves that failure to the place it can be fixed. These tests need no
/// display and no Avalonia runtime, because nothing here opens a window.
/// </remarks>
public sealed class CompositionTests
{
    [Fact]
    public async Task The_whole_view_model_graph_resolves_from_the_default_configuration()
    {
        using IHost host = DesktopServices.CreateHost([]);

        var main = host.Services.GetRequiredService<MainWindowViewModel>();

        main.Connection.Should().NotBeNull();
        main.ServerList.Should().NotBeNull();
        main.Log.Should().NotBeNull();

        await DisposeAsync(host);
    }

    [Fact]
    public async Task The_defaults_are_the_self_contained_ones()
    {
        using IHost host = DesktopServices.CreateHost([]);

        // Nothing to install, no backend to start, no privileges: the point of the defaults.
        host.Services.GetRequiredService<IVpnTunnel>().Should().BeOfType<SimulatedTunnel>();
        host.Services.GetRequiredService<IServerCatalogClient>().Should().BeOfType<InMemoryServerCatalogClient>();
        host.Services.GetRequiredService<ILatencyProbe>().Should().BeOfType<DeterministicLatencyProbe>();

        await DisposeAsync(host);
    }

    [Fact]
    public async Task The_view_models_and_the_manager_are_single_instances()
    {
        using IHost host = DesktopServices.CreateHost([]);

        // A second copy would leave the window bound to a different session from the one
        // the commands act on, which is a bug that looks like the UI freezing.
        host.Services.GetRequiredService<MainWindowViewModel>()
            .Should().BeSameAs(host.Services.GetRequiredService<MainWindowViewModel>());
        host.Services.GetRequiredService<VpnConnectionManager>()
            .Should().BeSameAs(host.Services.GetRequiredService<VpnConnectionManager>());
        host.Services.GetRequiredService<MainWindowViewModel>().Connection
            .Should().BeSameAs(host.Services.GetRequiredService<ConnectionViewModel>());

        await DisposeAsync(host);
    }

    [Fact]
    public async Task The_application_log_is_routed_to_the_pane_the_window_shows()
    {
        using IHost host = DesktopServices.CreateHost([]);
        var main = host.Services.GetRequiredService<MainWindowViewModel>();

        await main.ServerList.RefreshAsync(CancellationToken.None);

        // The manager logs a line per refresh, and the pane is where it ends up.
        main.Log.Entries.Should().NotBeEmpty();
        main.Log.Entries.Should().Contain(entry => entry.Message.Contains("Refreshed catalog", StringComparison.Ordinal));

        await DisposeAsync(host);
    }

    [Fact]
    public async Task Overriding_a_switch_on_the_command_line_changes_what_is_registered()
    {
        using IHost host = DesktopServices.CreateHost(["--Desktop:UseControlPlaneApi=true"]);

        host.Services.GetRequiredService<IServerCatalogClient>().Should().BeOfType<HttpServerCatalogClient>();

        await DisposeAsync(host);
    }

    /// <summary>
    /// Disposes the host asynchronously.
    /// </summary>
    /// <remarks>
    /// The connection manager and the view models are <see cref="IAsyncDisposable"/> only,
    /// and the container refuses to dispose such a service from a synchronous
    /// <c>Dispose</c>. This is the same path the application takes on shutdown.
    /// </remarks>
    private static async Task DisposeAsync(IHost host)
    {
        if (host is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
    }
}
