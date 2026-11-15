using System;
using System.Globalization;
using System.Windows.Data;

namespace CleanPrint.App.Converters;

/// <summary>Inverts a bool both ways — used to bind the "off" state of a pair of mutually-exclusive
/// RadioButtons to the same underlying property the "on" one binds directly to.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => !(bool)value;
}
