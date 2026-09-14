using System.Globalization;
using System.Windows.Data;

namespace SteamVault.Converters;

public class EqualityConverter : IValueConverter, IMultiValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null && parameter == null) return true;
        if (value == null || parameter == null) return false;
        return value.ToString()?.Equals(parameter.ToString(), StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        return value?.Equals(true) == true ? parameter : System.Windows.Data.Binding.DoNothing;
    }

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return false;
        if (values[0] == null && values[1] == null) return true;
        if (values[0] == null || values[1] == null) return false;
        return values[0].ToString()?.Equals(values[1].ToString(), StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
