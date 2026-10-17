using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;

namespace Rambutan.App.ViewModels;

/// <summary>
/// A solid colour the user picks in Settings: a hex box, a picker and the
/// ready-made swatches. The visualizer's custom colour and the custom accent
/// each have one. A valid colour goes to <c>apply</c> as #RRGGBB; while the
/// text isn't one, the last valid colour stays in use.
/// </summary>
public sealed partial class CustomColorField : ObservableObject
{
    private readonly Action<string>? _apply;
    private readonly string _pickKey;
    private readonly string _hexNameKey;
    private readonly string _invalidKey;

    /// <param name="apply">Takes each valid colour; null in design data.</param>
    /// <param name="pickKey">Names the picker.</param>
    /// <param name="hexNameKey">Names the hex box.</param>
    /// <param name="invalidKey">The note shown while the text isn't a colour.</param>
    public CustomColorField(string hex, Action<string>? apply, string pickKey, string hexNameKey, string invalidKey)
    {
        _hex = hex;
        _applied = hex;
        _apply = apply;
        _pickKey = pickKey;
        _hexNameKey = hexNameKey;
        _invalidKey = invalidKey;
    }

    public string PickName => Strings.Get(_pickKey);
    public string HexName => Strings.Get(_hexNameKey);

    private string _applied;

    [ObservableProperty] private string _hex;
    [ObservableProperty] private string? _error;

    /// <summary>Ready-made colours, beside the picker.</summary>
    public IReadOnlyList<ColorPreset> Presets => AllPresets;

    private static readonly IReadOnlyList<ColorPreset> AllPresets =
    [
        new("#FF4E45", "settings.appearance.visualizerColor.preset.red"),
        new("#FF9F43", "settings.appearance.visualizerColor.preset.orange"),
        new("#FFD93D", "settings.appearance.visualizerColor.preset.yellow"),
        new("#6BCB77", "settings.appearance.visualizerColor.preset.green"),
        new("#4FC3F7", "settings.appearance.visualizerColor.preset.cyan"),
        new("#5B8DEF", "settings.appearance.visualizerColor.preset.blue"),
        new("#B388FF", "settings.appearance.visualizerColor.preset.violet"),
        new("#F06292", "settings.appearance.visualizerColor.preset.pink"),
    ];

    /// <summary>The picker's side of <see cref="Hex"/>: the colour in use.</summary>
    public Avalonia.Media.Color Color
    {
        get => CssHexColor.Parse(_applied, Avalonia.Media.Colors.White);
        set
        {
            if (value == Color)
                return;
            Hex = CssHexColor.Format(Avalonia.Media.Color.FromRgb(value.R, value.G, value.B));
        }
    }

    public Avalonia.Media.IBrush Swatch => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color);

    partial void OnHexChanged(string value)
    {
        // Solid colours only: an alpha has no meaning for either use.
        if (CssHexColor.Normalize(value) is not { Length: 4 or 7 } normalized)
        {
            Error = Strings.Get(_invalidKey);
            return;
        }
        Error = null;
        _applied = AppearanceSettings.ParseCustomColor(normalized, _applied);
        OnPropertyChanged(nameof(Color));
        OnPropertyChanged(nameof(Swatch));
        _apply?.Invoke(_applied);
    }

    [RelayCommand]
    private void UsePreset(ColorPreset preset) => Hex = preset.Hex;
}
