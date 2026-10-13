using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Converters;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>What the lyrics pane is showing. Every state but <see cref="Hidden"/>
/// has its own visual: the pane is never blank.</summary>
public enum LyricsState
{
    /// <summary>Nothing is playing, so there's no pane.</summary>
    Hidden,
    Loading,
    Synced,
    Unsynced,
    Instrumental,
    /// <summary>Every source answered and none had the track.</summary>
    None,
    /// <summary>No source could be asked (offline, timed out): retryable.</summary>
    Unavailable,
    /// <summary>No lyrics of the user's own or cached, and looking up online is
    /// off: the pane offers to switch it on.</summary>
    NotLookedUp,
}

/// <summary>The user's own lyrics, as the pane edits them. Save and Remove
/// throw on I/O failure; Reload looks the track up without joining a fetch in flight.</summary>
public sealed record CustomLyricsAccess(
    Func<string, CustomLyrics?> Get,
    Action<string, CustomLyrics> Save,
    Action<string> Remove,
    Func<LyricsQuery, CancellationToken, Task<LyricsLookup>> Reload)
{
    public static CustomLyricsAccess For(LyricsService service) =>
        new(service.GetCustom, service.SaveCustom, service.RemoveCustom, service.ReloadAsync);
}

/// <summary>
/// The Now Playing lyrics pane. Follows the
/// coordinator: a track change cancels the lookup in flight and starts one for
/// the new track, and each progress report moves the active line, found by binary
/// search. No Core port: the pane is the only consumer, so App wires the
/// coordinator's events straight here.
/// </summary>
public sealed partial class LyricsViewModel : ViewModelBase
{
    private readonly Func<LyricsQuery, CancellationToken, Task<LyricsLookup>>? _fetch;
    private readonly Func<TimeSpan, Task>? _seek;
    private readonly Action<bool>? _applyLookUpOnline;
    private readonly CustomLyricsAccess? _custom;

