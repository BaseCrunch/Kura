using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Kura.Core.Models;

namespace Kura.App.Converters;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        (value is Visibility.Visible) ^ Invert;
}

public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var present = value is not null && value is not string { Length: 0 };
        return present ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class TagCategoryToBrush : IValueConverter
{
    public static Brush For(TagCategory c)
    {
        var key = c switch
        {
            TagCategory.Artist => "TagArtistBrush",
            TagCategory.Copyright => "TagCopyrightBrush",
            TagCategory.Character => "TagCharacterBrush",
            TagCategory.Species => "TagSpeciesBrush",
            TagCategory.Meta => "TagMetaBrush",
            TagCategory.Lore => "TagLoreBrush",
            TagCategory.Invalid => "TagInvalidBrush",
            _ => "TagGeneralBrush",
        };
        return (Brush)Application.Current.Resources[key];
    }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is TagCategory c ? For(c) : For(TagCategory.General);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Formats large counts compactly: 1234 → 1.2k.</summary>
public sealed class CompactCount : IValueConverter
{
    public static string Format(long n) => n switch
    {
        >= 1_000_000 => (n / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M",
        >= 1_000 => (n / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "k",
        _ => n.ToString(CultureInfo.InvariantCulture),
    };
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is long l ? Format(l) : value is int i ? Format(i) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
