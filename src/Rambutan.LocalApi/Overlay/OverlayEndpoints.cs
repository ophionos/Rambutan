using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Rambutan.Core.Remote;

namespace Rambutan.LocalApi.Overlay;

/// <summary>
/// The OBS overlay's routes. The page is open; everything under
/// <c>/api/v1/overlay/</c> needs the overlay's own token as a Bearer header and a
/// client on this computer. That token only reads: what plays, its cover, the
/// style and the visualizer's bands. It can't send a command, so an OBS scene
/// collection that carries the URL gives away no control of the player.
/// </summary>
internal sealed class OverlayEndpoints : IDisposable
{
    /// <summary>Open overlay streams allowed at once (each page holds two).</summary>
    internal const int MaxStreams = 16;

    /// <summary>How often a band frame may go out: the analyzer makes ~47 a
    /// second; the page smooths between them, so 30 is plenty.</summary>
    internal static TimeSpan BandInterval { get; set; } = TimeSpan.FromMilliseconds(33);

    private readonly IRemotePlayer _player;
    private readonly OverlayOptions _options;
    private readonly Func<string, ArtworkSize, CancellationToken, Task<RemoteArtwork?>>? _artwork;
    private readonly RemoteEndpoints.ChangeSignal _playerChanges = new();
    private readonly RemoteEndpoints.ChangeSignal _configChanges = new();
    private readonly Lock _sync = new();
    private CancellationTokenSource _disconnect = new();
    private int _streams;
    private int _pages;

    public OverlayEndpoints(IRemotePlayer player, OverlayOptions options,
        Func<string, ArtworkSize, CancellationToken, Task<RemoteArtwork?>>? artwork)
    {
        _player = player;
        _options = options;
        _artwork = artwork;
        _player.Changed += _playerChanges.Pulse;
        _options.Config.Changed += _configChanges.Pulse;
    }

    /// <summary>Overlay pages following the state now (bands streams aren't counted).</summary>
    public int ConnectedClients => Volatile.Read(ref _pages);

    public event Action? ClientsChanged;

