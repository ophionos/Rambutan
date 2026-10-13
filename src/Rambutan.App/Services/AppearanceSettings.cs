using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rambutan.App.Services;

/// <summary>Whether the app animates. <see cref="System"/> follows
/// the operating system's reduce-motion setting.</summary>
public enum MotionPreference { System, Reduced, Full }

/// <summary>
/// Appearance state shared live between the shell and the settings page — the
/// same arrangement <see cref="Rambutan.Core.Settings.PlaybackSettings"/> has for
/// playback, but observable, because these take effect on the frame they change
/// rather than on the next track.
///
/// App-layer rather than Core: Core takes no package references, and this needs
/// CommunityToolkit's generator. Persistence mirrors into
/// <see cref="AppSettings"/> on save, like everything else.
/// </summary>
public sealed partial class AppearanceSettings : ObservableObject
{
    /// <summary>Tint the whole shell with the now-playing artwork (the
    /// page-background layer). Off restores the flat surfaces exactly — the
    /// translucent panel tokens composite to the old opaque values over the
    /// window background.</summary>
    [ObservableProperty] private bool _ambientBackground = true;

    /// <summary>The small meter over the player bar's artwork while a track
    /// plays. Reduced motion hides it whatever this says.</summary>
    [ObservableProperty] private bool _artworkMeter = true;

    /// <summary>The most times a second the meter and the Now Playing visualizer
    /// redraw; 0 follows the display. One of <see cref="FrameRateChoices"/>.</summary>
    [ObservableProperty] private int _visualizerFrameRate = DefaultFrameRate;

    /// <summary>Each redraw makes the compositor present a frame, and that frame
    /// is the visualizer's GPU cost (at 100 Hz the meter drops from about 3.7% to
    /// 1.2%). The maintainer chose 30 as enough for the meter and Now Playing.</summary>
    public const int DefaultFrameRate = 30;

    /// <summary>The settings page's choices, in its order.</summary>
    public static readonly int[] FrameRateChoices = [30, 60, 0];

    /// <summary>A saved rate that isn't a choice (hand-edited, or from a newer
    /// build) reads as the default.</summary>
    public static int ParseFrameRate(int value) =>
        Array.IndexOf(FrameRateChoices, value) >= 0 ? value : DefaultFrameRate;

    /// <summary>The user's choice; System by default, so an OS-wide setting is
    /// honoured without anyone visiting Settings.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReduceMotion))]
    private MotionPreference _motion = MotionPreference.System;

    /// <summary>What the OS asks for, as <see cref="SystemMotion"/> last read it.
    /// False when it can't be read: the OS setting is an opt-in to less motion.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ReduceMotion))]
    private bool _systemReducesMotion;

    /// <summary>The one switch everything that moves reads: the backdrop fade,
    /// toast entrances, the skeleton pulse, the visualizer, card hover growth.</summary>
    public bool ReduceMotion => Resolve(Motion, SystemReducesMotion);

    public static bool Resolve(MotionPreference preference, bool systemReduces) => preference switch
    {
        MotionPreference.Reduced => true,
        MotionPreference.Full => false,
        _ => systemReduces,
    };

    /// <summary><paramref name="full"/>, or zero when motion is reduced: every
    /// timed transition goes through this, so "reduced" means none at all rather
    /// than a shorter one.</summary>
    public static TimeSpan Duration(TimeSpan full, bool reduceMotion) =>
        reduceMotion ? TimeSpan.Zero : full;

    /// <summary>The setting as persisted: "system", "reduced" or "full". Unknown
    /// words read as system, so a file from a newer build degrades to the default.</summary>
    public static MotionPreference ParseMotion(string? value) => value switch
    {
        "reduced" => MotionPreference.Reduced,
        "full" => MotionPreference.Full,
        _ => MotionPreference.System,
    };

    public static string FormatMotion(MotionPreference value) => value switch
    {
        MotionPreference.Reduced => "reduced",
        MotionPreference.Full => "full",
        _ => "system",
    };
}
