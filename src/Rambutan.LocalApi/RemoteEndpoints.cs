using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Rambutan.Core.Remote;

namespace Rambutan.LocalApi;

/// <summary>
/// The routes. Static pages are open; everything under <c>/api/</c> needs
/// <c>Authorization: Bearer &lt;token&gt;</c>, so an unpaired client gets the
/// page and nothing else. A header rather than a cookie: the browser never
/// attaches it by itself, so another site can't make a paired phone's browser
/// send commands. No CORS headers are sent, ever.
///
/// Every handler is a plain <see cref="RequestDelegate"/> and JSON goes through
/// the source-generated context: no reflection-built delegates (NativeAOT).
/// </summary>
internal sealed class RemoteEndpoints : IDisposable
{
    /// <summary>Live-updates connections allowed at once; more is a misbehaving client.</summary>
    internal const int MaxStreams = 8;

    /// <summary>A comment line sent this often on an idle stream, so a client
    /// notices a dead connection and the server notices a gone client.</summary>
    internal static TimeSpan Heartbeat { get; set; } = TimeSpan.FromSeconds(15);

    private const long MaxSeekMs = 24 * 60 * 60 * 1000;

    private readonly LocalApiOptions _options;
    private readonly ChangeSignal _changes = new();
    private readonly Lock _sync = new();
    private CancellationTokenSource _disconnect = new();
    private int _streams;

    public RemoteEndpoints(LocalApiOptions options)
    {
        _options = options;
        _options.Player.Changed += _changes.Pulse;
    }

    public int ConnectedClients => Volatile.Read(ref _streams);

    public event Action? ClientsChanged;

    public void Map(WebApplication app)
    {
        app.Use(SecurityHeaders);
        app.Use(RequireToken);

        app.MapGet("/", (RequestDelegate)(ctx =>
        {
            ctx.Response.Redirect("/remote/");
            return Task.CompletedTask;
        }));
        // One route for the page and its assets: a template's trailing slash
        // doesn't count, so "/remote" and "/remote/" can't be told apart by routes.
        app.MapGet("/remote/{**path}", (RequestDelegate)(ctx =>
        {
            var path = (string?)ctx.Request.RouteValues["path"];
            if (!string.IsNullOrEmpty(path))
                return StaticAssets.ServeAsync(ctx, "remote/" + path);
            // The page's asset URLs are relative to the trailing slash.
            if (!ctx.Request.Path.Value!.EndsWith('/'))
            {
                ctx.Response.Redirect("/remote/");
                return Task.CompletedTask;
            }
            return StaticAssets.ServeAsync(ctx, "remote/index.html");
        }));

        app.MapGet("/api/v1/state", (RequestDelegate)(ctx =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return ctx.Response.WriteAsJsonAsync(_options.Player.Current, LocalApiJsonContext.Default.RemoteState);
        }));
        app.MapGet("/api/v1/events", (RequestDelegate)StreamAsync);
        app.MapGet("/api/v1/artwork", (RequestDelegate)ArtworkAsync);

