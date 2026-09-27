using Avalonia.Controls;
using Avalonia.Interactivity;
using VpnControl.Desktop.ViewModels;

namespace VpnControl.Desktop.Views;

/// <summary>
/// The application window.
/// </summary>
/// <remarks>
/// The only code here starts the view model's first load once the window is on screen.
/// That is a lifecycle concern, which is the one thing a view is allowed to own: the view
/// model cannot know when it has been shown, and doing the first catalog fetch in its
/// constructor would block the window from appearing.
/// </remarks>
public partial class MainWindow : Window
{
    /// <summary>Creates the window.</summary>
    public MainWindow() => InitializeComponent();

    /// <inheritdoc />
    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);

        if (DataContext is MainWindowViewModel viewModel)
        {
            // Fire and observe, not fire and forget: the view model handles its own
            // failures and reports them through its Error property, so there is nothing
            // for this method to await and nothing it could do with an exception.
            _ = viewModel.InitializeAsync();
        }
    }
}
