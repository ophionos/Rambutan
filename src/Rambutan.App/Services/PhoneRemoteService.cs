using System;
using System.Collections.Generic;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;
using Rambutan.Core.Remote;
using Rambutan.LocalApi;

namespace Rambutan.App.Services;

public enum PhoneRemoteStatus
{
    Off,
    Starting,
    Listening,
    Failed,
}

/// <summary>
/// The phone remote: the local API host on the LAN, running only while the
/// setting is on. Starts and stops go through one gate, so a quick off-and-on
/// can't leave a host that nothing stops. The pairing token is made on the first
/// start, not at launch: a user who never turns the remote on gets no credential.
/// </summary>
public sealed class PhoneRemoteService
{
    private readonly PlaybackCoordinator _coordinator;
    private readonly Action<Action> _dispatch;
    private readonly RemoteTokenVault _vault;
    private readonly CoverSource _covers;
    private readonly IPAddress _listenAddress;
    private readonly SongSearch? _searchSongs;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _wanted;
    private RemoteSession? _session;
    private LocalApiHost? _host;
    private volatile string? _token;
    private volatile string _accent = RemoteState.DefaultAccent;

    /// <param name="dispatch">Where phone commands run; the app passes its UI thread.</param>
    /// <param name="covers">Where covers come from.</param>
    /// <param name="listenAddress">Every address (the LAN) unless a test says loopback.</param>
    /// <param name="searchSongs">The catalog's song search, for the phone's search.</param>
    public PhoneRemoteService(
        PlaybackCoordinator coordinator, Action<Action> dispatch, RemoteTokenVault vault,
        CoverSource covers,
        IPAddress? listenAddress = null,
        SongSearch? searchSongs = null,
        int port = LocalApiHost.DefaultPort)
    {
        _listenAddress = listenAddress ?? IPAddress.Any;
        PreferredPort = port;
        _searchSongs = searchSongs;
        _coordinator = coordinator;
        _dispatch = dispatch;
        _vault = vault;
        _covers = covers;
    }

    public PhoneRemoteStatus Status { get; private set; }

    /// <summary>Why the last start failed, while <see cref="Status"/> is Failed.</summary>
    public LocalApiFailure? Failure { get; private set; }

    public int Port { get; private set; }

    /// <summary>The port tried first: this data folder's own.</summary>
    public int PreferredPort { get; }

    /// <summary>The usual port was taken, so links bookmarked before don't work now.</summary>
    public bool PreferredPortBusy { get; private set; }

    /// <summary>This machine's addresses, best first; empty when no network is up.</summary>
    public IReadOnlyList<IPAddress> Addresses { get; private set; } = [];

    public int ConnectedClients => _host?.ConnectedClients ?? 0;

    /// <summary>The app's accent as #RRGGBB; the remote's page takes it with
    /// the player's state. Kept while the remote is off, for its next start.</summary>
    public string Accent
    {
        get => _accent;
        set
        {
            _accent = value;
            if (_session is { } session)
                session.Accent = value;
        }
    }

    /// <summary>Raised on any thread when anything above changes.</summary>
    public event Action? Changed;

    /// <summary>The pairing link for an address: the token rides in the fragment,
    /// which the browser keeps to itself.</summary>
    public string? LinkFor(IPAddress address) =>
        Status == PhoneRemoteStatus.Listening && _token is { } token
            ? $"{AddressFor(address)}#t={token}"
            : null;

    /// <summary>The page's address without the token, for showing as text.</summary>
    public string AddressFor(IPAddress address) => $"http://{address}:{Port}/remote/";

    public Task SetEnabledAsync(bool enabled)
    {
        _wanted = enabled;
        return ApplyAsync();
    }

    /// <summary>A new token: phones paired before are refused and disconnected.</summary>
    public async Task ResetPairingAsync()
    {
        // Rotate first, so a phone that reconnects at once meets the new token.
        // Off the UI thread: the keyring may block, or ask the user to unlock it.
        _token = await Task.Run(_vault.Rotate).ConfigureAwait(false);
        _host?.DisconnectClients();
        AppLog.Write("remote", "pairing reset");
        Changed?.Invoke();
    }

    /// <summary>Stops the host if it runs; for the app's exit.</summary>
    public Task StopAsync() => SetEnabledAsync(false);

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
        Set(PhoneRemoteStatus.Starting);
        _token ??= await Task.Run(_vault.LoadOrCreate).ConfigureAwait(false);
        _session = new RemoteSession(
            _coordinator, _dispatch, _covers.Has, searchSongs: _searchSongs)
        {
            Accent = _accent,
        };
        var result = await LocalApiHost.StartAsync(new LocalApiOptions
        {
            Address = _listenAddress,
            Port = PreferredPort,
            FallBackToAnyPort = true,
            Player = _session,
            Library = _session,
            Token = () => _token,
            Artwork = ArtworkAsync,
        }).ConfigureAwait(false);

        if (result.Host is not { } host)
        {
            _session.Dispose();
            _session = null;
            Failure = result.Failure;
            Set(PhoneRemoteStatus.Failed);
            return;
        }

        _host = host;
        host.ClientsChanged += OnClientsChanged;
        Port = host.Port;
        PreferredPortBusy = result.PreferredPortBusy;
        Failure = null;
        Addresses = LanAddresses.Discover();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        AppLog.Write("remote", $"on: port {Port}, {Addresses.Count} address(es)");
        Set(PhoneRemoteStatus.Listening);
    }

    private async Task StopCoreAsync()
    {
        if (_host is { } host)
        {
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            host.ClientsChanged -= OnClientsChanged;
            _host = null;
            await host.DisposeAsync().ConfigureAwait(false);
            AppLog.Write("remote", "off");
        }
        _session?.Dispose();
        _session = null;
        Failure = null;
        PreferredPortBusy = false;
        Set(PhoneRemoteStatus.Off);
    }

    private void Set(PhoneRemoteStatus status)
    {
        Status = status;
        Changed?.Invoke();
    }

    private void OnClientsChanged() => Changed?.Invoke();

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        Addresses = LanAddresses.Discover();
        Changed?.Invoke();
    }

    /// <summary>A cover the phone may see: the playing track's, a queued one's
    /// or a found one's (<see cref="RemoteSession.CoverTrack"/>).</summary>
    private Task<RemoteArtwork?> ArtworkAsync(string videoId, ArtworkSize size, CancellationToken ct) =>
        _covers.GetAsync(_session?.CoverTrack(videoId), size, "remote", ct);
}
