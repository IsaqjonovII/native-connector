using System;
using AibaShell.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AibaShell.Common;

/// <summary>
/// StatusKind is the single source of colour in the prototype. Two palettes so the
/// dots stay readable on both the light and dark Mica backdrop.
/// </summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var kind = value is StatusKind k ? k : StatusKind.Idle;
        var dark = App.IsDarkTheme;
        Color c = kind switch
        {
            StatusKind.Healthy => dark ? Color.FromArgb(255, 92, 202, 128) : Color.FromArgb(255, 16, 137, 62),
            StatusKind.Busy => dark ? Color.FromArgb(255, 96, 167, 245) : Color.FromArgb(255, 0, 95, 184),
            StatusKind.Warning => dark ? Color.FromArgb(255, 240, 178, 64) : Color.FromArgb(255, 157, 93, 0),
            StatusKind.Error => dark ? Color.FromArgb(255, 240, 110, 110) : Color.FromArgb(255, 176, 30, 40),
            _ => dark ? Color.FromArgb(255, 150, 150, 154) : Color.FromArgb(255, 110, 110, 116)
        };
        return new SolidColorBrush(c);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class LogLevelToBrushConverter : IValueConverter
{
    private static readonly StatusToBrushConverter Inner = new();

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var level = value is Services.LogLevel l ? l : Services.LogLevel.Info;
        var kind = level switch
        {
            Services.LogLevel.Error => StatusKind.Error,
            Services.LogLevel.Warning => StatusKind.Warning,
            Services.LogLevel.Debug => StatusKind.Idle,
            _ => StatusKind.Busy
        };
        return Inner.Convert(kind, targetType, parameter, language);
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is bool v && v;
        if (parameter is string s && s.Equals("invert", StringComparison.OrdinalIgnoreCase)) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        value is Visibility vis && vis == Visibility.Visible;
}

public sealed class DoubleToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var d = value is double v ? v : 0;
        return new GridLength(Math.Max(0, d));
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}