    private CancellationTokenSource? _loading;
    private TrackInfo? _track;
    private TimeSpan _position;
    private TimeSpan[] _starts = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading), nameof(IsSynced), nameof(IsUnsynced), nameof(HasLyrics),
        nameof(IsInstrumental), nameof(IsNone), nameof(IsUnavailable), nameof(IsNotLookedUp), nameof(IsShown))]
    private LyricsState _state;

    [ObservableProperty] private IReadOnlyList<LyricLineViewModel> _lines = [];

    /// <summary>The unsynced lyrics as one block, for a selectable text view.</summary>
    [ObservableProperty] private string _plainText = "";

    /// <summary>The line playing now; -1 before the first line or when unsynced.</summary>
    [ObservableProperty] private int _activeLineIndex = -1;

    /// <summary>"Lyrics from LRCLIB", "Your lyrics": where they came from, in the pane's footer.</summary>
    [ObservableProperty] private string _sourceLabel = "";

    /// <summary>Set in <see cref="LyricsState.Unavailable"/>, rendered by the shared ErrorState template.</summary>
    [ObservableProperty] private LoadError? _loadError;

    public bool IsShown => State is not LyricsState.Hidden;
    public bool IsLoading => State is LyricsState.Loading;
    public bool IsSynced => State is LyricsState.Synced;
    public bool IsUnsynced => State is LyricsState.Unsynced;
    public bool HasLyrics => State is LyricsState.Synced or LyricsState.Unsynced;
    public bool IsInstrumental => State is LyricsState.Instrumental;
    public bool IsNone => State is LyricsState.None;
    public bool IsUnavailable => State is LyricsState.Unavailable;
    public bool IsNotLookedUp => State is LyricsState.NotLookedUp;

    /// <summary>The lyrics on screen are the user's own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOffsetShown))]
    private bool _isCustom;

    /// <summary>The timing correction on the user's own lyrics; positive shows them sooner.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OffsetLabel), nameof(IsOffsetSet))]
    private int _customOffsetMs;

    /// <summary>Why the last timing change couldn't be saved; null when it was.</summary>
    [ObservableProperty] private string? _customError;

    /// <summary>The timing nudges show for the user's own synced lyrics: the
    /// offset lives with them (CustomLyricsStore).</summary>
    public bool IsOffsetShown => IsCustom && IsSynced;

    public bool IsOffsetSet => CustomOffsetMs != 0;

    /// <summary>"0.3 s earlier", "In time".</summary>
    public string OffsetLabel => CustomOffsetMs switch
    {
        > 0 => Strings.Format("lyrics.offset.earlier", ("amount", Seconds(CustomOffsetMs))),
        < 0 => Strings.Format("lyrics.offset.later", ("amount", Seconds(-CustomOffsetMs))),
        _ => Strings.Get("lyrics.offset.inTime"),
    };

    /// <summary>Lyrics of the user's own can be added or edited: there is a track
    /// and somewhere to keep them.</summary>
    public bool CanEditLyrics => _custom is not null && _track is not null;

    /// <summary>Shows the editor; set by the window, which owns dialogs.</summary>
    public Func<LyricsEditorViewModel, Task>? OpenEditor { get; set; }

    public const int SmallNudgeMs = 100, LargeNudgeMs = 500;

    /// <summary>The nudge sizes as shown: "0.1 s", "0.5 s".</summary>
    public string SmallStep => Seconds(SmallNudgeMs);
    public string LargeStep => Seconds(LargeNudgeMs);

    /// <summary>Kept within a minute either way: past that it is the wrong lyrics, not drift.</summary>
    public const int MaxOffsetMs = 60_000;

    /// <summary>
    /// The position between progress reports, for the karaoke sweep to read each
    /// frame. Fed from the same coordinator events as the line
    /// highlight, so the two can't disagree about which track they follow.
    /// </summary>
    public PlaybackClock Clock { get; } = new();

    /// <summary>Raised after a line is clicked, so the view can resume following playback.</summary>
    public event Action? LineSought;

    // Design-time: a synced verse, second line playing.
    public LyricsViewModel()
    {
        var doc = new LyricsDocument
        {
            Source = LrcLibProvider.SourceName,
            IsSynced = true,
            Lines =
            [
                new("Streetlights blinking out of time", TimeSpan.FromSeconds(4)),
                new("夜の街に 光が揺れる", TimeSpan.FromSeconds(8)),
                new("I kept your number, never dialled a line", TimeSpan.FromSeconds(12)),
                new("Static on the radio 📻", TimeSpan.FromSeconds(16)),
            ],
        };
        Apply(new LyricsLookup(LyricsStatus.Found, doc));
        OnPosition(TimeSpan.FromSeconds(9));
    }

    /// <summary>For tests and composition: how to look lyrics up and how to seek.</summary>
    /// <param name="applyLookUpOnline">Applies and remembers a change of
    /// <see cref="LookUpOnline"/>.</param>
    public LyricsViewModel(
        Func<LyricsQuery, CancellationToken, Task<LyricsLookup>> fetch,
        Func<TimeSpan, Task> seek,
        Action<bool>? applyLookUpOnline = null,
        CustomLyricsAccess? custom = null)
    {
        _fetch = fetch;
        _seek = seek;
        _applyLookUpOnline = applyLookUpOnline;
        _custom = custom;
        // Re-applying the last answer re-reads the source label, the error and
        // the gap lines' names in the new language.
        Strings.WhenChanged(this, static vm =>
        {
            if (vm._applied is { } last)
                vm.Apply(last);
        });
    }

    public LyricsViewModel(
        LyricsService service, PlaybackCoordinator coordinator, QueueModel queue, bool lookUpOnline, Action<bool> remember)
        // A line picked before anything is loaded (a restored queue) starts the
        // queue's track from that line; a seek would do nothing.
        : this(service.GetAsync, position => coordinator.NowPlaying is null && queue.CurrentIndex >= 0
            ? coordinator.JumpToAsync(queue.CurrentIndex, position)
            : coordinator.SeekAsync(position), online =>
        {
            service.LookUpOnline = online;
            remember(online);
        }, CustomLyricsAccess.For(service))
    {
        service.LookUpOnline = lookUpOnline;
        _lookUpOnline = lookUpOnline;
        // Coordinator events fire under its lock and on engine threads; marshal
        // like every other subscriber.
        coordinator.NowPlayingChanged += track => Dispatcher.UIThread.Post(() => OnLoadedTrackChanged(track, queue.Current));
        coordinator.ProgressChanged += progress => Dispatcher.UIThread.Post(() => OnPosition(progress.Position));
        coordinator.StateChanged += state => Dispatcher.UIThread.Post(() => OnPlaybackState(state));
        queue.Changed += () => Dispatcher.UIThread.Post(() => OnQueueChanged(queue.Current));
        OnLoadedTrackChanged(coordinator.NowPlaying, queue.Current);
        OnPlaybackState(coordinator.State);
    }

    /// <summary>
    /// The loaded track changed. The pane follows it, else the queue's current
    /// track, as the player bar does: a queue restored at startup isn't loaded
    /// until Play.
    /// </summary>
    public void OnLoadedTrackChanged(TrackInfo? loaded, TrackInfo? queued)
    {
        _loaded = loaded;
        OnTrackChanged(loaded ?? queued);
    }

    /// <summary>The queue changed. With nothing loaded, its current track is the
    /// one the pane shows; an edit that keeps that track asks nothing again.</summary>
    public void OnQueueChanged(TrackInfo? queued)
    {
        if (_loaded is null && queued?.VideoId != _track?.VideoId)
            OnTrackChanged(queued);
    }

    private TrackInfo? _loaded;

    /// <summary>
    /// The pane is switched on (Now Playing's lyrics toggle). Off, nothing is
    /// looked up: a hidden pane has no reason to send track names to a lyrics
    /// source. Switching it on looks up the track already playing.
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetProperty(ref _isEnabled, value))
                return;

            if (value)
            {
                if (_track is { } track)
                    _ = LoadAsync(track);
            }
            else
            {
                CancelLoad();
                Clear();
                State = LyricsState.Hidden;
            }
        }
    }

    private bool _isEnabled = true;

    /// <summary>
    /// Whether tracks may be looked up on LRCLIB, shared with
    /// Settings. Switching it on looks the playing track up again if the pane is
    /// waiting on it; switching it off keeps what is on screen, since nothing more
    /// is sent for it.
    /// </summary>
    public bool LookUpOnline
    {
        get => _lookUpOnline;
        set
        {
            if (!SetProperty(ref _lookUpOnline, value))
                return;

            _applyLookUpOnline?.Invoke(value);
            if (value && State is LyricsState.NotLookedUp && _track is { } track)
                _ = LoadAsync(track);
        }
    }

    private bool _lookUpOnline = true;

    [RelayCommand]
    private void TurnOnLookUp()
    {
        LookUpOnline = true;
        // Already on (switched in Settings since this track was answered): the
        // setter had nothing to change, so ask again here.
        if (State is LyricsState.NotLookedUp && _track is { } track)
            _ = LoadAsync(track);
    }

    /// <summary>A new track (or none): drop the old lyrics and look the new ones up.</summary>
    public void OnTrackChanged(TrackInfo? track)
    {
        if (track is not null && _track is not null && track.VideoId == _track.VideoId &&
            State is not (LyricsState.Hidden or LyricsState.Unavailable or LyricsState.NotLookedUp))
        {
            // The same track again (repeat-one, a re-announcement): nothing changes.
            _track = track;
            return;
        }

        CancelLoad();
        _track = track;
        OnPropertyChanged(nameof(CanEditLyrics));
        _position = TimeSpan.Zero;
        Clock.Snap(TimeSpan.Zero);
        Clear();

        if (track is null || !IsEnabled)
        {
            State = LyricsState.Hidden;
            return;
        }

        _ = LoadAsync(track);
    }

    private void CancelLoad()
    {
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = null;
    }

    /// <summary>A progress report: moves the active line when a synced line boundary passes.</summary>
    public void OnPosition(TimeSpan position)
    {
        _position = position;
        Clock.Report(position);
        if (State is LyricsState.Synced)
            SetActive(LyricTiming.FindActiveLine(_starts, position));
    }

    /// <summary>Playback started or stopped advancing: the clock runs only while playing.</summary>
    public void OnPlaybackState(PlaybackState state) => Clock.SetRunning(state is PlaybackState.Playing);

    [RelayCommand]
    private void Retry()
    {
        if (_track is { } track && State is LyricsState.Unavailable)
            _ = LoadAsync(track);
    }

    /// <summary>Seek-to-line. Synced lyrics only: unsynced starts are filler.</summary>
    [RelayCommand]
    private async Task SeekToLine(LyricLineViewModel? line)
    {
        if (line is null || State is not LyricsState.Synced || _seek is null)
            return;

        // Move the highlight now rather than on the next progress report, so the
        // click lands on the line the listener picked.
        _position = line.Start;
        Clock.Snap(line.Start);
        SetActive(line.Index);
        LineSought?.Invoke();
        await _seek(line.Start);
    }

    /// <summary>
    /// Opens the editor on the user's own lyrics for the track, or on the lyrics
    /// found online (written out as LRC) as a starting point, or empty. A save or
    /// removal reloads the pane.
    /// </summary>
    [RelayCommand]
    private async Task EditLyrics()
    {
        if (_custom is null || _track is not { } track || OpenEditor is null)
            return;

        var videoId = track.VideoId;
        var raw = _custom.Get(videoId);
        var text = raw?.Text
                   ?? (_applied?.Document is { Lines.Count: > 0 } found ? LrcWriter.Write(found) : "");
        var editor = new LyricsEditorViewModel(
            track.Title,
            text,
            canRemove: raw is not null,
            save: edited => _custom.Save(videoId, new CustomLyrics(edited, raw?.OffsetMs ?? 0)),
            remove: () => _custom.Remove(videoId));

        await OpenEditor(editor);

        // The track may have moved on while the editor was open: the lyrics were
        // saved for the one it was opened on, and the pane follows the new one.
        if (editor.Outcome is not LyricsEditorOutcome.Cancelled && _track is { } now && now.VideoId == videoId)
            await ReloadAsync(now);
    }

    [RelayCommand] private Task ShowEarlier() => NudgeAsync(SmallNudgeMs);
    [RelayCommand] private Task ShowMuchEarlier() => NudgeAsync(LargeNudgeMs);
    [RelayCommand] private Task ShowLater() => NudgeAsync(-SmallNudgeMs);
    [RelayCommand] private Task ShowMuchLater() => NudgeAsync(-LargeNudgeMs);
    [RelayCommand] private Task ResetOffset() => NudgeAsync(-CustomOffsetMs);

    /// <summary>
    /// Moves the user's own lyrics by <paramref name="deltaMs"/> and saves it
    /// with them. The store keeps the raw text and the offset, and the pane
    /// re-reads both, so the offset is applied once, to the text as written.
    /// </summary>
    private async Task NudgeAsync(int deltaMs)
    {
        if (_custom is null || _track is not { } track || !IsCustom || deltaMs == 0)
            return;

        try
        {
            if (_custom.Get(track.VideoId) is not { } raw)
                return;

            var offset = Math.Clamp(raw.OffsetMs + deltaMs, -MaxOffsetMs, MaxOffsetMs);
            _custom.Save(track.VideoId, raw with { OffsetMs = offset });
            CustomError = null;
        }
        catch (Exception ex)
        {
            CustomError = Strings.Format("lyrics.offset.saveFailed", ("message", ex.Message));
            return;
        }

        await ReloadAsync(track);
    }

    /// <summary>
    /// Shows the track's lyrics again after the user changed their own: no
    /// skeleton in between, and the same place in the song.
    /// </summary>
    private async Task ReloadAsync(TrackInfo track)
    {
        if (_custom is null)
            return;

        CancelLoad();
        var loading = _loading = new CancellationTokenSource();

        LyricsLookup result;
        try
        {
            result = await _custom.Reload(LyricsQuery.FromTrack(track), loading.Token);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Write("lyrics", $"reload failed for {track.VideoId}: {ex.GetType().Name}: {ex.Message}");
            result = LyricsLookup.Unavailable;
        }

        if (loading.IsCancellationRequested || !ReferenceEquals(_loading, loading))
            return;

        Apply(result);
    }

    private static string Seconds(int ms) =>
        Strings.Format("lyrics.offset.seconds", ("seconds", (ms / 1000.0).ToString("0.0##", Strings.Culture)));

    private async Task LoadAsync(TrackInfo track)
    {
        if (_fetch is null)
            return;

        CancelLoad();
        var loading = _loading = new CancellationTokenSource();
        Clear();
        State = LyricsState.Loading;

        LyricsLookup result;
        try
        {
            result = await _fetch(LyricsQuery.FromTrack(track), loading.Token);
        }
        catch (OperationCanceledException) when (loading.IsCancellationRequested)
        {
            return; // superseded by the next track; it owns the pane now
        }
        catch (Exception ex)
        {
            // The service promises not to throw, but a pane stuck on its skeleton
            // is the one outcome that isn't allowed.
            AppLog.Write("lyrics", $"lookup failed for {track.VideoId}: {ex.GetType().Name}: {ex.Message}");
            result = LyricsLookup.Unavailable;
        }

        if (loading.IsCancellationRequested || !ReferenceEquals(_loading, loading))
            return;

        Apply(result);
    }

    private LyricsLookup? _applied;

    private void Apply(LyricsLookup result)
    {
        Clear();
        _applied = result;
        var doc = result.Document;
        // Close the last line at the track's end, so its words can sweep too.
        if (doc is not null && _track?.Duration is { } duration)
            doc = doc.ClosedAt(duration);
        switch (result.Status)
        {
            case LyricsStatus.Found when doc is { Lines.Count: > 0 }:
                SourceLabel = Describe(doc.Source);
                if (doc.Source == CustomLyricsStore.SourceName && _track is { } track)
                {
                    IsCustom = true;
                    CustomOffsetMs = _custom?.Get(track.VideoId)?.OffsetMs ?? 0;
                }
                if (doc.IsSynced)
                {
                    Lines = doc.Lines.Select((l, i) => new LyricLineViewModel(l, i)).ToArray();
                    _starts = doc.Lines.Select(l => l.Start).ToArray();
                    State = LyricsState.Synced;
                    OnPropertyChanged(nameof(IsOffsetShown));
                    SetActive(LyricTiming.FindActiveLine(_starts, _position));
                }
                else
                {
                    PlainText = string.Join('\n', doc.Lines.Select(l => l.Text));
                    State = LyricsState.Unsynced;
                }
                break;

            case LyricsStatus.Instrumental:
                State = LyricsState.Instrumental;
                break;

            case LyricsStatus.Unavailable:
                LoadError = new LoadError(
                    Strings.Get("lyrics.unavailable.title"),
                    Strings.Get("lyrics.unavailable.hint"),
                    Strings.Get("lyrics.unavailable.detail"),
                    RetryCommand);
                State = LyricsState.Unavailable;
                break;

            case LyricsStatus.NotLookedUp:
                State = LyricsState.NotLookedUp;
                break;

            default: // NotFound, or a document with nothing in it
                State = LyricsState.None;
                break;
        }
    }

    private void Clear()
    {
        _applied = null;
        Lines = [];
        _starts = [];
        PlainText = "";
        SourceLabel = "";
        LoadError = null;
        IsCustom = false;
        CustomOffsetMs = 0;
        CustomError = null;
        ActiveLineIndex = -1;
    }

    private void SetActive(int index)
    {
        if (index == ActiveLineIndex)
            return;

        ActiveLineIndex = index;
        foreach (var line in Lines)
            line.SetDistance(index < 0 ? int.MaxValue : Math.Abs(line.Index - index));
    }

    private static string Describe(string source) => source switch
    {
        LrcLibProvider.SourceName => Strings.Get("lyrics.source.lrclib"),
        CustomLyricsStore.SourceName => Strings.Get("lyrics.source.custom"),
        _ => Strings.Format("lyrics.source.other", ("source", source)),
    };
}

