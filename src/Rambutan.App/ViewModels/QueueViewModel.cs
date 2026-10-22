using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;
using Rambutan.Core.Local;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>
/// One queue row. The instance lives as long as its track stays in the queue: the
/// row is moved and updated in place, never rebuilt, so scroll position,
/// selection and a drag in progress survive the song advancing.
/// </summary>
public sealed partial class QueueItemViewModel(TrackInfo track) : ObservableObject, IHasAccessibleName, ITrackRow
{
    public TrackInfo Track { get; } = track;
    public string Title => Track.Title;

    private string? _artworkUrl;
    private bool _artworkRead;

    /// <summary>The row's cover (<see cref="Rambutan.App.Converters.ArtworkConverter.For"/>),
    /// read when it is first shown.</summary>
    public string? ArtworkUrl
    {
        get
        {
            if (!_artworkRead)
            {
                _artworkUrl = Rambutan.App.Converters.ArtworkConverter.For(Track);
                _artworkRead = true;
            }
            return _artworkUrl;
        }
    }

    /// <summary>Reads the cover again, for a local file a scan can give another
    /// one; the row tells its view only when it changed.</summary>
    public void RefreshArtwork()
    {
        if (!_artworkRead)
            return;
        var url = Rambutan.App.Converters.ArtworkConverter.For(Track);
        if (url == _artworkUrl)
            return;
        _artworkUrl = url;
        OnPropertyChanged(nameof(ArtworkUrl));
    }
    public string Artist => Track.ArtistDisplay;

    /// <summary>Position in the queue as of the last refresh.</summary>
    [ObservableProperty] private int _index;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isCurrent;

    /// <summary>The current track is in the player, playing or paused. False for
    /// a queue restored at startup that nothing has played from yet.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isLoaded;

    /// <summary>Already played: above the current row, drawn quieter.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isHistory;

    /// <summary>Offline, and the track is neither downloaded nor cached: playback
    /// will skip it. Never set on the track in the player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _needsConnection;

    /// <summary>A local file that isn't there now (or isn't in the index): playback
    /// will skip it, online or not. Never set on the track in the player.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isFileMissing;

    /// <summary>What a screen reader says for the row: the marker
    /// and the dimming are visual only, so the state goes into the name. It says
    /// what the section caption says: a restored row isn't playing yet.</summary>
    public string AccessibleName
    {
        get
        {
            var name = Strings.Format(
                IsCurrent ? (IsLoaded ? "queue.row.nowPlaying" : "queue.row.whereYouLeftOff")
                    : IsHistory ? "queue.row.played" : "queue.row.name",
                ("name", string.IsNullOrEmpty(Artist) ? Title : Strings.Format("queue.row.nameWithArtist", ("title", Title), ("artist", Artist))));
            return IsFileMissing ? Strings.Format("queue.row.fileMissing", ("row", name))
                : NeedsConnection ? Strings.Format("queue.row.needsConnection", ("row", name))
                : name;
        }
    }

    internal void OnLanguageChanged() => OnPropertyChanged(nameof(AccessibleName));

    /// <summary>Caption drawn above this row when it starts a section
    /// ("Played", "Now playing", "Up next (n)", "Autoplay"), otherwise null.</summary>
    [ObservableProperty] private string? _section;
}

/// <summary>A drag or keyboard move: take the row at <paramref name="From"/> and
/// put it at <paramref name="To"/> (indices as <see cref="QueueModel.Move"/> takes them).</summary>
public sealed record ReorderRequest(int From, int To);

public partial class QueueViewModel : ViewModelBase, ITrackMenuHost
{
    private readonly QueueModel _queue;
    private readonly PlaybackCoordinator? _coordinator;
    private readonly NetworkStatus? _network;
    private readonly IOfflineAvailability? _playsOffline;
    private QueueItemViewModel? _lastCurrent;
    // Offline: the queued ids that won't play without the network. Worked out off
    // the UI thread, since the cache answers from the disk.
    private IReadOnlySet<string> _needConnection = new HashSet<string>();
    // Online or not: the queued local ids whose file won't play.
    private IReadOnlySet<string> _missingFiles = new HashSet<string>();
    private int _availabilityGeneration;

    public ObservableCollection<QueueItemViewModel> Items { get; } = [];

    /// <summary>
    /// The current row changed: a new track started, or a restored queue appeared.
    /// The view brings it into view. Only then: reorders and removals leave the
    /// scroll position where the user put it.
    /// </summary>
    public event Action<QueueItemViewModel>? CurrentChanged;

    // Design-time
    public QueueViewModel() : this(new QueueModel(), null)
    {
        var played = new TrackInfo { VideoId = "a", Title = "Get Lucky", Artists = [new("Daft Punk")] };
        var track = new TrackInfo { VideoId = "x", Title = "Instant Crush", Artists = [new("Daft Punk")] };
        var next = new TrackInfo { VideoId = "y", Title = "The Grid", Artists = [new("Daft Punk")] };
        _queue.SetQueue([played, track, next], 1);
        Refresh();
    }

