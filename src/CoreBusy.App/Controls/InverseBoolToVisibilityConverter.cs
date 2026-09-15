namespace CoreBusy.App.Controls;

using System.Windows;

/// <summary>可见性取反转换器。</summary>
public sealed class InverseBoolToVisibilityConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, System.Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();
}
