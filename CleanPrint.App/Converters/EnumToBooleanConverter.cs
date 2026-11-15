using System;
using System.Globalization;
using System.Windows.Data;

namespace CleanPrint.App.Converters;

/// <summary>Standard pattern for binding several mutually-exclusive RadioButtons to a single enum
/// property: each RadioButton's IsChecked binds to the SAME source property with a different
/// ConverterParameter (the enum member's name) — true when the property's current value matches
/// that parameter. ConvertBack does the reverse when a RadioButton becomes checked, parsing the
/// parameter back into the enum type WPF reports as targetType for a two-way binding.</summary>
public sealed class EnumToBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value?.ToString() == parameter?.ToString();

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is bool isChecked && isChecked && parameter is not null
            ? Enum.Parse(targetType, parameter.ToString()!)
            : Binding.DoNothing;
}
