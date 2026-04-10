using System.Globalization;
using System.Windows.Data;

namespace WatchDog.App.Converters;

/// <summary>
/// Converts between an enum value and a boolean for RadioButton binding.
/// ConverterParameter is the enum member name (e.g., "Highlight").
/// Returns true when the bound value matches the parameter.
/// </summary>
public sealed class EnumBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string paramStr)
            return false;

        return value.ToString() == paramStr;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is true && parameter is string paramStr)
        {
            return Enum.Parse(targetType, paramStr);
        }

        return Binding.DoNothing;
    }
}
