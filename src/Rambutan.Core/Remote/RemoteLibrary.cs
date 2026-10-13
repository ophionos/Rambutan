using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.Core.Remote;

/// <summary>
/// A window of the queue as a remote client sees it: the tracks around the
/// current one, with their positions in the whole queue. A long playlist or a
/// radio can run to thousands of tracks; a phone shows a few before the current
/// track and the next few hundred, and says how many more there are.
/// </summary>
/// <param name="Version">The <see cref="RemoteState.QueueVersion"/> this was taken
/// at, or an earlier one: a client that sees a newer version in the state asks again.</param>
/// <param name="CurrentIndex">The current track's position in the whole queue; -1 when empty.</param>
public sealed record RemoteQueue(long Version, int CurrentIndex, int Total, IReadOnlyList<RemoteQueueItem> Items)
{
    /// <summary>Tracks shown before the current one.</summary>
    public const int Before = 20;

    /// <summary>Tracks shown from the current one on.</summary>
    public const int From = 200;
}

/// <param name="Index">Position in the whole queue, as the queue commands take it.</param>
/// <param name="Autoplay">Autoplay added it when the queue ran out.</param>
/// <param name="HasArtwork">The host serves its cover by videoId.</param>
public sealed record RemoteQueueItem(
    int Index, string VideoId, string Title, string Artists, long? DurationMs, bool Autoplay,
    bool HasArtwork = false);

/// <summary>A song a search found. A client adds it to the queue by its
/// <paramref name="VideoId"/>; the host keeps the track itself.</summary>
/// <param name="HasArtwork">The host serves its cover by videoId.</param>
public sealed record RemoteSearchResult(
    string VideoId, string Title, string Artists, string? Album, long? DurationMs, bool HasArtwork = false);

public enum RemoteSearchFailure
{
    /// <summary>The catalog couldn't be reached: the computer is offline.</summary>
    Offline,

    /// <summary>Anything else; the client offers to try again.</summary>
    Failed,
}

/// <param name="More">Asks for the next page (<see cref="IRemoteLibrary.MoreAsync"/>);
/// null on the last page. The host keeps the catalog's own token and hands
/// out only this key for it.</param>
public sealed record RemoteSearchOutcome(
    IReadOnlyList<RemoteSearchResult> Results, RemoteSearchFailure? Failure = null, string? More = null);

public sealed record RemoteSongPage(IReadOnlyList<TrackInfo> Songs, ContinuationToken? Next = null)
{
    public static RemoteSongPage From(SearchPage page) =>
        new(page.Items.OfType<SongEntry>().Select(e => e.Track).ToArray(), page.Continuation);

    public static SongSearch SearcherFor(ICatalog catalog) => async (query, next, ct) => From(next is null
        ? await catalog.SearchPagedAsync(query, SearchFilter.Songs, ct).ConfigureAwait(false)
        : await catalog.GetSearchContinuationAsync(query, SearchFilter.Songs, next, ct).ConfigureAwait(false));
}

/// <summary>Songs for <paramref name="query"/>: the first page when
/// <paramref name="next"/> is null, otherwise the page it points at.</summary>
public delegate Task<RemoteSongPage> SongSearch(string query, ContinuationToken? next, CancellationToken ct);

/// <summary>
/// What a remote client may do to the queue. Each command names its track by
/// position and videoId: the host checks that the position still holds that
/// track before it acts, so a client showing a queue that has since changed
/// can't jump to, remove or move the wrong track. A mismatch does nothing,
/// and the client's next look at the queue shows why.
/// </summary>
public abstract record RemoteQueueCommand
{
    public sealed record Jump(int Index, string VideoId) : RemoteQueueCommand;

    /// <summary>The current track can't be removed; the command does nothing then.</summary>
    public sealed record Remove(int Index, string VideoId) : RemoteQueueCommand;

    /// <summary><paramref name="To"/> as <see cref="Queue.QueueModel.Move"/> takes it.</summary>
    public sealed record Move(int From, string VideoId, int To) : RemoteQueueCommand;
}

/// <summary>
/// The queue and the catalog search as a remote client reaches them.
/// <see cref="RemoteSession"/> is the real one; the local API depends on this
/// so its tests can stand in for it.
/// </summary>
public interface IRemoteLibrary
{
    RemoteQueue GetQueue();

    void Execute(RemoteQueueCommand command);

    /// <summary>Songs for a query. Never throws for a catalog failure; a
    /// cancelled request still throws <see cref="OperationCanceledException"/>.</summary>
    Task<RemoteSearchOutcome> SearchAsync(string query, CancellationToken ct);

    /// <summary>The next page of an earlier search, by its outcome's
    /// <see cref="RemoteSearchOutcome.More"/>. Null when the host no longer
    /// knows that key (it keeps the last few); fails and throws as
    /// <see cref="SearchAsync"/> does.</summary>
    Task<RemoteSearchOutcome?> MoreAsync(string more, CancellationToken ct);

    /// <summary>Queues a song an earlier search found: right after the current
    /// track when <paramref name="next"/>, otherwise at the end. With nothing
    /// playing, it also starts. False when no recent search found that videoId:
    /// a client can only add what the host itself found.</summary>
    bool Add(string videoId, bool next);

    /// <summary>Replaces the queue with a song an earlier search found, and
    /// plays it. False, as for <see cref="Add"/>, when no recent search found it.</summary>
    bool Play(string videoId);

    /// <summary>Whether the host serves this track's cover: the playing track,
    /// a queued one, or one a recent search found, and only when it has one.
    /// The host fetches no cover for any other id, so it is no general image proxy.</summary>
    bool HasCover(string videoId);
}
