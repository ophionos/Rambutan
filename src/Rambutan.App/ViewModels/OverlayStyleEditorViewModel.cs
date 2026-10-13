using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The OBS overlay's style editor. Every edit reaches open overlays at
/// once (<see cref="ObsOverlayService.PreviewStyle"/>) and the native preview
/// beside the controls, but nothing is kept until Save writes the versioned
/// style file. Closing without saving puts the saved style back.
/// </summary>
public sealed partial class OverlayStyleEditorViewModel : ViewModelBase, IDisposable
{
    /// <summary>The browser source's size the setup text asks for (600 × 200).</summary>
    public const double SourceWidth = 600, SourceHeight = 200;

    /// <summary>Edits to one field closer together than this are one undo step:
    /// one slider drag, one burst of typing.</summary>
    public static readonly TimeSpan CoalesceWindow = TimeSpan.FromSeconds(1);

    public const double ScaleStep = 0.05;

    private readonly ObsOverlayService _overlay;
    private readonly AppearanceSettings? _appearance;
    private readonly Func<DateTimeOffset> _now;
    private readonly TrackInfo? _track;
    private readonly List<OverlayConfig> _undo = [];
    private string? _lastEditKey;
    private DateTimeOffset _lastEditAt;
    private bool _loading;
    private bool _closed;

    // What is shown.
    [ObservableProperty] private bool _showArtwork;
    [ObservableProperty] private bool _showAlbum;
    [ObservableProperty] private bool _showProgress;
    [ObservableProperty] private bool _showTimes;
    [ObservableProperty] private bool _showVisualizer;

    /// <summary>0: a "Nothing playing" card; 1: nothing (see <see cref="OverlayIdle"/>).</summary>
    [ObservableProperty] private int _idleIndex;

    /// <summary>0: left; 1: right (see <see cref="OverlayAlign"/>).</summary>
    [ObservableProperty] private int _alignIndex;

    [ObservableProperty] private double _scale;
    [ObservableProperty] private double _cornerRadius;
    [ObservableProperty] private double _bars;
    [ObservableProperty] private bool _peaks;

    /// <summary>The preview shows the idle state instead of a track.</summary>
    [ObservableProperty] private bool _previewIdle;

    [ObservableProperty] private string? _error;

    /// <param name="overlay">The overlay whose style is edited.</param>
    /// <param name="appearance">For reduced motion: the preview's bars stand still.</param>
    /// <param name="now">The clock that groups edits into undo steps; tests pass their own.</param>
    public OverlayStyleEditorViewModel(
        ObsOverlayService overlay, AppearanceSettings? appearance = null, Func<DateTimeOffset>? now = null)
    {
        _overlay = overlay;
        _appearance = appearance;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _track = overlay.NowPlaying;
        Saved = overlay.SavedStyle;
        Current = overlay.Style;

        TextColor = new ColorField("settings.overlay.editor.color.text", this);
        SecondaryTextColor = new ColorField("settings.overlay.editor.color.secondaryText", this);
        AccentColor = new ColorField("settings.overlay.editor.color.accent", this);
        BackgroundColor = new ColorField("settings.overlay.editor.color.background", this);
        Colors = [TextColor, SecondaryTextColor, AccentColor, BackgroundColor];

        Load(Current);
        if (_appearance is not null)
            _appearance.PropertyChanged += OnAppearanceChanged;
    }

    /// <summary>The style as edited; open overlays show it.</summary>
    public OverlayConfig Current { get; private set; }

    /// <summary>The style that is kept (the file's, or the default).</summary>
    public OverlayConfig Saved { get; private set; }

    public ColorField TextColor { get; }
    public ColorField SecondaryTextColor { get; }
    public ColorField AccentColor { get; }
    public ColorField BackgroundColor { get; }
    public IReadOnlyList<ColorField> Colors { get; }

    public bool IsDirty => Current != Saved;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRevert => IsDirty;
    public bool CanUseDefault => Current != OverlayConfig.Default;
    public bool IsOverlayOn => _overlay.Status == OverlayStatus.Listening;