    /// <param name="ownsRoot">This host serves nothing else, so "/" leads to the overlay.</param>
    public void Map(WebApplication app, bool ownsRoot)
    {
        app.Use(RequireToken);

        if (ownsRoot)
        {
            app.MapGet("/", (RequestDelegate)(ctx =>
            {
                ctx.Response.Redirect("/overlay/");
                return Task.CompletedTask;
            }));
        }
        app.MapGet("/overlay/{**path}", (RequestDelegate)(ctx =>
        {
            var path = (string?)ctx.Request.RouteValues["path"];
            if (!string.IsNullOrEmpty(path))
                return StaticAssets.ServeAsync(ctx, "overlay/" + path);
            if (!ctx.Request.Path.Value!.EndsWith('/'))
            {
                // The page's asset URLs are relative to the trailing slash. A
                // redirect keeps the URL's fragment, and the token with it.
                ctx.Response.Redirect("/overlay/");
                return Task.CompletedTask;
            }
            return StaticAssets.ServeAsync(ctx, "overlay/index.html");
        }));

        app.MapGet("/api/v1/overlay/config", (RequestDelegate)(ctx =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            ctx.Response.ContentType = "application/json; charset=utf-8";
            return ctx.Response.WriteAsync(OverlayConfigFormat.ToWireJson(_options.Config.Current), ctx.RequestAborted);
        }));
        app.MapGet("/api/v1/overlay/events", (RequestDelegate)EventsAsync);
        app.MapGet("/api/v1/overlay/bands", (RequestDelegate)BandsAsync);
        app.MapGet("/api/v1/overlay/artwork", (RequestDelegate)ArtworkAsync);
    }

    internal static bool IsOverlayApi(PathString path) =>
        path.StartsWithSegments("/api/v1/overlay", StringComparison.Ordinal);

    private Task RequireToken(HttpContext ctx, RequestDelegate next)
    {
        if (!IsOverlayApi(ctx.Request.Path))
            return next(ctx);

        // The overlay is for programs on this computer. Its host binds loopback
        // only; this holds even if the overlay ever shares a host on the network.
        if (ctx.Connection.RemoteIpAddress is not { } peer || !IPAddress.IsLoopback(peer))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }

        var header = ctx.Request.Headers.Authorization.ToString();
        var presented = header.StartsWith("Bearer ", StringComparison.Ordinal) ? header["Bearer ".Length..].Trim() : null;
        if (PairingToken.Matches(_options.Token(), presented))
            return next(ctx);

        ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
        ctx.Response.Headers.WWWAuthenticate = "Bearer";
        return Task.CompletedTask;
    }

    // Only the cover of what plays now: the host is no general image proxy.
    private Task ArtworkAsync(HttpContext ctx) =>
        ArtworkResponse.WriteAsync(ctx, _artwork, _player.Current.Track is { HasArtwork: true } track ? track.VideoId : null);

    /// <summary>Server-sent events: the style and the state now, then each new
    /// one as it happens. The style arrives the same way, so a page restyles
    /// itself the moment the user imports one, with no reload in OBS.</summary>
    private async Task EventsAsync(HttpContext ctx)
    {
        if (!TryOpenStream(ctx, out var ct, out var linked))
            return;
        Interlocked.Increment(ref _pages);
        ClientsChanged?.Invoke();

        using (linked)
        {
            try
            {
                long sentState = -1, sentConfig = -1;
                while (!ct.IsCancellationRequested)
                {
                    var (configVersion, configNext) = _configChanges.Current;
                    var (stateVersion, stateNext) = _playerChanges.Current;
                    if (configVersion != sentConfig)
                    {
                        sentConfig = configVersion;
                        var json = OverlayConfigFormat.ToWireJson(_options.Config.Current);
                        await RemoteEndpoints.WriteAsync(ctx, $"event: config\ndata: {json}\n\n", ct);
                    }
                    if (stateVersion != sentState)
                    {
                        sentState = stateVersion;
                        var json = JsonSerializer.Serialize(_player.Current, LocalApiJsonContext.Default.RemoteState);
                        await RemoteEndpoints.WriteAsync(ctx, $"event: state\ndata: {json}\n\n", ct);
                    }

                    var heartbeat = Task.Delay(RemoteEndpoints.Heartbeat, ct);
                    if (await Task.WhenAny(stateNext, configNext, heartbeat) == heartbeat)
                    {
                        await heartbeat; // throws when cancelled
                        await RemoteEndpoints.WriteAsync(ctx, ": ping\n\n", ct);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // The page went away, or the host ended the stream.
            }
            finally
            {
                Interlocked.Decrement(ref _pages);
                CloseStream();
                ClientsChanged?.Invoke();
            }
        }
    }

    /// <summary>The analyzer's raw bands as events, at most every
    /// <see cref="BandInterval"/> and only when a new frame exists: a paused
    /// player sends nothing, and the page lets its bars fall. The page does all
    /// the smoothing, the peaks and the styling, as the app's visualizer does.</summary>
    private async Task BandsAsync(HttpContext ctx)
    {
        if (_options.Bands is not { } read)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (!TryOpenStream(ctx, out var ct, out var linked))
            return;

        using (linked)
        {
            try
            {
                var bands = new float[OverlayOptions.BandCount];
                var sent = long.MinValue;
                var quiet = Stopwatch.StartNew();
                using var timer = new PeriodicTimer(BandInterval);
                // An empty comment first, so the client sees the stream open at once.
                await RemoteEndpoints.WriteAsync(ctx, ": bands\n\n", ct);
                while (await timer.WaitForNextTickAsync(ct))
                {
                    var frame = read(bands);
                    if (frame >= 0 && frame != sent)
                    {
                        sent = frame;
                        quiet.Restart();
                        await RemoteEndpoints.WriteAsync(ctx, BandsEvent(frame, bands), ct);
                    }
                    else if (quiet.Elapsed >= RemoteEndpoints.Heartbeat)
                    {
                        quiet.Restart();
                        await RemoteEndpoints.WriteAsync(ctx, ": ping\n\n", ct);
                    }
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
            }
            finally
            {
                CloseStream();
            }
        }
    }

    /// <summary><c>{"frame":n,"bands":[…]}</c> with three decimals, written by hand:
    /// thirty times a second, and a fixed shape.</summary>
    internal static string BandsEvent(long frame, ReadOnlySpan<float> bands)
    {
        var text = new StringBuilder(32 + bands.Length * 6);
        text.Append("event: bands\ndata: {\"frame\":").Append(frame.ToString(CultureInfo.InvariantCulture)).Append(",\"bands\":[");
        for (var i = 0; i < bands.Length; i++)
        {
            if (i > 0)
                text.Append(',');
            var value = float.IsFinite(bands[i]) ? Math.Clamp(bands[i], 0f, 1f) : 0f;
            text.Append(value.ToString("0.###", CultureInfo.InvariantCulture));
        }
        return text.Append("]}\n\n").ToString();
    }

    private bool TryOpenStream(HttpContext ctx, out CancellationToken ct, out CancellationTokenSource linked)
    {
        if (Interlocked.Increment(ref _streams) > MaxStreams)
        {
            Interlocked.Decrement(ref _streams);
            ctx.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            ct = default;
            linked = null!;
            return false;
        }

        CancellationToken disconnect;
        lock (_sync) disconnect = _disconnect.Token;
        linked = CancellationTokenSource.CreateLinkedTokenSource(ctx.RequestAborted, disconnect);
        ct = linked.Token;
        ctx.Response.ContentType = "text/event-stream";
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        return true;
    }

    private void CloseStream() => Interlocked.Decrement(ref _streams);

    /// <summary>Ends every stream; a page must authenticate again to reconnect.</summary>
    public void DisconnectAll()
    {
        CancellationTokenSource old;
        lock (_sync)
        {
            old = _disconnect;
            _disconnect = new CancellationTokenSource();
        }
        _ = Task.Run(() =>
        {
            old.Cancel();
            old.Dispose();
        });
    }

    public void Dispose()
    {
        _player.Changed -= _playerChanges.Pulse;
        _options.Config.Changed -= _configChanges.Pulse;
        lock (_sync) _disconnect.Cancel();
    }
}
