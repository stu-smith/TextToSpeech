using System.Globalization;
using System.Windows;
using System.Windows.Data;
using TtsStudio.Core;
using TtsStudio.Core.Model;

namespace TtsStudio.App.Controls;

/// <summary>true → Visible; with Invert, true → Collapsed.</summary>
public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Negates a bool (both ways).</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value is not true;

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is not true;
}

/// <summary>Set (non-null, non-empty, non-zero) → Visible; with Invert, unset → Visible.</summary>
public sealed class NullToVisibility : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        bool has = value is not (null or string { Length: 0 } or 0 or System.Collections.ICollection { Count: 0 });
        return has ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>null ↔ "" so ComboBoxes can offer a "not set" item.</summary>
public sealed class NullToEmpty : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) => value ?? "";

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) => value is string { Length: > 0 } s ? s : null;
}

/// <summary>double? ↔ invariant string ("" for null), for ComboBoxes with numeric tags.</summary>
public sealed class NullableDoubleToString : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is double d ? d.ToString(CultureInfo.InvariantCulture) : "";

    public object? ConvertBack(object? value, Type t, object? p, CultureInfo c) =>
        value is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;
}

/// <summary>Region → brush of its kind's colour. ConverterParameter is an optional alpha (0-255).</summary>
public sealed class RegionBrush : IValueConverter
{
    public object? Convert(object? value, Type t, object? p, CultureInfo c)
    {
        byte alpha = p is string s && byte.TryParse(s, out byte a) ? a : (byte)255;
        return value switch
        {
            Region r => RegionColors.Brush(r, alpha),
            RegionKind k => RegionColors.Brush(k, alpha),
            _ => null,
        };
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>[start, end, text] → the covered text, quoted and shortened.</summary>
public sealed class RangeText : IMultiValueConverter
{
    public object Convert(object[] values, Type t, object? p, CultureInfo c)
    {
        if (values.Length < 3 || values[0] is not int start || values[1] is not int end || values[2] is not string text) return "";
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        string s = text[start..end].Replace('\n', ' ');
        if (s.Length > 60) s = s[..57] + "…";
        return $"“{s}”";
    }

    public object[] ConvertBack(object? value, Type[] t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>First line of a text, for list previews.</summary>
public sealed class FirstLine : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c)
    {
        string s = (value as string ?? "").Trim();
        int nl = s.IndexOf('\n');
        return nl < 0 ? s : s[..nl].TrimEnd() + " …";
    }

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Seconds → "1.25 s".</summary>
public sealed class Seconds : IValueConverter
{
    public object Convert(object? value, Type t, object? p, CultureInfo c) =>
        value is double d && d >= 0 ? d.ToString("0.00", CultureInfo.InvariantCulture) + " s" : "";

    public object ConvertBack(object? value, Type t, object? p, CultureInfo c) => throw new NotSupportedException();
}
