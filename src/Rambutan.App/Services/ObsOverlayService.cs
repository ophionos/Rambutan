using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;
using Rambutan.Core.Remote;
using Rambutan.LocalApi;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.Services;

public enum OverlayStatus
{
    Off,
    Starting,
    Listening,
    Failed,
}

/// <summary>
/// The OBS overlay: a page that OBS's browser source shows, with what
/// plays, its progress and cover, and visualizer bars. It runs only while its
/// setting is on.
/// <para>
/// It has its own host, bound to loopback, rather than sharing the phone
/// remote's: the overlay's client (OBS) is on this computer, so nothing about it
/// needs the network, and the remote turning on or off never restarts it (which
/// would blink the overlay on stream). It has its own token too, which only
/// reads: OBS keeps the URL in scene collections that streamers share, and that
/// URL must not control the player.
/// </para>
/// </summary>
public sealed class ObsOverlayService
{
    private readonly PlaybackCoordinator _coordinator;
    private readonly RemoteTokenVault _vault;
    private readonly OverlayStyleStore _styles;
    private readonly CoverSource _covers;
    private readonly BandReader? _bands;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly OverlayConfigSource _style;

    private volatile bool _wanted;
    private RemoteSession? _session;
    private LocalApiHost? _host;
    private volatile string? _token;

    /// <param name="covers">Where covers come from.</param>
    /// <param name="bands">The analyzer's raw bands; null leaves the bars out.</param>
    /// <param name="port">The port to try first; tests pass 0.</param>
    public ObsOverlayService(
        PlaybackCoordinator coordinator, RemoteTokenVault vault, OverlayStyleStore styles,
        CoverSource covers,
        BandReader? bands, int port = OverlayOptions.DefaultPort)
    {
        _coordinator = coordinator;
        _vault = vault;
        _styles = styles;
        _covers = covers;
        _bands = bands;
        PreferredPort = port;

        var loaded = styles.Load();
        _style = new OverlayConfigSource(loaded.Config);
        SavedStyle = loaded.Config;
        IsCustomStyle = loaded.IsCustom;
        StyleProblem = loaded.Problem;
    }

    public OverlayStatus Status { get; private set; }

    /// <summary>Why the last start failed, while <see cref="Status"/> is Failed.</summary>
    public LocalApiFailure? Failure { get; private set; }

    public int Port { get; private set; }

    /// <summary>The port tried first: this data folder's own.</summary>
    public int PreferredPort { get; }

    /// <summary>The usual port was taken, so the URL in OBS must be pasted again.</summary>
    public bool PreferredPortBusy { get; private set; }

    public int ConnectedClients => _host?.OverlayClients ?? 0;

    public OverlayConfig Style => _style.Current;

    /// <summary>The style is the user's own (imported), not the default.</summary>
    public bool IsCustomStyle { get; private set; }

    /// <summary>Why the saved style couldn't be used at start; None when it could.</summary>
    public OverlayConfigProblem StyleProblem { get; private set; }

    /// <summary>Raised on any thread when anything above changes.</summary>
    public event Action? Changed;

    /// <summary>The address OBS opens, token included. 127.0.0.1 rather than
    /// localhost: the host binds IPv4 loopback, and localhost may resolve to ::1.
    /// The token rides in the fragment, which never reaches a server log.</summary>
    public string? Url => Status == OverlayStatus.Listening && _token is { } token ? $"{Address}#t={token}" : null;

    /// <summary>The address without the token, for showing on screen (which may be on stream).</summary>
    public string Address => $"http://127.0.0.1:{Port}/overlay/";

    public Task SetEnabledAsync(bool enabled)
    {
        _wanted = enabled;
        return ApplyAsync();
    }

    /// <summary>A new token: the URL in OBS stops working until it is pasted again.</summary>
    public async Task ResetLinkAsync()
    {
        _token = await Task.Run(_vault.Rotate).ConfigureAwait(false);
        _host?.DisconnectClients();
        AppLog.Write("overlay", "link reset");
        Changed?.Invoke();
    }

    /// <summary>Stops the host if it runs; for the app's exit.</summary>
    public Task StopAsync() => SetEnabledAsync(false);

