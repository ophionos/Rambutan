using Rambutan.Core.Models;

namespace Rambutan.Core.Queue;

public enum RepeatMode { Off, All, One }

/// <summary>
/// A consistent copy of the queue: its tracks, the current index, the repeat
/// mode and the shuffle mode, taken under one lock. What the app persists between
/// runs; the file format itself belongs to the app, not to Core.
/// </summary>
/// <param name="Tracks">In play order: shuffled, while shuffle is on.</param>
/// <param name="ShuffleRanks">Null while shuffle is off. While it's on, each track's
/// position in the original order (a permutation of 0..Count-1, parallel to
/// <paramref name="Tracks"/>), which is what turning shuffle off puts back.</param>
/// <param name="AutoplayIndices">Positions in <paramref name="Tracks"/> of the tracks
/// autoplay added, ascending; null or empty when there are none.</param>
public sealed record QueueSnapshot(
    IReadOnlyList<TrackInfo> Tracks, int CurrentIndex, RepeatMode Repeat,
    IReadOnlyList<int>? ShuffleRanks = null, IReadOnlyList<int>? AutoplayIndices = null)
{
    public bool Shuffle => ShuffleRanks is not null;

    /// <summary>True when <paramref name="ranks"/> can stand for the original order
    /// of <paramref name="count"/> tracks: each of 0..count-1 exactly once.</summary>
    public static bool IsPermutation(IReadOnlyList<int> ranks, int count)
    {
        if (ranks.Count != count)
            return false;
        var seen = new bool[count];
        foreach (var rank in ranks)
        {
            if (rank < 0 || rank >= count || seen[rank])
                return false;
            seen[rank] = true;
        }
        return true;
    }
}

public enum TrackEndReason
{
    /// <summary>The current track played to its end (RepeatMode.One repeats it).</summary>
    Natural,
    /// <summary>The user pressed next (always advances, even under RepeatMode.One).</summary>
    Manual,
}

/// <summary>
/// The play queue: ordered tracks plus a current index. Pure state — no I/O, no
/// playback knowledge. Mutating methods only move the index when they return a
/// track, so callers can probe with <see cref="PeekNext"/> safely.
/// </summary>
/// <remarks>
/// <para><b>Shuffle is a mode</b>, like <see cref="Repeat"/>. The
/// shuffled order is the queue: <see cref="Items"/> always lists what will play,
/// in the order it will play. What the mode adds is memory of the original order,
/// so turning it off can put that back:</para>
/// <list type="bullet">
/// <item>On: the tracks after the current one are shuffled. The current track and
/// everything already played stay where they are.</item>
/// <item>Off: the whole queue returns to its original order and the current track
/// keeps playing from its original position. So "next" continues the album or
/// playlist from there, which is what a listener who turns shuffle off is asking
/// for. The price is that tracks already heard while shuffled may come round
/// again and tracks skipped over won't, the same trade other players make.</item>
/// <item>While on, a new queue plays the chosen track first and shuffles the rest
/// (all of them, including those before it). Radio tracks arriving through
/// <see cref="ExtendFromSeed"/> are shuffled among themselves. "Add to queue" and
/// "Play next" are explicit placements and stay where they were put.</item>
/// <item>Adds and removals while on carry into the original order: an added track
/// goes where it would have gone unshuffled (the end, or right after the current
/// track for "Play next"). A manual move only reorders what plays now; turning
/// shuffle off puts the moved track back in its original place.</item>
/// <item>Repeat all wraps to the start of the shuffled order; it doesn't reshuffle.</item>
/// </list>
/// <para><b>Autoplay</b>: tracks <see cref="AppendAutoplay"/> adds when the
/// queue runs out are marked, so the queue can caption them and keep the user's own
/// choices ahead of them. "Add to queue" goes before the upcoming autoplay tracks,
/// the way YouTube Music does it, and moving an autoplay track by hand makes it the
/// user's. A new queue drops the marks.</para>
/// </remarks>
public sealed class QueueModel(Random random)
{
    private readonly object _sync = new();
    private readonly List<TrackInfo> _items = [];
    private int _currentIndex = -1;

    // Non-null exactly while shuffle is on: for each entry of _items, its position
    // in the original (unshuffled) order. Always a permutation of 0..Count-1.
    private List<int>? _ranks;

    // The track instances autoplay added (by reference: identity, like the queue rows).
    private readonly HashSet<TrackInfo> _autoplay = new(ReferenceEqualityComparer.Instance);

    public QueueModel() : this(Random.Shared) { }

