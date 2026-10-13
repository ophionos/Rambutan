using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Services;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playback;
using Rambutan.InnerTube;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly SidecarBootstrapper? _bootstrapper;
    private readonly Func<SettingsPageViewModel>? _settingsFactory;
    private readonly AppSettings? _settings;
    private readonly DownloadsPageViewModel? _downloads;

    // The status strip is a state line, not a message log:
    // it shows only conditions that are true right now, each in its own slot with
    // its own affordance, and hides when there are none. Outcomes ("Added to
    // queue", "Signed out", errors from an action) are toasts, which expire.

    /// <summary>Sidecar preparation in progress; empty once it has settled.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private string _sidecarProgress = "";

    /// <summary>Sidecar preparation failed. Browsing is unaffected; only playback
    /// waits on the sidecars, so the strip carries a Retry.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatus))]
    private bool _sidecarsFailed;

    [ObservableProperty] private string _sidecarError = "";

    /// <summary>The track that lost the network mid-play and waits for it
    /// (<see cref="PlaybackCoordinator.TrackWaitingForNetwork"/>). The wait can last
    /// minutes, and until it ends the player bar shows only Buffering.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NetworkWaitText), nameof(HasStatus))]
    private Rambutan.Core.Models.TrackInfo? _trackWaitingForNetwork;

    /// <summary>Empty when nothing waits: the strip's text goes from empty to the
    /// message as the wait starts, which is what a screen reader announces.</summary>
    public string NetworkWaitText => TrackWaitingForNetwork is { } track
        ? Strings.Format("status.waitingForNetwork", ("title", track.Title))
        : "";

    private bool _sidecarsSettled;

    public bool HasStatus =>
        SidecarProgress.Length > 0 || SidecarsFailed || ShowSessionNotice ||
        TrackWaitingForNetwork is not null || CrashNotice?.IsVisible == true;

    /// <summary>An expired session shows on the strip only while the sidebar is
    /// collapsed: an open sidebar already says "Session expired" with its own
    /// Sign in, right above the strip, and both together read as a duplicate.
    /// The unverified notice always shows; it says why, which the sidebar doesn't.</summary>
    public bool ShowSessionNotice =>
        Session.Notice.Length > 0 && !(Session.NeedsSignIn && IsSidebarOpen);

    public ToastHostViewModel Toasts => Actions.Toasts;

    // Panel collapse state. Both default open; the setters mirror into
    // AppSettings and disk persistence rides the app's normal save-on-exit —
    // same arrangement as WindowStatePersistence.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSessionNotice), nameof(HasStatus))]
    private bool _isSidebarOpen = true;
    [ObservableProperty] private bool _isQueueOpen;

    /// <summary>Shared with the settings page, so the ambient background follows
    /// the toggle on the frame it changes rather than on the next navigation.</summary>
    public AppearanceSettings Appearance { get; }

    public NavigationService Navigation { get; }
    public PlayerBarViewModel Player { get; }
    public NowPlayingViewModel NowPlaying { get; }
    public QueueViewModel QueuePanel { get; }
    public TrackActionsViewModel Actions { get; }

    /// <summary>The sign-in and its upkeep; the sidebar, status strip, Library
    /// and Settings all read it.</summary>
    public SessionViewModel Session { get; }

    /// <summary>The profiles, for the account menu; set by the app before
    /// the window exists, null in the designer.</summary>
    public ProfilesViewModel? Profiles { get; set; }

    /// <summary>The last run's crash, if it has one to report; set by the
    /// app before the window exists, null in the designer.</summary>
    public CrashNoticeViewModel? CrashNotice
    {
        get => _crashNotice;
        set
        {
            _crashNotice = value;
            if (value is not null)
            {
                value.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(CrashNoticeViewModel.IsVisible))
                        OnPropertyChanged(nameof(HasStatus));
                };
            }
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasStatus));
        }
    }

    private CrashNoticeViewModel? _crashNotice;

    // Design-time
    public MainViewModel()
    {
        Appearance = new AppearanceSettings();
        Actions = new TrackActionsViewModel();
        Navigation = new NavigationService(null!, null!, null!);
        Player = new PlayerBarViewModel();
        NowPlaying = new NowPlayingViewModel(Player) { IsOpen = false };
        QueuePanel = new QueueViewModel();
        Session = new SessionViewModel();
    }

    public MainViewModel(
        NavigationService navigation,
        PlayerBarViewModel player,
        NowPlayingViewModel nowPlaying,
        QueueViewModel queuePanel,
        TrackActionsViewModel actions,
        PlaybackCoordinator coordinator,
        SidecarBootstrapper bootstrapper,
        SessionViewModel session,
        CookieVault vault,
        Func<SettingsPageViewModel> settingsFactory,
        AppSettings settings,
        AppearanceSettings appearance,
        DownloadsPageViewModel? downloads = null)
    {
        _downloads = downloads;
        Appearance = appearance;
        Navigation = navigation;
        Player = player;
        NowPlaying = nowPlaying;
        QueuePanel = queuePanel;
        Actions = actions;
        _bootstrapper = bootstrapper;
        Session = session;
        session.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(SessionViewModel.Notice) or nameof(SessionViewModel.NeedsSignIn))
            {
                OnPropertyChanged(nameof(ShowSessionNotice));
                OnPropertyChanged(nameof(HasStatus));
            }
        };
        _settingsFactory = settingsFactory;
        _settings = settings;

        // Assigned after _settings so the generated change handler has somewhere
        // to write; restoring the stored value back onto itself is a no-op.
        IsSidebarOpen = !settings.SidebarCollapsed;
        IsQueueOpen = !settings.QueueCollapsed;

        // Property, not ctor arg: NavigationService already depends on Actions.
        actions.OpenArtist = navigation.OpenArtist;
        actions.OpenAlbum = navigation.OpenAlbum;
        if (downloads is not null)
            navigation.GoDownloads = GoDownloads;

        // Now Playing overlays the content region, so navigating behind it looks
        // like a dead click. Hanging this off Current rather than the individual
        // Go* commands covers every route into a page — sidebar, drill-downs, and
        // the queue's "Go to artist", which is reachable while the overlay is up.
        navigation.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(NavigationService.Current))
                nowPlaying.IsOpen = false;
        };

        TrackWaitingForNetwork = coordinator.TrackWaitingForNetwork;
        coordinator.NetworkWaitChanged += track =>
            Dispatcher.UIThread.Post(() => TrackWaitingForNetwork = track);
        Strings.WhenChanged(this, static vm => vm.OnPropertyChanged(nameof(NetworkWaitText)));
        coordinator.Notification += notice =>
            Dispatcher.UIThread.Post(() => Toasts.Show(NoticeText.For(notice)));
        vault.Warning += message =>
            Dispatcher.UIThread.Post(() => Toasts.Show(message));
    }

    public async Task InitializeAsync()
    {
        if (_bootstrapper is null)
            return;

        // Home first, and unconditionally: browsing needs neither sidecar nor a
        // session, so neither is allowed to decide whether the content region has
        // anything in it: a failed sidecar download would leave a blank window.
        Navigation.GoHome();

        var sidecars = PrepareSidecarsAsync();

        try
        {
            await Session.RestoreAsync();
        }
        catch (Exception ex)
        {
            // Fire-and-forget from App: nothing above us handles a throw.
            Toasts.Show(Strings.Format("toast.session.restoreFailed", ("error", ex.Message)), ToastKind.Error);
        }

        await sidecars;
    }

    /// <summary>Prepares ffmpeg/yt-dlp behind the shell. Failure is reported and
    /// left retryable rather than thrown — nothing on screen depends on it yet.</summary>
    private async Task PrepareSidecarsAsync()
    {
        try
        {
            SidecarsFailed = false;
            _sidecarsSettled = false;
            // Progress<T> posts, so a late report can land after RunAsync returns;
            // the flag keeps a trailing "Ready" from reappearing on a settled strip.
            await _bootstrapper!.RunAsync(new Progress<string>(s =>
            {
                if (!_sidecarsSettled)
                    SidecarProgress = s;
            }));
        }
        catch (Exception ex)
        {
            AppLog.Write("startup", ex.ToString());
            SidecarError = ex.Message;
            SidecarsFailed = true;
        }
        finally
        {
            _sidecarsSettled = true;
            SidecarProgress = "";
        }
    }

    [RelayCommand]
    private Task RetrySidecars() => PrepareSidecarsAsync();

    partial void OnIsSidebarOpenChanged(bool value)
    {
        if (_settings is not null)
            _settings.SidebarCollapsed = !value;
    }

    partial void OnIsQueueOpenChanged(bool value)
    {
        if (_settings is not null)
            _settings.QueueCollapsed = !value;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarOpen = !IsSidebarOpen;

    [RelayCommand]
    private void ToggleQueue() => IsQueueOpen = !IsQueueOpen;

    [RelayCommand]
    private void GoHome() => Navigation.GoHome();

    [RelayCommand]
    private void GoSearch() => Navigation.GoSearch();

    [RelayCommand]
    private void GoLibrary() => Navigation.GoLibrary();

    /// <summary>One page for the app's lifetime: it follows the jobs while away.</summary>
    [RelayCommand]
    private void GoDownloads()
    {
        if (_downloads is not null)
            Navigation.GoTo(_downloads, NavSection.Downloads);
    }

    // A fresh VM each visit re-seeds from the live settings objects.
    [RelayCommand]
    private void GoSettings()
    {
        if (_settingsFactory is not null)
            Navigation.GoTo(_settingsFactory(), NavSection.Settings);
    }
}
