using System.Net.Http;
using System.Net.Sockets;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.Core.Remote;

public sealed partial class RemoteSession : IRemoteLibrary
{
    /// <summary>How many songs from recent searches the session keeps for adding.</summary>
    public const int FoundLimit = 300;

    public const int PageLimit = 20;

    private readonly SongSearch? _searchSongs;

    // Songs recent searches found, by videoId, least recently found first. A
    // client adds by videoId, and the queue gets the catalog's own track, never
    // one built from what a client sent.
    private readonly Lock _foundSync = new();
    private readonly OrderedDictionary<string, TrackInfo> _found = new(StringComparer.Ordinal);

    // The catalog's tokens for next pages, behind keys of the session's own:
    // a client can only ask for a page a search of the host's led to.
    private readonly OrderedDictionary<string, (string Query, ContinuationToken Next)> _pages = new(StringComparer.Ordinal);

    // Only counts and signals: QueueModel raises Changed after its own lock is
    // released, but the coordinator may hold its own.
    private void OnQueueChanged() => Update(() => _queueVersion++);

    public RemoteQueue GetQueue()
    {
        // The version first: if the queue changes in between, the client sees a
        // newer version in the state and asks again.
        long version;
        lock (_sync) version = _queueVersion;
        var snapshot = _coordinator.Queue.Snapshot();
        var tracks = snapshot.Tracks;
        var autoplay = snapshot.AutoplayIndices is { } indices ? new HashSet<int>(indices) : null;

        var first = Math.Max(0, snapshot.CurrentIndex - RemoteQueue.Before);
        var last = Math.Min(tracks.Count, Math.Max(snapshot.CurrentIndex, 0) + RemoteQueue.From);
        var items = new RemoteQueueItem[Math.Max(0, last - first)];
        for (var i = first; i < last; i++)
        {
            var track = tracks[i];
            items[i - first] = new RemoteQueueItem(
                i, track.VideoId, track.Title, track.ArtistDisplay, Milliseconds(track.Duration),
                autoplay?.Contains(i) == true, _hasArtwork(track));
        }
        return new RemoteQueue(version, snapshot.CurrentIndex, tracks.Count, items);
    }

    public void Execute(RemoteQueueCommand command) => _dispatch(() =>
    {
        var queue = _coordinator.Queue;
        switch (command)
        {
            case RemoteQueueCommand.Jump jump when Holds(queue, jump.Index, jump.VideoId):
                _ = _coordinator.JumpToAsync(jump.Index);
                break;
            case RemoteQueueCommand.Remove remove when Holds(queue, remove.Index, remove.VideoId):
                queue.Remove(remove.Index); // refuses the current track
                break;
            case RemoteQueueCommand.Move move when Holds(queue, move.From, move.VideoId):
                queue.Move(move.From, move.To); // ignores targets out of range
                break;
        }
    });

    private static bool Holds(QueueModel queue, int index, string videoId)
    {
        var items = queue.Items;
        return index >= 0 && index < items.Count && items[index].VideoId == videoId;
    }

    public Task<RemoteSearchOutcome> SearchAsync(string query, CancellationToken ct) =>
        SearchPageAsync(query, null, ct);

    public async Task<RemoteSearchOutcome?> MoreAsync(string more, CancellationToken ct)
    {
        (string Query, ContinuationToken Next) page;
        lock (_foundSync)
        {
            if (!_pages.TryGetValue(more, out page))
                return null;
        }
        return await SearchPageAsync(page.Query, page.Next, ct).ConfigureAwait(false);
    }

    private async Task<RemoteSearchOutcome> SearchPageAsync(string query, ContinuationToken? next, CancellationToken ct)
    {
        if (_searchSongs is null)
            return new([], RemoteSearchFailure.Failed);

        RemoteSongPage page;
        try
        {
            page = await _searchSongs(query, next, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            AppLog.Write("remote", $"search failed: {ex.GetType().Name}: {ex.Message}");
            return new([], IsOffline(ex) ? RemoteSearchFailure.Offline : RemoteSearchFailure.Failed);
        }

        string? more = null;
        lock (_foundSync)
        {
            // Found again moves a song to the back, so it is kept as long as
            // one found for the first time.
            foreach (var song in page.Songs)
            {
                _found.Remove(song.VideoId);
                _found.Add(song.VideoId, song);
            }
            while (_found.Count > FoundLimit)
                _found.RemoveAt(0);

            if (page.Next is { } token)
            {
                more = Guid.NewGuid().ToString("N");
                _pages.Add(more, (query, token));
                while (_pages.Count > PageLimit)
                    _pages.RemoveAt(0);
            }
        }

        return new(page.Songs
            .DistinctBy(s => s.VideoId)
            .Select(s => new RemoteSearchResult(
                s.VideoId, s.Title, s.ArtistDisplay, s.Album, Milliseconds(s.Duration), _hasArtwork(s)))
            .ToArray(), More: more);
    }

    /// <summary>No answer at all: offline, DNS, a dropped connection, a timeout
    /// (as the desktop's load errors tell them apart).</summary>
    private static bool IsOffline(Exception ex) =>
        ex is HttpRequestException { StatusCode: null } or SocketException or TimeoutException
            or OperationCanceledException or OfflineException;

    /// <summary>A copy of a song a recent search found, or null. A copy: queue
    /// rows are told apart by instance, so adding one song twice makes two rows,
    /// as it does on the desktop.</summary>
    private TrackInfo? FoundCopy(string videoId)
    {
        lock (_foundSync)
            return _found.TryGetValue(videoId, out var found) ? found with { } : null;
    }

    public bool Play(string videoId)
    {
        if (FoundCopy(videoId) is not { } track)
            return false;
        _dispatch(() => _ = _coordinator.PlayQueueAsync([track]));
        return true;
    }

    public bool Add(string videoId, bool next)
    {
        if (FoundCopy(videoId) is not { } track)
            return false;

        _dispatch(() =>
        {
            var queue = _coordinator.Queue;
            if (next)
                queue.EnqueueNext(track);
            else
                queue.Enqueue(track);

            // With nothing playing, start what was added, as the desktop
            // search's "+" starts the queue.
            if (_coordinator.State is PlaybackState.Stopped)
            {
                var items = queue.Items;
                for (var i = 0; i < items.Count; i++)
                {
                    if (ReferenceEquals(items[i], track))
                    {
                        _ = _coordinator.JumpToAsync(i);
                        break;
                    }
                }
            }
        });
        return true;
    }

    public bool HasCover(string videoId) => CoverTrack(videoId) is not null;

    /// <summary>The track whose cover the host may serve for this id (see
    /// <see cref="IRemoteLibrary.HasCover"/>), or null.</summary>
    public TrackInfo? CoverTrack(string videoId)
    {
        TrackInfo? track;
        lock (_sync)
            track = _track?.VideoId == videoId ? _track : null;
        if (track is null)
        {
            foreach (var queued in _coordinator.Queue.Snapshot().Tracks)
            {
                if (queued.VideoId == videoId)
                {
                    track = queued;
                    break;
                }
            }
        }
        if (track is null)
        {
            lock (_foundSync)
                _found.TryGetValue(videoId, out track);
        }
        return track is not null && _hasArtwork(track) ? track : null;
    }
}
