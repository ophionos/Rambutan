using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows.Input;
using Rambutan.Core.Catalog;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>
/// A page that couldn't load, told the way a listener can act on it: a plain title,
/// a hint about what to do, a way to try again, and the raw message for bug reports
/// (shown behind "Details", never up front: it can leak transport internals).
/// Rendered by the ErrorState.Full / ErrorState.Compact templates (Styles/States.axaml).
/// </summary>
/// <param name="Retry">Re-runs the load that failed; null when there's nothing to retry.</param>
public sealed record LoadError(string Title, string Hint, string Detail, ICommand? Retry)
{
    /// <summary>The request never got an answer: the network, not YouTube Music.</summary>
    public bool IsOffline { get; init; }

    /// <summary>Opens the downloads (On this computer), which still play offline; shown beside
    /// Try again when set. Only Home sets it.</summary>
    public ICommand? GoToDownloads { get; init; }

    /// <summary>Classifies <paramref name="error"/> into what the listener can do about it.</summary>
    /// <param name="what">What failed to load. The tables word each subject per
    /// message with an ICU <c>select</c>, so a translation can decline it.</param>
    public static LoadError From(Exception error, LoadSubject what, ICommand? retry)
    {
        var detail = $"{error.GetType().Name}: {error.Message}";
        var subject = what switch
        {
            LoadSubject.Home => "home",
            LoadSubject.Album => "album",
            LoadSubject.Artist => "artist",
            LoadSubject.Playlist => "playlist",
            LoadSubject.MoreTracks => "moreTracks",
            LoadSubject.Library => "library",
            LoadSubject.MorePlaylists => "morePlaylists",
            LoadSubject.MoreShelves => "moreShelves",
            LoadSubject.MoreResults => "moreResults",
            LoadSubject.MoreAlbums => "moreAlbums",
            LoadSubject.MoreArtists => "moreArtists",
            LoadSubject.MoreSongs => "moreSongs",
            LoadSubject.Station => "station",
            LoadSubject.Moods => "moods",
            LoadSubject.MoodCategory => "moodCategory",
            _ => "search",
        };

        // No status code means the request never got an answer: offline, DNS,
        // a refused proxy, a dropped connection. A timeout surfaces as a
        // cancellation nobody asked for.
        var offline = error is HttpRequestException { StatusCode: null } or SocketException or
            TimeoutException or TaskCanceledException;

        var (title, hint) = error switch
        {
            NotSignedInException => (
                Strings.Format("error.load.signIn.title", ("what", subject)),
                Strings.Get("error.load.signIn.hint")),

            HttpRequestException { StatusCode: HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden } => (
                Strings.Get("error.load.refused.title"),
                Strings.Get("error.load.refused.hint")),

            HttpRequestException { StatusCode: HttpStatusCode.NotFound } => (
                Strings.Format("error.load.notFound.title", ("what", subject)),
                Strings.Get("error.load.notFound.hint")),

            HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } or
            HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError } => (
                Strings.Get("error.load.serverError.title"),
                Strings.Get("error.load.serverError.hint")),

            _ when offline => (
                Strings.Get("error.load.offline.title"),
                Strings.Get("error.load.offline.hint")),

            // The answer arrived but didn't parse: InnerTube changed shape.
            JsonException or KeyNotFoundException or InvalidOperationException or FormatException => (
                Strings.Format("error.load.unreadable.title", ("what", subject)),
                Strings.Get("error.load.unreadable.hint")),

            _ => (
                Strings.Format("error.load.other.title", ("what", subject)),
                Strings.Get("error.load.other.hint")),
        };

        return new LoadError(title, hint, detail, retry) { IsOffline = offline };
    }
}

/// <summary>What a page was loading when it failed (<see cref="LoadError.From"/>).</summary>
public enum LoadSubject
{
    Home,
    Album,
    Artist,
    Playlist,
    MoreTracks,
    Library,
    MorePlaylists,
    MoreShelves,
    SearchResults,
    MoreResults,
    MoreAlbums,
    MoreArtists,
    MoreSongs,
    Station,
    Moods,
    MoodCategory,
}
