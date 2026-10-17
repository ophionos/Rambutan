using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rambutan.App.Services;

/// <summary>Whether the app animates. <see cref="System"/> follows
/// the operating system's reduce-motion setting.</summary>
public enum MotionPreference { System, Reduced, Full }

/// <summary>How the Now Playing visualizer draws. <see cref="Radial"/> draws
/// around the artwork, and the band shows bars while it does. From
/// <see cref="Feedback"/> on, the animated presets, offered only in advanced mode.</summary>
public enum VisualizerStyle
{
    Bars, MirroredBars, Led, Line, Area, Radial, Oscilloscope, Vectorscope,
    Feedback, Plasma, Tunnel, Kaleidoscope,
}

/// <summary>Whether the visualizer changes style on its own, as Winamp's did.</summary>
public enum VisualizerCycle { Off, Random, TrackChange }

/// <summary>Where the Now Playing visualizer's colour comes from.</summary>
public enum VisualizerColorSource { Accent, Artwork, Custom }

/// <summary>Where the app's accent comes from: its own red, the playing
/// track's cover, or a colour of the user's.</summary>
public enum AccentSource { Default, Artwork, Custom }

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

    /// <summary>The Now Playing visualizer's style. Bars by default, as it drew
    /// before there was a choice. The meter over the player bar's artwork is
    /// always bars.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownStyle))]
    private VisualizerStyle _visualizerStyle = VisualizerStyle.Bars;

    [ObservableProperty] private VisualizerColorSource _visualizerColorSource = VisualizerColorSource.Accent;

    /// <summary>The colour for <see cref="VisualizerColorSource.Custom"/>, as
    /// #RRGGBB. Kept while another source is chosen, so switching back finds it.</summary>
    [ObservableProperty] private string _visualizerCustomColor = DefaultCustomColor;

    public const string DefaultCustomColor = "#4FC3F7";

    /// <summary>The app's accent. The default red unless the user chooses
    /// otherwise; the palette derived from it is <see cref="Core.Appearance.AccentPalette"/>.</summary>
    [ObservableProperty] private AccentSource _accentSource = AccentSource.Default;

    /// <summary>The colour for <see cref="AccentSource.Custom"/>, as #RRGGBB.
    /// Starts as the default red, so choosing Custom changes nothing until a
    /// colour is picked. Kept while another source is chosen.</summary>
    [ObservableProperty] private string _accentCustomColor = DefaultAccentColor;

    public const string DefaultAccentColor = "#FF4E45";

    /// <summary>The settings page's style choices, in its order.</summary>
    public static readonly VisualizerStyle[] StyleChoices =
    [
        VisualizerStyle.Bars, VisualizerStyle.MirroredBars, VisualizerStyle.Led, VisualizerStyle.Line,
        VisualizerStyle.Area, VisualizerStyle.Radial, VisualizerStyle.Oscilloscope, VisualizerStyle.Vectorscope,
    ];

    /// <summary>The animated presets, in the settings page's order after
    /// <see cref="StyleChoices"/>.</summary>
    public static readonly VisualizerStyle[] PresetChoices =
    [
        VisualizerStyle.Feedback, VisualizerStyle.Plasma, VisualizerStyle.Tunnel, VisualizerStyle.Kaleidoscope,
    ];

    /// <summary>Everything the settings page lists, the presets last.</summary>
    public static readonly VisualizerStyle[] AllStyleChoices = [.. StyleChoices, .. PresetChoices];

    public static bool IsPreset(VisualizerStyle style) => Array.IndexOf(PresetChoices, style) >= 0;

    /// <summary>The animated presets: they move all the time while anything
    /// plays and cost more of the graphics card than the basic styles, so they
    /// are offered only while this is on. Off by default. Reduced motion still
    /// stops them.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShownStyle))]
    private bool _visualizerAdvanced;

    /// <summary>Off by default: the visualizer stays on the chosen style.</summary>
    [ObservableProperty] private VisualizerCycle _visualizerCycle = VisualizerCycle.Off;

    /// <summary>How long random cycling stays on a style while it plays. The
    /// maintainer chose 30 seconds.</summary>
    public static readonly TimeSpan CycleInterval = TimeSpan.FromSeconds(30);

    /// <summary>What the chosen style draws as: itself, or bars when it is a
    /// preset and advanced mode is off. The choice itself is kept, so turning
    /// advanced mode back on finds it.</summary>
    public VisualizerStyle ShownStyle => Available(VisualizerStyle, VisualizerAdvanced);

    public static VisualizerStyle Available(VisualizerStyle style, bool advanced) =>
        IsPreset(style) && !advanced ? VisualizerStyle.Bars : style;

    /// <summary>The styles the picker offers, and so the ones cycling picks from.</summary>
    public static VisualizerStyle[] Offered(bool advanced) => advanced ? AllStyleChoices : StyleChoices;

    /// <summary>The style as persisted. Unknown words (hand-edited, or a style
    /// from a newer build) read as bars.</summary>
    public static VisualizerStyle ParseStyle(string? value) => value switch
    {
        "mirrored" => VisualizerStyle.MirroredBars,
        "led" => VisualizerStyle.Led,
        "line" => VisualizerStyle.Line,
        "area" => VisualizerStyle.Area,
        "radial" => VisualizerStyle.Radial,
        "oscilloscope" => VisualizerStyle.Oscilloscope,
        "vectorscope" => VisualizerStyle.Vectorscope,
        "feedback" => VisualizerStyle.Feedback,
        "plasma" => VisualizerStyle.Plasma,
        "tunnel" => VisualizerStyle.Tunnel,
        "kaleidoscope" => VisualizerStyle.Kaleidoscope,
        _ => VisualizerStyle.Bars,
    };

    public static string FormatStyle(VisualizerStyle value) => value switch
    {
        VisualizerStyle.MirroredBars => "mirrored",
        VisualizerStyle.Led => "led",
        VisualizerStyle.Line => "line",
        VisualizerStyle.Area => "area",
        VisualizerStyle.Radial => "radial",
        VisualizerStyle.Oscilloscope => "oscilloscope",
        VisualizerStyle.Vectorscope => "vectorscope",
        VisualizerStyle.Feedback => "feedback",
        VisualizerStyle.Plasma => "plasma",
        VisualizerStyle.Tunnel => "tunnel",
        VisualizerStyle.Kaleidoscope => "kaleidoscope",
        _ => "bars",
    };

    /// <summary>The cycle mode as persisted: "off", "random" or "track";
    /// unknown words read as off.</summary>
    public static VisualizerCycle ParseCycle(string? value) => value switch
    {
        "random" => VisualizerCycle.Random,
        "track" => VisualizerCycle.TrackChange,
        _ => VisualizerCycle.Off,
    };

    public static string FormatCycle(VisualizerCycle value) => value switch
    {
        VisualizerCycle.Random => "random",
        VisualizerCycle.TrackChange => "track",
        _ => "off",
    };

    /// <summary>The colour source as persisted: "accent", "artwork" or "custom";
    /// unknown words read as accent.</summary>
    public static VisualizerColorSource ParseColorSource(string? value) => value switch
    {
        "artwork" => VisualizerColorSource.Artwork,
        "custom" => VisualizerColorSource.Custom,
        _ => VisualizerColorSource.Accent,
    };

    public static string FormatColorSource(VisualizerColorSource value) => value switch
    {
        VisualizerColorSource.Artwork => "artwork",
        VisualizerColorSource.Custom => "custom",
        _ => "accent",
    };

    /// <summary>A saved custom colour as #RRGGBB, or <paramref name="fallback"/>
    /// when it isn't one. An alpha is dropped: the visualizer's colour and the
    /// accent are always solid.</summary>
    public static string ParseCustomColor(string? value, string fallback = DefaultCustomColor) =>
        CssHexColor.TryParse(CssHexColor.Normalize(value), out var color)
            ? CssHexColor.Format(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B))
            : fallback;

    /// <summary>The accent source as persisted: "default", "artwork" or
    /// "custom"; unknown words read as default.</summary>
    public static AccentSource ParseAccentSource(string? value) => value switch
    {
        "artwork" => AccentSource.Artwork,
        "custom" => AccentSource.Custom,
        _ => AccentSource.Default,
    };

    public static string FormatAccentSource(AccentSource value) => value switch
    {
        AccentSource.Artwork => "artwork",
        AccentSource.Custom => "custom",
        _ => "default",
    };

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