    /// <summary>What the editor says about where the edits go.</summary>
    public string StatusText => Strings.Get(
        IsOverlayOn
            ? IsDirty ? "settings.overlay.editor.status.liveUnsaved" : "settings.overlay.editor.status.liveSaved"
            : IsDirty ? "settings.overlay.editor.status.offUnsaved" : "settings.overlay.editor.status.offSaved");

    /// <summary>The saved file couldn't be used (a newer version's, or unreadable),
    /// so the editor starts from the default; saving replaces that file.</summary>
    public string? SavedStyleWarning => _overlay.StyleProblem == OverlayConfigProblem.None
        ? null
        : OverlaySettingsViewModel.StyleText(false, _overlay.StyleProblem) + " " +
          Strings.Get("settings.overlay.editor.replacesFile");

    public string ScaleText => Strings.Format("settings.overlay.editor.percent", ("value", (int)Math.Round(Scale * 100)));
    public string CornerRadiusText => Strings.Format("settings.overlay.editor.pixels", ("value", (int)Math.Round(CornerRadius)));
    public string BarsText => ((int)Math.Round(Bars)).ToString(Strings.Culture);

    public double MinScale => OverlayConfig.MinScale;
    public double MaxScale => OverlayConfig.MaxScale;
    public double MaxCornerRadius => OverlayConfig.MaxCornerRadius;
    public double MinBars => OverlayVisualizer.MinBars;
    public double MaxBars => OverlayVisualizer.MaxBars;

    // ---- Edits ----

    partial void OnShowArtworkChanged(bool value) => Edited(nameof(ShowArtwork));
    partial void OnShowAlbumChanged(bool value) => Edited(nameof(ShowAlbum));
    partial void OnShowProgressChanged(bool value) => Edited(nameof(ShowProgress));
    partial void OnShowTimesChanged(bool value) => Edited(nameof(ShowTimes));
    partial void OnShowVisualizerChanged(bool value) => Edited(nameof(ShowVisualizer));
    partial void OnIdleIndexChanged(int value) => Edited(nameof(IdleIndex));
    partial void OnAlignIndexChanged(int value) => Edited(nameof(AlignIndex));
    partial void OnScaleChanged(double value) => Edited(nameof(Scale));
    partial void OnCornerRadiusChanged(double value) => Edited(nameof(CornerRadius));
    partial void OnBarsChanged(double value) => Edited(nameof(Bars));
    partial void OnPeaksChanged(bool value) => Edited(nameof(Peaks));
    partial void OnPreviewIdleChanged(bool value) => RaisePreviewChanged();

    private void Edited(string field)
    {
        if (_loading)
            return;
        Edit(field, Build());
    }

    /// <summary>The style the fields describe, kept to what the file can hold:
    /// values clamped to their ranges and rounded to the controls' steps, so the
    /// default edited back by hand is the default again.</summary>
    internal OverlayConfig Build() => new()
    {
        Show = new OverlayShow
        {
            Artwork = ShowArtwork,
            Album = ShowAlbum,
            Progress = ShowProgress,
            Times = ShowTimes,
            Visualizer = ShowVisualizer,
        },
        WhenIdle = IdleIndex == 1 ? OverlayIdle.Hide : OverlayIdle.Message,
        Align = AlignIndex == 1 ? OverlayAlign.Right : OverlayAlign.Left,
        Scale = Math.Round(Math.Round(Math.Clamp(Scale, OverlayConfig.MinScale, OverlayConfig.MaxScale) / ScaleStep) * ScaleStep, 2),
        CornerRadius = Math.Round(Math.Clamp(CornerRadius, 0, OverlayConfig.MaxCornerRadius)),
        Colors = new OverlayColors
        {
            Text = TextColor.Value,
            SecondaryText = SecondaryTextColor.Value,
            Accent = AccentColor.Value,
            Background = BackgroundColor.Value,
        },
        Visualizer = new OverlayVisualizer
        {
            Bars = (int)Math.Round(Math.Clamp(Bars, OverlayVisualizer.MinBars, OverlayVisualizer.MaxBars)),
            Peaks = Peaks,
        },
    };

