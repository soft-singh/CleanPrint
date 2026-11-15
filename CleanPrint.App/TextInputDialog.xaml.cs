using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace CleanPrint.App;

/// <summary>Minimal reusable "type a name, OK/Cancel" dialog — used for naming a new Effects
/// preset, since presets now save to a fixed system location rather than a user-chosen file path
/// (which is what a SaveFileDialog was for previously), so a plain text prompt for just the name
/// is all that's needed instead. Optionally shows a list of existing values (SetExistingValues) —
/// clicking a name copies it into the text box, so choosing an existing preset's name here means
/// "overwrite this one" rather than needing to type it out by hand; each entry also has its own
/// delete (🗑) button to remove it outright, via the caller-supplied onDeleteRequested callback.</summary>
public partial class TextInputDialog : Window
{
    public string InputText { get; private set; } = "";

    private Action<string>? _onDeleteRequested;

    public TextInputDialog(string title, string prompt, string initialValue = "")
    {
        InitializeComponent();
        Title = title;
        PromptText.Text = prompt;
        InputTextBox.Text = initialValue;
        InputTextBox.SelectAll();
        Loaded += (_, _) => InputTextBox.Focus();
    }

    /// <summary>Shows a clickable list of existing names below the text box — omit or pass an
    /// empty list to hide it entirely (e.g. when there's nothing to overwrite/delete yet).
    /// onDeleteRequested, if provided, is invoked with the name whenever that entry's own delete
    /// button is clicked (after the person confirms) — this dialog only manages its OWN displayed
    /// list; actually deleting the underlying thing (e.g. the preset file on disk) is the caller's
    /// responsibility via this callback.</summary>
    public void SetExistingValues(IEnumerable<string> values, Action<string>? onDeleteRequested = null)
    {
        var list = values.ToList();
        _onDeleteRequested = onDeleteRequested;
        if (list.Count == 0) return;

        ExistingLabel.Visibility = Visibility.Visible;
        ExistingListBox.Visibility = Visibility.Visible;
        foreach (var value in list)
            ExistingListBox.Items.Add(value);
    }

    private void ExistingListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ExistingListBox.SelectedItem is string selected)
            InputTextBox.Text = selected;
    }

    private void DeleteExistingItemButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string name) return;

        var confirm = MessageBox.Show(
            $"Delete \"{name}\"? This cannot be undone.",
            "Confirm Delete", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        _onDeleteRequested?.Invoke(name);
        ExistingListBox.Items.Remove(name);

        if (ExistingListBox.Items.Count == 0)
        {
            ExistingLabel.Visibility = Visibility.Collapsed;
            ExistingListBox.Visibility = Visibility.Collapsed;
        }

        // If the name just deleted was also sitting in the text box (e.g. clicked moments earlier
        // to pre-fill an overwrite), clear it too — otherwise OK would immediately re-save a
        // preset under the exact name just asked to be removed.
        if (InputTextBox.Text == name)
            InputTextBox.Text = "";
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        InputText = InputTextBox.Text.Trim();
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
