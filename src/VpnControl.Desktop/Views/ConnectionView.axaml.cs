using Avalonia.Controls;

namespace VpnControl.Desktop.Views;

/// <summary>
/// The session panel: state, gateway, elapsed time and counters.
/// </summary>
/// <remarks>
/// Display only. The buttons that change the session are in
/// <see cref="MainWindow"/>, where both the session and the gateway selection are in
/// scope.
/// </remarks>
public partial class ConnectionView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ConnectionView() => InitializeComponent();
}
