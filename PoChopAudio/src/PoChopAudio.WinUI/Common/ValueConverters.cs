using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using PoChopAudio.WinUI.Services;

namespace PoChopAudio.WinUI.Common;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var flag = value is bool b && b;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        var isVisible = value is Visibility v && v == Visibility.Visible;
        return Invert ? !isVisible : isVisible;
    }
}

public sealed class InvertedBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        return value is bool b ? !b : false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        return value is bool b ? !b : false;
    }
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var hasValue = value is not null;
        if (value is string s) hasValue = !string.IsNullOrWhiteSpace(s);
        if (Invert) hasValue = !hasValue;
        return hasValue ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

/// <summary>
/// Colours the dot beside a diagnostic. Present capabilities are green, absent ones amber rather
/// than red: every one of them degrades to something that still works, so none of them is an error.
/// </summary>
public sealed class DiagnosticStateToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        new SolidColorBrush(value switch
        {
            DiagnosticState.Good => ColorHelper.FromArgb(0xFF, 0x16, 0xA3, 0x4A),
            DiagnosticState.Missing => ColorHelper.FromArgb(0xFF, 0xF5, 0x9E, 0x0B),
            _ => ColorHelper.FromArgb(0x66, 0x94, 0xA3, 0xB8),
        });

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Builds a screen-reader label out of a fixed verb and the row's own value, so a column of
/// buttons all labelled "Play" announces as "Play sound 1", "Play sound 2" and so on. Without it
/// every row in a list is indistinguishable to anyone not looking at the screen.
/// </summary>
public sealed class LabelWithValueConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var prefix = parameter as string ?? string.Empty;
        return string.IsNullOrEmpty(prefix) ? $"{value}" : $"{prefix} {value}";
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language) =>
        throw new NotSupportedException();
}

/// <summary>
/// Two-way bridge between an enum property and <c>ComboBox.SelectedIndex</c>, for a combo whose
/// items are declared in source order.
/// <para>
/// This exists to kill a shim. The normalize combo used to bind to a plain CLR property on the
/// page that cast to and from <c>int</c>. A plain property raises no change notification, so
/// "Reset to defaults" — which replaces the whole knobs object — left the combo showing the old
/// mode while the view model exported with the new one. Binding the enum itself means the combo
/// is told when it changes.
/// </para>
/// </summary>
public sealed class EnumToIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) =>
        value is Enum e ? System.Convert.ToInt32(e, System.Globalization.CultureInfo.InvariantCulture) : 0;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
    {
        // targetType is the nullable-stripped enum the source property declares.
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (!enumType.IsEnum || value is not int index)
        {
            return value;
        }

        // A ComboBox reports -1 while it has no selection, which is not a member of any enum.
        var values = Enum.GetValues(enumType);
        return index >= 0 && index < values.Length ? values.GetValue(index)! : values.GetValue(0)!;
    }
}
