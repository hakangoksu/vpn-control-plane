using Avalonia.Controls;

namespace VpnControl.Desktop.Views;

/// <summary>
/// The log pane, bound to the same lines the application writes to its console logger.
/// </summary>
public partial class LogView : UserControl
{
    /// <summary>Creates the view.</summary>
    public LogView() => InitializeComponent();
}
