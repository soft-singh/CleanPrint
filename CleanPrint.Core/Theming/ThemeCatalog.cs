using System;
using System.Collections.Generic;
using System.Linq;

namespace CleanPrint.Core.Theming;

/// <summary>
/// The look of one theme: a handful of base colours and how round the corners are. Everything else — hover shades, borders, disabled states, the colours of every
/// control — is worked out from these by the theme manager, the way the CMS application does it, so a theme is a dozen values rather than a hundred brushes.
/// </summary>
public sealed class ThemeSettings
{
    /// <summary>A dark theme: light text on dark surfaces. Controls are shaded the other way round (hover = lighter).</summary>
    public bool IsDark { get; set; }

    /// <summary>The plain grey look of Windows: square corners, grey buttons, white check boxes. False for every other theme ("modern" controls: accent-coloured ticks, softer buttons).</summary>
    public bool Classic { get; set; }

    /// <summary>Corner radius of buttons, text boxes, drop-down lists and check boxes (pixels).</summary>
    public int ControlRadius { get; set; }
    /// <summary>Corner radius of cards, group boxes and menus.</summary>
    public int CardRadius { get; set; }
    /// <summary>Corner radius of the tabs.</summary>
    public int TabRadius { get; set; }

    /// <summary>The one strong colour: focus rings, the default button, ticks, the selection in text boxes.</summary>
    public string Accent { get; set; } = "";
    /// <summary>The background of windows and dialogs.</summary>
    public string Window { get; set; } = "";
    /// <summary>The title bar, status bar and toolbars.</summary>
    public string Chrome { get; set; } = "";
    /// <summary>Panels and the surfaces inside a window (the Effects panel, group boxes, lists).</summary>
    public string Page { get; set; } = "";
    public string Border { get; set; } = "";
    /// <summary>List and table headers.</summary>
    public string Header { get; set; } = "";
    /// <summary>The selected row of a list, the highlighted item of a drop-down.</summary>
    public string Selection { get; set; } = "";
    public string Tab { get; set; } = "";
    /// <summary>Behind the page preview.</summary>
    public string Canvas { get; set; } = "";
    /// <summary>Inside text boxes and drop-down lists.</summary>
    public string Input { get; set; } = "";
    public string Text { get; set; } = "";
    /// <summary>Icons on the toolbars, if the theme wants a particular colour; empty = worked out.</summary>
    public string Icon { get; set; } = "";

    public ThemeSettings Copy() => (ThemeSettings)MemberwiseClone();
}

/// <summary>One theme of the list.</summary>
public sealed record ThemeInfo(string Name, string Description, Func<ThemeSettings> Create)
{
    public bool IsDark => Create().IsDark;
    public string Accent => Create().Accent;
}

/// <summary>
/// The themes the app ships with. Classic is the look CleanPrint always had (and the default); Modern, Emerald, Teal, Violet, Rose, Amber and Slate are the CMS application's
/// light themes; Dark and Darker are neutral-grey dark themes in the manner of Photoshop's interface — a dark canvas, slightly lighter panels, a restrained blue accent.
/// </summary>
public static class ThemeCatalog
{
    public const string Classic = "Classic";
    public const string Modern = "Modern";
    public const string Dark = "Dark";
    public const string Darker = "Darker";

    /// <summary>
    /// A light theme. The SELECTION colour (the highlighted row of a list, drop-down or menu) is worked out from the accent — 40% of the way from the accent to white — rather than being a pale
    /// hand-picked tint: that is clearly visible as a highlight yet the dark text on it still reads (checked for every theme).
    /// </summary>
    private static ThemeSettings Light(string accent, string header, string chrome, string window, string border, string canvas) => new()
    {
        ControlRadius = 5, CardRadius = 8, TabRadius = 5,
        Accent = accent, Selection = ColourMath.Mix(Rgb.Parse(accent, Rgb.Black), Rgb.White, 0.40).ToHex(), Header = header, Tab = header, Chrome = chrome, Window = window, Page = "#FFFFFF",
        Border = border, Canvas = canvas, Input = "#FFFFFF", Text = "#1F2A3A",
    };