        app.MapPost("/api/v1/player/play", Command(new RemoteCommand.Play()));
        app.MapPost("/api/v1/player/pause", Command(new RemoteCommand.Pause()));
        app.MapPost("/api/v1/player/next", Command(new RemoteCommand.Next()));
        app.MapPost("/api/v1/player/previous", Command(new RemoteCommand.Previous()));
        app.MapPost("/api/v1/player/seek", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LocalApiJsonContext.Default.SeekRequest) is not { } body || body.PositionMs < 0)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            // Past the end is the coordinator's to handle; a day caps absurd values.
            Execute(ctx, new RemoteCommand.Seek(TimeSpan.FromMilliseconds(Math.Min(body.PositionMs, MaxSeekMs))));
        }));
        app.MapPost("/api/v1/player/volume", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LocalApiJsonContext.Default.VolumeRequest) is not { } body ||
                !double.IsFinite(body.Volume))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }
            Execute(ctx, new RemoteCommand.SetVolume(Math.Clamp(body.Volume, 0, 1)));
        }));

        if (_options.Library is { } library)
            LibraryEndpoints.Map(app, library);
    }

    internal static Task SecurityHeaders(HttpContext ctx, RequestDelegate next)
    {
        var headers = ctx.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.XFrameOptions = "DENY";
        headers.ContentSecurityPolicy =
            "default-src 'self'; img-src 'self' blob: data:; style-src 'self' 'unsafe-inline'; " +
            "connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        return next(ctx);
    }

    private Task RequireToken(HttpContext ctx, RequestDelegate next)
    {
        // The overlay's routes check its own token (OverlayEndpoints).
        if (!ctx.Request.Path.StartsWithSegments("/api", StringComparison.Ordinal) ||
            Overlay.OverlayEndpoints.IsOverlayApi(ctx.Request.Path))
        {
            return next(ctx);
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
        if (PairingToken.Matches(_options.Token(), presented))
            return next(ctx);

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    private RequestDelegate Command(RemoteCommand command) => ctx =>
    {
        Execute(ctx, command);
        return Task.CompletedTask;
    };

    private void Execute(HttpContext ctx, RemoteCommand command)
    {
        _options.Player.Execute(command);
        // Accepted, not done: the result arrives as a state on the stream.
        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext ctx, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync(type, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }

    private Task ArtworkAsync(HttpContext ctx)
    {
        // The playing track's cover, or (with ?v=) a queued or found track's:
        // never any other id, so the host is no general image proxy.
        var asked = ctx.Request.Query["v"].ToString();
        var current = _options.Player.Current.Track;
        string? videoId = asked.Length == 0 || asked == current?.VideoId
            ? current is { HasArtwork: true } ? current.VideoId : null
            : asked.Length <= 64 && _options.Library?.HasCover(asked) == true ? asked : null;
        var size = ctx.Request.Query["size"] == "row" ? ArtworkSize.Row : ArtworkSize.Full;
        return ArtworkResponse.WriteAsync(ctx, _options.Artwork, videoId, size);
    }

    /// <summary>Server-sent events: the state now, then each new state as it
    /// happens. Read with fetch rather than EventSource, which can't send the
    /// Authorization header.</summary>
    private async Task StreamAsync(HttpContext ctx)
    {
        if (Interlocked.Increment(ref _streams) > MaxStreams)
        {
            Interlocked.Decrement(ref _streams);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }
        ClientsChanged?.Invoke();

        CancellationToken disconnect;
        lock (_sync) disconnect = _disconnect.Token;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, disconnect);
        var ct = linked.Token;

        try
        {
            ctx.Response.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            var sent = -1L;
            while (!ct.IsCancellationRequested)
            {
                var (version, next) = _changes.Current;
                if (version != sent)
                {
                    sent = version;
                    var json = JsonSerializer.Serialize(_options.Player.Current, LocalApiJsonContext.Default.RemoteState);
                    await WriteAsync(ctx, $"event: state\ndata: {json}\n\n", ct);
                }

                var heartbeat = Task.Delay(Heartbeat, ct);
                if (await Task.WhenAny(next, heartbeat) == heartbeat)
                {
                    await heartbeat; // throws when cancelled
                    await WriteAsync(ctx, ": ping\n\n", ct);
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            // The client went away, or the host ended the stream.
        }
        finally
        {
            Interlocked.Decrement(ref _streams);
            ClientsChanged?.Invoke();
        }
    }

    internal static async Task WriteAsync(HttpContext ctx, string text, CancellationToken ct)
    {
        await ctx.Response.Body.WriteAsync(Encoding.UTF8.GetBytes(text), ct);
        await ctx.Response.Body.FlushAsync(ct);
    }

    public void DisconnectAll()
    {
        CancellationTokenSource old;
        lock (_sync)
        {
            old = _disconnect;
            _disconnect = new CancellationTokenSource();
        }
        // Off the caller's thread: cancelling runs the streams' continuations.
        _ = Task.Run(() =>
        {
            old.Cancel();
            old.Dispose();
        });
    }

    public void Dispose()
    {
        _options.Player.Changed -= _changes.Pulse;
        lock (_sync) _disconnect.Cancel();
    }

    /// <summary>A version number and a task that completes when it next changes.
    /// <see cref="Pulse"/> may run under the coordinator's lock: it only swaps a
    /// completion source, whose waiters resume elsewhere.</summary>
    internal sealed class ChangeSignal
    {
        private readonly Lock _sync = new();
        private long _version;
        private TaskCompletionSource _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public (long Version, Task Next) Current
        {
            get
            {
                lock (_sync) return (_version, _next.Task);
            }
        }

        public void Pulse()
        {
            TaskCompletionSource fired;
            lock (_sync)
            {
                _version++;
                fired = _next;
                _next = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            fired.TrySetResult();
        }
    }
}
