using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using RemoteDeskHub.Shared;

namespace RemoteDeskHub.Desktop.Converters;

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && !b;
}

public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is not null;
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value is string s && !string.IsNullOrWhiteSpace(s);
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class CountToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            int i => i > 0,
            long l => l > 0,
            System.Collections.ICollection c => c.Count > 0,
            _ => false
        };
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            ComputerStatus.Online => "SuccessBrush",
            ComputerStatus.Warning => "WarningBrush",
            ComputerStatus.Critical => "DangerBrush",
            ComputerStatus.Offline => "TextMutedBrush",
            ComputerStatus.PendingApproval => "AccentCyanBrush",
            _ => "TextSecondaryBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class SeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var key = value switch
        {
            AlertSeverity.Critical => "DangerBrush",
            AlertSeverity.Warning => "WarningBrush",
            AlertSeverity.Info => "AccentCyanBrush",
            _ => "TextSecondaryBrush"
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BytesToTextConverter : IValueConverter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var bytes = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };
        if (bytes <= 0)
        {
            return "0 B";
        }

        var order = (int)Math.Floor(Math.Log(bytes, 1024));
        order = Math.Clamp(order, 0, Units.Length - 1);
        var size = bytes / Math.Pow(1024, order);
        return $"{size:0.#} {Units[order]}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PercentToTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var number = value switch
        {
            double d => d,
            float f => f,
            int i => i,
            _ => 0d
        };
        return $"{number:0.#}%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class UtcToLocalConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var format = parameter as string ?? "yyyy-MM-dd HH:mm:ss";
        return value switch
        {
            // SQLite round-trips DateTime values without preserving DateTimeKind. Every timestamp this
            // converter is used for is stored as UTC, so treat an unspecified kind as UTC rather than
            // displaying a raw UTC value as if it were local time.
            DateTime dt => dt.Kind switch
            {
                DateTimeKind.Utc => dt.ToLocalTime().ToString(format, culture),
                DateTimeKind.Unspecified => DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime().ToString(format, culture),
                _ => dt.ToString(format, culture)
            },
            DateTimeOffset dto => dto.ToLocalTime().ToString(format, culture),
            _ => string.Empty
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToYesNoConverter : IValueConverter
{
    public string TrueText { get; set; } = "Yes";
    public string FalseText { get; set; } = "No";

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? TrueText : FalseText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public string TrueKey { get; set; } = "SuccessBrush";
    public string FalseKey { get; set; } = "DangerBrush";

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => Application.Current.TryFindResource(value is true ? TrueKey : FalseKey) as Brush ?? Brushes.Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