    public static IReadOnlyList<ThemeInfo> All { get; } = new List<ThemeInfo>
    {
        new(Classic, "The grey look CleanPrint has always had", () => new ThemeSettings
        {
            Classic = true, ControlRadius = 0, CardRadius = 0, TabRadius = 0,
            Accent = "#595959", Window = "#FFFFFF", Chrome = "#F3F3F3", Page = "#FFFFFF", Border = "#ADADAD", Header = "#E8E8E8",
            Selection = "#ABABAB", Tab = "#E1E1E1", Canvas = "#EEEEEE", Input = "#FFFFFF", Text = "#000000", Icon = "#595959",
        }),
        new(Modern, "Soft blue, rounded corners", () => Light("#3B82F6", "#EEF2F7", "#F4F7FB", "#F6F8FB", "#C5CEDB", "#E7ECF3")),
        new("Emerald", "Green accent", () => Light("#059669", "#ECF6F1", "#F3F8F5", "#F5F9F7", "#C2D3CB", "#E6EEEA")),
        new("Teal", "Teal accent", () => Light("#0891B2", "#E6F4F8", "#F0F8FA", "#F3F9FB", "#B9D6DF", "#E3EEF2")),
        new("Violet", "Violet accent", () => Light("#7C3AED", "#F1EEFA", "#F6F4FB", "#F8F7FC", "#CDC6E0", "#EBE8F3")),
        new("Rose", "Rose accent", () => Light("#E11D48", "#FBEEF1", "#FCF5F7", "#FBF7F8", "#DEC8CE", "#F1E8EB")),
        new("Amber", "Amber accent", () => Light("#D97706", "#FBF3E6", "#FCF8F1", "#FBF9F5", "#DCCFB8", "#F0EBE1")),
        new("Slate", "Quiet blue-grey", () => Light("#475569", "#EEF0F3", "#F3F4F6", "#F6F7F9", "#C9CED6", "#E6E8EC")),
        new(Dark, "Medium-dark grey with a blue accent — easy on the eyes, like Photoshop", () => new ThemeSettings
        {
            IsDark = true, ControlRadius = 3, CardRadius = 4, TabRadius = 3,
            Accent = "#2D8CEB", Window = "#3E3E3E", Chrome = "#333333", Page = "#454545", Border = "#5A5A5A", Header = "#505050",
            Selection = "#2B63A6", Tab = "#4B4B4B", Canvas = "#525252", Input = "#303030", Text = "#E0E0E0", Icon = "#C8C8C8",
        }),
        new(Darker, "Near-black, for dim rooms and the focus on the page", () => new ThemeSettings
        {
            IsDark = true, ControlRadius = 3, CardRadius = 4, TabRadius = 3,
            Accent = "#2D8CEB", Window = "#232323", Chrome = "#1B1B1B", Page = "#282828", Border = "#3A3A3A", Header = "#313131",
            Selection = "#264F7D", Tab = "#2E2E2E", Canvas = "#141414", Input = "#1C1C1C", Text = "#CFCFCF", Icon = "#BDBDBD",
        }),
    };

    /// <summary>
    /// The colour of the toolbar icons for a theme: the theme's own icon colour (light grey on the dark themes; a grey tinted with the accent on the light ones), shifted by the user's
    /// lightness slider — <paramref name="lightness"/> from -100 (darkest) to +100 (lightest), 0 = the theme's own. However far the slider goes the icons stay readable: if the shifted colour
    /// would be hard to see against the toolbar (under 3:1), it is moved back out until it isn't.
    /// </summary>
    public static Rgb IconColour(ThemeSettings theme, double lightness)
    {
        Rgb Parse(string hex) => Rgb.Parse(hex, Rgb.Black);
        Rgb accent = Parse(theme.Accent);
        Rgb baseColour = !string.IsNullOrWhiteSpace(theme.Icon) ? Parse(theme.Icon)
            : theme.IsDark ? Parse("#C8C8C8") : ColourMath.Mix(Parse("#595959"), accent, 0.35);

        double v = Math.Clamp(lightness, -100, 100) / 100.0;
        Rgb shifted = v >= 0 ? ColourMath.Mix(baseColour, Rgb.White, v * 0.70) : ColourMath.Mix(baseColour, Rgb.Black, -v * 0.70);

        // Readable on the toolbar, whatever the slider says: step away from the toolbar's own lightness until the contrast is enough.
        Rgb chrome = Parse(theme.Chrome);
        Rgb away = ColourMath.Luminance(chrome) < 0.5 ? Rgb.White : Rgb.Black;
        for (int step = 0; step < 20 && ColourMath.Contrast(shifted, chrome) < 3.0; step++)
            shifted = ColourMath.Mix(shifted, away, 0.1);
        return shifted;
    }

    /// <summary>The theme with this name (any case), or Classic if there is none (a name from a newer version, a typo in the settings file).</summary>
    public static ThemeInfo Find(string? name) =>
        All.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)) ?? All[0];

    /// <summary>
    /// The theme a settings file means. Files written before themes existed have only the old Dark mode switch: that becomes Dark, or Classic (what the app looked like with it off).
    /// </summary>
    public static string Resolve(string? savedTheme, bool legacyDarkMode) =>
        string.IsNullOrWhiteSpace(savedTheme) ? (legacyDarkMode ? Dark : Classic) : Find(savedTheme).Name;
}
