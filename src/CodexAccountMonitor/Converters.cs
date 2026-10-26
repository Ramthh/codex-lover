using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using CodexAccountMonitor.Core.Models;

namespace CodexAccountMonitor;

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class AuthenticationBrushConverter : IValueConverter
{
    private static readonly Brush Success = Freeze("#75DCB1");
    private static readonly Brush Warning = Freeze("#E5BE55");
    private static readonly Brush Danger = Freeze("#EE7D7D");
    private static readonly Brush Muted = Freeze("#788690");

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        AuthenticationHealth.Healthy => Success,
        AuthenticationHealth.Expired => Danger,
        AuthenticationHealth.Error => Warning,
        _ => Muted
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static Brush Freeze(string value)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        brush.Freeze();
        return brush;
    }
}
