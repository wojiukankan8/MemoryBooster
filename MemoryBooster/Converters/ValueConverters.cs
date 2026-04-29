using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace MemoryBooster.Converters;

public class PercentToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (!(value is double pct)) return new SolidColorBrush(Colors.Green);
        byte r, g;
        if (pct < 50) { r = (byte)(255 * pct / 50); g = 255; }
        else { r = 255; g = (byte)(255 * (100 - pct) / 50); }
        return new SolidColorBrush(Color.FromRgb(r, g, 80));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}
