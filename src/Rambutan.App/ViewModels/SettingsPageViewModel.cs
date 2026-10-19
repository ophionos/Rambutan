using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Services;
using Rambutan.Core.Settings;
using Rambutan.Integrations;
using Rambutan.Integrations.Discord;
using Rambutan.Integrations.LastFm;
using Rambutan.App.Localization;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings page. A fresh
/// VM per visit re-seeds from the live settings objects (see MainViewModel).
/// </summary>
public partial class SettingsPageViewModel : ViewModelBase
{
    private readonly PlaybackSettings? _playback;
    private readonly AppSettings? _app;
    private readonly AppearanceSettings? _appearance;
    private readonly Action? _persist;
    private readonly UpdateService? _updates;
    private readonly DiscordPresenceService? _discord;

    [ObservableProperty] private bool _crossfadeEnabled;
    [ObservableProperty] private bool _crossfadeOnSkip = true;
    [ObservableProperty] private double _crossfadeSeconds = 6;
    [ObservableProperty] private bool _normalizeVolume = true;
    [ObservableProperty] private bool _autoplay = true;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _taskbarControls;

    /// <summary>Applies the taskbar controls setting at once; null where there
    /// are none (not Windows, tests), and the row doesn't show.</summary>
    public Action<bool>? ApplyTaskbarControls { get; init; }

    public bool ShowsTaskbarControls => ApplyTaskbarControls is not null;
    [ObservableProperty] private bool _ambientBackground = true;
    [ObservableProperty] private bool _artworkMeter = true;

    /// <summary>Position in <see cref="AppearanceSettings.FrameRateChoices"/>.</summary>
    [ObservableProperty] private int _visualizerFrameRateIndex;

    /// <summary>Position in <see cref="AppearanceSettings.AllStyleChoices"/>: of
    /// the shown style, so a preset kept while advanced mode is off reads as
    /// the bars that draw.</summary>
    [ObservableProperty] private int _visualizerStyleIndex;

    /// <summary>Offer the animated presets; the picker's last items show only
    /// while this is on.</summary>
    [ObservableProperty] private bool _visualizerAdvanced;

    /// <summary>Off / Random / On track change, in the order of
    /// <see cref="VisualizerCycle"/>.</summary>
    [ObservableProperty] private int _visualizerCycleIndex;

    /// <summary>Accent / From the artwork / Custom, in the order of
    /// <see cref="VisualizerColorSource"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomVisualizerColor))]
    private int _visualizerColorIndex;

    public bool IsCustomVisualizerColor => VisualizerColorIndex == (int)VisualizerColorSource.Custom;

    /// <summary>The visualizer's custom colour.</summary>
    public CustomColorField VisualizerCustom { get; } =
        new(AppearanceSettings.DefaultCustomColor, null, "settings.appearance.visualizerColor.pick",
            "settings.appearance.visualizerColor.hexName", "settings.appearance.visualizerColor.invalid");

    /// <summary>Rambutan red / From the artwork / Custom, in the order of
    /// <see cref="AccentSource"/>.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomAccent))]
    private int _accentIndex;

    public bool IsCustomAccent => AccentIndex == (int)AccentSource.Custom;

    /// <summary>The accent's custom colour.</summary>
    public CustomColorField AccentCustom { get; } =
        new(AppearanceSettings.DefaultAccentColor, null, "settings.appearance.accent.pick",
            "settings.appearance.accent.hexName", "settings.appearance.accent.invalid");

    /// <summary>The lyrics pane may ask LRCLIB. Written through the
    /// pane's view model, which applies and remembers it.</summary>
    [ObservableProperty] private bool _lookUpLyricsOnline;
    private readonly LyricsViewModel? _lyrics;

    /// <summary>Follow system / Reduce / Full, in the order of
    /// <see cref="MotionPreference"/>: items with a template rather than literal
    /// ComboBoxItems, so the closed box re-renders on a language change.</summary>
    public IReadOnlyList<MotionChoice> MotionChoices { get; } =
        [new(MotionPreference.System), new(MotionPreference.Reduced), new(MotionPreference.Full)];

