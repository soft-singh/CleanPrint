using System;
using System.Globalization;

namespace CleanPrint.Core.Theming;

/// <summary>An opaque colour. Plain numbers, so the palette logic can be tested without WPF.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static readonly Rgb White = new(255, 255, 255);
    public static readonly Rgb Black = new(0, 0, 0);

    /// <summary>"#RRGGBB" (or "#RGB"). Anything else gives <paramref name="fallback"/>.</summary>
    public static Rgb Parse(string? text, Rgb fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length == 3) hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        if (hex.Length == 8) hex = hex[2..];   // ARGB: the alpha is ignored
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) return fallback;
        return new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>The few bits of colour arithmetic the themes are built from.</summary>
public static class ColourMath
{
    /// <summary>A blend: 0 gives <paramref name="a"/>, 1 gives <paramref name="b"/>.</summary>
    public static Rgb Mix(Rgb a, Rgb b, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        byte Channel(byte x, byte y) => (byte)Math.Round(x + (y - x) * amount);
        return new Rgb(Channel(a.R, b.R), Channel(a.G, b.G), Channel(a.B, b.B));
    }

    /// <summary>Relative luminance (WCAG): 0 black, 1 white.</summary>
    public static double Luminance(Rgb c)
    {
        static double Channel(byte v)
        {
            double x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio between two colours, 1 (identical) to 21 (black on white). 4.5 is the usual bar for body text, 3 for large or bold text.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        double la = Luminance(a), lb = Luminance(b);
        double hi = Math.Max(la, lb), lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }

    /// <summary>Black or white, whichever reads better on <paramref name="background"/> — for the label of a button filled with an accent colour.</summary>
    public static Rgb BestTextOn(Rgb background) => Contrast(Rgb.White, background) >= Contrast(Rgb.Black, background) ? Rgb.White : Rgb.Black;

    /// <summary>
    /// The label colour for a button filled with a coloured accent: WHITE, as people expect on a coloured button, unless the colour is so light (luminance above 0.55 — a pale yellow, say)
    /// that white would be hard to read, when it is black. (BestTextOn picks purely by contrast, which chooses black for a mid blue; white on that blue is the convention and reads fine.)
    /// </summary>
    public static Rgb LabelOn(Rgb accent) => Luminance(accent) > 0.55 ? Rgb.Black : Rgb.White;

    /// <summary>A hover/pressed shade of a colour: darker normally, but LIGHTER when the colour is already very dark (darkening black does nothing).</summary>
    public static Rgb Shade(Rgb c, double amount) =>
        Luminance(c) < 0.10 ? Mix(c, Rgb.White, amount * 1.3) : Mix(c, Rgb.Black, amount);
}
