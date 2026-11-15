using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace CleanPrint.Core.Models;

/// <summary>
/// Every adjustable image-effect parameter, applied non-destructively: the ORIGINAL captured page
/// on disk is never modified — these settings are re-applied fresh every time a page is loaded,
/// previewed, printed, or exported (see IImageProcessingService.ApplyEffectsAsync), the same way
/// AutoCropToContent already works. Defaults reproduce the app's previous fixed, always-on
/// behavior (full-strength background whitening, everything else off/neutral), so loading a job
/// saved before this feature existed looks the same as it always did until a preset changes it.
///
/// Implements INotifyPropertyChanged (a standard BCL interface, not a WPF-specific one — this
/// doesn't pull a UI framework dependency into Core) so the Effects dialog can bind directly to
/// these properties for live, two-way updates, the same way the rest of this app's ViewModels work.
/// </summary>
public sealed class ImageEffectSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised once at the end of CopyFrom, in addition to the normal per-property
    /// PropertyChanged events raised along the way — lets a subscriber distinguish "a whole preset
    /// (or job's saved state) was just loaded in one go" from "individual properties are changing
    /// one at a time" (e.g. dragging a slider), which need different reactions in EffectsPanel: a
    /// slider edit should immediately clear the preset combo's selection, but a bulk load should
    /// instead re-check whether the newly-loaded values happen to match a saved preset.</summary>
    public event EventHandler? BulkChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool _whiteningEnabled = true;
    /// <summary>Master on/off switch — matches how whitening was always-on before this feature
    /// existed. When off, WhiteningStrength is ignored entirely.</summary>
    public bool WhiteningEnabled
    {
        get => _whiteningEnabled;
        set { _whiteningEnabled = value; OnPropertyChanged(); }
    }

    private double _whiteningStrength = 100;
    /// <summary>0-100. 100 = the original full-strength correction; lower values blend toward the
    /// unwhitened image, for scans where full correction looks too aggressive.</summary>
    public double WhiteningStrength
    {
        get => _whiteningStrength;
        set { _whiteningStrength = value; OnPropertyChanged(); }
    }

    private double _brightness;
    /// <summary>-100 to 100. 0 = no change.</summary>
    public double Brightness
    {
        get => _brightness;
        set { _brightness = value; OnPropertyChanged(); }
    }

    private double _contrast;
    /// <summary>-100 to 100. 0 = no change.</summary>
    public double Contrast
    {
        get => _contrast;
        set { _contrast = value; OnPropertyChanged(); }
    }

    private double _midtone = 1.0;
    /// <summary>Gamma correction targeting midtones specifically (matches most photo editors'
    /// "midtone" slider, which IS a gamma adjustment) — 0.1 to 3.0, 1.0 = no change. Values above 1
    /// brighten midtones, below 1 darken them, both while leaving pure black/white endpoints fixed.</summary>
    public double Midtone
    {
        get => _midtone;
        set { _midtone = value; OnPropertyChanged(); }
    }

    private bool _thresholdEnabled;
    /// <summary>When enabled, binarizes the image to pure black/white at ThresholdLevel — a
    /// deliberately drastic, final-stage effect (applied last, after everything else below).</summary>
    public bool ThresholdEnabled
    {
        get => _thresholdEnabled;
        set { _thresholdEnabled = value; OnPropertyChanged(); }
    }

    private int _thresholdLevel = 128;
    /// <summary>0-255. Only meaningful when ThresholdEnabled.</summary>
    public int ThresholdLevel
    {
        get => _thresholdLevel;
        set { _thresholdLevel = value; OnPropertyChanged(); }
    }

    private double _noiseReduction;
    /// <summary>0-100. 0 = off. Median-blur based; higher values use a larger kernel.</summary>
    public double NoiseReduction
    {
        get => _noiseReduction;
        set { _noiseReduction = value; OnPropertyChanged(); }
    }

    private double _sharpen;
    /// <summary>0-100. 0 = off. Unsharp-mask based.</summary>
    public double Sharpen
    {
        get => _sharpen;
        set { _sharpen = value; OnPropertyChanged(); }
    }

    private bool _autoCropEnabled = true;
    /// <summary>Whether the page is auto-cropped to its detected content at all — moved here from
    /// MainViewModel.AutoCropToContent since auto-crop is conceptually one of the effects/
    /// adjustments in this dialog, not a separate toolbar-level setting.</summary>
    public bool AutoCropEnabled
    {
        get => _autoCropEnabled;
        set { _autoCropEnabled = value; OnPropertyChanged(); }
    }

    private int _autoCropWhiteThreshold = 250;
    /// <summary>0-255. How dark a pixel must be to count as "content" rather than background when
    /// auto-cropping — matches CropToContentAsync's own previous hardcoded value (250) by default.
    /// Lower it if faint content near the page edge is being cropped away as if it were background.</summary>
    public int AutoCropWhiteThreshold
    {
        get => _autoCropWhiteThreshold;
        set { _autoCropWhiteThreshold = value; OnPropertyChanged(); }
    }

    private int _autoCropPaddingPixels = -1;
    /// <summary>Extra margin (pixels, at the page's actual DPI) kept around the detected content
    /// when auto-cropping. -1 means "use the built-in DPI-scaled default" (matching
    /// CropToContentAsync's own previous hardcoded formula); any value >= 0 overrides it directly.</summary>
    public int AutoCropPaddingPixels
    {
        get => _autoCropPaddingPixels;
        set { _autoCropPaddingPixels = value; OnPropertyChanged(); }
    }

    public ImageEffectSettings Clone() => new()
    {
        WhiteningEnabled = WhiteningEnabled,
        WhiteningStrength = WhiteningStrength,
        Brightness = Brightness,
        Contrast = Contrast,
        Midtone = Midtone,
        ThresholdEnabled = ThresholdEnabled,
        ThresholdLevel = ThresholdLevel,
        NoiseReduction = NoiseReduction,
        Sharpen = Sharpen,
        AutoCropEnabled = AutoCropEnabled,
        AutoCropWhiteThreshold = AutoCropWhiteThreshold,
        AutoCropPaddingPixels = AutoCropPaddingPixels,
    };

    /// <summary>Every value at its most neutral/off setting — no whitening, no tonal adjustment, no
    /// auto-crop, nothing. Used when no default preset is configured for newly-loaded jobs (see
    /// AppSettings.DefaultPresetName): "if no preset is chosen, no initial effect is applied",
    /// rather than silently carrying over whatever was last used.</summary>
    /// <summary>
    /// True if any effect that CHANGES THE PIXELS is on: whitening, brightness, contrast, midtone, threshold, noise reduction or sharpen. Auto-crop is not one of them — it
    /// only decides which part of the page is shown, not what it looks like. Used to tell whether baking these settings into a page would actually alter it.
    /// </summary>
    public bool ChangesPixels =>
        (WhiteningEnabled && WhiteningStrength >= 0.01) ||
        Math.Abs(Brightness) >= 0.01 ||
        Math.Abs(Contrast) >= 0.01 ||
        Math.Abs(Midtone - 1.0) >= 0.01 ||
        ThresholdEnabled ||
        NoiseReduction >= 0.01 ||
        Sharpen >= 0.01;

    public static ImageEffectSettings NoEffects() => new()
    {
        WhiteningEnabled = false,
        AutoCropEnabled = false,
    };

    /// <summary>True if every value matches — used by EffectsPanel to figure out, when it's first
    /// created, whether the CURRENT settings (e.g. just applied via a default preset on job load)
    /// happen to match one of the saved presets, so the preset combo can show it as selected
    /// instead of blank even though nothing was selected FROM the combo itself.</summary>
    public bool HasSameValues(ImageEffectSettings other) =>
        WhiteningEnabled == other.WhiteningEnabled &&
        Math.Abs(WhiteningStrength - other.WhiteningStrength) < 0.01 &&
        Math.Abs(Brightness - other.Brightness) < 0.01 &&
        Math.Abs(Contrast - other.Contrast) < 0.01 &&
        Math.Abs(Midtone - other.Midtone) < 0.01 &&
        ThresholdEnabled == other.ThresholdEnabled &&
        ThresholdLevel == other.ThresholdLevel &&
        Math.Abs(NoiseReduction - other.NoiseReduction) < 0.01 &&
        Math.Abs(Sharpen - other.Sharpen) < 0.01 &&
        AutoCropEnabled == other.AutoCropEnabled &&
        AutoCropWhiteThreshold == other.AutoCropWhiteThreshold &&
        AutoCropPaddingPixels == other.AutoCropPaddingPixels;

    /// <summary>Copies every value from another instance into this one, raising the normal
    /// per-property change notifications — used when loading a preset or restoring persisted
    /// settings, so the SAME ImageEffectSettings instance (which MainViewModel and the Effects
    /// dialog both hold a reference to) gets updated in place, rather than needing every binding
    /// point to be re-wired to a brand new instance.</summary>
    public void CopyFrom(ImageEffectSettings other)
    {
        WhiteningEnabled = other.WhiteningEnabled;
        WhiteningStrength = other.WhiteningStrength;
        Brightness = other.Brightness;
        Contrast = other.Contrast;
        Midtone = other.Midtone;
        ThresholdEnabled = other.ThresholdEnabled;
        ThresholdLevel = other.ThresholdLevel;
        NoiseReduction = other.NoiseReduction;
        Sharpen = other.Sharpen;
        AutoCropEnabled = other.AutoCropEnabled;
        AutoCropWhiteThreshold = other.AutoCropWhiteThreshold;
        AutoCropPaddingPixels = other.AutoCropPaddingPixels;
        BulkChanged?.Invoke(this, EventArgs.Empty);
    }
}