    /// <summary>Index into <see cref="MotionChoices"/>.</summary>
    [ObservableProperty] private int _motionIndex;
    [ObservableProperty] private string _systemMotionNote = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AudioCacheSizeText))]
    private double _audioCacheSizeMb = 512;

    /// <summary>"512 MB", or "Off" at 0: the help text says 0 turns the cache off,
    /// so the value says it too.</summary>
    public string AudioCacheSizeText => AudioCacheSizeMb <= 0
        ? Strings.Get("settings.playback.cacheOff")
        : Strings.Format("settings.playback.cacheSize", ("value", (int)AudioCacheSizeMb));

    /// <summary>High / Data saver, in the order of <see cref="AudioQuality"/>;
    /// templated items for the same reason as <see cref="MotionChoices"/>.</summary>
    public IReadOnlyList<AudioQualityChoice> AudioQualityChoices { get; } =
        [new(AudioQuality.High), new(AudioQuality.DataSaver)];

    /// <summary>Index into <see cref="AudioQualityChoices"/>.</summary>
    [ObservableProperty] private int _audioQualityIndex;
    [ObservableProperty] private string _versionDisplay = "Rambutan 0.0.0-design";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateReady;

    /// <summary>The Account section: the shared session, so signing in or out
    /// here shows everywhere else at once.</summary>
    public SessionViewModel Session { get; } = new();

    // Discord. Available only when the build carries an app id.
    [ObservableProperty] private bool _discordAvailable;
    [ObservableProperty] private bool _discordEnabled;
    [ObservableProperty] private bool _discordShowTrack = true;
    [ObservableProperty] private bool _discordShowArtwork = true;
    [ObservableProperty] private bool _discordShowElapsed = true;
    [ObservableProperty] private string _discordStatus = UnavailableNote;
    [ObservableProperty] private string _discordSharingNote = "";

    private readonly LastFmScrobbler? _lastFm;
    private readonly Func<string, Task<bool>>? _openUrl;
    // Ours, not the scrobbler's, so a refresh from its Changed event keeps it.
    private string? _browserNotice;

    [ObservableProperty] private bool _lastFmScrobbling = true;
    /// <summary>Home shows the Last.fm mix and recommendations. Off by default.</summary>
    [ObservableProperty] private bool _lastFmStationsOnHome;
    [ObservableProperty] private bool _lastFmBusy;
    [ObservableProperty] private string _lastFmStatus = LastFmUnavailableText;
    [ObservableProperty] private string _lastFmNotice = "";
    [ObservableProperty] private string _lastFmPending = "";
    [ObservableProperty] private string _lastFmAuthorizeUrl = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFmCanConnect), nameof(LastFmShowConnect), nameof(LastFmAwaiting), nameof(LastFmConnected),
        nameof(LastFmShowDisconnect), nameof(LastFmRecording), nameof(LastFmStationsNote))]
    private LastFmConnection _lastFmConnection;

    /// <summary>Last.fm ended the connection and plays are still recorded
    /// (<see cref="LastFmScrobbler.SessionEnded"/>).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastFmShowDisconnect), nameof(LastFmRecording))]
    private bool _lastFmSessionEnded;

    public static string LastFmUnavailableText => Strings.Get("settings.lastfm.unavailable");

    public bool LastFmAvailable => _lastFm is not null;
    public bool LastFmCanConnect => _lastFm is not null && LastFmConnection == LastFmConnection.Disconnected;
    /// <summary>Connect shows whenever there's nothing else to do, disabled in a build without keys.</summary>
    public bool LastFmShowConnect => LastFmConnection == LastFmConnection.Disconnected;
    public bool LastFmAwaiting => LastFmConnection == LastFmConnection.AwaitingApproval;
    public bool LastFmConnected => LastFmConnection == LastFmConnection.Connected;

    /// <summary>Plays are recorded: connected, or kept after Last.fm ended the
    /// connection. The scrobble toggle and the waiting count show then.</summary>
    public bool LastFmRecording => LastFmConnected || LastFmSessionEnded;

    /// <summary>What the stations setting sends, and, while no account is
    /// connected, why it can't be turned on.</summary>
    public string LastFmStationsNote => LastFmConnected
        ? Strings.Get("settings.lastfm.stations.disclosure")
        : Strings.Get("settings.lastfm.stations.needsAccount") + " " + Strings.Get("settings.lastfm.stations.disclosure");

    /// <summary>Told when the stations setting changes, so Home adds or removes them.</summary>
    public HomeStations? Stations { get; init; }

    /// <summary>Disconnect is how plays kept after Last.fm ended the connection
    /// are discarded, so it shows then too, beside Connect.</summary>
    public bool LastFmShowDisconnect => LastFmConnected ||
        (LastFmSessionEnded && LastFmConnection == LastFmConnection.Disconnected);

    /// <summary>"Follow system" first, then every language with a table.</summary>
    public IReadOnlyList<LanguageChoice> Languages { get; } = LanguageChoice.All();

    [ObservableProperty] private LanguageChoice? _selectedLanguage;

    /// <summary>Settings → Downloads; null where downloads aren't composed (tests).</summary>
    public DownloadsSettingsViewModel? Downloads { get; init; }

    /// <summary>Settings → Playback's output device; null where it isn't composed (tests).</summary>
    public OutputDeviceSettingsViewModel? Output { get; init; }

    public EqualizerSettingsViewModel? Equalizer { get; init; }

    /// <summary>Settings → Phone remote; null where it isn't composed (tests).</summary>
    public RemoteSettingsViewModel? Remote { get; init; }

    /// <summary>Settings → OBS overlay; null where it isn't composed (tests).</summary>
    public OverlaySettingsViewModel? Overlay { get; init; }

    // Design-time
    public SettingsPageViewModel() => _selectedLanguage = Languages[0];

    public SettingsPageViewModel(
        PlaybackSettings playback, AppSettings app, AppearanceSettings appearance,
        UpdateService updates, SessionViewModel session, Action persist,
        DiscordPresenceService? discord = null,
        LastFmScrobbler? lastFm = null, Func<string, Task<bool>>? openUrl = null,
        LyricsViewModel? lyrics = null)
    {
        _lyrics = lyrics;
        _lookUpLyricsOnline = lyrics?.LookUpOnline ?? app.LookUpLyricsOnline;
        Session = session;
        _playback = playback;
        _app = app;
        _appearance = appearance;
        _updates = updates;
        _persist = persist;
        _crossfadeEnabled = playback.CrossfadeEnabled;
        _crossfadeOnSkip = playback.CrossfadeOnSkip;
        _crossfadeSeconds = playback.CrossfadeSeconds;
        _normalizeVolume = playback.NormalizeVolume;
        _autoplay = playback.Autoplay;
        _closeToTray = app.CloseToTray;
        _taskbarControls = app.TaskbarControls;
        _ambientBackground = appearance.AmbientBackground;
        _artworkMeter = appearance.ArtworkMeter;
        _visualizerFrameRateIndex = Math.Max(0,
            Array.IndexOf(AppearanceSettings.FrameRateChoices, appearance.VisualizerFrameRate));
        _motionIndex = (int)appearance.Motion;
        _visualizerStyleIndex = StyleIndex(appearance.ShownStyle);
        _visualizerAdvanced = appearance.VisualizerAdvanced;
        _visualizerCycleIndex = (int)appearance.VisualizerCycle;
        _visualizerColorIndex = (int)appearance.VisualizerColorSource;
        VisualizerCustom = new(appearance.VisualizerCustomColor, hex =>
        {
            appearance.VisualizerCustomColor = hex;
            _persist?.Invoke();
        }, "settings.appearance.visualizerColor.pick",
            "settings.appearance.visualizerColor.hexName", "settings.appearance.visualizerColor.invalid");
        _accentIndex = (int)appearance.AccentSource;
        AccentCustom = new(appearance.AccentCustomColor, hex =>
        {
            appearance.AccentCustomColor = hex;
            _persist?.Invoke();
        }, "settings.appearance.accent.pick",
            "settings.appearance.accent.hexName", "settings.appearance.accent.invalid");
        _systemMotionNote = MotionNote(appearance.SystemReducesMotion);
        appearance.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppearanceSettings.SystemReducesMotion))
                SystemMotionNote = MotionNote(appearance.SystemReducesMotion);
        };
        _selectedLanguage = Languages.FirstOrDefault(l => l.Code == app.Language) ?? Languages[0];
        Strings.WhenChanged(this, static vm => vm.RefreshText());
        _audioCacheSizeMb = app.AudioCacheSizeMb;
        _audioQualityIndex = (int)playback.AudioQuality;
        _versionDisplay = Strings.Format("settings.updates.version", ("version", updates.CurrentVersion));
        OnStagedChanged();
        updates.StagedChanged += OnStagedChanged;

        _discord = discord;
        _discordAvailable = discord?.IsAvailable ?? false;
        _discordEnabled = _discordAvailable && app.DiscordEnabled;
        _discordShowTrack = app.DiscordShowTrack;
        _discordShowArtwork = app.DiscordShowArtwork;
        _discordShowElapsed = app.DiscordShowElapsed;
        _discordSharingNote = SharingNote(DiscordOptions(app));
        RefreshDiscordStatus();
        if (discord is not null)
            discord.ConnectionChanged += () => Dispatcher.UIThread.Post(RefreshDiscordStatus);
        _lastFm = lastFm;
        _openUrl = openUrl;
        _lastFmScrobbling = app.LastFmScrobbling;
        _lastFmStationsOnHome = app.LastFmStationsOnHome;
        if (lastFm is not null)
        {
            RefreshLastFm();
            lastFm.Changed += () => Dispatcher.UIThread.Post(RefreshLastFm);
        }
    }

    public static string UnavailableNote => Strings.Get("settings.discord.unavailable");

    public static DiscordPresenceOptions DiscordOptions(AppSettings app) =>
        new(app.DiscordShowTrack, app.DiscordShowArtwork, app.DiscordShowElapsed);

    /// <summary>Exactly what leaves the machine with the current toggles: each
    /// outbound integration says what it shares.</summary>
    public static string SharingNote(DiscordPresenceOptions options)
    {
        var parts = new List<string>();
        if (options.ShowTrack)
            parts.Add(Strings.Get("settings.discord.share.track"));
        if (options.ShowArtwork)
            parts.Add(Strings.Get("settings.discord.share.artwork"));
        if (options.ShowElapsed)
            parts.Add(Strings.Get("settings.discord.share.elapsed"));
        var what = parts.Count == 0 ? Strings.Get("settings.discord.share.nothing") : string.Join("; ", parts);
        var note = Strings.Format("settings.discord.sharingNote", ("what", what));
        // Discord draws a clock on every activity; without timestamps it can't be hidden.
        return options.ShowElapsed ? note : note + " " + Strings.Get("settings.discord.ownClock");
    }

    /// <summary>Honest connection state: off, waiting, refused or connected.</summary>
    public static string StatusText(bool available, bool enabled, DiscordConnectionState state, bool refused, string? reason)
    {
        if (!available)
            return UnavailableNote;
        if (!enabled)
            return Strings.Get("settings.discord.status.off");
        if (state == DiscordConnectionState.Connected)
            return Strings.Get("settings.discord.status.connected");
        if (!refused)
            return Strings.Get("settings.discord.status.waiting");
        // Discord's own reason, as Discord wrote it.
        return reason is not null
            ? Strings.Format("settings.discord.status.refused", ("reason", reason))
            : Strings.Get("settings.discord.status.refusedNoReason");
    }

    private void RefreshDiscordStatus() =>
        DiscordStatus = StatusText(
            DiscordAvailable, DiscordEnabled,
            _discord?.ConnectionState ?? DiscordConnectionState.Off,
            _discord?.IsRefused ?? false, _discord?.Refusal);

    partial void OnDiscordEnabledChanged(bool value) => ApplyDiscord();
    partial void OnDiscordShowTrackChanged(bool value) => ApplyDiscord();
    partial void OnDiscordShowArtworkChanged(bool value) => ApplyDiscord();
    partial void OnDiscordShowElapsedChanged(bool value) => ApplyDiscord();

    private void ApplyDiscord()
    {
        if (_app is null)
            return;
        _app.DiscordEnabled = DiscordEnabled;
        _app.DiscordShowTrack = DiscordShowTrack;
        _app.DiscordShowArtwork = DiscordShowArtwork;
        _app.DiscordShowElapsed = DiscordShowElapsed;
        var options = DiscordOptions(_app);
        _discord?.Configure(DiscordEnabled, options);
        DiscordSharingNote = SharingNote(options);
        RefreshDiscordStatus();
        _persist?.Invoke();
    }

    /// <summary>Re-reads the scrobbler's state. UI thread.</summary>
    public void RefreshLastFm()
    {
        // Mid-request, the "Contacting…" line stands until the request answers.
        if (_lastFm is null || LastFmBusy)
            return;
        LastFmConnection = _lastFm.Connection;
        LastFmSessionEnded = _lastFm.SessionEnded;
        LastFmStatus = LastFmConnection switch
        {
            LastFmConnection.Connected => Strings.Format("settings.lastfm.status.connected", ("user", _lastFm.Username)),
            LastFmConnection.AwaitingApproval => Strings.Get("settings.lastfm.status.awaiting"),
            _ when LastFmSessionEnded => Strings.Get("settings.lastfm.status.ended"),
            _ => Strings.Get("settings.lastfm.status.notConnected"),
        };
        if (LastFmConnection != LastFmConnection.AwaitingApproval)
        {
            LastFmAuthorizeUrl = "";
            _browserNotice = null;
        }
        LastFmNotice = (_lastFm.Notice is { } notice ? NoticeText.For(notice) : null) ?? _browserNotice ?? "";
        var pending = _lastFm.PendingCount;
        LastFmPending = pending == 0
            ? ""
            : Strings.Format(LastFmRecording ? "settings.lastfm.pendingConnected" : "settings.lastfm.pending", ("count", pending));
    }

    [RelayCommand]
    private async Task ConnectLastFmAsync()
    {
        if (_lastFm is null || LastFmBusy)
            return;
        LastFmBusy = true;
        LastFmStatus = Strings.Get("settings.lastfm.contacting");
        LastFmNotice = "";
        _browserNotice = null;
        string? url;
        try
        {
            url = await _lastFm.BeginConnectAsync();
        }
        finally
        {
            LastFmBusy = false;
            RefreshLastFm();
        }

        if (url is null)
            return;
        LastFmAuthorizeUrl = url;
        if (_openUrl is null || !await _openUrl(url))
        {
            _browserNotice = Strings.Get("settings.lastfm.browserFailed");
            RefreshLastFm();
        }
    }

    [RelayCommand]
    private async Task FinishLastFmAsync()
    {
        if (_lastFm is null || LastFmBusy)
            return;
        LastFmBusy = true;
        LastFmStatus = Strings.Get("settings.lastfm.checking");
        try
        {
            await _lastFm.FinishConnectAsync();
        }
        finally
        {
            LastFmBusy = false;
            RefreshLastFm();
        }
    }

    [RelayCommand]
    private async Task CancelLastFmAsync()
    {
        if (_lastFm is null)
            return;
        await _lastFm.CancelConnectAsync();
        RefreshLastFm();
    }

    [RelayCommand]
    private async Task DisconnectLastFmAsync()
    {
        if (_lastFm is null)
            return;
        await _lastFm.DisconnectAsync();
        RefreshLastFm();
    }

    partial void OnLastFmStationsOnHomeChanged(bool value)
    {
        if (_app is null)
            return;
        _app.LastFmStationsOnHome = value;
        if (Stations is not null)
            Stations.Enabled = value;
        _persist?.Invoke();
    }

    partial void OnLastFmScrobblingChanged(bool value)
    {
        if (_app is null)
            return;
        _app.LastFmScrobbling = value;
        if (_lastFm is not null)
            _lastFm.ScrobblingEnabled = value;
        _persist?.Invoke();
    }

    /// <summary>Says the standing update state: a staged update, or a build that
    /// can't update itself (said at rest, rather than after a check that does nothing).</summary>
    private void OnStagedChanged()
    {
        if (_updates?.StagedVersion is { } staged)
        {
            UpdateReady = true;
            UpdateStatus = Strings.Format("settings.updates.ready", ("version", staged));
        }
        else if (_updates is { IsPackaged: false })
        {
            UpdateStatus = Strings.Get("settings.updates.notPackaged");
        }
    }

    private bool CanCheckForUpdates() => _updates?.IsPackaged == true;

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdatesAsync()
    {
        if (_updates is null)
            return;

        UpdateStatus = Strings.Get("settings.updates.checking");
        try
        {
            var staged = await _updates.CheckAndStageAsync();
            if (staged is null)
                UpdateStatus = Strings.Get("settings.updates.upToDate");
        }
        catch (Exception)
        {
            UpdateStatus = Strings.Get("settings.updates.failed");
        }
    }

    [RelayCommand]
    private void RestartNow() => _updates?.RestartNow();

    partial void OnCrossfadeEnabledChanged(bool value)
    {
        if (_playback is null)
            return;
        _playback.CrossfadeEnabled = value;
        _persist?.Invoke();
    }

    partial void OnCrossfadeOnSkipChanged(bool value)
    {
        if (_playback is null)
            return;
        _playback.CrossfadeOnSkip = value;
        _persist?.Invoke();
    }

    partial void OnCrossfadeSecondsChanged(double value)
    {
        if (_playback is null)
            return;
        _playback.CrossfadeSeconds = value;
        _persist?.Invoke();
    }

    partial void OnNormalizeVolumeChanged(bool value)
    {
        if (_playback is null)
            return;
        _playback.NormalizeVolume = value;
        _persist?.Invoke();
    }

    partial void OnAutoplayChanged(bool value)
    {
        if (_playback is null)
            return;
        _playback.Autoplay = value;
        _persist?.Invoke();
    }

    partial void OnLookUpLyricsOnlineChanged(bool value)
    {
        if (_lyrics is not null)
            _lyrics.LookUpOnline = value;
    }

    /// <summary>The desktop draws no tray icons (Linux without a StatusNotifier
    /// host), so closing minimizes instead; the page says so.</summary>
    [ObservableProperty] private bool _noTray;

    /// <summary>Asks whether the tray shows; the answer arrives on the caller's thread.</summary>
    public async Task CheckTrayAsync(Func<Task<bool>> trayShows) => NoTray = !await trayShows();

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_app is null)
            return;
        _app.CloseToTray = value;
        _persist?.Invoke();
    }

    partial void OnTaskbarControlsChanged(bool value)
    {
        if (_app is null)
            return;
        _app.TaskbarControls = value;
        ApplyTaskbarControls?.Invoke(value);
        _persist?.Invoke();
    }

    // Writes to the shared AppearanceSettings, not to AppSettings: the shell binds
    // that object, so the tint follows the checkbox immediately. AppSettings picks
    // the value up on save like every other setting.
    partial void OnAmbientBackgroundChanged(bool value)
    {
        if (_appearance is null)
            return;
        _appearance.AmbientBackground = value;
        _persist?.Invoke();
    }

    partial void OnArtworkMeterChanged(bool value)
    {
        if (_appearance is null)
            return;
        _appearance.ArtworkMeter = value;
        _persist?.Invoke();
    }

    partial void OnVisualizerFrameRateIndexChanged(int value)
    {
        if (_appearance is null || value < 0 || value >= AppearanceSettings.FrameRateChoices.Length)
            return;
        _appearance.VisualizerFrameRate = AppearanceSettings.FrameRateChoices[value];
        _persist?.Invoke();
    }

    partial void OnSelectedLanguageChanged(LanguageChoice? value)
    {
        if (_app is null || value is null)
            return;
        _app.Language = value.Code;
        _persist?.Invoke();
        Strings.SetLanguage(value.Code);
    }

    /// <summary>Re-words the text this page computes in code after a language
    /// change; the XAML's own strings follow by themselves.</summary>
    private void RefreshText()
    {
        if (_app is null || _appearance is null || _updates is null)
            return;
        SystemMotionNote = MotionNote(_appearance.SystemReducesMotion);
        VersionDisplay = Strings.Format("settings.updates.version", ("version", _updates.CurrentVersion));
        OnPropertyChanged(nameof(AudioCacheSizeText));
        DiscordSharingNote = SharingNote(DiscordOptions(_app));
        RefreshDiscordStatus();
        if (_lastFm is null)
            LastFmStatus = LastFmUnavailableText;
        RefreshLastFm();
        OnPropertyChanged(nameof(LastFmStationsNote));
        // A check's outcome is a moment, not a state: it goes rather than stay
        // in the old language. A staged update or an unpackaged build is a state
        // and is re-said.
        UpdateStatus = "";
        OnStagedChanged();
    }

    /// <summary>What "Follow system" means on this machine right now.</summary>
    public static string MotionNote(bool systemReduces) => systemReduces
        ? Strings.Get("settings.appearance.motionNoteReduced")
        : Strings.Get("settings.appearance.motionNoteFull");

    private static int StyleIndex(VisualizerStyle style) =>
        Math.Max(0, Array.IndexOf(AppearanceSettings.AllStyleChoices, style));

    partial void OnVisualizerStyleIndexChanged(int value)
    {
        if (_appearance is null || value < 0 || value >= AppearanceSettings.AllStyleChoices.Length)
            return;
        var style = AppearanceSettings.AllStyleChoices[value];
        // A hidden preset can't be picked; a stale index from the picker
        // while advanced mode turns off must not replace the kept choice.
        if (style == _appearance.ShownStyle)
            return;
        _appearance.VisualizerStyle = style;
        _persist?.Invoke();
    }

    partial void OnVisualizerAdvancedChanged(bool value)
    {
        if (_appearance is null)
            return;
        _appearance.VisualizerAdvanced = value;
        _persist?.Invoke();
        // The picker follows what draws: the kept preset comes back with
        // advanced mode, and bars stand in for it without.
        _visualizerStyleIndex = StyleIndex(_appearance.ShownStyle);
        OnPropertyChanged(nameof(VisualizerStyleIndex));
    }

    partial void OnVisualizerCycleIndexChanged(int value)
    {
        if (_appearance is null || value is < 0 or > (int)VisualizerCycle.TrackChange)
            return;
        _appearance.VisualizerCycle = (VisualizerCycle)value;
        _persist?.Invoke();
    }

    partial void OnVisualizerColorIndexChanged(int value)
    {
        if (_appearance is null || value is < 0 or > (int)VisualizerColorSource.Custom)
            return;
        _appearance.VisualizerColorSource = (VisualizerColorSource)value;
        _persist?.Invoke();
    }

    partial void OnAccentIndexChanged(int value)
    {
        if (_appearance is null || value is < 0 or > (int)AccentSource.Custom)
            return;
        _appearance.AccentSource = (AccentSource)value;
        _persist?.Invoke();
    }

    partial void OnMotionIndexChanged(int value)
    {
        if (_appearance is null || value is < 0 or > (int)MotionPreference.Full)
            return;
        _appearance.Motion = (MotionPreference)value;
        _persist?.Invoke();
    }

    partial void OnAudioQualityIndexChanged(int value)
    {
        if (_playback is null || value is < 0 or > (int)AudioQuality.DataSaver)
            return;
        _playback.AudioQuality = (AudioQuality)value;
        _persist?.Invoke();
    }

    partial void OnAudioCacheSizeMbChanged(double value)
    {
        if (_app is null)
            return;
        _app.AudioCacheSizeMb = (int)value;
        _persist?.Invoke();
    }
}

