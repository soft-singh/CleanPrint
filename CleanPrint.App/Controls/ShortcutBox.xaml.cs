using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CleanPrint.App.Services;
using CleanPrint.Core.Models;

namespace CleanPrint.App.Controls;

/// <summary>
/// A single-line control for recording a keyboard shortcut: click it, then press the desired key
/// combination — the display updates immediately and the underlying Shortcut value (a plain string
/// token, see KeyboardShortcutFormat) updates to match. Press Escape to clear it to "no shortcut".
///
/// Read-only TextBox rather than a Button, so it visually matches ordinary text-entry fields
/// elsewhere in the Settings dialog and gets a normal focus rectangle when active.
///
/// Conflict checking: if both AllShortcuts and ActionPropertyName are set, a newly-recorded
/// combination is checked against every OTHER action's current shortcut BEFORE being committed —
/// checking before rather than after means declining the conflict just means "don't change
/// anything", with no need to track/restore a previous value.
/// </summary>
public partial class ShortcutBox : UserControl
{
    public static readonly DependencyProperty ShortcutProperty = DependencyProperty.Register(
        nameof(Shortcut), typeof(string), typeof(ShortcutBox),
        new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnShortcutChanged));

    public string Shortcut
    {
        get => (string)GetValue(ShortcutProperty);
        set => SetValue(ShortcutProperty, value);
    }

    /// <summary>The full KeyboardShortcuts instance to check the new value against — omit (leave
    /// null) to skip conflict checking entirely.</summary>
    public static readonly DependencyProperty AllShortcutsProperty = DependencyProperty.Register(
        nameof(AllShortcuts), typeof(KeyboardShortcuts), typeof(ShortcutBox), new PropertyMetadata(null));

    public KeyboardShortcuts? AllShortcuts
    {
        get => (KeyboardShortcuts?)GetValue(AllShortcutsProperty);
        set => SetValue(AllShortcutsProperty, value);
    }

    /// <summary>Which action THIS box represents (e.g. "OpenImage", matching
    /// KeyboardShortcutFormat.AllActions' PropertyName) — excluded from its own conflict check.</summary>
    public static readonly DependencyProperty ActionPropertyNameProperty = DependencyProperty.Register(
        nameof(ActionPropertyName), typeof(string), typeof(ShortcutBox), new PropertyMetadata(null));

    public string? ActionPropertyName
    {
        get => (string?)GetValue(ActionPropertyNameProperty);
        set => SetValue(ActionPropertyNameProperty, value);
    }

    public ShortcutBox()
    {
        InitializeComponent();
    }

    private static void OnShortcutChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var box = (ShortcutBox)d;
        box.DisplayTextBox.Text = KeyboardShortcutFormat.ToDisplayString((string?)e.NewValue);
    }

    private void DisplayTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // always swallow — this box only ever records, never types actual text

        // Alt-held keys (Key.System), and keys held by an IME or a dead-key layout, report the real key
        // somewhere other than e.Key — ResolveKey unwraps all three.
        var key = KeyboardShortcutFormat.ResolveKey(e);

        // Ignore a bare modifier press — wait for the actual key that completes the combination.
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        if (key == Key.Escape)
        {
            Shortcut = string.Empty; // clears it to "no shortcut assigned"
            return;
        }

        string candidate = KeyboardShortcutFormat.FromKeyEvent(Keyboard.Modifiers, key);

        if (AllShortcuts is not null && ActionPropertyName is not null)
        {
            var conflict = KeyboardShortcutFormat.AllActions.FirstOrDefault(a =>
                a.PropertyName != ActionPropertyName && KeyboardShortcutFormat.TokensEqual(a.Get(AllShortcuts), candidate));

            if (conflict.PropertyName is not null)
            {
                var thisAction = KeyboardShortcutFormat.AllActions.First(a => a.PropertyName == ActionPropertyName);
                var result = MessageBox.Show(
                    $"'{KeyboardShortcutFormat.ToDisplayString(candidate)}' is already assigned to '{conflict.DisplayName}'.\n\n" +
                    $"Reassign it to '{thisAction.DisplayName}' instead? This will clear it from '{conflict.DisplayName}'.",
                    "Shortcut Conflict", MessageBoxButton.YesNo, MessageBoxImage.Warning);

                if (result != MessageBoxResult.Yes)
                    return; // declined — leave both actions exactly as they were, nothing committed yet

                conflict.Set(AllShortcuts, string.Empty); // clear the OTHER action, since this one now takes it
            }
        }

        Shortcut = candidate;
    }
}
