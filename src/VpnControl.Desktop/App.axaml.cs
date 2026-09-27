using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using VpnControl.Desktop.ViewModels;
using VpnControl.Desktop.Views;

namespace VpnControl.Desktop;

/// <summary>
/// The Avalonia application object: loads the styles and opens the window.
/// </summary>
/// <remarks>
/// It resolves exactly one thing, the window's view model, and the container builds
/// everything below that. Keeping the resolution to a single call is what stops the
/// container from being used as a service locator from inside the view models.
/// </remarks>
public sealed partial class App : Application
{
    /// <summary>
    /// Services built by the host, assigned before the framework is initialised.
    /// </summary>
    /// <remarks>
    /// A settable property because Avalonia constructs this type itself and offers no way
    /// to pass a constructor argument. It stays null under the XAML previewer, which
    /// creates the application without a host, and the initialisation below handles that.
    /// </remarks>
    public IServiceProvider? Services { get; set; }

    /// <inheritdoc />
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <inheritdoc />
    public override void OnFrameworkInitializationCompleted()
    {
        if (Services is not null && ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var viewModel = Services.GetRequiredService<MainWindowViewModel>();
            desktop.MainWindow = new MainWindow { DataContext = viewModel };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