public sealed partial class LyricLineViewModel : ObservableObject
{
    public LyricLineViewModel(LyricLine line, int index)
    {
        Index = index;
        Start = line.Start;
        IsGap = string.IsNullOrWhiteSpace(line.Text);
        Text = IsGap ? "♪" : line.Text;
        Words = IsGap ? null : KaraokeSpan.Build(Text, line.Words);
        AccessibleName = IsGap ? Strings.Format("lyrics.line.gapName", ("time", DurationFormat.Clock(Start))) : Text;
        SeekHint = Strings.Format("lyrics.line.seekHint", ("time", DurationFormat.Clock(Start)));
    }

    public int Index { get; }
    public TimeSpan Start { get; }
    public string Text { get; }

    /// <summary>Word timings as ranges of <see cref="Text"/>, for the karaoke
    /// sweep; null for a line timed only as a whole.</summary>
    public IReadOnlyList<KaraokeSpan>? Words { get; }

    /// <summary>A stamped blank line: a break in the singing, drawn as a note.</summary>
    public bool IsGap { get; }

    public string AccessibleName { get; }
    public string SeekHint { get; }

    [ObservableProperty] private bool _isActive;

    /// <summary>One line either side of the active one: dimmed less than the rest.</summary>
    [ObservableProperty] private bool _isNear;

