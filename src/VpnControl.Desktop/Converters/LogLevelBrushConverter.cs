using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Microsoft.Extensions.Logging;

namespace VpnControl.Desktop.Converters;

/// <summary>
/// Colours a log row by severity, so a warning is visible in a scrolling pane.
/// </summary>
public sealed class LogLevelBrushConverter : IValueConverter
{
    /// <summary>A single shared instance, so the resource does not allocate per binding.</summary>
    public static LogLevelBrushConverter Instance { get; } = new();

    private static readonly SolidColorBrush Ordinary = new(Color.FromRgb(0x9C, 0xA3, 0xAF));
    private static readonly SolidColorBrush Notable = new(Color.FromRgb(0xD9, 0x7C, 0x0B));
    private static readonly SolidColorBrush Serious = new(Color.FromRgb(0xE0, 0x5A, 0x4C));

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        LogLevel.Warning => Notable,
        LogLevel.Error or LogLevel.Critical => Serious,
        _ => Ordinary,
    };

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The level to colour mapping is one way.");
}