    private RepeatMode _repeat = RepeatMode.Off;

    /// <summary>The repeat mode. A change raises <see cref="Changed"/>, so every
    /// surface that shows it (player bar, OS media session) follows.</summary>
    public RepeatMode Repeat
    {
        get { lock (_sync) return _repeat; }
        set
        {
            lock (_sync)
            {
                if (value == _repeat)
                    return;
                _repeat = value;
            }
            Changed?.Invoke();
        }
    }

    public bool Shuffle
    {
        get { lock (_sync) return _ranks is not null; }
    }

    public event Action? Changed;

    public IReadOnlyList<TrackInfo> Items
    {
        get { lock (_sync) return _items.ToArray(); }
    }

    public int CurrentIndex
    {
        get { lock (_sync) return _currentIndex; }
    }

    public TrackInfo? Current
    {
        get { lock (_sync) return CurrentUnsafe; }
    }

    private TrackInfo? CurrentUnsafe =>
        _currentIndex >= 0 && _currentIndex < _items.Count ? _items[_currentIndex] : null;

    public void SetQueue(IEnumerable<TrackInfo> tracks, int startIndex = 0)
    {
        lock (_sync)
        {
            _items.Clear();
            _items.AddRange(tracks);
            _autoplay.Clear();
            _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(startIndex, 0, _items.Count - 1);

            if (_ranks is not null)
            {
                _ranks = Enumerable.Range(0, _items.Count).ToList();
                if (_currentIndex > 0)
                {
                    SwapUnsafe(0, _currentIndex);
                    _currentIndex = 0;
                }
                ShuffleRangeUnsafe(_currentIndex + 1, _items.Count);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Turns shuffle on or off. See the class remarks for what each direction does
    /// to the order. Setting the mode it's already in changes nothing.
    /// </summary>
    public void SetShuffle(bool on)
    {
        lock (_sync)
        {
            if (on == (_ranks is not null))
                return;

            if (on)
            {
                _ranks = Enumerable.Range(0, _items.Count).ToList();
                ShuffleRangeUnsafe(_currentIndex + 1, _items.Count);
            }
            else
            {
                var original = new TrackInfo[_items.Count];
                for (var i = 0; i < _items.Count; i++)
                    original[_ranks![i]] = _items[i];
                if (_currentIndex >= 0)
                    _currentIndex = _ranks![_currentIndex];
                _items.Clear();
                _items.AddRange(original);
                _ranks = null;
            }
        }
        Changed?.Invoke();
    }

    private void ShuffleRangeUnsafe(int start, int end)
    {
        start = Math.Max(start, 0);
        for (var i = end - 1; i > start; i--)
            SwapUnsafe(i, random.Next(start, i + 1));
    }

    private void SwapUnsafe(int a, int b)
    {
        (_items[a], _items[b]) = (_items[b], _items[a]);
        if (_ranks is not null)
            (_ranks[a], _ranks[b]) = (_ranks[b], _ranks[a]);
    }

    /// <summary>Makes room at original position <paramref name="rank"/>: every later
    /// original position moves up one.</summary>
    private void OpenRankUnsafe(int rank)
    {
        for (var i = 0; i < _ranks!.Count; i++)
        {
            if (_ranks[i] >= rank)
                _ranks[i]++;
        }
    }

    /// <summary>The queue as it stands, in one piece (reading <see cref="Items"/> and
    /// <see cref="CurrentIndex"/> separately can straddle a change).</summary>
    public QueueSnapshot Snapshot()
    {
        lock (_sync)
            return new QueueSnapshot(
                _items.ToArray(), _currentIndex, _repeat, _ranks?.ToArray(),
                _autoplay.Count == 0 ? null : AutoplayIndicesUnsafe());
    }

    /// <summary>
    /// Puts a saved queue back: tracks in their saved order, current position,
    /// repeat mode and shuffle mode (with its memory of the original order, so
    /// turning shuffle off later still restores it). Nothing is reshuffled and
    /// nothing starts playing, which is the point of restoring a session rather
    /// than resuming it. An out-of-range index is clamped; ranks that aren't a
    /// permutation of the tracks restore with shuffle off.
    /// </summary>
    public void Restore(QueueSnapshot snapshot)
    {
        lock (_sync)
        {
            _repeat = snapshot.Repeat;
            _items.Clear();
            _items.AddRange(snapshot.Tracks);
            _currentIndex = _items.Count == 0 ? -1 : Math.Clamp(snapshot.CurrentIndex, 0, _items.Count - 1);
            _ranks = snapshot.ShuffleRanks is { } ranks && QueueSnapshot.IsPermutation(ranks, _items.Count)
                ? ranks.ToList()
                : null;
            _autoplay.Clear();
            foreach (var i in snapshot.AutoplayIndices ?? [])
            {
                if (i >= 0 && i < _items.Count)
                    _autoplay.Add(_items[i]);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>"Add to queue": after the user's upcoming tracks, ahead of any
    /// upcoming autoplay tracks (see the class remarks).</summary>
    public void Enqueue(TrackInfo track)
    {
        lock (_sync)
        {
            var at = FirstUpcomingAutoplayUnsafe();
            if (at < 0)
            {
                _ranks?.Add(_items.Count);
                _items.Add(track);
            }
            else
            {
                if (_ranks is not null)
                {
                    // In the original order it goes where the autoplay run begins.
                    var rank = int.MaxValue;
                    for (var i = at; i < _items.Count; i++)
                    {
                        if (_autoplay.Contains(_items[i]))
                            rank = Math.Min(rank, _ranks[i]);
                    }
                    OpenRankUnsafe(rank);
                    _ranks.Insert(at, rank);
                }
                _items.Insert(at, track);
            }
            if (_currentIndex < 0)
                _currentIndex = 0;
        }
        Changed?.Invoke();
    }

    private int FirstUpcomingAutoplayUnsafe()
    {
        if (_autoplay.Count == 0)
            return -1;
        for (var i = _currentIndex + 1; i < _items.Count; i++)
        {
            if (_autoplay.Contains(_items[i]))
                return i;
        }
        return -1;
    }

    private int[] AutoplayIndicesUnsafe()
    {
        var indices = new List<int>();
        for (var i = 0; i < _items.Count; i++)
        {
            if (_autoplay.Contains(_items[i]))
                indices.Add(i);
        }
        return indices.ToArray();
    }

    public bool IsAutoplay(TrackInfo track)
    {
        lock (_sync) return _autoplay.Contains(track);
    }

    /// <summary>
    /// Autoplay continuation: appends <paramref name="tracks"/> after
    /// <paramref name="seed"/>, marked as autoplay. Applies only while the seed, the
    /// same instance, is still the last track: if the user queued more, or replaced the
    /// queue, while the tracks were being fetched, the queue no longer runs out there
    /// and the late arrivals are dropped. Tracks already queued are skipped. With
    /// shuffle on, they are shuffled among themselves, like a radio's.
    /// </summary>
    /// <returns>How many tracks were added.</returns>
    public int AppendAutoplay(TrackInfo seed, IEnumerable<TrackInfo> tracks)
    {
        var added = 0;
        lock (_sync)
        {
            if (_items.Count == 0 || !ReferenceEquals(_items[^1], seed))
                return 0;

            var queued = new HashSet<string>(_items.Select(t => t.VideoId));
            foreach (var track in tracks)
            {
                if (queued.Add(track.VideoId))
                {
                    _ranks?.Add(_items.Count);
                    _items.Add(track);
                    _autoplay.Add(track);
                    added++;
                }
            }

            if (_ranks is not null)
                ShuffleRangeUnsafe(Math.Max(_items.Count - added, _currentIndex + 1), _items.Count);
        }

        if (added > 0)
            Changed?.Invoke();
        return added;
    }

    public void EnqueueNext(TrackInfo track)
    {
        lock (_sync)
        {
            if (_currentIndex < 0)
            {
                _ranks?.Add(_items.Count);
                _items.Add(track);
                _currentIndex = 0;
            }
            else
            {
                if (_ranks is not null)
                {
                    var rank = _ranks[_currentIndex] + 1;
                    OpenRankUnsafe(rank);
                    _ranks.Insert(_currentIndex + 1, rank);
                }
                _items.Insert(_currentIndex + 1, track);
            }
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Appends <paramref name="tracks"/> to the queue <paramref name="seed"/> started: the
    /// second half of "play this song", where the song plays at once and the tracks that
    /// follow it (its radio) arrive later. Applies only while the seed, the same instance,
    /// is still the head of the queue. If anything replaced the queue in the meantime, the
    /// late arrivals are dropped rather than grafted onto the user's new choice. Tracks
    /// already queued are skipped, including the seed itself, since a radio leads with it.
    /// </summary>
    /// <returns>How many tracks were added.</returns>
    public int ExtendFromSeed(TrackInfo seed, IEnumerable<TrackInfo> tracks)
    {
        var added = 0;
        lock (_sync)
        {
            if (_items.Count == 0 || !ReferenceEquals(_items[0], seed))
                return 0;

            var queued = new HashSet<string>(_items.Select(t => t.VideoId));
            foreach (var track in tracks)
            {
                if (queued.Add(track.VideoId))
                {
                    _ranks?.Add(_items.Count);
                    _items.Add(track);
                    added++;
                }
            }

            if (_ranks is not null)
                ShuffleRangeUnsafe(Math.Max(_items.Count - added, _currentIndex + 1), _items.Count);
        }

        if (added > 0)
            Changed?.Invoke();
        return added;
    }

    public TrackInfo? PeekNext(TrackEndReason reason)
    {
        lock (_sync) return NextIndexUnsafe(reason) is { } i ? _items[i] : null;
    }

    /// <summary>Advances to the next track, or returns null (index unchanged) if there is none.</summary>
    public TrackInfo? NextTrack(TrackEndReason reason)
    {
        TrackInfo? track;
        lock (_sync)
        {
            if (NextIndexUnsafe(reason) is not { } i)
                return null;
            _currentIndex = i;
            track = _items[i];
        }
        Changed?.Invoke();
        return track;
    }

    private int? NextIndexUnsafe(TrackEndReason reason)
    {
        if (_items.Count == 0 || _currentIndex < 0)
            return null;

        if (reason == TrackEndReason.Natural && _repeat == RepeatMode.One)
            return _currentIndex;

        if (_currentIndex + 1 < _items.Count)
            return _currentIndex + 1;

        return _repeat == RepeatMode.All ? 0 : null;
    }

    /// <summary>Moves to the previous track, or returns null (index unchanged) at the start.</summary>
    public TrackInfo? PreviousTrack()
    {
        TrackInfo? track;
        lock (_sync)
        {
            if (_currentIndex <= 0)
                return null;
            _currentIndex--;
            track = _items[_currentIndex];
        }
        Changed?.Invoke();
        return track;
    }

    public TrackInfo? JumpTo(int index)
    {
        TrackInfo? track;
        lock (_sync)
        {
            if (index < 0 || index >= _items.Count)
                return null;
            _currentIndex = index;
            track = _items[index];
        }
        Changed?.Invoke();
        return track;
    }

    /// <summary>
    /// Puts <paramref name="replacement"/> in place of the current track, if the
    /// current track is still <paramref name="expected"/> (the same instance): an
    /// album row that plays its listed video after all. It keeps its place,
    /// its shuffle rank and whether autoplay added it.
    /// </summary>
    public bool ReplaceCurrent(TrackInfo expected, TrackInfo replacement)
    {
        lock (_sync)
        {
            if (!ReferenceEquals(CurrentUnsafe, expected))
                return false;
            _items[_currentIndex] = replacement;
            if (_autoplay.Contains(expected))
            {
                _autoplay.Add(replacement);
                if (!_items.Contains(expected, ReferenceEqualityComparer.Instance))
                    _autoplay.Remove(expected);
            }
        }
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes the track at <paramref name="index"/>. The current track cannot be removed.</summary>
    public bool Remove(int index)
    {
        lock (_sync)
        {
            if (index < 0 || index >= _items.Count || index == _currentIndex)
                return false;
            var removed = _items[index];
            _items.RemoveAt(index);
            if (!_items.Contains(removed, ReferenceEqualityComparer.Instance))
                _autoplay.Remove(removed);
            if (_ranks is not null)
            {
                var rank = _ranks[index];
                _ranks.RemoveAt(index);
                for (var i = 0; i < _ranks.Count; i++)
                {
                    if (_ranks[i] > rank)
                        _ranks[i]--;
                }
            }
            if (index < _currentIndex)
                _currentIndex--;
        }
        Changed?.Invoke();
        return true;
    }

    public void Move(int from, int to)
    {
        lock (_sync)
        {
            if (from < 0 || from >= _items.Count || to < 0 || to >= _items.Count || from == to)
                return;

            var track = _items[from];
            _items.RemoveAt(from);
            _items.Insert(to, track);
            // Placed by hand: the user's choice now, not autoplay's.
            _autoplay.Remove(track);
            if (_ranks is not null)
            {
                // A manual move reorders what plays, not the original order.
                var rank = _ranks[from];
                _ranks.RemoveAt(from);
                _ranks.Insert(to, rank);
            }

            if (_currentIndex == from)
                _currentIndex = to;
            else if (from < _currentIndex && to >= _currentIndex)
                _currentIndex--;
            else if (from > _currentIndex && to <= _currentIndex)
                _currentIndex++;
        }
        Changed?.Invoke();
    }
}
