using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The stations' rows in the Settings page's ListenBrainz section: their own
/// token, which goes only to ListenBrainz itself, apart from the scrobbling
/// connection. Connecting it is what turns the artists' ListenBrainz stations on.
/// </summary>
public partial class ListenBrainzStationsSettingsViewModel : ViewModelBase
{
    private readonly ListenBrainzStationsAccount _account;
    private readonly AppSettings? _app;
    private readonly Action? _persist;
    private readonly HomeStations? _homeStations;

    /// <summary>Show the playlists ListenBrainz made for the user on Home
    /// (<see cref="AppSettings.ListenBrainzCreatedForOnHome"/>); it can be
    /// changed only while <see cref="Connected"/>.</summary>
    [ObservableProperty] private bool _createdForOnHome;

    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _notice = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _busy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private string _token = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowConnect), nameof(CanConnect))]
    private bool _connected;

    /// <param name="app">Holds the Created for you setting; null leaves it unsaved.</param>
    /// <param name="homeStations">Told when that setting changes, so Home adds or removes the shelves.</param>
    public ListenBrainzStationsSettingsViewModel(
        ListenBrainzStationsAccount account, AppSettings? app = null, Action? persist = null,
        HomeStations? homeStations = null)
    {
        _account = account;
        _app = app;
        _persist = persist;
        _homeStations = homeStations;
        _createdForOnHome = app?.ListenBrainzCreatedForOnHome ?? false;
        Refresh();
        account.Changed += () => Dispatcher.UIThread.Post(Refresh);
    }

    /// <summary>The token field and Connect show while no token is connected.</summary>
    public bool ShowConnect => !Connected;

    public bool CanConnect => ShowConnect && !Busy && Token.Trim().Length > 0;

    /// <summary>Re-reads the account's state and re-words it. UI thread.</summary>
    public void Refresh()
    {
        Connected = _account.Connected;
        Busy = _account.Busy;
        Status = Busy
            ? Strings.Get("settings.listenbrainz.checking")
            : Connected
                ? Strings.Format("settings.listenbrainz.status.connected", ("user", _account.Username))
                : Strings.Get("settings.listenbrainz.status.notConnected");
        Notice = _account.Notice is { } notice ? NoticeText(notice) : "";
    }

    public static string NoticeText(ListenBrainzStationsNotice notice) => Strings.Get(notice switch
    {
        ListenBrainzStationsNotice.TokenInvalid => "settings.listenbrainz.notice.connectTokenInvalid",
        ListenBrainzStationsNotice.RateLimited => "settings.listenbrainz.notice.connectRateLimited",
        ListenBrainzStationsNotice.Refused => "settings.listenbrainz.notice.connectRefused",
        ListenBrainzStationsNotice.TokenRefused => "settings.listenbrainz.stations.notice.tokenRefused",
        // The scrobbling section's words ask to check a server address, which the stations don't have.
        _ => "settings.listenbrainz.stations.notice.unreachable",
    });

    [RelayCommand]
    private async Task ConnectAsync()
    {
        if (!CanConnect)
            return;
        Busy = true;
        Status = Strings.Get("settings.listenbrainz.checking");
        Notice = "";
        try
        {
            await _account.ConnectAsync(Token);
        }
        finally
        {
            Refresh();
        }
        // The vault has it now; the field shouldn't keep it on screen.
        if (Connected)
            Token = "";
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        await _account.DisconnectAsync();
        Refresh();
    }

    partial void OnCreatedForOnHomeChanged(bool value)
    {
        if (_app is not null)
            _app.ListenBrainzCreatedForOnHome = value;
        if (_homeStations is not null)
            _homeStations.CreatedForEnabled = value;
        _persist?.Invoke();
    }
}