    /// <summary>Makes <paramref name="config"/> the edited style. A new undo step
    /// starts unless the same field changed within <see cref="CoalesceWindow"/>;
    /// a null <paramref name="field"/> always starts one.</summary>
    private void Edit(string? field, OverlayConfig config)
    {
        if (config == Current)
            return;
        var now = _now();
        if (field is null || field != _lastEditKey || now - _lastEditAt > CoalesceWindow)
            _undo.Add(Current);
        _lastEditKey = field;
        _lastEditAt = now;
        Show(config);
    }

    [RelayCommand]
    private void Undo()
    {
        if (_undo.Count == 0)
            return;
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _lastEditKey = null;
        Show(previous);
        Load(previous);
    }

    /// <summary>Back to the saved style; one undo step, like any edit.</summary>
    [RelayCommand]
    private void RevertToSaved() => Replace(Saved);

    /// <summary>The default style, as an edit: Save keeps it.</summary>
    [RelayCommand]
    private void UseDefault() => Replace(OverlayConfig.Default);

    private void Replace(OverlayConfig config)
    {
        Edit(null, config);
        _lastEditKey = null;
        Load(config);
    }

    /// <summary>Writes the style file. Only with an edit to keep: a saved file
    /// this version couldn't read (one from a newer version) stays until the user
    /// saves an edit over it.</summary>
    [RelayCommand(CanExecute = nameof(IsDirty))]
    private void Save() => TrySave();

    public bool TrySave()
    {
        // Nothing to keep: writing now would replace (or delete) a file this
        // version couldn't read, which must stay until an edit is saved over it.
        if (!IsDirty)
            return true;
        try
        {
            _overlay.SaveStyle(Current);
            Saved = _overlay.SavedStyle;
            Error = null;
            RaiseStateChanged();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = Strings.Get("settings.overlay.style.ioFailed");
            return false;
        }
    }

    /// <summary>The editor is closing: unsaved edits leave the overlays, which
    /// show the saved style again.</summary>
    public void Close()
    {
        if (_closed)
            return;
        _closed = true;
        if (_appearance is not null)
            _appearance.PropertyChanged -= OnAppearanceChanged;
        if (_overlay.Style != _overlay.SavedStyle)
            _overlay.PreviewStyle(_overlay.SavedStyle);
    }

    public void Dispose() => Close();

    private void Show(OverlayConfig config)
    {
        Current = config;
        _overlay.PreviewStyle(config);
        RaiseStateChanged();
    }

