using Rambutan.Core.Models;
using Rambutan.Integrations.Scrobbling;

namespace Rambutan.Integrations.Tests.Scrobbling;

/// <summary>A clock that moves only when the test says so.</summary>
internal sealed class ManualClock(DateTimeOffset start, bool runsTimers = false) : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>One-shot timers waiting on <see cref="Advance"/> (with runsTimers only).</summary>
    public int PendingTimers
    {
        get
        {
            lock (_timers)
                return _timers.Count;
        }
    }

    /// <summary>Moves time on and, with runsTimers, fires the timers now due.</summary>
    public void Advance(TimeSpan by)
    {
        Now += by;
        ManualTimer[] due;
        lock (_timers)
        {
            due = _timers.Where(t => t.DueAt <= Now).ToArray();
            foreach (var timer in due)
                _timers.Remove(timer);
        }
        foreach (var timer in due)
            timer.Fire();
    }

    /// <summary>Without runsTimers, timers run on real time, so they never fire in a test.</summary>
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (!runsTimers)
            return base.CreateTimer(callback, state, dueTime, period);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                throw new NotSupportedException("Only one-shot timers.");
            lock (clock._timers)
            {
                clock._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan)
                    return true;
                DueAt = clock.Now + dueTime;
                clock._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (clock._timers)
                clock._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>A fresh directory under the temp folder, deleted with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "rambutan-scrobble-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class Tracks
{
    public static TrackInfo Make(
        string title = "Instant Crush", TimeSpan? duration = null, string? album = "Random Access Memories",
        params string[] artists) => new()
    {
        VideoId = "v-" + title,
        Title = title,
        Artists = (artists.Length == 0 ? ["Daft Punk", "Julian Casablancas"] : artists)
            .Select(a => new ArtistRef(a)).ToArray(),
        Album = album,
        Duration = duration ?? TimeSpan.FromMinutes(5),
    };

    public static ScrobbleEntry Entry(string track, long timestamp, string artist = "Daft Punk") => new()
    {
        Artist = artist,
        Track = track,
        Timestamp = timestamp,
    };
}
