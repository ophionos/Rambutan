using System.Collections.Generic;
using System.Windows.Input;
using Rambutan.Core.Links;
using Rambutan.Core.Models;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>One entry of a context menu, a track's or a card's. <paramref name="IconKey"/> names a
/// geometry in <c>Styles/Icons.axaml</c>.</summary>
public sealed record MenuEntry(
    string Header, string IconKey, ICommand Command, object? Parameter, bool IsEnabled = true);

/// <summary>
/// What one surface adds to the shared track menu. <see cref="Play"/>, when set,
/// replaces the standard "Play next / Add to queue" pair (the queue's own rows
/// are already queued, so they get queue moves instead). <see cref="Edit"/> is
/// appended as a last group: removals, which read best at the bottom.
/// </summary>
public sealed record TrackMenuExtras(
    IReadOnlyList<MenuEntry>? Play = null,
    IReadOnlyList<MenuEntry>? Edit = null)
{
    public static readonly TrackMenuExtras None = new();
}

/// <summary>A surface whose rows add entries to the track menu (queue, playlist).</summary>
public interface ITrackMenuHost
{
    /// <param name="row">The row's own DataContext (a queue item, a track).</param>
    TrackMenuExtras MenuExtrasFor(object? row, TrackInfo track);
}

/// <summary>
/// The track context menu, built in one place. Groups come back in
/// display order; the control draws a separator between them.
/// </summary>
public static class TrackMenuModel
{
    public static IReadOnlyList<IReadOnlyList<MenuEntry>> Build(
        TrackInfo track, TrackActionsViewModel actions, TrackMenuExtras? extras = null)
    {
        extras ??= TrackMenuExtras.None;

        var play = new List<MenuEntry>();
        if (extras.Play is { } hostPlay)
        {
            play.AddRange(hostPlay);
        }
        else
        {
            play.Add(new(Strings.Get("menu.playNext"), "Icon.Next", actions.PlayNextCommand, track));
            play.Add(new(Strings.Get("common.addToQueue"), "Icon.Add", actions.AddToQueueCommand, track));
        }
        // A local file has only the queue moves and the navigation, which it
        // can't follow: what acts on YouTube Music isn't offered for it.
        var onYouTube = YouTubeIds.IsVideoId(track.VideoId);
        if (onYouTube)
            play.Add(new(Strings.Get("menu.startMix"), "Icon.Radio", actions.StartRadioCommand, track));

        // Like is a toggle, labelled for what choosing it does now, like the
        // thumbs: a liked song offers "Remove like" with a filled thumb.
        var liked = actions.RatingOf(track) is Rating.Like;
        List<MenuEntry> library =
        [
            liked
                ? new(Strings.Get("rating.removeLike"), "Icon.ThumbUp", actions.LikeCommand, track)
                : new(Strings.Get("rating.like"), "Icon.ThumbUpOutline", actions.LikeCommand, track),
            new(Strings.Get("menu.saveToPlaylist"), "Icon.AddToPlaylist", actions.AddToPlaylistCommand, track),
        ];
        // Save to library, a toggle too. Rows that didn't say whether the song is
        // saved (signed out, radio rows) leave it out rather than guess.
        if (actions.InLibraryOf(track) is { } inLibrary)
        {
            library.Add(inLibrary
                ? new(Strings.Get("menu.removeFromLibrary"), "Icon.LibraryAdded", actions.ToggleInLibraryCommand, track)
                : new(Strings.Get("menu.saveToLibrary"), "Icon.LibraryAdd", actions.ToggleInLibraryCommand, track));
        }
        // Download is a toggle too, labelled for what choosing it does now.
        if (actions.Downloads is { } downloads)
        {
            library.Add(downloads.IsDownloaded(track.VideoId)
                ? new(Strings.Get("menu.removeDownload"), "Icon.DownloadDone", actions.RemoveDownloadCommand, track)
                : downloads.IsQueued(track.VideoId)
                    ? new(Strings.Get("menu.cancelDownload"), "Icon.Download", actions.CancelDownloadCommand, track)
                    : new(Strings.Get("menu.download"), "Icon.Download", actions.DownloadCommand, track));
        }

        // Tracks whose source linked no album keep the entry, disabled, so
        // the menu's shape doesn't change from row to row.
        List<MenuEntry> navigate =
        [
            new(Strings.Get("common.goToAlbum"), "Icon.Album", actions.GoToAlbumCommand, track,
                IsEnabled: track.AlbumBrowseId is not null),
            new(Strings.Get("common.goToArtist"), "Icon.Person", actions.GoToArtistCommand, track.PrimaryArtist,
                IsEnabled: track.PrimaryArtist is not null),
            new(Strings.Get("menu.share"), "Icon.Share", actions.ShareCommand, track,
                IsEnabled: ShareLinks.ForTrack(track) is not null),
        ];

        var groups = new List<IReadOnlyList<MenuEntry>> { play };
        if (onYouTube)
            groups.Add(library);
        groups.Add(navigate);
        if (extras.Edit is { Count: > 0 } edit)
            groups.Add(edit);
        return groups;
    }
}
