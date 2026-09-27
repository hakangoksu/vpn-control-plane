using Avalonia.Controls;

namespace VpnControl.Desktop.Views;

/// <summary>
/// The gateway list.
/// </summary>
/// <remarks>
/// No code beyond loading the XAML. Every behaviour the list has, the selection, the
/// refresh, the dimming of an excluded row, is a binding to
/// <see cref="ViewModels.ServerListViewModel"/>, which is what keeps the logic in a place
/// a test can reach.
/// </remarks>
public partial class ServerListView : UserControl
{
    /// <summary>Creates the view.</summary>
    public ServerListView() => InitializeComponent();
}
