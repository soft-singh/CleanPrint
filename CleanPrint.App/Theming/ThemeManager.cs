using System;
using System.Windows;
using System.Windows.Media;
using CleanPrint.Core.Theming;

namespace CleanPrint.App.Theming;

/// <summary>
/// Puts a theme into the application's resources. The palette itself (a dozen base colours and the corner radii) is in Core (ThemeCatalog); this works every brush the styles use out of
/// it — hover and pressed shades, borders, disabled states, the colours of buttons, text boxes, tabs, menus, lists, scroll bars — and writes them into Application.Resources, where
/// every style refers to them with DynamicResource, so every open window follows a change at once. The approach is the CMS application's.
///
/// Two families of keys are written. <c>Theme*</c> are what Themes/InputStyles.xaml uses. <c>App*</c> are the older names the rest of the XAML already refers to (window, panel and
/// preview backgrounds, text, icons, scroll bars, sliders, the title bar); they are filled in from the same palette so there is one source of truth. Themes/LightTheme.xaml still defines
/// the App* keys with the Classic values — a fallback for the instant before the first Apply, and for the designer.
/// </summary>
public static class ThemeManager
{
    private static double _iconLightness;

    public static string CurrentName { get; private set; } = ThemeCatalog.Classic;
    public static bool CurrentIsDark { get; private set; }

    private static Color ToColor(Rgb c) => Color.FromRgb(c.R, c.G, c.B);

    private static SolidColorBrush Brush(Rgb c) => Brush(ToColor(c));

    private static SolidColorBrush Brush(Color c)
    {
        var brush = new SolidColorBrush(c);
        brush.Freeze();
        return brush;
    }

