using System.Windows.Media;
using CleanPrint.Core.Theming;

namespace CleanPrint.App.ViewModels;

/// <summary>One entry of Settings' Theme box: its name, a line of description, and a little swatch (the theme's window colour with its accent) so the choice can be seen before it is made.</summary>
public sealed class ThemeOption
{
    public ThemeOption(ThemeInfo info)
    {
        var settings = info.Create();
        Name = info.Name;
        Description = info.Description;
        WindowBrush = Make(settings.Window);
        AccentBrush = Make(settings.Accent);
        BorderBrush = Make(settings.Border);
    }

    public string Name { get; }
    public string Description { get; }
    public Brush WindowBrush { get; }
    public Brush AccentBrush { get; }
    public Brush BorderBrush { get; }

    private static Brush Make(string hex)
    {
        var c = Rgb.Parse(hex, Rgb.Black);
        var brush = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
        brush.Freeze();
        return brush;
    }
}