    /// <param name="network">With <paramref name="playsOffline"/>, marks the rows
    /// that need a connection while offline; either null, no row is marked.</param>
    /// <param name="playsOffline">Also marks the local files that won't play.</param>
    /// <param name="localFilesChanged">Subscribes to changes of the local index,
    /// after which the local rows are checked again, and their covers read.</param>
    public QueueViewModel(
        QueueModel queue, PlaybackCoordinator? coordinator,
        NetworkStatus? network = null, IOfflineAvailability? playsOffline = null,
        Action<Action>? localFilesChanged = null)
    {
        _queue = queue;
        _coordinator = coordinator;
        _network = network;
        _playsOffline = playsOffline;

        queue.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            Refresh();
            if (_network?.IsOffline == true || HasLocalRows)
                _ = UpdateAvailabilityAsync();
        });
        localFilesChanged?.Invoke(() => Dispatcher.UIThread.Post(() =>
        {
            if (!HasLocalRows)
                return;
            _ = UpdateAvailabilityAsync();
            foreach (var item in Items)
            {
                if (LocalIds.IsLocal(item.Track.VideoId))
                    item.RefreshArtwork();
            }
        }));
        if (network is not null)
            network.Changed += offline => Dispatcher.UIThread.Post(() => _ = UpdateAvailabilityAsync());
        if (coordinator is not null)
            coordinator.NowPlayingChanged += _ => Dispatcher.UIThread.Post(Refresh);

        // A queue restored before this VM existed is already there to show.
        Refresh();
        // So is a network found missing before it: the startup check can answer
        // first. A restored local file may be gone.
        if (network?.IsOffline == true || HasLocalRows)
            _ = UpdateAvailabilityAsync();

        // Section captions and row names are worded here, not in XAML.
        Strings.WhenChanged(this, static vm =>
        {
            vm.Refresh();
            foreach (var item in vm.Items)
                item.OnLanguageChanged();
        });
    }

    /// <summary>Brings <see cref="Items"/> in line with the queue model. Called on
    /// every queue and track change; public so tests can drive it synchronously.</summary>
    public void Refresh()
    {
        var snapshot = _queue.Snapshot();
        Sync(Items, snapshot.Tracks);

        var tracks = snapshot.Tracks;
        var current = snapshot.CurrentIndex;
        var loaded = _coordinator?.NowPlaying is not null;

        // Autoplay's tracks follow the user's own and get their own caption;
        // "Up next" counts only what the user queued.
        var autoplayFrom = tracks.Count;
        foreach (var index in snapshot.AutoplayIndices ?? [])
        {
            if (index > current)
            {
                autoplayFrom = index;
                break;
            }
        }

        for (var i = 0; i < Items.Count; i++)
        {
            var item = Items[i];
            item.Index = i;
            item.IsCurrent = i == current;
            item.IsLoaded = loaded;
            item.IsHistory = i < current;
            item.NeedsConnection = _needConnection.Contains(item.Track.VideoId) && !(i == current && loaded);
            item.IsFileMissing = _missingFiles.Contains(item.Track.VideoId) && !(i == current && loaded);
            item.Section =
                i == 0 && current > 0 ? Strings.Get("queue.section.played")
                : i == current ? Strings.Get(loaded ? "queue.section.nowPlaying" : "queue.section.whereYouLeftOff")
                : i == autoplayFrom ? Strings.Get("queue.section.autoplay")
                : i == current + 1 ? Strings.Format("queue.section.upNext", ("count", autoplayFrom - current - 1))
                : null;
        }

        var currentItem = current >= 0 && current < Items.Count ? Items[current] : null;
        if (!ReferenceEquals(currentItem, _lastCurrent))
        {
            _lastCurrent = currentItem;
            if (currentItem is not null)
                CurrentChanged?.Invoke(currentItem);
        }
    }

    private bool HasLocalRows => Items.Any(i => LocalIds.IsLocal(i.Track.VideoId));

    /// <summary>
    /// Works out which rows need a connection, now that the network or what plays
    /// offline changed, and which local files won't play, and marks them. Online,
    /// it clears the connection marks.
    /// </summary>
    public async Task UpdateAvailabilityAsync()
    {
        var generation = ++_availabilityGeneration;
        IReadOnlySet<string> needed = new HashSet<string>();
        IReadOnlySet<string> missing = new HashSet<string>();
        if (_playsOffline is { } playsOffline)
        {
            var ids = Items.Select(i => i.Track.VideoId).Distinct().ToArray();
            var offline = _network?.IsOffline == true;
            var wontPlay = await Task.Run(() => ids
                .Where(id => (offline || LocalIds.IsLocal(id)) && !playsOffline.IsAvailableOffline(id))
                .ToList());
            needed = wontPlay.Where(id => !LocalIds.IsLocal(id)).ToHashSet();
            missing = wontPlay.Where(LocalIds.IsLocal).ToHashSet();
        }

        if (generation != _availabilityGeneration)
            return;
        _needConnection = needed;
        _missingFiles = missing;
        Refresh();
    }

    /// <summary>
    /// Edits <paramref name="items"/> into the order of <paramref name="tracks"/>
    /// with removes, moves and inserts, reusing each row whose track instance is
    /// still queued. Identity is by reference: the same song queued twice is two
    /// rows, and each keeps its own row.
    /// </summary>
    public static void Sync(ObservableCollection<QueueItemViewModel> items, IReadOnlyList<TrackInfo> tracks)
    {
        // 1. Drop rows whose track left the queue (as a multiset: a duplicate
        //    removed once leaves one row behind).
        var available = new Dictionary<TrackInfo, int>(ReferenceEqualityComparer.Instance);
        foreach (var track in tracks)
            available[track] = available.GetValueOrDefault(track) + 1;
        for (var i = 0; i < items.Count;)
        {
            var track = items[i].Track;
            if (available.GetValueOrDefault(track) > 0)
            {
                available[track]--;
                i++;
            }
            else
            {
                items.RemoveAt(i);
            }
        }

        // 2. Put the survivors in order and add the newcomers.
        for (var i = 0; i < tracks.Count; i++)
        {
            if (i < items.Count && ReferenceEquals(items[i].Track, tracks[i]))
                continue;

            // One row moved *down* shows up here as every row after it being one
            // early. Moving that one row is a single change instead of a cascade.
            if (i + 1 < items.Count && ReferenceEquals(items[i + 1].Track, tracks[i]) &&
                IndexOf(tracks, items[i].Track, i + 1) is var target and > 0)
            {
                items.Move(i, Math.Min(target, items.Count - 1));
                continue;
            }

            var existing = IndexOf(items, tracks[i], i + 1);
            if (existing >= 0)
                items.Move(existing, i);
            else
                items.Insert(i, new QueueItemViewModel(tracks[i]));
        }

        while (items.Count > tracks.Count)
            items.RemoveAt(items.Count - 1);
    }

    private static int IndexOf(IReadOnlyList<TrackInfo> tracks, TrackInfo track, int from)
    {
        for (var i = from; i < tracks.Count; i++)
        {
            if (ReferenceEquals(tracks[i], track))
                return i;
        }
        return -1;
    }

    private static int IndexOf(IList<QueueItemViewModel> items, TrackInfo track, int from)
    {
        for (var i = from; i < items.Count; i++)
        {
            if (ReferenceEquals(items[i].Track, track))
                return i;
        }
        return -1;
    }

    // Concurrent executions allowed for the same reason as the transport buttons:
    // the coordinator supersedes, and rapid jumps must not be dropped mid-load.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task JumpAsync(QueueItemViewModel item) =>
        _coordinator?.JumpToAsync(item.Index) ?? Task.CompletedTask;

    [RelayCommand]
    private void Remove(QueueItemViewModel item) => _queue.Remove(item.Index);

    // QueueModel.Move ignores out-of-range targets, so the edges need no guards.
    [RelayCommand]
    private void MoveUp(QueueItemViewModel item) => _queue.Move(item.Index, item.Index - 1);

    [RelayCommand]
    private void MoveDown(QueueItemViewModel item) => _queue.Move(item.Index, item.Index + 1);

    /// <summary>Drag-reorder and Alt+↑/↓.</summary>
    [RelayCommand]
    private void Reorder(ReorderRequest request) => _queue.Move(request.From, request.To);

    /// <summary>"Play next" on a queued row moves it to just after the current
    /// track; queueing a second copy would be the other page's meaning.</summary>
    [RelayCommand]
    private void PlayNext(QueueItemViewModel item)
    {
        var current = _queue.CurrentIndex;
        _queue.Move(item.Index, item.Index < current ? current : current + 1);
    }

    public TrackMenuExtras MenuExtrasFor(object? row, TrackInfo track)
    {
        if (row is not QueueItemViewModel item)
            return TrackMenuExtras.None;

        var current = _queue.CurrentIndex;
        return new TrackMenuExtras(
            Play:
            [
                new(Strings.Get("menu.playNow"), "Icon.Play", JumpCommand, item, IsEnabled: !item.IsCurrent),
                new(Strings.Get("menu.playNext"), "Icon.Next", PlayNextCommand, item,
                    IsEnabled: !item.IsCurrent && item.Index != current + 1),
            ],
            Edit:
            [
                // The playing track can't leave the queue (QueueModel.Remove refuses).
                new(Strings.Get("queue.removeTip"), "Icon.Close", RemoveCommand, item, IsEnabled: !item.IsCurrent),
            ]);
    }
}
