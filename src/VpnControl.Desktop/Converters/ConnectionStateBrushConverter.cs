using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using VpnControl.Core.Connection;

namespace VpnControl.Desktop.Converters;

/// <summary>
/// Maps a session state onto the colour used for the status dot and the primary button.
/// </summary>
/// <remarks>
/// A converter rather than a brush property on the view model, because a brush is a UI
/// type and the view models in this project hold none: that is what lets them be tested
/// without an Avalonia runtime. The mapping is presentation, so it lives on this side of
/// the binding.
/// <para>
/// The in-between states get their own colour rather than borrowing the connected one.
/// Showing green while a handshake is still in flight tells the user their traffic is
/// protected before it is.
/// </para>
/// </remarks>
public sealed class ConnectionStateBrushConverter : IValueConverter
{
    /// <summary>A single shared instance, so the resource does not allocate per binding.</summary>
    public static ConnectionStateBrushConverter Instance { get; } = new();

    private static readonly SolidColorBrush Idle = new(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly SolidColorBrush Working = new(Color.FromRgb(0xD9, 0x7C, 0x0B));
    private static readonly SolidColorBrush Live = new(Color.FromRgb(0x2E, 0x9E, 0x4F));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xC0, 0x39, 0x2B));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionState.Connected => Live,
        ConnectionState.Connecting or ConnectionState.Switching or ConnectionState.Disconnecting => Working,
        ConnectionState.Faulted => Bad,
        _ => Idle,
    };

    /// <inheritdoc />
    /// <remarks>
    /// One way only. A colour does not identify a state, and nothing in this application
    /// writes back through this binding.
    /// </remarks>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The state to colour mapping is one way.");
}
