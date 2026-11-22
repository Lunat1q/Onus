using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Onus.Core;

namespace Onus.Views;

/// <summary>File path or URL → decoded bitmap at the width given as parameter (keeps memory low in long lists,
/// and OnLoad caching means local art files are never locked).</summary>
public sealed class ImageConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string { Length: > 0 } s) return DependencyProperty.UnsetValue;
        try
        {
            var isUrl = s.StartsWith("http", StringComparison.OrdinalIgnoreCase);
            if (!isUrl && !File.Exists(s)) return DependencyProperty.UnsetValue;
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri(s);
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (int.TryParse(parameter as string, out var w)) img.DecodePixelWidth = w;
            img.EndInit();
            if (img.CanFreeze && !img.IsDownloading) img.Freeze();
            return img;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UriFormatException or FileFormatException)
        {
            return DependencyProperty.UnsetValue;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>bool / non-null / non-empty → Visible. Parameter "not" inverts.</summary>
public sealed class VisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var on = value switch
        {
            bool b => b,
            string s => s.Length > 0,
            int n => n > 0,
            null => false,
            _ => true,
        };
        if (parameter as string == "not") on = !on;
        return on ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HasValueConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>value == parameter → true. ConvertBack lets RadioButton chips bind to a string/enum (checked → parameter).</summary>
public sealed class MatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString() ?? "", parameter as string ?? "", StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true) return Binding.DoNothing;
        var p = parameter as string ?? "";
        if (targetType.IsEnum) return Enum.Parse(targetType, p, ignoreCase: true);
        return p.Length == 0 ? null! : p;
    }
}

public sealed class SecondsConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        TitleText.Duration(value is double d ? d : 0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>keep / star / trash → glyph (parameter "glyph") or brush (default). Used for row and player badges.</summary>
public sealed class TagConverter : IValueConverter
{
    private static readonly Brush Keep = Frozen(0x22, 0xC5, 0x5E), Star = Frozen(0xEA, 0xB3, 0x08), Trash = Frozen(0xF4, 0x3F, 0x5E);

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var tag = value as string;
        if (parameter as string == "glyph")
            return tag switch { "keep" => "", "star" => "", "trash" => "", _ => "" };
        return tag switch { "keep" => Keep, "star" => Star, "trash" => Trash, _ => Brushes.Transparent };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
