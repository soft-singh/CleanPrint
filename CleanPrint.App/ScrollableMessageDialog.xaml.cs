using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace CleanPrint.App;

/// <summary>
/// A confirmation/info dialog whose item list scrolls instead of growing the window past the
/// screen — unlike a native MessageBox, which has no size cap and no scrolling at all, so a long
/// per-item list (e.g. confirming deletion of many selected jobs) could grow taller than the
/// screen and get clipped. MaxHeight is set here (not in XAML) to a fraction of the actual screen's
/// WorkArea height, so it adapts to whatever monitor the dialog opens on.
/// </summary>
public partial class ScrollableMessageDialog : Window
{
    /// <summary>True if the affirmative button (Yes, or OK when showCancelButton is false) was
    /// clicked; false for No/Cancel/closing the window any other way.</summary>
    public bool Result { get; private set; }

    /// <param name="header">The main message, shown above the scrollable item list.</param>
    /// <param name="items">One line per item (e.g. one per job being deleted or that failed to
    /// delete) — shown in a scrollable list below the header.</param>
    /// <param name="showCancelButton">False for a plain info/error dialog (just one "OK" button);
    /// true (the default) for a Yes/No confirmation.</param>
    public ScrollableMessageDialog(string title, string header, IEnumerable<string> items, bool showCancelButton = true)
    {
        InitializeComponent();
        Title = title;
        HeaderText.Text = header;
        ItemsListControl.ItemsSource = items.ToList();
        MaxHeight = SystemParameters.WorkArea.Height * 0.8;

        if (!showCancelButton)
        {
            YesButton.Content = "OK";
            NoButton.Visibility = Visibility.Collapsed;
        }
    }

    private void YesButton_Click(object sender, RoutedEventArgs e)
    {
        Result = true;
        DialogResult = true;
    }

    private void NoButton_Click(object sender, RoutedEventArgs e)
    {
        Result = false;
        DialogResult = false;
    }
}
