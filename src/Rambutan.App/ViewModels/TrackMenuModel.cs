using System.Collections.Generic;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Links;
using Rambutan.Core.Local;
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
    // The match's line only names it; it does nothing.
    private static readonly ICommand Nothing = new RelayCommand(() => { });

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
        {
            groups.Add(library);
        }
        else
        {
            // Its own label: a match's group below has Save to playlist too,
            // which saves the song, not the file.
            if (actions.LocalPlaylists is not null)
                groups.Add([new(Strings.Get("menu.saveToLocalPlaylist"), "Icon.AddToPlaylist", actions.AddToPlaylistCommand, track)]);
            if (actions.YouTubeMatches?.StatusOf(track) is { } match)
                groups.Add(MatchGroup(match, actions));
        }
        groups.Add(navigate);
        if (extras.Edit is { Count: > 0 } edit)
            groups.Add(edit);
        return groups;
    }

    /// <summary>
    /// A local file's YouTube Music song: a line that names it, or says why there
    /// is none, and the actions that take it, off without one. They act on the
    /// song as a YouTube Music track's own entries do, so a like or a playlist
    /// gets the song, never the file.
    /// </summary>
    private static List<MenuEntry> MatchGroup(LocalMatchStatus match, TrackActionsViewModel actions)
    {
        var song = match.Song;
        var line = match.State switch
        {
            LocalMatchState.Found when song is not null =>
                Strings.Format("menu.match.found", ("title", song.Title), ("artist", song.ArtistDisplay)),
            LocalMatchState.NotFound => Strings.Get("menu.match.notFound"),
            LocalMatchState.NoTitle => Strings.Get("menu.match.noTitle"),
            LocalMatchState.Failed => Strings.Get("menu.match.failed"),
            _ => Strings.Get("menu.match.searching"),
        };
        var liked = song is not null && actions.RatingOf(song) is Rating.Like;
        return
        [
            new(line, "Icon.Search", Nothing, null, IsEnabled: false),
            new(Strings.Get("menu.startMix"), "Icon.Radio", actions.StartRadioCommand, song, IsEnabled: song is not null),
            liked
                ? new(Strings.Get("rating.removeLike"), "Icon.ThumbUp", actions.LikeCommand, song)
                : new(Strings.Get("rating.like"), "Icon.ThumbUpOutline", actions.LikeCommand, song, IsEnabled: song is not null),
            new(Strings.Get("menu.saveToPlaylist"), "Icon.AddToPlaylist", actions.AddToPlaylistCommand, song,
                IsEnabled: song is not null),
        ];
    }
}
