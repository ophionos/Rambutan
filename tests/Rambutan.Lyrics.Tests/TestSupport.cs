using System.Net;
using System.Text;
using Rambutan.Lyrics.Providers;

namespace Rambutan.Lyrics.Tests;

/// <summary>Serves canned LRCLIB responses by endpoint and records every request.</summary>
internal sealed class FakeLrcLibHandler : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Answers <c>/api/{endpoint}</c> with a fixture file.</summary>
    public FakeLrcLibHandler Serve(string endpoint, string fixture, HttpStatusCode status = HttpStatusCode.OK) =>
        Serve(endpoint, _ => new HttpResponseMessage(status)
        {
            Content = new StringContent(Fixtures.Read(fixture), Encoding.UTF8, "application/json"),
        });

    public FakeLrcLibHandler Serve(string endpoint, Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _routes["/api/" + endpoint] = respond;
        return this;
    }

    public IEnumerable<string> Endpoints => Requests.Select(r => r.RequestUri!.AbsolutePath);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        return Task.FromResult(_routes.TryGetValue(request.RequestUri!.AbsolutePath, out var respond)
            ? respond(request)
            : new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent(Fixtures.Read("lrclib-not-found.json")),
            });
    }
}

internal static class Fixtures
{
    public static string Read(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));
}

/// <summary>A clock the test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>A provider whose answer the test scripts, counting calls.</summary>
internal sealed class ScriptedProvider(string name, Func<LyricsQuery, CancellationToken, Task<LyricsResult?>> answer)
    : ILyricsProvider
{
    private int _calls;

    public string Name => name;

    public int Calls => Volatile.Read(ref _calls);

    public Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct)
    {
        Interlocked.Increment(ref _calls);
        return answer(query, ct);
    }

    public static ScriptedProvider Returning(string name, LyricsResult? result) =>
        new(name, (_, _) => Task.FromResult(result));

    public static ScriptedProvider Throwing(string name) =>
        new(name, (_, _) => throw new HttpRequestException("connection refused"));

    /// <summary>Never answers; only cancellation ends it.</summary>
    public static ScriptedProvider Hanging(string name) =>
        new(name, async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return null;
        });
}

/// <summary>A fresh directory under the temp folder, deleted afterwards.</summary>
internal sealed class TempDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "rambutan-lyrics-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }
}
