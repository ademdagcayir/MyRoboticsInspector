using System.Globalization;

namespace MyRoboticsInspector.Converters;

public class InvertedBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b ? !b : value!;
}

public class BoolToRecordTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "■ Kaydı Durdur" : "● Kayda Başla";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToRecordColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Color.FromArgb("#a33") : Color.FromArgb("#444");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToLightTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "💡 Işık Kapat" : "💡 Işık Aç";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToConnectionTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "● Bağlı" : "○ Bağlı değil";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class StringNotEmptyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !string.IsNullOrWhiteSpace(value as string);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToBackupColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Color.FromArgb("#5fc97e") : Color.FromArgb("#ffcc66");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToPlayPauseTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "⏸ Duraklat" : "▶ Oynat";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToGamepadButtonTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? "Kapat" : "Aç";
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Picks the right card border style — accent (success) when inspecting, neutral otherwise.</summary>
public class BoolToCardStyleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = (value is bool b && b) ? "CardBorderAccent" : "CardBorder";
        var app = Application.Current;
        if (app?.Resources.TryGetValue(key, out var style) == true && style is Style s)
            return s;
        return BindableProperty.UnsetValue;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToConnectionColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Color.FromArgb("#30d158") : Color.FromArgb("#8e8e93");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class BoolToFlowColorConverter : IValueConverter
{
    // True = ters akış (yokuş yukarı) → uyarı rengi; False = normal → secondary
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is bool b && b ? Color.FromArgb("#ff9f0a") : Color.FromArgb("#aeaeb2");
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IntToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i > 0;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>0-100 int → 0.0-1.0 double (ProgressBar.Progress için).</summary>
public class ProgressNormConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var p = value switch { int i => i, double d => d, _ => 0.0 };
        return Math.Clamp(p / 100.0, 0.0, 1.0);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>DateTime ↔ TimeSpan — TimePicker'a DateTime'ın TimeOfDay'ini bağlamak için.</summary>
public class DateTimeToTimeSpanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is DateTime dt ? dt.TimeOfDay : TimeSpan.Zero;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is TimeSpan ts ? DateTime.Today.Add(ts) : DateTime.Today;
}

/// <summary>Long milliseconds -> "hh:mm:ss" display.</summary>
public class MsToTimestampConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var ms = value switch
        {
            long l => l,
            int i => i,
            double d => (long)d,
            _ => 0L
        };
        return TimeSpan.FromMilliseconds(ms).ToString(@"hh\:mm\:ss");
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
