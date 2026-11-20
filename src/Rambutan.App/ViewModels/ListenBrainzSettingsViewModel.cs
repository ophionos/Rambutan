using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The ListenBrainz section of the Settings page. The server is chosen only
/// while disconnected: the vault keeps it with the token, so a token entered
/// for one server never goes to another.
/// </summary>
public partial class ListenBrainzSettingsViewModel : ViewModelBase
{
    private readonly ListenBrainzScrobbler _scrobbler;
    private readonly AppSettings _app;
    private readonly Action? _persist;

    [ObservableProperty] private bool _scrobbling;
    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _notice = "";
    [ObservableProperty] private string _pending = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ServerNote), nameof(CanConnect))]
    private string _serverText = ListenBrainzClient.DefaultServer.AbsoluteUri;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private string _token = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Connected), nameof(ShowConnect), nameof(Recording), nameof(ShowDisconnect), nameof(CanConnect))]
    private ListenBrainzConnectionState _connection;

    /// <summary>The server ended the connection (it refused the token) and plays
    /// are still recorded (<see cref="ListenBrainzScrobbler.SessionEnded"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Recording), nameof(ShowDisconnect))]
    private bool _sessionEnded;

    /// <param name="stations">The stations' own account; null leaves out their rows.</param>
    /// <param name="homeStations">Told when the Created for you setting changes.</param>
    public ListenBrainzSettingsViewModel(
        ListenBrainzScrobbler scrobbler, AppSettings app, Action? persist, ListenBrainzStationsAccount? stations = null,
        HomeStations? homeStations = null)
    {
        _scrobbler = scrobbler;
        _app = app;
        _persist = persist;
        Stations = stations is null ? null : new ListenBrainzStationsSettingsViewModel(stations, app, persist, homeStations);
        _scrobbling = app.ListenBrainzScrobbling;
        Refresh();
        scrobbler.Changed += () => Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>The stations' rows; null when the app has no stations account.</summary>
    public ListenBrainzStationsSettingsViewModel? Stations { get; }

    public bool Connected => Connection == ListenBrainzConnectionState.Connected;

    /// <summary>The server and token fields and Connect show whenever no account is connected.</summary>
    public bool ShowConnect => Connection != ListenBrainzConnectionState.Connected;

    public bool CanConnect => ShowConnect && !Busy && Token.Trim().Length > 0
        && ListenBrainzClient.TryParseServer(ServerText, out _);

    /// <summary>Plays are recorded: connected, or kept after the server ended the
    /// connection. The toggle and the waiting count show then.</summary>
    public bool Recording => Connected || SessionEnded;

    /// <summary>Disconnect is how plays kept after the server ended the connection
    /// are discarded, so it shows then too, beside Connect.</summary>
    public bool ShowDisconnect => Connected || (SessionEnded && Connection == ListenBrainzConnectionState.Disconnected);

    /// <summary>What is wrong with the server field, or that plain http sends the
    /// token unencrypted; empty when neither.</summary>
    public string ServerNote =>
        !ListenBrainzClient.TryParseServer(ServerText, out var server) || server is null
            ? Strings.Get("settings.listenbrainz.server.invalid")
            : ListenBrainzClient.IsUnencrypted(server)
                ? Strings.Get("settings.listenbrainz.server.unencrypted")
                : "";

    partial void OnBusyChanged(bool value) => OnPropertyChanged(nameof(CanConnect));

    /// <summary>Re-reads the scrobbler's state and re-words it. UI thread.</summary>
    public void Refresh()
    {
        // Mid-request, the "Contacting…" line stands until the request answers.
        if (Busy)
            return;
        Connection = _scrobbler.Connection;
        SessionEnded = _scrobbler.SessionEnded;
        // So that after a disconnect the field offers the server last used.
        if (_scrobbler.Server is { } connectedTo)
            ServerText = connectedTo.AbsoluteUri;
        Status = Connection switch
        {
            ListenBrainzConnectionState.Connected when _scrobbler.Server is { } server
                && server != ListenBrainzClient.DefaultServer =>
                Strings.Format("settings.listenbrainz.status.connectedOn", ("user", _scrobbler.Username), ("server", server.Authority)),
            ListenBrainzConnectionState.Connected =>
                Strings.Format("settings.listenbrainz.status.connected", ("user", _scrobbler.Username)),
            ListenBrainzConnectionState.Connecting => Strings.Get("settings.listenbrainz.checking"),
            _ when SessionEnded => Strings.Get("settings.listenbrainz.status.ended"),
            _ => Strings.Get("settings.listenbrainz.status.notConnected"),
        };
        Notice = _scrobbler.Notice is { } notice ? NoticeText.For(notice) : "";
        var pending = _scrobbler.PendingCount;
        Pending = pending == 0
            ? ""
            : Strings.Format(Recording ? "settings.listenbrainz.pendingConnected" : "settings.listenbrainz.pending", ("count", pending));
        OnPropertyChanged(nameof(ServerNote));
    }

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (Busy || !ListenBrainzClient.TryParseServer(ServerText, out var server) || server is null)
            return;
        Busy = true;
        Status = Strings.Get("settings.listenbrainz.checking");
        Notice = "";
        try
        {
            await _scrobbler.ConnectAsync(server, Token);
        }
        finally
        {
            Busy = false;
            Refresh();
        }
        // The vault has it now; the field shouldn't keep it on screen.
        if (Connected)
            Token = "";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _scrobbler.DisconnectAsync();
        Refresh();
    }

    partial void OnScrobblingChanged(bool value)
    {
        _app.ListenBrainzScrobbling = value;
        _scrobbler.ScrobblingEnabled = value;
        _persist?.Invoke();
    }
}
