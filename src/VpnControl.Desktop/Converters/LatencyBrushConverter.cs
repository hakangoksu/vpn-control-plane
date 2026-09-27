using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace VpnControl.Desktop.Converters;

/// <summary>
/// Colours a latency figure by band, so the list can be read without comparing numbers.
/// </summary>
/// <remarks>
/// The thresholds are presentation choices, not measurements: under 40 ms reads as near,
/// over 120 ms as far. They are named constants so the reason they exist is visible and
/// changing them does not mean reading the conversion logic.
/// </remarks>
public sealed class LatencyBrushConverter : IValueConverter
{
    /// <summary>Latency in milliseconds below which a gateway is shown as near.</summary>
    public const double GoodMilliseconds = 40;

    /// <summary>Latency in milliseconds above which a gateway is shown as far.</summary>
    public const double PoorMilliseconds = 120;

    /// <summary>A single shared instance, so the resource does not allocate per binding.</summary>
    public static LatencyBrushConverter Instance { get; } = new();

    private static readonly SolidColorBrush Near = new(Color.FromRgb(0x2E, 0x9E, 0x4F));
    private static readonly SolidColorBrush Middling = new(Color.FromRgb(0xB4, 0x8A, 0x00));
    private static readonly SolidColorBrush Far = new(Color.FromRgb(0xC0, 0x39, 0x2B));
    private static readonly SolidColorBrush Unknown = new(Color.FromRgb(0x6B, 0x72, 0x80));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        double ms when ms <= GoodMilliseconds => Near,
        double ms when ms <= PoorMilliseconds => Middling,
        double => Far,

        // Null is the unreachable case, which the row already explains in its tooltip.
        _ => Unknown,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The latency to colour mapping is one way.");
}
