using System.Net;
using System.Text;
using Rambutan.Core.Models;
using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

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

/// <summary>One recorded POST to the Last.fm endpoint, its form decoded.</summary>
internal sealed record LastFmRequest(Uri Uri, IReadOnlyDictionary<string, string> Form)
{
    public string Method => Form["method"];
}

/// <summary>
/// Stands in for ws.audioscrobbler.com. Each API method answers from its own
/// queue of canned responses; an empty queue answers with the method's
/// synthetic fixture (the documented success shape).
/// </summary>
internal sealed class FakeLastFmHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Queue<Func<HttpResponseMessage>>> _responses = [];
    private readonly object _sync = new();

    public List<LastFmRequest> Requests { get; } = [];

    public IEnumerable<LastFmRequest> Calls(string method)
    {
        lock (_sync)
            return Requests.Where(r => r.Method == method).ToArray();
    }

    public void Enqueue(string method, HttpStatusCode status, string body)
    {
        lock (_sync)
        {
            if (!_responses.TryGetValue(method, out var queue))
                _responses[method] = queue = new();
            queue.Enqueue(() => Json(status, body));
        }
    }

    public void EnqueueError(string method, int code, HttpStatusCode status = HttpStatusCode.BadRequest) =>
        Enqueue(method, status, $$"""{"error":{{code}},"message":"synthetic error {{code}}"}""");

    public void EnqueueNetworkFailure(string method)
    {
        lock (_sync)
        {
            if (!_responses.TryGetValue(method, out var queue))
                _responses[method] = queue = new();
            queue.Enqueue(() => throw new HttpRequestException("no route to host"));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        var form = body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : ""));
        var recorded = new LastFmRequest(request.RequestUri!, form);

        Func<HttpResponseMessage>? respond = null;
        lock (_sync)
        {
            Requests.Add(recorded);
            if (_responses.TryGetValue(recorded.Method, out var queue) && queue.Count > 0)
                respond = queue.Dequeue();
        }

        return respond is not null ? respond() : Json(HttpStatusCode.OK, Fixture(DefaultFixture(recorded)));
    }

    private static string DefaultFixture(LastFmRequest request) => request.Method switch
    {
        "track.scrobble" when request.Form.ContainsKey("artist[1]") => "track.scrobble.batch",
        "track.scrobble" => "track.scrobble.single",
        var method => method,
    };

    public static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "LastFm", $"{name}.synthetic.json"));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };
}

internal sealed class MemorySessionStore : ILastFmSessionStore
{
    public LastFmSession? Saved { get; set; }
    public int Clears { get; private set; }

    public LastFmSession? Load() => Saved;

    public void Save(LastFmSession session) => Saved = session;

    public void Clear()
    {
        Saved = null;
        Clears++;
    }
}

/// <summary>A fresh directory under the temp folder, deleted with the test.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "rambutan-lastfm-" + Guid.NewGuid().ToString("N"));

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