/// <summary>A choice in the Settings language list; a null code follows the OS.</summary>
public sealed record LanguageChoice(string? Code, string NativeName)
{
    public bool IsSystem => Code is null;

    /// <summary>What the list reads out, for screen readers and the closed box.</summary>
    public override string ToString() => IsSystem ? Strings.Get("settings.language.system") : NativeName;

    public static IReadOnlyList<LanguageChoice> All() =>
    [
        new(null, ""),
        .. Strings.Available(Strings.OfferPseudo).Select(o => new LanguageChoice(o.Code, o.NativeName)),
    ];
}

/// <summary>A choice in the Settings motion list.</summary>
public sealed record MotionChoice(MotionPreference Value)
{
    public bool IsSystem => Value == MotionPreference.System;
    public bool IsReduced => Value == MotionPreference.Reduced;
    public bool IsFull => Value == MotionPreference.Full;

    /// <summary>What the list reads out, for screen readers.</summary>
    public override string ToString() => Strings.Get(Value switch
    {
        MotionPreference.Reduced => "settings.appearance.motionReduce",
        MotionPreference.Full => "settings.appearance.motionFull",
        _ => "settings.appearance.motionSystem",
    });
}

/// <summary>A choice in the Settings streaming-quality list.</summary>
public sealed record AudioQualityChoice(AudioQuality Value)
{
    public bool IsHigh => Value == AudioQuality.High;
    public bool IsDataSaver => Value == AudioQuality.DataSaver;

    /// <summary>What the list reads out, for screen readers.</summary>
    public override string ToString() => Strings.Get(
        Value == AudioQuality.DataSaver ? "settings.playback.qualityDataSaver" : "settings.playback.qualityHigh");
}

/// <summary>One of the custom colour's ready-made choices.</summary>
public sealed class ColorPreset(string hex, string nameKey)
{
    public string Hex { get; } = hex;

    public Avalonia.Media.IBrush Brush { get; } =
        new Avalonia.Media.Immutable.ImmutableSolidColorBrush(CssHexColor.Parse(hex, Avalonia.Media.Colors.White));

    /// <summary>For screen readers and the tooltip: the colour's name.</summary>
    public string Name => Strings.Get(nameKey);
}