    /// <summary>Reads a style the user picked. A usable one replaces the current
    /// style, is kept for the next start, and reaches open overlays at once.</summary>
    public OverlayConfigProblem ImportStyle(string json)
    {
        var result = OverlayConfigFormat.Parse(json);
        if (result.Config is not { } config)
            return result.Problem;
        _styles.Save(config);
        Apply(config, custom: true);
        return OverlayConfigProblem.None;
    }

    /// <summary>The style that is kept: the saved file's, or the default. While
    /// the editor previews a change, <see cref="Style"/> differs from this.</summary>
    public OverlayConfig SavedStyle { get; private set; }

    /// <summary>What plays now, for the editor's preview.</summary>
    public Core.Models.TrackInfo? NowPlaying => _coordinator.NowPlaying;

    /// <summary>Shows <paramref name="config"/> on open overlays at once without
    /// keeping it (the editor's live preview). <see cref="PreviewStyle"/>
    /// with <see cref="SavedStyle"/> undoes it.</summary>
    public void PreviewStyle(OverlayConfig config) => _style.Current = config;

    /// <summary>Keeps <paramref name="config"/> as the style and shows it. The
    /// default needs no file, so saving it deletes the file and a later default
    /// reaches the user.</summary>
    public void SaveStyle(OverlayConfig config)
    {
        var custom = config != OverlayConfig.Default;
        if (custom)
            _styles.Save(config);
        else
            _styles.Delete();
        Apply(config, custom);
    }

    /// <summary>The current style as a file the user can share.</summary>
    public string ExportStyle() => OverlayConfigFormat.ToFileJson(_style.Current);

    /// <summary>Back to the default style; the saved file goes.</summary>
    public void ResetStyle()
    {
        _styles.Delete();
        Apply(OverlayConfig.Default, custom: false);
    }

    private void Apply(OverlayConfig config, bool custom)
    {
        IsCustomStyle = custom;
        StyleProblem = OverlayConfigProblem.None;
        SavedStyle = config;
        _style.Current = config;
        Changed?.Invoke();
    }

    private async Task ApplyAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_wanted && _host is null)
                await StartCoreAsync().ConfigureAwait(false);
            else if (!_wanted)
                await StopCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        Set(OverlayStatus.Starting);
        _token ??= await Task.Run(_vault.LoadOrCreate).ConfigureAwait(false);
        // Never dispatched: the overlay host serves no commands.
        _session = new RemoteSession(_coordinator, static _ => { }, track => track.ThumbnailUrl is not null);
        var result = await LocalApiHost.StartAsync(new LocalApiOptions
        {
            Address = IPAddress.Loopback,
            Port = PreferredPort,
            FallBackToAnyPort = true,
            Player = _session,
            // The remote's API isn't served here; no remote token is accepted either.
            Token = static () => null,
            ServeRemote = false,
            Artwork = ArtworkAsync,
            Overlay = new OverlayOptions
            {
                Token = () => _token,
                Config = _style,
                Bands = _bands,
            },
        }).ConfigureAwait(false);

        if (result.Host is not { } host)
        {
            _session.Dispose();
            _session = null;
            Failure = result.Failure;
            Set(OverlayStatus.Failed);
            return;
        }

        _host = host;
        host.ClientsChanged += OnClientsChanged;
        Port = host.Port;
        PreferredPortBusy = result.PreferredPortBusy;
        Failure = null;
        AppLog.Write("overlay", $"on: port {Port}");
        Set(OverlayStatus.Listening);
    }

    private async Task StopCoreAsync()
    {
        if (_host is { } host)
        {
            host.ClientsChanged -= OnClientsChanged;
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
            AppLog.Write("overlay", "off");
        }
        _session?.Dispose();
        _session = null;
        Failure = null;
        PreferredPortBusy = false;
        Set(OverlayStatus.Off);
    }

    private void Set(OverlayStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private void OnClientsChanged() => Changed?.Invoke();

    /// <summary>The playing track's cover; the overlay serves no other.</summary>
    private Task<RemoteArtwork?> ArtworkAsync(string videoId, ArtworkSize size, CancellationToken ct) =>
        _covers.GetAsync(_coordinator.NowPlaying is { } track && track.VideoId == videoId ? track : null, size, "overlay", ct);
}
