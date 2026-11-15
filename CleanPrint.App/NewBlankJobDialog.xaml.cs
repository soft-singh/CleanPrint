using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CleanPrint.App.Controls;
using CleanPrint.Core.Models;

namespace CleanPrint.App;

/// <summary>
/// "New Blank Job": choose the paper size, orientation, resolution and number of pages of a job that starts out as plain white pages. Shows, live, the size in
/// pixels the page will have and whether that is allowed (see BlankPageSizes: there is a ceiling on a page's pixels, since every page is held in memory).
/// Create is only enabled while the choices make a valid page. The arithmetic itself is in Core (BlankPageSizes), where it is tested.
/// </summary>
public partial class NewBlankJobDialog : Window
{
    // What the caller reads after the dialog closes with OK.
    public string PaperName { get; private set; } = "A4";
    public bool Landscape { get; private set; }
    public int WidthPx { get; private set; }
    public int HeightPx { get; private set; }
    public int Dpi { get; private set; } = 300;
    public int PageCount { get; private set; } = 1;

    // Remembered for the rest of the session, so a second blank job starts where the last one left off.
    private static string _lastPaper = "A4";
    private static bool _lastLandscape;
    private static int _lastDpi = 300;
    private static int _lastPages = 1;
    private static double _lastCustomWidth = 210, _lastCustomHeight = 297;
    private static bool _lastCustomInches;

    private bool _ready;           // false while the constructor is filling the controls in (their change events must not run the calculation half-set-up)
    private bool _valid;

    public NewBlankJobDialog()
    {
        InitializeComponent();

        PaperCombo.ItemsSource = BlankPageSizes.Standard.Select(p => p.Name).Append(BlankPageSizes.CustomName).ToList();
        PaperCombo.SelectedItem = PaperCombo.Items.Contains(_lastPaper) ? _lastPaper : "A4";
        (_lastLandscape ? LandscapeRadio : PortraitRadio).IsChecked = true;
        DpiCombo.Text = _lastDpi.ToString();
        PageCountBox.Value = _lastPages;
        CustomWidthBox.Value = _lastCustomWidth;
        CustomHeightBox.Value = _lastCustomHeight;
        UnitsCombo.SelectedIndex = _lastCustomInches ? 1 : 0;

        // The number box has no change event of its own, so watch its Value property.
        var valueProperty = DependencyPropertyDescriptor.FromProperty(NumericUpDown.ValueProperty, typeof(NumericUpDown));
        foreach (var box in new[] { CustomWidthBox, CustomHeightBox, PageCountBox })
            valueProperty.AddValueChanged(box, (_, _) => Recalculate());

        _ready = true;
        Recalculate();
    }

    private void Input_Changed(object sender, RoutedEventArgs e) => Recalculate();

    // A typed resolution arrives key by key, in the editable box's Text.
    private void DpiCombo_KeyUp(object sender, KeyEventArgs e) => Recalculate();

    // A resolution picked from the list: during SelectionChanged an editable ComboBox's Text still holds the OLD value, so copy the chosen item's text in
    // ourselves before calculating.
    private void DpiCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        if (DpiCombo.SelectedItem is ComboBoxItem { Content: string chosen })
            DpiCombo.Text = chosen;
        Recalculate();
    }

    private bool IsCustom => PaperCombo.SelectedItem as string == BlankPageSizes.CustomName;

    private int? ReadDpi() => int.TryParse(DpiCombo.Text.Trim(), out int dpi) ? dpi : null;

    private void Recalculate()
    {
        if (!_ready) return;

        bool custom = IsCustom;
        CustomLabel.Visibility = CustomPanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        PortraitRadio.IsEnabled = LandscapeRadio.IsEnabled = !custom;

        // The page in millimetres, as it will be (orientation applied).
        double widthMm, heightMm;
        string paperName;
        bool landscape;
        if (custom)
        {
            double unit = UnitsCombo.SelectedIndex == 1 ? BlankPageSizes.MmPerInch : 1.0;
            widthMm = CustomWidthBox.Value * unit;
            heightMm = CustomHeightBox.Value * unit;
            landscape = widthMm > heightMm;                       // the numbers decide; the orientation buttons just follow
            PortraitRadio.IsChecked = !landscape;
            LandscapeRadio.IsChecked = landscape;
            paperName = BlankPageSizes.CustomName;
        }
        else
        {
            var paper = BlankPageSizes.Standard.First(p => p.Name == (string)PaperCombo.SelectedItem);
            landscape = LandscapeRadio.IsChecked == true;
            widthMm = landscape ? paper.HeightMm : paper.WidthMm;
            heightMm = landscape ? paper.WidthMm : paper.HeightMm;
            paperName = paper.Name;
        }

        int? dpi = ReadDpi();
        string? problem = dpi is null
            ? "Enter the resolution as a whole number, such as 300."
            : BlankPageSizes.Validate(widthMm, heightMm, dpi.Value);

        _valid = problem is null;
        CreateButton.IsEnabled = _valid;

        if (problem is not null)
        {
            SummaryText.Text = problem;
            SummaryText.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
            return;
        }

        int widthPx = BlankPageSizes.MmToPixels(widthMm, dpi!.Value);
        int heightPx = BlankPageSizes.MmToPixels(heightMm, dpi.Value);
        int pages = (int)Math.Round(PageCountBox.Value);
        SummaryText.ClearValue(TextBlock.ForegroundProperty);   // back to the theme's own text colour (the problem message below sets its own red)
        SummaryText.Text =
            $"{paperName}, {(landscape ? "landscape" : "portrait")}: {widthPx:N0} × {heightPx:N0} pixels at {dpi} DPI " +
            $"({(long)widthPx * heightPx / 1_000_000.0:0.0} megapixels; {widthMm:0.#} × {heightMm:0.#} mm)" +
            (pages > 1 ? $" — {pages} pages." : ".");

        PaperName = paperName;
        Landscape = landscape;
        WidthPx = widthPx;
        HeightPx = heightPx;
        Dpi = dpi.Value;
        PageCount = pages;
    }

    private void CreateButton_Click(object sender, RoutedEventArgs e)
    {
        Recalculate();
        if (!_valid) return;

        _lastPaper = (string)PaperCombo.SelectedItem;
        _lastLandscape = LandscapeRadio.IsChecked == true;
        _lastDpi = Dpi;
        _lastPages = PageCount;
        _lastCustomWidth = CustomWidthBox.Value;
        _lastCustomHeight = CustomHeightBox.Value;
        _lastCustomInches = UnitsCombo.SelectedIndex == 1;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
