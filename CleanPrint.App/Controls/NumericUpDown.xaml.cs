using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace CleanPrint.App.Controls;

/// <summary>
/// A numeric input matching HTML's &lt;input type="number"&gt;: a text field plus small up/down
/// stepper buttons, with Min/Max clamping and configurable decimal precision (0 for whole numbers
/// only — e.g. opacity 0-100 — or higher to allow fractional values).
///
/// Value is a proper WPF DependencyProperty (registered BindsTwoWayByDefault, like TextBox.Text),
/// so it drops into ordinary "{Binding SomeProperty}" usage with no special Mode= needed. It
/// commits to the bound property on every keystroke (matching how HTML number inputs typically
/// two-way-bind via the "input" event) — safe to do because PART_TextBox_PreviewTextInput already
/// guarantees the text can only ever be a syntactically valid (if possibly incomplete) partial
/// number, and the displayed text itself is only reformatted to its canonical form on blur/Enter/
/// button-click, not on every keystroke, so typing multi-digit or decimal numbers isn't disrupted
/// mid-edit.
/// </summary>
public partial class NumericUpDown : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(NumericUpDown),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged, CoerceValue));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(NumericUpDown), new PropertyMetadata(0.0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(NumericUpDown), new PropertyMetadata(100.0, OnRangeChanged));

    /// <summary>How much Value changes per click of the up/down buttons (and per repeat tick while
    /// held, via the RepeatButtons' own repeat timing) or per Up/Down arrow key.</summary>
    public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
        nameof(Increment), typeof(double), typeof(NumericUpDown), new PropertyMetadata(1.0));

    /// <summary>0 for whole-number-only input; set higher (e.g. 1 or 2) to allow decimals.</summary>
    public static readonly DependencyProperty DecimalPlacesProperty = DependencyProperty.Register(
        nameof(DecimalPlaces), typeof(int), typeof(NumericUpDown), new PropertyMetadata(0, OnValueChanged));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Increment
    {
        get => (double)GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public int DecimalPlaces
    {
        get => (int)GetValue(DecimalPlacesProperty);
        set => SetValue(DecimalPlacesProperty, value);
    }

    private bool _isLiveTyping;

    /// <summary>True only while RefreshDisplayedText is writing the formatted text into the TextBox.
    /// That write raises TextChanged like any other, and without this guard the live-typing handler
    /// parsed the freshly formatted (ROUNDED) text straight back into Value — e.g. a bound 87.34 shown
    /// as "87" got committed as 87, silently nudging the underlying setting away from what it was just
    /// set to. Most visibly: loading a saved Effects preset (whose slider-set values are often
    /// fractional) changed the live settings so they no longer matched the preset, and the preset
    /// combo went blank. Only text the PERSON typed should ever be parsed back into Value.</summary>
    private bool _isRefreshingText;

    public NumericUpDown()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshDisplayedText();
    }

    private static object CoerceValue(DependencyObject d, object baseValue)
    {
        var control = (NumericUpDown)d;
        double value = (double)baseValue;
        return System.Math.Clamp(value, control.Minimum, control.Maximum);
    }

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (NumericUpDown)d;
        control.CoerceValue(ValueProperty); // re-clamp Value if Min/Max just moved past its current value
        control.RefreshDisplayedText();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var control = (NumericUpDown)d;
        // Skip reformatting while the live-typing commit below is what triggered this change —
        // otherwise, e.g. with DecimalPlaces=2, typing "5" would immediately get rewritten to
        // "5.00" mid-keystroke, before the user has a chance to type the rest of the number they
        // actually wanted. The canonical formatted text still gets applied normally on blur/Enter/
        // spinner-click/external (ViewModel-driven) changes, just not on every character typed.
        if (!control._isLiveTyping)
            control.RefreshDisplayedText();
    }

    private void RefreshDisplayedText()
    {
        if (PART_TextBox is null) return; // control not yet loaded

        string formatted = Value.ToString("F" + DecimalPlaces, CultureInfo.InvariantCulture);
        // Only overwrite if it actually differs — avoids fighting the caret/selection while the
        // user is actively typing, since Value's own change notification fires this too.
        if (PART_TextBox.Text != formatted)
        {
            _isRefreshingText = true;
            try { PART_TextBox.Text = formatted; }
            finally { _isRefreshingText = false; }
        }
    }

    /// <summary>
    /// Commits the bound Value as the user types, not just on blur/Enter/button-click — matching
    /// how an HTML number input's bound frameworks typically update on every keystroke (the
    /// "input" event), and directly avoiding the bug this control exists partly to prevent: typing
    /// a new value and having nothing visibly update until some UNRELATED control happens to steal
    /// focus afterward. Safe to commit on every keystroke here specifically because
    /// PART_TextBox_PreviewTextInput already guarantees the text can only ever be a syntactically
    /// valid (if possibly incomplete, e.g. "-" or "5.") partial number — TryParse simply skips
    /// committing until it's a complete one.
    /// </summary>
    private void PART_TextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isRefreshingText) return; // a programmatic display refresh, not the person typing — see _isRefreshingText

        if (double.TryParse(PART_TextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
        {
            _isLiveTyping = true;
            Value = parsed; // still runs through CoerceValue, clamping to Min/Max even while live-typing
            _isLiveTyping = false;
        }
    }

    /// <summary>Blocks invalid CHARACTERS from being typed at all (only digits, at most one leading
    /// minus when Minimum allows negative values, at most one decimal point when DecimalPlaces > 0)
    /// — rather than letting anything through and correcting/resetting the whole field afterward,
    /// which is jarring to type against and is what made the reference implementation's
    /// TextChanged-based approach reset to "0" mid-typing.</summary>
    private void PART_TextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        string proposed = PART_TextBox.Text
            .Remove(PART_TextBox.SelectionStart, PART_TextBox.SelectionLength)
            .Insert(PART_TextBox.SelectionStart, e.Text);

        string pattern = DecimalPlaces > 0
            ? (Minimum < 0 ? @"^-?\d*\.?\d*$" : @"^\d*\.?\d*$")
            : (Minimum < 0 ? @"^-?\d*$" : @"^\d*$");

        e.Handled = !Regex.IsMatch(proposed, pattern);
    }

    private void PART_TextBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitText();
            Keyboard.ClearFocus(); // matches HTML number inputs committing on Enter, not just blur
            e.Handled = true;
        }
        else if (e.Key == Key.Up)
        {
            Value = System.Math.Round(Value + Increment, DecimalPlaces);
            e.Handled = true;
        }
        else if (e.Key == Key.Down)
        {
            Value = System.Math.Round(Value - Increment, DecimalPlaces);
            e.Handled = true;
        }
    }

    private void PART_TextBox_LostFocus(object sender, RoutedEventArgs e) => CommitText();

    private void CommitText()
    {
        // If the text is just the canonical display of the CURRENT value (nothing was typed), there's
        // nothing to commit — and committing would round the value to the displayed precision, so
        // merely clicking into a box and tabbing out would quietly change 87.34 into 87.
        if (PART_TextBox.Text == Value.ToString("F" + DecimalPlaces, CultureInfo.InvariantCulture))
            return;

        if (double.TryParse(PART_TextBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
            Value = parsed; // runs through CoerceValue automatically, clamping to Min/Max
        else
            RefreshDisplayedText(); // invalid/incomplete text (e.g. just "-" or empty) — revert to the last valid Value rather than force it to 0
    }

    private void UpButton_Click(object sender, RoutedEventArgs e) =>
        Value = System.Math.Round(Value + Increment, DecimalPlaces);

    private void DownButton_Click(object sender, RoutedEventArgs e) =>
        Value = System.Math.Round(Value - Increment, DecimalPlaces);
}
