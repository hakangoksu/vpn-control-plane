using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using VpnControl.Core.Connection;
using VpnControl.Core.Latency;
using VpnControl.Core.Servers;
using VpnControl.Core.Tunneling;
using VpnControl.Desktop.Logging;
using VpnControl.Desktop.Threading;
using VpnControl.Desktop.ViewModels;
using VpnControl.Tunnel;

namespace VpnControl.Desktop.Composition;

/// <summary>
/// The composition root: the one place that decides which implementation satisfies each
/// interface.
/// </summary>
/// <remarks>
/// Nothing else in the application resolves a service, and there is no static state and no
/// service locator anywhere in the view models. That is what makes the choice between a
/// simulated tunnel and a real one, or between an in-memory catalog and the API, a
/// configuration change rather than an edit spread over several files.
/// </remarks>
public static class DesktopServices
{
    /// <summary>
    /// Where this user's client settings live: the device token and the control plane address.
    /// </summary>
    /// <remarks>
    /// In the user's own configuration directory (<c>~/.config</c> on Linux, <c>%APPDATA%</c>
    /// on Windows), not next to the executable or in the source tree. A token beside the
    /// build output is copied wherever the output is, including into the test project's, and
    /// a secret belongs in exactly one place that only its owner can read.
    /// </remarks>
    /// <returns>The path, whether or not the file exists.</returns>
    public static string DefaultClientSettingsPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "vpn-control-plane",
        "client.json");

    /// <summary>Builds the host the application runs inside, with this user's client settings.</summary>
    /// <param name="args">Command line arguments, so a setting can be overridden at launch.</param>
    /// <returns>An unstarted host whose service provider has everything the window needs.</returns>
    public static IHost CreateHost(string[] args) => CreateHost(args, DefaultClientSettingsPath());

    /// <summary>Builds the host the application runs inside.</summary>
    /// <param name="args">Command line arguments, so a setting can be overridden at launch.</param>
    /// <param name="clientSettingsPath">
    /// The user's client settings file, or <c>null</c> to use none. Tests pass <c>null</c>, so
    /// what they check never depends on the machine they run on.
    /// </param>
    /// <returns>An unstarted host whose service provider has everything the window needs.</returns>
    public static IHost CreateHost(string[] args, string? clientSettingsPath)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

        // Optional, so a machine without it starts in demo mode. Added last, so it overrides
        // the defaults shipped in appsettings.json.
        string? directory = clientSettingsPath is null ? null : Path.GetDirectoryName(clientSettingsPath);
        if (directory is not null && Directory.Exists(directory))
        {
            builder.Configuration.AddJsonFile(
                new Microsoft.Extensions.FileProviders.PhysicalFileProvider(directory),
                Path.GetFileName(clientSettingsPath!),
                optional: true,
                reloadOnChange: false);
        }

        builder.Services.AddOptions<DesktopOptions>()
            .Bind(builder.Configuration.GetSection(DesktopOptions.SectionName));
        builder.Services.AddOptions<VpnConnectionOptions>()
            .Bind(builder.Configuration.GetSection(VpnConnectionOptions.SectionName));
        builder.Services.AddOptions<ServerSelectionOptions>()
            .Bind(builder.Configuration.GetSection(ServerSelectionOptions.SectionName));
        builder.Services.AddOptions<ServerCatalogOptions>()
            .Bind(builder.Configuration.GetSection(ServerCatalogOptions.SectionName));
        builder.Services.AddOptions<WgQuickOptions>()
            .Bind(builder.Configuration.GetSection(WgQuickOptions.SectionName));

        // Bound once here rather than injected everywhere, because these decide which
        // registrations are made and so are needed before the container exists.
        DesktopOptions desktop = builder.Configuration
            .GetSection(DesktopOptions.SectionName)
            .Get<DesktopOptions>() ?? new DesktopOptions();

        // Registered as a service so constructors can take it by type. Several classes
        // accept it with a default of null, and the default container does not fill a
        // defaulted parameter, so leaving it out would silently give them the system clock.
        builder.Services.AddSingleton(TimeProvider.System);

        builder.Services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        builder.Services.AddSingleton<ServerSelector>();

        AddCatalogClient(builder, desktop);
        AddLatencyProbe(builder.Services, desktop);
        AddTunnel(builder.Services, desktop);

        builder.Services.AddSingleton<VpnConnectionManager>();

        // One instance each: the window binds to them, and a second copy would show a
        // different session from the one the commands act on.
        builder.Services.AddSingleton<LogViewModel>();
        builder.Services.AddSingleton<ILogSink>(sp => sp.GetRequiredService<LogViewModel>());
        builder.Services.AddSingleton<ConnectionViewModel>();
        builder.Services.AddSingleton<ServerListViewModel>();
        builder.Services.AddSingleton<MainWindowViewModel>();

        // The log pane shows the same lines the console does, rather than a separate
        // message stream the code would have to remember to write to twice.
        builder.Services.AddSingleton<ILoggerProvider>(sp =>
            new SinkLoggerProvider(sp.GetRequiredService<ILogSink>()));

        return builder.Build();
    }

    /// <summary>Chooses between the API client and the in-memory one.</summary>
    private static void AddCatalogClient(HostApplicationBuilder builder, DesktopOptions desktop)
    {
        if (!desktop.UseControlPlaneApi)
        {
            builder.Services.AddSingleton<IServerCatalogClient>(sp =>
                new InMemoryServerCatalogClient(timeProvider: sp.GetRequiredService<TimeProvider>()));
            return;
        }

        // A typed client, so the factory owns the handler lifetime. The base address is
        // applied by the client itself from its options, which keeps the address in
        // configuration rather than split between here and there.
        builder.Services.AddHttpClient<IServerCatalogClient, HttpServerCatalogClient>();
    }

    /// <summary>Chooses between measuring latency and deriving it.</summary>
    private static void AddLatencyProbe(IServiceCollection services, DesktopOptions desktop)
    {
        if (desktop.UseRealLatencyProbe)
        {
            services.AddSingleton<ILatencyProbe>(_ =>
                string.Equals(desktop.LatencyProbeKind, "Tcp", StringComparison.OrdinalIgnoreCase)
                    ? new TcpConnectLatencyProbe(desktop.LatencyProbePort)
                    : new IcmpLatencyProbe());
            return;
        }

        services.AddSingleton<ILatencyProbe>(_ => new DeterministicLatencyProbe());
    }

    /// <summary>Chooses the tunnel backend.</summary>
    private static void AddTunnel(IServiceCollection services, DesktopOptions desktop)
    {
        if (desktop.UseRealTunnel)
        {
            if (!OperatingSystem.IsLinux())
            {
                // Failing here is the honest outcome. The Windows backend is a documented
                // gap, so a client asked for a real tunnel on Windows must say it cannot
                // provide one rather than quietly simulating and reporting success.
                throw new PlatformNotSupportedException(
                    "A real tunnel is only implemented for Linux through wg-quick. See WindowsServiceTunnel for what a Windows backend would require.");
            }

            services.AddSingleton<IVpnTunnel, WgQuickTunnel>();
            return;
        }

        services.AddSingleton<IVpnTunnel>(sp => new SimulatedTunnel(
            sp.GetRequiredService<ILogger<SimulatedTunnel>>(),
            timeProvider: sp.GetRequiredService<TimeProvider>()));
    }
}