    internal void SetDistance(int distance)
    {
        IsActive = distance == 0;
        IsNear = distance == 1;
    }
}

/// <summary>One timed word of a line, as the range of the line's text it covers.</summary>
public sealed record KaraokeSpan(int Start, int Length, LyricWord Word)
{
    /// <summary>
    /// Maps word timings onto <paramref name="text"/>, the line as displayed: the
    /// words concatenated and trimmed, which is how the parser builds a line's text.
    /// A word that falls wholly in the trimmed edges (leading or trailing spaces)
    /// is dropped. Null when there are no words or they don't spell the text, so a
    /// mismatch degrades to line-level highlighting instead of sweeping the wrong
    /// letters.
    /// </summary>
    public static IReadOnlyList<KaraokeSpan>? Build(string text, IReadOnlyList<LyricWord>? words)
    {
        if (words is not { Count: > 0 })
            return null;

        var joined = string.Concat(words.Select(w => w.Text));
        var lead = joined.Length - joined.TrimStart().Length;
        if (!string.Equals(joined.Trim(), text, StringComparison.Ordinal))
            return null;

        var spans = new List<KaraokeSpan>(words.Count);
        var at = -lead;
        foreach (var word in words)
        {
            var start = Math.Max(0, at);
            var end = Math.Min(text.Length, at + word.Text.Length);
            if (end > start)
                spans.Add(new KaraokeSpan(start, end - start, word));
            at += word.Text.Length;
        }

        return spans.Count > 0 ? spans : null;
    }
}
