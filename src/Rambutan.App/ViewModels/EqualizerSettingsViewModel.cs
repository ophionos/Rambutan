using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Audio;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings → Playback → Equalizer: a switch, and a slider per band of
/// <see cref="Equalizer.Frequencies"/>. Each change is applied at once and saved
/// with the rest of the settings; the bands are stored with their frequencies.
/// </summary>
public partial class EqualizerSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly Action<bool, IReadOnlyList<double>> _apply;
    private readonly Action _persist;
    private bool _resetting;

    [ObservableProperty] private bool _enabled;

    public IReadOnlyList<EqualizerBandViewModel> Bands { get; }

    /// <param name="apply">Gives the engine the switch and the gains, one per band.</param>
    public EqualizerSettingsViewModel(AppSettings app, Action<bool, IReadOnlyList<double>> apply, Action persist)
    {
        _app = app;
        _apply = apply;
        _persist = persist;
        _enabled = app.EqualizerEnabled;
        var gains = Stored(app);
        Bands = [.. Equalizer.Frequencies.Select((hz, i) => new EqualizerBandViewModel(hz, gains[i]))];
        foreach (var band in Bands)
            band.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(EqualizerBandViewModel.Gain) && !_resetting)
                    Changed();
            };
        Strings.WhenChanged(this, static vm =>
        {
            foreach (var band in vm.Bands)
                band.Relabel();
        });
    }

    /// <summary>The stored gains, one per band; flat when there are none or they
    /// don't name the current bands.</summary>
    public static double[] Stored(AppSettings app) =>
        Equalizer.FromBands(app.EqualizerBands?.Select(b => (b.Hz, b.Db)));

    partial void OnEnabledChanged(bool value) => Changed();

    /// <summary>Every band back to 0 dB, applied as one change.</summary>
    [RelayCommand]
    private void Reset()
    {
        _resetting = true;
        try
        {
            foreach (var band in Bands)
                band.Gain = 0;
        }
        finally
        {
            _resetting = false;
        }
        Changed();
    }

    private void Changed()
    {
        var gains = Bands.Select(b => b.Gain).ToArray();
        _app.EqualizerEnabled = Enabled;
        _app.EqualizerBands = [.. Bands.Select(b => new EqualizerBandSetting { Hz = b.Hz, Db = b.Gain })];
        _apply(Enabled, gains);
        _persist();
    }
}

/// <summary>One band's slider: its centre, its gain in whole dB, and what a screen reader calls it.</summary>
public partial class EqualizerBandViewModel : ObservableObject
{
    public EqualizerBandViewModel(int hz, double gain)
    {
        Hz = hz;
        _gain = gain;
    }

    public int Hz { get; }

    public double Minimum => Equalizer.MinGainDb;
    public double Maximum => Equalizer.MaxGainDb;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GainText))]
    [NotifyPropertyChangedFor(nameof(ShortGainText))]
    private double _gain;

    /// <summary>"31 Hz", "1 kHz".</summary>
    public string Label => Hz >= 1000
        ? Strings.Format("settings.equalizer.khz", ("value", (Hz / 1000.0).ToString("0.#", Strings.Culture)))
        : Strings.Format("settings.equalizer.hz", ("value", Hz.ToString(Strings.Culture)));

    /// <summary>"31", "1k": under the slider, where ten full labels don't fit a
    /// narrow window. The help text and the slider's name carry the unit.</summary>
    public string ShortLabel => Hz >= 1000
        ? Strings.Format("settings.equalizer.khzShort", ("value", (Hz / 1000.0).ToString("0.#", Strings.Culture)))
        : Hz.ToString(Strings.Culture);

    /// <summary>"Equalizer, 1 kHz": each slider's own name, so they can be told apart.</summary>
    public string AccessibleName => Strings.Format("settings.equalizer.band", ("band", Label));

    /// <summary>"+3 dB", "0 dB", "−3 dB".</summary>
    public string GainText => Strings.Format("settings.equalizer.db", ("value", ShortGainText));

    /// <summary>"+3", "0", "−3": above the slider.</summary>
    public string ShortGainText => Math.Round(Gain).ToString("+0;−0;0", Strings.Culture);

    partial void OnGainChanged(double value)
    {
        // Whole dB only, whatever the slider's drag lands on.
        var whole = Math.Clamp(Math.Round(value), Minimum, Maximum);
        if (whole != value)
            Gain = whole;
    }

    internal void Relabel()
    {
        OnPropertyChanged(nameof(Label));
        OnPropertyChanged(nameof(ShortLabel));
        OnPropertyChanged(nameof(AccessibleName));
        OnPropertyChanged(nameof(GainText));
    }
}
