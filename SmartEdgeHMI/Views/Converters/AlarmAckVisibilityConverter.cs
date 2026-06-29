using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Views.Converters;

public class AlarmAckVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is AlarmState state && state is AlarmState.UNACK or AlarmState.RTN_UNACK)
            return Visibility.Visible;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Binding.DoNothing;
}
