using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VpnControl.Desktop.Converters;

/// <summary>
/// Fills one bar of a three-bar quality indicator: lit when the signal level reaches it.
/// </summary>
/// <remarks>
/// The bars are neutral rather than green, amber and red. Quality is a ranking aid, not an
/// alarm, and colour is kept for states the user has to act on. The converter parameter is
/// the bar's position, 1 to 3.
/// </remarks>
public sealed class SignalBarBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush Lit = new(Color.FromRgb(0xC9, 0xD1, 0xD9));
    private static readonly SolidColorBrush Unlit = new(Color.FromRgb(0x30, 0x38, 0x42));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int bar = parameter is string text && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : 1;

        return value is int level && level >= bar ? Lit : Unlit;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The signal to brush mapping is one way.");
}
