using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SteamVault.Converters;

/// <summary>
/// Converts bool to Visibility. Supports optional inversion.
/// Normal: true→Visible, false→Collapsed
/// With ConverterParameter="invert" (or "Invert"): true→Collapsed, false→Visible
/// </summary>
public class InvertibleBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool boolValue = value is true;
        bool invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);

        if (invert)
            return boolValue ? Visibility.Collapsed : Visibility.Visible;

        return boolValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool invert = parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase);

        if (value is Visibility visibility)
        {
            bool result = visibility == Visibility.Visible;
            return invert ? !result : result;
        }

        return false;
    }
}

/// <summary>
/// Converts a string to Visibility. When the value equals the ConverterParameter, returns Visible.
/// Otherwise returns Collapsed. Used for showing elements conditionally based on status strings.
/// </summary>
public class StringEqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        string? actual = value as string;
        string? expected = parameter as string;

        if (string.IsNullOrEmpty(expected))
            return Visibility.Collapsed;

        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}