using Avalonia;
using Microsoft.Extensions.Hosting;
using VpnControl.Desktop.Composition;

namespace VpnControl.Desktop;

/// <summary>Process entry point.</summary>
internal static class Program
{
    /// <summary>Builds the host, runs the Avalonia desktop lifetime, then tears both down.</summary>
    /// <param name="args">Command line arguments, forwarded to configuration and to Avalonia.</param>
    /// <returns>The process exit code.</returns>
    /// <remarks>
    /// The host is created before Avalonia starts so that a configuration mistake fails
    /// here, with a readable message on the console, rather than half way through opening
    /// a window.
    /// </remarks>
    [STAThread]
    public static int Main(string[] args)
    {
        IHost host = DesktopServices.CreateHost(args);

        try
        {
            return BuildAvaloniaApp()
                .AfterSetup(builder =>
                {
                    if (builder.Instance is App app)
                    {
                        app.Services = host.Services;
                    }
                })
                .StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            // Asynchronous disposal, because it brings the tunnel down and releases the
            // peer registration, and the connection manager offers no synchronous path
            // for that on purpose. Blocking is safe at this point: the window is gone and
            // this is the last thing the process does.
            //
            // The container disposes its singletons in reverse order of creation, so the
            // view models stop polling before the manager they poll is torn down.
            if (host is IAsyncDisposable asyncDisposable)
            {
                asyncDisposable.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            else
            {
                host.Dispose();
            }
        }
    }

    /// <summary>
    /// Configures Avalonia.
    /// </summary>
    /// <returns>The configured builder.</returns>
    /// <remarks>
    /// Public and parameterless by convention: the XAML previewer looks for a method with
    /// exactly this shape and calls it to render a view without running the application.
    /// </remarks>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
