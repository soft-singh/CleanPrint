namespace CleanPrint.Core.Models;

/// <summary>
/// The one rule about an annotation's colours, kept in one place: it must have SOMETHING to see — an outline, a fill, or both. An annotation with neither is invisible and can't be
/// found again by eye, and the toolbar's Color/Fill boxes have nothing sensible to show for it.
///
/// The toolbar enforces it by only OFFERING "No Stroke" when the selected annotation has a fill, and "No Fill" when it has a stroke. But those offers are decided by the FIRST
/// selected annotation, while a choice is applied to ALL of them — so selecting a text and a rectangle together and choosing "No Fill" could strip the only visible part of the other
/// one. These helpers are what every place that changes or creates a style checks, so the rule holds whatever is selected.
/// </summary>
public static class StyleRules
{
    /// <summary>Something can be seen: an outline or a fill.</summary>
    public static bool IsVisible(bool hasStroke, bool hasFill) => hasStroke || hasFill;

    /// <summary>Whether the outline may be switched OFF for an annotation: only if a fill remains to show it.</summary>
    public static bool AllowStrokeOff(bool hasFill) => hasFill;

    /// <summary>Whether the fill may be switched OFF for an annotation: only if an outline remains to show it.</summary>
    public static bool AllowFillOff(bool hasStroke) => hasStroke;
}