    public static void Apply(string? themeName)
    {
        var info = ThemeCatalog.Find(themeName);
        var t = info.Create();
        CurrentName = info.Name;
        CurrentIsDark = t.IsDark;

        bool dark = t.IsDark, classic = t.Classic, modern = !classic;
        Rgb white = Rgb.White, black = Rgb.Black;
        Rgb P(string hex) => Rgb.Parse(hex, black);
        Rgb Mix(Rgb a, Rgb b, double amount) => ColourMath.Mix(a, b, amount);

        Rgb accent = P(t.Accent), window = P(t.Window), chrome = P(t.Chrome), page = P(t.Page), border = P(t.Border), header = P(t.Header);
        Rgb selection = P(t.Selection), tab = P(t.Tab), canvas = P(t.Canvas), input = P(t.Input), text = P(t.Text);
        Rgb accentText = ColourMath.LabelOn(accent);   // white on a coloured button (black only on a very pale accent)

        var r = Application.Current.Resources;
        void Set(string key, Rgb c) => r[key] = Brush(c);

        // ---- accent
        Set("ThemeAccentBrush", accent);
        Set("ThemeAccentHoverBrush", ColourMath.Shade(accent, 0.14));
        Set("ThemeAccentPressedBrush", ColourMath.Shade(accent, 0.28));
        Set("ThemeAccentTextBrush", accentText);
        Set("ThemeHoverBrush", Mix(page, accent, 0.14));
        Set("ThemePressedBrush", Mix(page, accent, 0.26));

        // ---- surfaces and lines
        Set("ThemeWindowBrush", window);
        Set("ThemePageBrush", page);
        Set("ThemeChromeBrush", chrome);
        Set("ThemeLineBrush", dark ? Mix(border, page, 0.20) : Mix(border, white, modern ? 0.30 : 0.15));
        Set("ThemeInputBackgroundBrush", input);
        Set("ThemeInputBorderBrush", dark ? Mix(border, white, 0.10) : modern ? Mix(border, black, 0.32) : P("#7A7A7A"));
        Set("ThemePopupBrush", dark ? Mix(page, white, 0.04) : input);

        // ---- buttons
        Rgb buttonBackground = dark ? Mix(page, white, 0.09) : modern ? white : P("#E1E1E1");
        Set("ThemeButtonBrush", buttonBackground);
        Set("ThemeButtonBorderBrush", dark ? Mix(border, white, 0.12) : modern ? Mix(border, black, 0.25) : border);
        Set("ThemeButtonHoverBrush", dark ? Mix(buttonBackground, white, 0.10) : modern ? Mix(buttonBackground, accent, 0.20) : ColourMath.Shade(buttonBackground, 0.07));
        Set("ThemeButtonPressedBrush", dark ? Mix(buttonBackground, black, 0.15) : modern ? Mix(buttonBackground, accent, 0.32) : ColourMath.Shade(buttonBackground, 0.14));
        Set("ThemeButtonHoverBorderBrush", classic ? ColourMath.Shade(border, 0.30) : accent);

        // ---- lists, headers, tabs
        Set("ThemeHeaderBrush", header);
        Set("ThemeGridLineBrush", Mix(border, dark ? page : white, 0.60));
        Set("ThemeAltRowBrush", dark ? Mix(page, white, 0.035) : modern ? Mix(window, white, 0.50) : Mix(white, border, 0.07));
        Set("ThemeSelectionBrush", selection);
        Set("ThemeTabBrush", tab);
        Set("ThemeTabHoverBrush", Mix(tab, accent, 0.10));

        // ---- text
        Set("ThemeTextBrush", text);
        Set("ThemeSubTextBrush", Mix(text, window, 0.20));
        Set("ThemeMutedBrush", Mix(text, window, 0.41));
        Set("ThemeDisabledTextBrush", Mix(text, window, 0.55));
        Set("ThemeReadOnlyBrush", Mix(window, border, 0.18));
        Set("ThemeDisabledBrush", Mix(window, border, 0.28));
        Set("ThemeTextSelectionBrush", accent);
        Set("ThemeTextSelectionTextBrush", accentText);

        // ---- tool tips
        Set("ThemeTooltipBrush", dark ? Mix(window, white, 0.16) : modern ? P("#1F2A3A") : P("#FFFFE1"));
        Set("ThemeTooltipTextBrush", dark ? text : modern ? white : black);
        r["ThemeTooltipBorderBrush"] = classic ? Brush(P("#767676")) : dark ? Brush(Mix(border, white, 0.10)) : Brush(Colors.Transparent);

        // ---- check boxes and radio buttons: accent-filled in every theme but Classic, whose boxes are white with a dark tick
        Set("ThemeCheckFillBrush", modern ? accent : input);
        Set("ThemeCheckTickBrush", modern ? accentText : P("#1F1F1F"));
        Set("ThemeCheckBorderBrush", modern ? accent : P("#333333"));

        // ---- group boxes
        r["ThemeGroupBrush"] = (modern && !dark) ? Brush(page) : Brush(Colors.Transparent);
        Set("ThemeGroupBorderBrush", dark ? border : modern ? Mix(border, white, 0.30) : P("#D5DFE5"));

        // ---- the box that has the keyboard focus
        Set("ThemeFocusBackgroundBrush", dark ? Mix(input, accent, 0.15) : Mix(accent, white, 0.86));
        Set("ThemeFocusBorderBrush", accent);

        // ---- corners
        int control = Math.Clamp(t.ControlRadius, 0, 24), card = Math.Clamp(t.CardRadius, 0, 32), tabRadius = Math.Clamp(t.TabRadius, 0, 24);
        r["RadiusControl"] = new CornerRadius(control);
        r["RadiusCheck"] = new CornerRadius(Math.Min(control, 5));
        r["RadiusCard"] = new CornerRadius(card);
        r["RadiusTabTop"] = new CornerRadius(tabRadius, tabRadius, 0, 0);
        r["RadiusPage"] = new CornerRadius(0, card, card, card);

        // ---- the names the rest of the XAML already uses (window, panels, preview, toolbars, scroll bars, sliders)
        Set("AppWindowBackgroundBrush", window);
        Set("AppWindowForegroundBrush", text);
        Set("AppSecondaryTextBrush", Mix(text, window, 0.41));
        Set("AppPanelBackgroundBrush", page);
        Set("AppPreviewCanvasBackgroundBrush", canvas);
        Set("AppBorderBrush", dark ? border : classic ? P("#808080") : Mix(border, black, 0.15));
        Set("AppChromeBackgroundBrush", chrome);
        r["AppHoverBrush"] = Brush(dark ? Color.FromArgb(0x26, 255, 255, 255) : Color.FromArgb(0x1A, 0, 0, 0));
        r["AppPressedBrush"] = Brush(dark ? Color.FromArgb(0x40, 255, 255, 255) : Color.FromArgb(0x33, 0, 0, 0));
        Set("AppSliderTrackBrush", dark ? P("#5A5A5A") : P("#C8C8C8"));
        Set("AppSliderThumbBrush", dark ? P("#E6E6E6") : white);
        Set("AppScrollThumbBrush", dark ? P("#5F5F5F") : modern ? border : P("#C2C2C2"));
        Set("AppScrollThumbHoverBrush", dark ? P("#8A8A8A") : modern ? Mix(border, accent, 0.45) : P("#8E8E8E"));
        Set("AppScrollThumbPressedBrush", dark ? P("#B0B0B0") : modern ? accent : P("#686868"));

        // ---- icons: a variation of the theme's colour, shifted by the Icon lightness slider
        ApplyIcons();
    }

    /// <summary>The Icon lightness slider (-100 darker ... +100 lighter, 0 = the theme's own). Changing it re-colours the icons at once, without re-applying the theme.</summary>
    public static void SetIconLightness(double lightness)
    {
        _iconLightness = Math.Clamp(lightness, -100, 100);
        if (Application.Current is not null) ApplyIcons();
    }

    private static void ApplyIcons()
    {
        var colour = ThemeCatalog.IconColour(ThemeCatalog.Find(CurrentName).Create(), _iconLightness);
        Application.Current.Resources["AppIconBrush"] = Brush(colour);
    }
}
