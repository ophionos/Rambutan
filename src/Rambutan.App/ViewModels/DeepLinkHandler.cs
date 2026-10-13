using System;
using System.Linq;
using System.Threading.Tasks;
using Rambutan.App.Localization;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Links;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Acts on a rambutan:// link through the app's own navigation and
/// playback. A web page can fire these links, so a link only reads and
/// navigates: it never deletes, signs out or changes a setting, and it never
/// replaces a queue the user already has.
/// </summary>
public sealed class DeepLinkHandler
{
    private readonly NavigationService _navigation;
    private readonly ICatalog _catalog;
    private readonly PlaybackCoordinator _coordinator;
    private readonly QueueModel _queue;
    private readonly TrackActionsViewModel _actions;
    private readonly NetworkStatus? _network;

    public DeepLinkHandler(
        NavigationService navigation, ICatalog catalog, PlaybackCoordinator coordinator,
        QueueModel queue, TrackActionsViewModel actions, NetworkStatus? network = null)
    {
        _navigation = navigation;
        _catalog = catalog;
        _coordinator = coordinator;
        _queue = queue;
        _actions = actions;
        _network = network;
    }

    /// <summary>Handles the link in these command-line arguments, if they carry
    /// one. Call on the UI thread.</summary>
    public Task HandleArgsAsync(string[] args) =>
        DeepLinkArgs.Find(args) is { } link ? HandleAsync(link) : Task.CompletedTask;

    /// <summary>Parses and acts on one link. A refused link says why, in a
    /// toast; it never throws.</summary>
    public async Task HandleAsync(string text)
    {
        var result = DeepLinkParser.Parse(text);
        // The kind, not the link: a link can carry a search someone typed.
        AppLog.Write("link", result.Link?.GetType().Name ?? $"refused: {result.Error} {result.Action}");

        switch (result.Link)
        {
            case null when result.Error is DeepLinkError.UnknownAction:
                _actions.ReportError(Strings.Format("toast.link.unknown", ("action", result.Action)));
                break;
            case null:
                _actions.ReportError(Strings.Get("toast.link.malformed"));
                break;
            case OpenAppLink:
                break;
            case AlbumLink album:
                _navigation.OpenAlbum(album.BrowseId);
                break;
            case PlaylistLink playlist:
                _navigation.OpenPlaylist(playlist.PlaylistId);
                break;
            case ArtistLink artist:
                _navigation.OpenArtist(artist.ChannelId);
                break;
            case SearchLink search:
                _navigation.GoSearch(search.Query);
                break;
            case PlayLink play:
                await PlayAsync(play.VideoId);
                break;
        }
    }

    /// <summary>
    /// With an empty queue the song plays at once, its radio after it, as a
    /// click on a song does. With a queue, the song goes in next and a toast
    /// offers "Play now", which moves to it without dropping the queue.
    /// </summary>
    private async Task PlayAsync(string videoId)
    {
        System.Collections.Generic.IReadOnlyList<Core.Models.TrackInfo> radio;
        try
        {
            // The watch queue for a song starts with the song itself, so one
            // request gives both its details and what follows it.
            radio = await _catalog.GetRadioAsync(videoId);
        }
        catch (Exception ex)
        {
            AppLog.Write("link", ex.ToString());
            if (_network?.IsOffline == true)
                _actions.ReportError(Strings.Get("toast.link.offline"));
            else
                _actions.ReportError(Strings.Format("toast.link.playFailed", ("error", ex.Message)));
            return;
        }

        var track = radio.FirstOrDefault(t => t.VideoId == videoId);
        if (track is null)
        {
            _actions.ReportError(Strings.Get("toast.link.notFound"));
            return;
        }

        if (_queue.Items.Count == 0)
        {
            await _coordinator.PlayQueueAsync([track]);
            _coordinator.ExtendFromSeed(track, radio);
            return;
        }

        _queue.EnqueueNext(track);
        _actions.Toasts.Offer(
            Strings.Format("toast.link.queued", ("title", track.Title)),
            Strings.Get("toast.link.playNow"),
            async () =>
            {
                // Found by reference: the queue may have changed since.
                var items = _queue.Items;
                for (var i = 0; i < items.Count; i++)
                {
                    if (ReferenceEquals(items[i], track))
                    {
                        await _coordinator.JumpToAsync(i);
                        return;
                    }
                }
                _queue.EnqueueNext(track);
                await _coordinator.NextAsync();
            });
    }
}
