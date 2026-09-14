using System.Globalization;
using System.Windows.Data;

namespace SteamVault.Converters;

public class MathConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is int intValue)
        {
            if (parameter is string paramStr)
            {
                bool result = false;
                if (paramStr == ">0") result = intValue > 0;
                else if (paramStr == "==0") result = intValue == 0;
                else if (paramStr == "!=0") result = intValue != 0;

                if (targetType == typeof(System.Windows.Visibility))
                    return result ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                return result;
            }

            // Simple scaling for bar charts: scale counts so max height is around 150
            // Since we don't know the max here, we'll just multiply by 15 and let the MaxHeight cap it.
            return Math.Min(150, Math.Max(4, intValue * 15));
        }
        
        if (value is double doubleVal)
        {
            if (parameter is string paramStr && paramStr == ">0")
            {
                bool result = doubleVal > 0;
                if (targetType == typeof(System.Windows.Visibility))
                    return result ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
                return result;
            }
        }

        if (targetType == typeof(System.Windows.Visibility))
            return System.Windows.Visibility.Collapsed;
        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
