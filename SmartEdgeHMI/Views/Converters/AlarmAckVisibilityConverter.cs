using System.Globalization;
using System.Windows;
using System.Windows.Data;
using SmartEdgeHMI.Common;

namespace SmartEdgeHMI.Views.Converters;

/// <summary>按报警状态控制确认按钮显隐</summary>
/// <remarks>
/// 只有 UNACK 与 RTN_UNACK 需要操作员确认; ACK、NORMAL 已确认过。
/// ConvertBack 不参与回写, 固定返回 <see cref="Binding.DoNothing"/>。
/// </remarks>
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