    /// <summary>Puts <paramref name="config"/>'s values into the fields without
    /// counting it as an edit.</summary>
    private void Load(OverlayConfig config)
    {
        _loading = true;
        try
        {
            ShowArtwork = config.Show.Artwork;
            ShowAlbum = config.Show.Album;
            ShowProgress = config.Show.Progress;
            ShowTimes = config.Show.Times;
            ShowVisualizer = config.Show.Visualizer;
            IdleIndex = config.WhenIdle == OverlayIdle.Hide ? 1 : 0;
            AlignIndex = config.Align == OverlayAlign.Right ? 1 : 0;
            Scale = config.Scale;
            CornerRadius = config.CornerRadius;
            Bars = config.Visualizer.Bars;
            Peaks = config.Visualizer.Peaks;
            TextColor.Load(config.Colors.Text);
            SecondaryTextColor.Load(config.Colors.SecondaryText);
            AccentColor.Load(config.Colors.Accent);
            BackgroundColor.Load(config.Colors.Background);
        }
        finally
        {
            _loading = false;
        }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanUndo));
        OnPropertyChanged(nameof(CanRevert));
        OnPropertyChanged(nameof(CanUseDefault));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(SavedStyleWarning));
        SaveCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(ScaleText));
        OnPropertyChanged(nameof(CornerRadiusText));
        OnPropertyChanged(nameof(BarsText));
        RaisePreviewChanged();
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppearanceSettings.ReduceMotion))
            RaisePreviewChanged();
    }

    // ---- The preview: the overlay page's layout (web/overlay App.svelte), drawn natively ----

    private bool ReduceMotion => _appearance?.ReduceMotion ?? false;

    /// <summary>The page scales everything; the preview lays the card out at
    /// scale 1 and scales it as a whole.</summary>
    public ITransform PreviewTransform => new ScaleTransform(Current.Scale, Current.Scale);

    /// <summary>The space the card has, before scaling: the page's 8 px padding
    /// on each side, then at most 560 px.</summary>
    public double CardWidth => Math.Min((SourceWidth - 16 * Current.Scale) / Current.Scale, 560);

    /// <summary>The browser source's width before scaling; the scene clips the
    /// card at its edge, as OBS does.</summary>
    public double InnerWidth => SourceWidth / Current.Scale;

    public CornerRadius CardCornerRadius => new(Current.CornerRadius);
    public CornerRadius CoverCornerRadius => new(Current.CornerRadius * 0.6);
    public HorizontalAlignment CardAlignment => Current.Align == OverlayAlign.Right ? HorizontalAlignment.Right : HorizontalAlignment.Left;
    public TextAlignment TextAlign => Current.Align == OverlayAlign.Right ? TextAlignment.Right : TextAlignment.Left;
    public int CoverColumn => Current.Align == OverlayAlign.Right ? 2 : 0;
    public Thickness CoverMargin => Current.Align == OverlayAlign.Right ? new(14, 0, 0, 0) : new(0, 0, 14, 0);

    public IBrush TextBrush => Brush(Current.Colors.Text, OverlayConfig.Default.Colors.Text);
    public IBrush SecondaryBrush => Brush(Current.Colors.SecondaryText, OverlayConfig.Default.Colors.SecondaryText);
    public IBrush AccentBrush => Brush(Current.Colors.Accent, OverlayConfig.Default.Colors.Accent);
    public IBrush BackgroundBrush => Brush(Current.Colors.Background, OverlayConfig.Default.Colors.Background);
    public Color AccentColorValue => CssHexColor.Parse(Current.Colors.Accent, Avalonia.Media.Colors.White);
    public Color SecondaryColorValue => CssHexColor.Parse(Current.Colors.SecondaryText, Avalonia.Media.Colors.White);

    /// <summary>The page's tints (an empty cover, the progress track): the
    /// secondary colour at a fifth of its strength.</summary>
    public IBrush TintBrush => new ImmutableSolidColorBrush(SecondaryColorValue, 0.2);

    private static IBrush Brush(string hex, string fallback) =>
        new ImmutableSolidColorBrush(CssHexColor.Parse(hex, CssHexColor.Parse(fallback, Avalonia.Media.Colors.White)));

    public bool ShowsTrack => !PreviewIdle;
    public bool ShowsIdleCard => PreviewIdle && Current.WhenIdle == OverlayIdle.Message;

    /// <summary>"Hide when idle": the source is empty, which the preview says, since
    /// an empty frame looks like a fault.</summary>
    public bool ShowsHiddenNote => PreviewIdle && Current.WhenIdle == OverlayIdle.Hide;

    public bool ShowsCover => Current.Show.Artwork;
    public string? CoverUrl => _track?.ThumbnailUrl;
    public bool HasCover => CoverUrl is not null;
    public string PreviewTitle => _track?.Title ?? Strings.Get("settings.overlay.editor.sample.title");
    public string PreviewArtists => _track is { } t
        ? string.Join(", ", t.Artists.Select(a => a.Name))
        : Strings.Get("settings.overlay.editor.sample.artist");
    public string PreviewAlbum => _track is { } t ? t.Album ?? "" : Strings.Get("settings.overlay.editor.sample.album");
    public bool ShowsAlbum => Current.Show.Album && PreviewAlbum.Length > 0;

    /// <summary>Whose details the preview shows.</summary>
    public string PreviewSource => Strings.Get(_track is null
        ? "settings.overlay.editor.preview.sample"
        : "settings.overlay.editor.preview.playing");

    private TimeSpan PreviewDuration => _track?.Duration is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(214);

    /// <summary>A still position, two-fifths in.</summary>
    public const double PreviewFraction = 0.4;

    public string PreviewElapsed => Clock(PreviewDuration * PreviewFraction);
    public string PreviewTotal => Clock(PreviewDuration);
    public bool ShowsProgress => Current.Show.Progress;
    public bool ShowsTimes => Current.Show.Progress && Current.Show.Times;

    private static string Clock(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss", Strings.Culture) : time.ToString(@"m\:ss", Strings.Culture);

    public bool ShowsBars => Current.Show.Visualizer;

    /// <summary>Moving bars from the analyzer: a track plays and motion isn't reduced.</summary>
    public bool LiveBars => ShowsBars && !ReduceMotion && _overlay.NowPlaying is not null;

    /// <summary>Still bars: a sample, when nothing plays or motion is reduced.</summary>
    public bool StillBars => ShowsBars && !LiveBars;

    public int PreviewBars => Current.Visualizer.Bars;
    public bool PreviewPeaks => Current.Visualizer.Peaks;

    public string? MotionNote => ShowsTrack && ShowsBars && ReduceMotion
        ? Strings.Get("settings.overlay.editor.preview.still")
        : null;

    private static readonly string[] PreviewProperties =
    [
        nameof(PreviewTransform), nameof(CardWidth), nameof(InnerWidth), nameof(CardCornerRadius),
        nameof(CoverCornerRadius), nameof(CardAlignment), nameof(TextAlign), nameof(CoverColumn), nameof(CoverMargin),
        nameof(TextBrush), nameof(SecondaryBrush), nameof(AccentBrush), nameof(BackgroundBrush),
        nameof(AccentColorValue), nameof(SecondaryColorValue), nameof(TintBrush),
        nameof(ShowsTrack), nameof(ShowsIdleCard), nameof(ShowsHiddenNote), nameof(ShowsCover),
        nameof(ShowsAlbum), nameof(ShowsProgress), nameof(ShowsTimes),
        nameof(ShowsBars), nameof(LiveBars), nameof(StillBars), nameof(PreviewBars), nameof(PreviewPeaks),
        nameof(MotionNote),
    ];

    private void RaisePreviewChanged()
    {
        foreach (var name in PreviewProperties)
            OnPropertyChanged(name);
    }

    /// <summary>One of the style's colours: a hex text field, the last valid
    /// value (what the style uses), and why the text isn't one.</summary>
    public sealed partial class ColorField : ObservableObject
    {
        private readonly OverlayStyleEditorViewModel _owner;
        private readonly string _labelKey;
        private bool _loading;

        [ObservableProperty] private string _hex = "";
        [ObservableProperty] private string? _error;

        internal ColorField(string labelKey, OverlayStyleEditorViewModel owner)
        {
            _labelKey = labelKey;
            _owner = owner;
        }

        /// <summary>The colour the style uses: the last valid text, normalised.</summary>
        public string Value { get; private set; } = "#FFFFFF";

        public string Label => Strings.Get(_labelKey);

        /// <summary>For screen readers: which colour, and that it is hex.</summary>
        public string AccessibleName => Strings.Format("settings.overlay.editor.color.hexName", ("color", Label));

        /// <summary>For screen readers: the swatch button opens the colour picker.</summary>
        public string PickerName => Strings.Format("settings.overlay.editor.color.pick", ("color", Label));

        public IBrush Swatch => new ImmutableSolidColorBrush(Color);

        /// <summary>The colour picker's side of <see cref="Hex"/>. A colour the
        /// text already names is left alone, so #FFF isn't rewritten as #FFFFFF
        /// just because the picker opened.</summary>
        public Color Color
        {
            get => CssHexColor.Parse(Value, Avalonia.Media.Colors.White);
            set
            {
                if (value == Color)
                    return;
                Hex = CssHexColor.Format(value);
            }
        }

        partial void OnHexChanged(string value)
        {
            if (_loading)
                return;
            if (CssHexColor.Normalize(value) is not { } normalized)
            {
                Error = Strings.Get("settings.overlay.editor.color.invalid");
                return;
            }
            Error = null;
            Value = normalized;
            OnPropertyChanged(nameof(Color));
            OnPropertyChanged(nameof(Swatch));
            _owner.Edited(_labelKey);
        }

        internal void Load(string value)
        {
            _loading = true;
            try
            {
                Value = value;
                Hex = value;
                Error = null;
                OnPropertyChanged(nameof(Color));
                OnPropertyChanged(nameof(Swatch));
            }
            finally
            {
                _loading = false;
            }
        }
    }
}
