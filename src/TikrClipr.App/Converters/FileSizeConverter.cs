using System.Globalization;
using System.Windows.Data;

namespace TikrClipr.App.Converters;

public sealed class FileSizeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is long bytes)
        {
            return bytes switch
            {
                >= 1024 * 1024 * 1024 => $"{bytes / (1024.0 * 1024.0 * 1024.0):F1} GB",
                >= 1024 * 1024 => $"{bytes / (1024.0 * 1024.0):F1} MB",
                >= 1024 => $"{bytes / 1024.0:F0} KB",
                _ => $"{bytes} B"
            };
        }
        return "0 B";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
