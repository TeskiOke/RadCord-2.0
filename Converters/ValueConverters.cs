using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace RadCord.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is true ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is Visibility.Visible;
}

public class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is true ? Visibility.Collapsed : Visibility.Visible;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is not Visibility.Visible;
}

public class BoolToInverseBoolConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is bool b && !b;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => v is bool b && !b;
}

public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v != null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class InverseNullToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v == null ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class StringToInitialConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is string s && s.Length > 0 ? s[0].ToString().ToUpper() : "?";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class HexColorToBrushConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        try
        {
            if (v is string hex)
                return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        }
        catch { }
        return new SolidColorBrush(Color.FromRgb(114, 137, 218));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class BoolToMuteIconConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => "🎤";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class BoolToDeafenIconConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c) => "�";
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class BoolToMuteColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is true
            ? new SolidColorBrush(Color.FromRgb(240, 71, 71))
            : new SolidColorBrush(Color.FromRgb(185, 187, 190));
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class BoolToDeafenColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => v is true
            ? new SolidColorBrush(Color.FromRgb(240, 71, 71))
            : new SolidColorBrush(Color.FromRgb(185, 187, 190));
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class EqualityToVisibilityConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
        => Equals(v, p) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}

public class PingToColorConverter : IValueConverter
{
    public object Convert(object v, Type t, object p, CultureInfo c)
    {
        if (v is int ms)
        {
            if (ms < 80)  return new SolidColorBrush(Color.FromRgb(67,  181, 129));
            if (ms < 150) return new SolidColorBrush(Color.FromRgb(250, 166,  26));
            return new SolidColorBrush(Color.FromRgb(240, 71,  71));
        }
        return new SolidColorBrush(Color.FromRgb(142, 146, 151));
    }
    public object ConvertBack(object v, Type t, object p, CultureInfo c)
        => DependencyProperty.UnsetValue;
}
