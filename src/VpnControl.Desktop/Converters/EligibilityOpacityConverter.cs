using System.Globalization;
using Avalonia.Data.Converters;

namespace VpnControl.Desktop.Converters;

/// <summary>
/// Dims a row that is not a candidate for connection.
/// </summary>
/// <remarks>
/// Dimmed rather than hidden, and that is the reason this converter exists rather than an
/// <c>IsVisible</c> binding: a gateway that vanished from the list would take its
/// exclusion reason with it, and the reason is the useful part.
/// </remarks>
public sealed class EligibilityOpacityConverter : IValueConverter
{
    /// <summary>Opacity applied to an excluded row.</summary>
    public const double DimmedOpacity = 0.45;

    /// <summary>A single shared instance, so the resource does not allocate per binding.</summary>
    public static EligibilityOpacityConverter Instance { get; } = new();

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 1.0 : DimmedOpacity;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("The eligibility to opacity mapping is one way.");
}
