using System;
using System.Globalization;
using System.Windows.Data;

namespace CleanPrint.App.Converters;

/// <summary>Combines FitToPage and ScalePercent into one scale factor for the Size-tab preview's
/// LayoutTransform: 1.0 (no extra scaling — Stretch="Uniform" already fills the sheet) when Fit to
/// Page is on, or ScalePercent/100 when a custom scale is selected — so dragging the scale control
/// visibly grows/shrinks the previewed page within the sheet instead of always filling it regardless
/// of the chosen percentage. A MultiBinding rather than a computed ViewModel property: both source
/// properties already raise PropertyChanged correctly on their own, so this re-evaluates whenever
/// either changes with no extra plumbing needed on the ViewModel side.</summary>
public sealed class FitOrScaleToFactorConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not bool fitToPage || values[1] is not double scalePercent)
            return 1.0;

        return fitToPage ? 1.0 : scalePercent / 100.0;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
