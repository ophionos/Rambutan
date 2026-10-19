using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.Core.Catalog;
using Rambutan.Core.Links;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.ViewModels;

/// <summary>
/// What an album, playlist or artist card does without opening its page: the
/// card's menu and its play button. Play, Shuffle and Add to queue load the
/// tracks first (a playlist's first page, as its page shows before scrolling).
/// While they load, <see cref="IsLoading"/> is true for that album or playlist,
/// on every card that shows it; a failed or empty load says so in a toast.
/// UI thread only.
/// </summary>
public sealed partial class CollectionActionsViewModel
{
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly TrackActionsViewModel _actions;
    private readonly HashSet<string> _loading = [];

    public CollectionActionsViewModel(ICatalog catalog, PlaybackCoordinator coordinator, TrackActionsViewModel actions)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _actions = actions;
    }

    /// <summary>An album or playlist started or finished loading.</summary>
    public event Action? LoadingChanged;

    public bool IsLoading(CatalogEntry? entry) => entry is not null && Key(entry) is { } key && _loading.Contains(key);

    /// <summary>The link Share copies; null where there is none to copy.</summary>
    public static string? ShareLinkFor(CatalogEntry entry) => ShareLinks.ForEntry(entry);

    // Concurrent executions allowed: one command serves every card, and another
    // card's load mustn't disable this one's entries.
    [RelayCommand(AllowConcurrentExecutions = true)]
    public Task PlayAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.playFailed", tracks => _coordinator.PlayQueueAsync(tracks));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task ShuffleAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.playFailed",
            tracks => _coordinator.PlayQueueAsync(EntryPlayback.Shuffled(tracks, Random.Shared)));

    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task AddToQueueAsync(CatalogEntry? entry) =>
        LoadThenAsync(entry, "toast.collection.queueFailed", tracks =>
        {
            _actions.EnqueueAll(tracks);
            _actions.Report(Strings.Format("toast.collection.addedToQueue",
                ("title", entry!.Title), ("count", tracks.Count)));
            return Task.CompletedTask;
        });

    [RelayCommand]
    private async Task ShareAsync(CatalogEntry? entry)
    {
        if (entry is null || ShareLinkFor(entry) is not { } link || _actions.CopyToClipboard is not { } copy)
            return;

        await copy(link);
        _actions.Report(Strings.Format("toast.track.linkCopied", ("title", entry.Title)));
    }

    /// <summary>Loads the entry's tracks, then hands them on. A second request for
    /// an album or playlist already loading is dropped: the card shows it is busy.</summary>
    private async Task LoadThenAsync(
        CatalogEntry? entry, string failedKey, Func<IReadOnlyList<TrackInfo>, Task> then)
    {
        if (entry is null || Key(entry) is not { } key || !_loading.Add(key))
            return;

        LoadingChanged?.Invoke();
        IReadOnlyList<TrackInfo> tracks;
        try
        {
            tracks = await EntryPlayback.ResolveAsync(_catalog, entry, _actions.DownloadedIds.Contains);
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format(failedKey, ("title", entry.Title), ("error", ex.Message)));
            return;
        }
        finally
        {
            _loading.Remove(key);
            LoadingChanged?.Invoke();
        }

        if (tracks.Count == 0)
        {
            _actions.Report(Strings.Format("toast.nothingToPlay", ("title", entry.Title)));
            return;
        }

        try
        {
            await then(tracks);
        }
        catch (Exception ex)
        {
            _actions.ReportError(Strings.Format(failedKey, ("title", entry.Title), ("error", ex.Message)));
        }
    }

    private static string? Key(CatalogEntry entry) => entry switch
    {
        AlbumEntry album => "album:" + album.BrowseId,
        PlaylistEntry playlist => "playlist:" + playlist.PlaylistId,
        _ => null,
    };
}
