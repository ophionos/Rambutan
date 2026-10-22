using System;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Rambutan.App.Services;
using Rambutan.App.Services.Demo;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Appearance;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Remote;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;
using Rambutan.Core.Stations;
using Rambutan.Audio;
using Rambutan.InnerTube;
using Rambutan.Integrations;
using Rambutan.Integrations.Discord;
using Rambutan.Integrations.LastFm;
using Rambutan.LocalLibrary;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;
using Rambutan.Streams;
using Rambutan.App.Localization;

namespace Rambutan.App;

public partial class App : Application
{
    /// <summary>Set when the app runs in demo mode (see <see cref="DemoOptions"/>).</summary>
    public static DemoOptions? Demo { get; set; }

    /// <summary>Where this run keeps its data; set by <c>Program.Main</c> before the
    /// single-instance check, read again here when a designer or test skips Main.</summary>
    public static AppDataLocation? DataLocation { get; set; }

    /// <summary>The profile this run opened; set by <c>Program.Main</c>.</summary>
    public static ProfileLaunch? Profile { get; set; }

    /// <summary>Run by <c>Program.Main</c> once the app has exited: a profile
    /// switch starts the app again in the chosen profile.</summary>
    public static Action? AfterExit { get; set; }

    private ServiceProvider? _services;
#if WINDOWS
    private TaskbarControls? _taskbar;
#endif
    private TrayIcon? _tray;
    private DispatcherTimer? _persistDebounce;
    private QueuePersistence? _queuePersistence;
    private LastFmScrobbler? _lastFm;
    private bool _exitRequested;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Before any control exists: names come from tooltips and row items
            // as they're set.
            Controls.AccessibleNames.Install();
            _services = ConfigureServices();

            // Every user-visible playback complaint is field evidence too.
            _services.GetRequiredService<PlaybackCoordinator>().Notification +=
                // The code, not the text: the log reads the same in every language.
                notice => AppLog.Write("playback", notice.ToString());
            // A download removed while it played kept its file open; once playback
            // moves on, the decoder has let go and the file can be deleted.
            var downloads = _services.GetRequiredService<DownloadStore>();
            _services.GetRequiredService<PlaybackCoordinator>().NowPlayingChanged +=
                _ => Task.Run(downloads.RetryLeftovers);
            Converters.ArtworkConverter.LocalArtwork = _services.GetRequiredService<KeptCovers>().PathFor;
            // Downloads made before covers were kept get theirs, in the background.
            _ = Task.Run(() => downloads.FillMissingArtworkAsync(CancellationToken.None));
            // Linux has no install step, so the AppImage registers rambutan:// as it
            // starts; profiles share it, a demo or other data folder never does.
            var installedData = Demo is null &&
                string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(AppDataLocation.OverrideVariable));
            _ = Task.Run(() => Rambutan.Integrations.Linux.LinuxDeepLinkRegistration.RegisterAppImageAsync(installedData));
            // And the desktop file a dock finds the window's icon through.
            _ = Task.Run(() => Rambutan.Integrations.Linux.LinuxAppEntry.Register(installedData, ReadAppIcon));

            var actions = _services.GetRequiredService<TrackActionsViewModel>();
            Controls.TrackMenu.Actions = actions;
            var collections = _services.GetRequiredService<NavigationService>().Collections;
            var session = _services.GetRequiredService<SessionViewModel>();
            collections.IsSignedIn = () => session.IsSignedIn;
            Controls.CollectionMenu.Actions = collections;
            // Rows show the downloaded mark through a binding; their spoken names
            // read the same set and are renamed when it changes.
            Controls.AccessibleNames.IsDownloaded = id => actions.DownloadedIds.Contains(id);
            actions.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TrackActionsViewModel.DownloadedIds))
                    Controls.AccessibleNames.RefreshTrackRows();
            };
            // Whether YouTube Music can be reached: asked now and on each network
            // change, and known for certain when a track was skipped for want of it.
            var network = _services.GetRequiredService<NetworkStatus>();
            _services.GetRequiredService<PlaybackCoordinator>().Notification += notice =>
            {
                if (notice.Kind is PlaybackNoticeKind.SkippedOffline or PlaybackNoticeKind.StoppedOffline)
                    network.ReportOffline();
            };
            NetworkChange.NetworkAddressChanged += (_, _) => _ = network.CheckAsync();
            _ = network.CheckAsync();
            // A download removed or finished offline changes what the queue can play.
            var queuePanel = _services.GetRequiredService<QueueViewModel>();
            actions.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TrackActionsViewModel.DownloadedIds) && network.IsOffline)
                    _ = queuePanel.UpdateAvailabilityAsync();
            };
            Controls.TrackWarmup.Scheduler = _services.GetRequiredService<HoverWarmup>();
            Controls.VisualizerControl.Source =
                _services.GetRequiredService<Func<FftAnalyzer?>>();
            // The accent before the first frame, so the window never shows the
            // default red first; then on each change, on the UI thread.
            var theme = _services.GetRequiredService<ThemeColors>();
            var remote = _services.GetRequiredService<PhoneRemoteService>();
            void ApplyAccent()
            {
                var palette = AccentPalette.From(theme.Accent);
                AccentResources.Apply(Resources, palette);
                remote.Accent = palette.Base.ToString();
            }
            ApplyAccent();
            theme.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ThemeColors.Accent))
                    ApplyAccent();
            };

            var main = _services.GetRequiredService<MainViewModel>();
            main.Profiles = _services.GetRequiredService<ProfilesViewModel>();
            main.CrashNotice = new CrashNoticeViewModel(
                _services.GetRequiredService<CrashFile>(), UpdateService.ReleasesFeed,
                OpenInBrowserAsync, _services.GetRequiredService<ToastHostViewModel>());
            var window = new MainWindow { DataContext = main };
            // Avalonia's Wayland loop ticks on every commit, not on frame callbacks.
            if (WaylandFramePacing.Install(window))
                AppLog.Write("render", "Wayland render loop paced on frame callbacks");
            // Nor sets an app id, so docks can't find the window's icon.
            WaylandToplevel.Install(window);
            if (Demo is not null)
                window.Title = "Rambutan (demo: recorded data, no network)";
            if (main.Profiles.ShowsProfile)
                window.Title += $" — {main.Profiles.CurrentName}";
            SetupProfileSwitch(desktop, main.Profiles);
            window.AttachPersistence(WindowStatePersistence.Attach(window, _services.GetRequiredService<AppSettings>()));
            StartupTrace.Attach(window);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => OnExit();

            SetupMotion(window, _services.GetRequiredService<AppearanceSettings>());
            SetupCloseToTray(window);
            SetupTray(desktop, window);
            SetupMediaSession(desktop, window);
#if WINDOWS
            _taskbar = TaskbarControls.Attach(
                window, _services.GetRequiredService<AppSettings>().TaskbarControls,
                _services.GetRequiredService<PlaybackCoordinator>(), _services.GetRequiredService<TrackRatings>(),
                _services.GetRequiredService<SessionViewModel>(), _services.GetRequiredService<TrackActionsViewModel>());
#endif
            _services.GetRequiredService<PlaybackListenerHub>()
                .Attach(_services.GetRequiredService<PlaybackCoordinator>());
            _lastFm?.Start();
            if (_services.GetRequiredService<AppSettings>().RemoteEnabled)
                _ = _services.GetRequiredService<PhoneRemoteService>().SetEnabledAsync(true);
            if (_services.GetRequiredService<AppSettings>().OverlayEnabled)
                _ = _services.GetRequiredService<ObsOverlayService>().SetEnabledAsync(true);
            // The watched folders are compared with the index once the window is up;
            // only what changed while the app was closed is read.
            if (_services.GetRequiredService<AppSettings>().WatchedFolders is { Count: > 0 })
            {
                // Once: a window hidden to the tray opens again when it comes back.
                void ScanOnce(object? sender, EventArgs e)
                {
                    window.Opened -= ScanOnce;
                    _services.GetRequiredService<LocalLibraryScans>().Start();
                }
                window.Opened += ScanOnce;
            }

            // A second launch surfaces the running window, and acts on a
            // rambutan:// link it carries.
            var links = _services.GetRequiredService<DeepLinkHandler>();
            if (Program.Instance is { } instance)
            {
                instance.ArgsReceived += args =>
                    Dispatcher.UIThread.Post(() =>
                    {
                        ShowMainWindow(window);
                        _ = links.HandleArgsAsync(args);
                    });
            }

            // Home goes up before the first await inside; a link handled after it
            // lands on top of Home instead of being replaced by it.
            _ = main.InitializeAsync();
            _ = HandleStartupLinkAsync(links, main.Session, desktop.Args ?? []);

            if (Demo is null)
                _ = CheckForUpdatesSafeAsync(_services.GetRequiredService<UpdateService>());
        }

        base.OnFrameworkInitializationCompleted();
    }

    private ServiceProvider ConfigureServices()
    {
        // Demo mode and RAMBUTAN_DATA_DIR keep their own settings, cookies and
        // caches. The sidecars are shared: the audio engine is real in a demo and
        // needs the same ffmpeg.
        var location = DataLocation ?? AppDataLocation.FromEnvironment(Demo is not null);
        var dataDir = location.DataDir;
        var toolsDir = location.ToolsDir;

        // Failures must leave traces. Sink installed before
        // anything that can fail; unhandled-exception hooks catch what slips past
        // every local handler.
        var log = new FileLog(Path.Combine(dataDir, "logs"));
        AppLog.Sink = log.Write;
        // The crash file is for the next start to offer as a GitHub issue;
        // nothing leaves the machine unless the user sends it.
        var crashFile = new CrashFile(dataDir);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Write("fatal", e.ExceptionObject.ToString() ?? "unknown");
            if (e.IsTerminating)
                crashFile.Record(e.ExceptionObject);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            AppLog.Write("task", e.Exception.ToString());

        var store = new AppSettingsStore(dataDir);
        var settings = store.Load();
        crashFile.UserFolders = () => [settings.DownloadsFolder, settings.ExportFolder, .. settings.WatchedFolders ?? []];

        // The language is chosen before any view model or window exists, so
        // everything built from here on is built in it. The env var is a session
        // override for testing (the pseudo-locale, or a table without a picker).
        var localeOverride = Environment.GetEnvironmentVariable("RAMBUTAN_LOCALE");
#if DEBUG
        Strings.OfferPseudo = true;
#endif
        Strings.OfferPseudo |= Demo is not null || localeOverride == Strings.PseudoLocale;
        Strings.SetLanguage(string.IsNullOrWhiteSpace(localeOverride) ? settings.Language : localeOverride);
        var playbackSettings = new PlaybackSettings
        {
            CrossfadeEnabled = settings.CrossfadeEnabled,
            CrossfadeSeconds = settings.CrossfadeSeconds,
            CrossfadeOnSkip = settings.CrossfadeOnSkip,
            NormalizeVolume = settings.NormalizeVolume,
            Autoplay = settings.Autoplay,
            AudioQuality = AudioQualityNames.Parse(settings.AudioQuality),
            Volume = settings.Volume,
        };

        var services = new ServiceCollection();

        services.AddSingleton(log);
        services.AddSingleton(crashFile);
        services.AddSingleton(store);
        services.AddSingleton(settings);
        services.AddSingleton(playbackSettings);
        services.AddSingleton(new AppearanceSettings
        {
            AmbientBackground = settings.AmbientBackground,
            ArtworkMeter = settings.ArtworkMeter,
            VisualizerFrameRate = AppearanceSettings.ParseFrameRate(settings.VisualizerFrameRate),
            VisualizerStyle = AppearanceSettings.ParseStyle(settings.VisualizerStyle),
            VisualizerAdvanced = settings.VisualizerAdvanced,
            VisualizerCycle = AppearanceSettings.ParseCycle(settings.VisualizerCycle),
            VisualizerColorSource = AppearanceSettings.ParseColorSource(settings.VisualizerColor),
            VisualizerCustomColor = AppearanceSettings.ParseCustomColor(settings.VisualizerCustomColor),
            AccentSource = AppearanceSettings.ParseAccentSource(settings.AccentColor),
            AccentCustomColor = AppearanceSettings.ParseCustomColor(settings.AccentCustomColor, AppearanceSettings.DefaultAccentColor),
            Motion = AppearanceSettings.ParseMotion(settings.Motion),
        });

        // The saved queue goes back in before any view model reads it, so the
        // first frame already shows it (the restored session). Not playing.
        var queue = new QueueModel();
        _queuePersistence = QueuePersistence.Attach(queue, new QueueStore(dataDir));
        services.AddSingleton(queue);

        var innerTube = Demo is null
            ? new InnerTubeClient()
            : new InnerTubeClient(new HttpClient(new DemoInnerTubeHandler(Demo)));
        services.AddSingleton(innerTube);
        // Behind the catalog, the copies of downloaded albums and playlists, so their
        // pages and the library's tabs still open offline.
        services.AddSingleton<ICatalog>(sp => new OfflineCatalog(
            innerTube, sp.GetRequiredService<OfflineCollections>(),
            id => sp.GetRequiredService<DownloadStore>().ArtworkPathFor(id) is { } path ? new Uri(path).AbsoluteUri : null));

        // Like RemoteImage.Cache: the thumbs read the session's ratings directly,
        // since every row binds to a bare TrackInfo.
        var ratings = new TrackRatings(innerTube);
        Controls.RateButton.Default = ratings;
        services.AddSingleton(ratings);

        services.AddSingleton(new YtDlpManager(toolsDir));
        services.AddSingleton(new FfmpegManager(toolsDir));
        // The quality preference is read live, so a change applies from the
        // next resolution.
        services.AddSingleton(sp => new StreamResolver(
            sp.GetRequiredService<YtDlpManager>(), () => playbackSettings.AudioQuality));
        services.AddSingleton(new AudioDiskCache(Path.Combine(dataDir, "cache", "audio"))
        {
            MaxSizeBytes = settings.AudioCacheSizeMb * 1024L * 1024,
        });
        // Registered concretely so the container still owns its disposal; the port
        // resolves to the gate, which holds playback until the sidecars are ready.
        services.AddSingleton<CachingStreamSource>();
        // Downloads sit in front of the cache: a downloaded track plays from its
        // file under any quality preference, with no resolution. Behind them, a
        // connectivity check turns a resolution that fails for want of a network
        // into "offline" within moments, not yt-dlp's 20 s of retries. The demo
        // "downloads" its generated audio, so the whole flow can be looked at.
        var defaultDownloads = DownloadsFolders.Default(dataDir);
        services.AddSingleton(sp => new DownloadStore(
            dataDir,
            () => DownloadsFolders.Effective(sp.GetRequiredService<AppSettings>(), defaultDownloads),
            Demo is null
                ? new GoogleVideoFetcher(
                    sp.GetRequiredService<StreamResolver>(), sp.GetRequiredService<AudioDiskCache>())
                : new DemoAudioFetcher(Demo),
            // Each download keeps its cover beside its audio, so it shows offline.
            sp.GetRequiredService<ArtworkCache>().GetBytesAsync));
        // The watched folders' music, read through libav once ffmpeg is ready
        // (not yt-dlp, which a first start offline can't fetch).
        services.AddSingleton<FfmpegMediaFileReader>();
        // Watched while the app runs too, except the app's own folders, whose
        // log and index writes would start a scan after every scan.
        services.AddSingleton(sp => new LocalLibraryScans(
            new LocalFileIndex(
                dataDir, sp.GetRequiredService<FfmpegMediaFileReader>(),
                ct => sp.GetRequiredService<SidecarBootstrapper>().AudioReady.WaitAsync(ct),
                () => sp.GetRequiredService<AppSettings>().WatchedFolders ?? [],
                // Files opened from outside the watched folders are kept only
                // while the queue holds them; it is restored before this runs.
                queued: () => queue.Snapshot().Tracks.Select(t => t.VideoId).ToHashSet(StringComparer.Ordinal)),
            new LocalFolderWatcher([dataDir, toolsDir]),
            () => sp.GetRequiredService<AppSettings>().WatchedFolders ?? []));
        services.AddSingleton(sp => new KeptCovers(
            sp.GetRequiredService<DownloadStore>(), sp.GetRequiredService<LocalLibraryScans>().Index));
        services.AddSingleton(sp =>
        {
            var queue = new DownloadQueue(sp.GetRequiredService<DownloadStore>());
            queue.Restore();
            return queue;
        });
        services.AddSingleton(sp =>
        {
            var queue = sp.GetRequiredService<DownloadQueue>();
            var kept = new OfflineCollections(new KeptCollectionStore(dataDir), queue.IsDownloaded, queue.IsQueued);
            // A copy goes with the last of its downloads, including any removed by hand
            // while the app was closed.
            kept.Prune();
            queue.DownloadsChanged += _ => kept.Prune();
            return kept;
        });
        services.AddSingleton(new ConnectivityProbe(new HttpClient()));
        // The queue marks what won't play while this says offline. The demo
        // is offline exactly when its streams are told to fail.
        services.AddSingleton(sp => new NetworkStatus(Demo is null
            ? async ct => await sp.GetRequiredService<ConnectivityProbe>().CheckAsync(ct) switch
            {
                Connectivity.Online => Reachability.Reachable,
                Connectivity.Offline => Reachability.Unreachable,
                _ => Reachability.Unknown,
            }
            : _ => Task.FromResult(Demo.Fails("streams") ? Reachability.Unreachable : Reachability.Reachable)));
        // Local files go round the gate: they need ffmpeg, not yt-dlp.
        services.AddSingleton<IStreamSource>(sp => new LocalFirstSource(
            new SidecarGate(
                new DownloadsFirstSource(
                    Demo is null
                        ? new OfflineAwareSource(
                            sp.GetRequiredService<CachingStreamSource>(),
                            sp.GetRequiredService<ConnectivityProbe>().CheckAsync)
                        : sp.GetRequiredService<DemoStreamSource>(),
                    sp.GetRequiredService<DownloadStore>()),
                sp.GetRequiredService<SidecarBootstrapper>()),
            sp.GetRequiredService<LocalLibraryScans>().Index,
            ct => sp.GetRequiredService<SidecarBootstrapper>().AudioReady.WaitAsync(ct)));

        // Hover warm-up: the gate passes it to a source that can warm.
        services.AddSingleton(sp => new HoverWarmup((IStreamWarmer)sp.GetRequiredService<IStreamSource>()));
        if (Demo is not null)
        {
            services.AddSingleton(sp => new DemoStreamSource(
                sp.GetRequiredService<QueueModel>(), Path.Combine(dataDir, "cache", "demo-audio"),
                offline: Demo.Fails("streams")));
        }
        services.AddSingleton(_ =>
        {
            var player = new EngineAudioPlayer { OutputDevice = settings.OutputDevice };
            player.SetEqualizer(settings.EqualizerEnabled, EqualizerSettingsViewModel.Stored(settings));
            return player;
        });
        // Named only, not part of "all": with all streams failing there is no
        // track playing to lose the network.
        services.AddSingleton<IAudioPlayer>(sp => Demo?.FailingAreas.Contains("network-drop") == true
            ? new DemoNetworkDrop(sp.GetRequiredService<EngineAudioPlayer>(), sp.GetRequiredService<DemoStreamSource>())
            : sp.GetRequiredService<EngineAudioPlayer>());
        services.AddSingleton(sp => new PlaybackCoordinator(
            sp.GetRequiredService<IStreamSource>(), sp.GetRequiredService<IAudioPlayer>(),
            sp.GetRequiredService<QueueModel>(), sp.GetRequiredService<PlaybackSettings>(),
            sp.GetRequiredService<ICatalog>()));
        services.AddSingleton(sp => new SidecarBootstrapper(
            sp.GetRequiredService<YtDlpManager>(), sp.GetRequiredService<FfmpegManager>(), needsYtDlp: Demo is null));
        // Linux: the Secret Service, looked for in the background while startup
        // goes on; credentials wait for the answer. Items are keyed by the data
        // folder, so another profile never touches these. A demo run keeps its
        // made-up cookie in its own folder instead: writing it to the keyring
        // would ask a user with no keyring yet to create one.
        var keyring = Demo is null ? SystemKeyring.Detect() : SystemKeyring.None;
        var vault = new CookieVault(dataDir, keyring);
        if (Demo is not null)
        {
            // The demo account is a cookie the demo handler accepts; anonymous
            // demo starts signed out. Either way it's the demo directory's vault.
            if (Demo.SignedIn)
                vault.Save("SAPISID=demo; __Secure-3PAPISID=demo");
            else
                vault.Clear();
        }
        services.AddSingleton(vault);

        var artwork = new ArtworkCache(dataDir, Demo is null ? null : new DemoArtworkHandler(offline: Demo.Fails("artwork")));
        Controls.RemoteImage.Cache = artwork;
        services.AddSingleton(artwork);

        // Like RemoteImage.Cache: composition-time hook for a control. The
        // engine (and analyzer) appears lazily on first play, hence a getter.
        services.AddSingleton<Func<FftAnalyzer?>>(sp =>
        {
            var player = sp.GetRequiredService<EngineAudioPlayer>();
            return () => player.Analyzer;
        });
        _ = System.Threading.Tasks.Task.Run(artwork.TrimDiskCache);

#if WINDOWS
        services.AddSingleton<IMediaSessionService>(sp =>
            new SmtcMediaSessionService(sp.GetRequiredService<KeptCovers>().PathFor));
#else
        services.AddSingleton<IMediaSessionService>(sp => OperatingSystem.IsLinux()
            ? new Rambutan.Integrations.Linux.MprisMediaSessionService("Rambutan",
                localFile: id => sp.GetRequiredService<LocalLibraryScans>().Index.Find(id) is { } file
                    ? (file.Path, file.CoverPath)
                    : null)
            : new NullMediaSessionService());
#endif

        services.AddSingleton(new UpdateService(settings.UpdateFeedUrl ?? UpdateService.ReleasesFeed));
        // Discord stays inert unless the build has an app id and the user
        // turned it on (off by default).
        var discord = new DiscordPresenceService(IntegrationKeys.DiscordAppId);
        discord.Configure(settings.DiscordEnabled, SettingsPageViewModel.DiscordOptions(settings));
        services.AddSingleton(discord);
        // Last.fm exists only in builds given its keys; without them the settings
        // section says so (IntegrationKeys). Nothing is sent until the user connects.
        if (IntegrationKeys.LastFmApiKey is { } lastFmKey && IntegrationKeys.LastFmSharedSecret is { } lastFmSecret)
        {
            _lastFm = new LastFmScrobbler(
                new LastFmClient(lastFmKey, lastFmSecret),
                new ScrobbleQueue(dataDir),
                new LastFmSessionVault(dataDir, keyring))
            {
                ScrobblingEnabled = settings.LastFmScrobbling,
            };
        }
        // Last.fm stations on Home, off by default. Matching searches go
        // through a signed-out client of their own: dozens of searches the user
        // never typed don't belong in the account's search history.
        var stationSearch = Demo is null
            ? new InnerTubeClient()
            : new InnerTubeClient(new HttpClient(new DemoInnerTubeHandler(Demo)));
        // The lists and the search outcomes are saved in the profile's folder,
        // so startup shows the last lists and a known track isn't searched again.
        var lastFm = _lastFm;
        var stationMatches = new StationMatchCache();
        var homeStations = new HomeStations(
            new LastFmStationClient(Demo is null ? null : new HttpClient(new DemoLastFmStationHandler(Demo))),
            new StationResolver(stationSearch.SearchSongsAsync, cache: stationMatches),
            () => Demo is not null ? DemoOptions.LastFmUser
                : lastFm is { Connection: LastFmConnection.Connected } ? lastFm.Username : null,
            new StationSnapshotStore(dataDir),
            new StationMatchStore(dataDir, stationMatches))
        {
            Enabled = settings.LastFmStationsOnHome,
        };
        if (lastFm is not null)
            lastFm.Changed += () => Dispatcher.UIThread.Post(homeStations.ConnectionChanged);
        services.AddSingleton(homeStations);

        services.AddSingleton(_ =>
        {
            var hub = new PlaybackListenerHub();
            hub.Add(discord);
            if (_lastFm is not null)
                hub.Add(_lastFm);
            return hub;
        });

        // Lyrics: custom → a local file's own → cache → LRCLIB, stored under the
        // data directory like the other stores. A demo answers LRCLIB from
        // invented lyrics. The tags are read through libav, so that waits for
        // ffmpeg, as playback does: a restored local track asks at startup.
        services.AddSingleton(sp => LyricsService.Create(dataDir,
        [
            new LrcLibProvider(Demo is null
                ? null
                : new HttpClient(new DemoLyricsHandler(Demo, sp.GetRequiredService<QueueModel>()))),
        ],
        new LocalLyrics(
            id => sp.GetRequiredService<LocalLibraryScans>().Index.Find(id) is { IsMissing: false } file ? file.Path : null,
            async (path, ct) =>
            {
                await sp.GetRequiredService<SidecarBootstrapper>().AudioReady.WaitAsync(ct);
                return sp.GetRequiredService<FfmpegMediaFileReader>().ReadLyrics(path);
            })));
        services.AddSingleton(sp => new LyricsViewModel(
            sp.GetRequiredService<LyricsService>(),
            sp.GetRequiredService<PlaybackCoordinator>(),
            sp.GetRequiredService<QueueModel>(),
            sp.GetRequiredService<AppSettings>().LookUpLyricsOnline,
            online =>
            {
                sp.GetRequiredService<AppSettings>().LookUpLyricsOnline = online;
                RequestPersist(sp);
            }));

        services.AddSingleton<ToastHostViewModel>();
        services.AddSingleton(sp => new LocalFileOpener(
            sp.GetRequiredService<LocalLibraryScans>().Index,
            tracks => sp.GetRequiredService<PlaybackCoordinator>().PlayAfterCurrentAsync(tracks),
            sp.GetRequiredService<QueueModel>().EnqueueRange,
            sp.GetRequiredService<ToastHostViewModel>()));
        services.AddSingleton(sp => new TrackActionsViewModel(
            sp.GetRequiredService<ICatalog>(), sp.GetRequiredService<QueueModel>(),
            sp.GetRequiredService<PlaybackCoordinator>(), sp.GetRequiredService<TrackRatings>(),
            sp.GetRequiredService<ToastHostViewModel>())
        {
            Downloads = sp.GetRequiredService<DownloadQueue>(),
            KeptCollections = sp.GetRequiredService<OfflineCollections>(),
            SongLibrary = new SongLibrary(sp.GetRequiredService<ICatalog>()),
        });
        // Export writes downloads out with the ffmpeg sidecar; off until the user
        // turns it on in Settings.
        services.AddSingleton(sp => new ExportSettings(sp.GetRequiredService<AppSettings>(), () => RequestPersist(sp)));
        services.AddSingleton(sp => new DownloadsPageViewModel(
            sp.GetRequiredService<DownloadQueue>(), sp.GetRequiredService<PlaybackCoordinator>(),
            sp.GetRequiredService<TrackActionsViewModel>(),
            exportSettings: sp.GetRequiredService<ExportSettings>(),
            export: new DownloadExport(
                new FfmpegExportWriter(sp.GetRequiredService<FfmpegManager>().EnsureExecutableAsync), File.Exists),
            exportSource: sp.GetRequiredService<DownloadStore>().ExportSourceFor));
        services.AddSingleton(new SearchHistory(new SearchHistoryStore(dataDir)));
        services.AddSingleton(sp => new LocalLibraryViewModel(
            sp.GetRequiredService<LocalLibraryScans>(), sp.GetRequiredService<PlaybackCoordinator>(),
            () => sp.GetRequiredService<AppSettings>().WatchedFolders ?? []));
        services.AddSingleton<NavigationService>();
        services.AddSingleton(sp => new QueueViewModel(
            sp.GetRequiredService<QueueModel>(), sp.GetRequiredService<PlaybackCoordinator>(),
            sp.GetRequiredService<NetworkStatus>(), sp.GetRequiredService<IStreamSource>() as IOfflineAvailability,
            changed => sp.GetRequiredService<LocalLibraryScans>().Changed += changed));
        services.AddSingleton(sp => new NowPlayingViewModel(
            sp.GetRequiredService<PlayerBarViewModel>(),
            sp.GetRequiredService<LyricsViewModel>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<AppearanceSettings>(),
            sp.GetRequiredService<ThemeColors>()));
        services.AddSingleton(sp => new ThemeColors(
            sp.GetRequiredService<AppearanceSettings>(),
            sp.GetRequiredService<PlayerBarViewModel>(),
            sp.GetRequiredService<ArtworkCache>().GetBytesAsync));
        services.AddSingleton<PlayerBarViewModel>(sp =>
        {
            var vm = new PlayerBarViewModel(
                sp.GetRequiredService<PlaybackCoordinator>(),
                sp.GetRequiredService<QueueModel>(),
                sp.GetRequiredService<TrackRatings>(),
                sp.GetRequiredService<ToastHostViewModel>(),
                changed => sp.GetRequiredService<LocalLibraryScans>().Changed += changed);
            vm.SettingsEdited += () => RequestPersist(sp);
            return vm;
        });
        services.AddSingleton(sp => new SessionViewModel(
            sp.GetRequiredService<InnerTubeClient>(),
            sp.GetRequiredService<CookieVault>(),
            sp.GetRequiredService<ToastHostViewModel>(),
            sp.GetRequiredService<NavigationService>().InvalidateLibrary,
            signInChanged: sp.GetRequiredService<NavigationService>().OnSignInChanged));
        // The phone remote listens only while its setting is on.
        services.AddSingleton(sp => new CoverSource(
            sp.GetRequiredService<ArtworkCache>().GetBytesAsync, sp.GetRequiredService<KeptCovers>().PathFor));
        services.AddSingleton(sp => new PhoneRemoteService(
            sp.GetRequiredService<PlaybackCoordinator>(),
            action => Dispatcher.UIThread.Post(action),
            new RemoteTokenVault(dataDir, keyring),
            sp.GetRequiredService<CoverSource>(),
            searchSongs: RemoteSongPage.SearcherFor(sp.GetRequiredService<ICatalog>()),
            port: location.PreferredPorts.Remote));
        // The OBS overlay is served, to this computer only, while its setting is on.
        services.AddSingleton(sp =>
        {
            var analyzer = sp.GetRequiredService<Func<FftAnalyzer?>>();
            return new ObsOverlayService(
                sp.GetRequiredService<PlaybackCoordinator>(),
                new RemoteTokenVault(dataDir, keyring, "overlay-link", "keyring.label.overlay"),
                new OverlayStyleStore(dataDir),
                sp.GetRequiredService<CoverSource>(),
                bands => analyzer() is { } a ? a.CopyLatest(bands) : -1,
                location.PreferredPorts.Overlay);
        });
        services.AddSingleton<Func<SettingsPageViewModel>>(sp => () => WithTrayCheck(new SettingsPageViewModel(
            sp.GetRequiredService<PlaybackSettings>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<AppearanceSettings>(),
            sp.GetRequiredService<UpdateService>(),
            sp.GetRequiredService<SessionViewModel>(),
            () => RequestPersist(sp),
            sp.GetRequiredService<DiscordPresenceService>(),
            _lastFm,
            OpenInBrowserAsync,
            sp.GetRequiredService<LyricsViewModel>())
        {
            Stations = sp.GetRequiredService<HomeStations>(),
#if WINDOWS
            ApplyTaskbarControls = on => _taskbar?.SetOn(on),
#endif
            Downloads = new DownloadsSettingsViewModel(
                sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<DownloadStore>(),
                defaultDownloads, () => RequestPersist(sp), sp.GetRequiredService<ExportSettings>()),
            Local = new LocalFilesSettingsViewModel(
                sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<LocalLibraryScans>(),
                () => RequestPersist(sp)),
            Remote = new RemoteSettingsViewModel(
                sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<PhoneRemoteService>(),
                () => RequestPersist(sp)),
            Output = OutputDevices(sp),
            Equalizer = new EqualizerSettingsViewModel(
                sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<EngineAudioPlayer>().SetEqualizer,
                () => RequestPersist(sp)),
            Overlay = new OverlaySettingsViewModel(
                sp.GetRequiredService<AppSettings>(), sp.GetRequiredService<ObsOverlayService>(),
                () => RequestPersist(sp))
            {
                Appearance = sp.GetRequiredService<AppearanceSettings>(),
            },
        }));
        services.AddSingleton<MainViewModel>();
        var profile = Profile ?? new ProfileLaunch(
            new ProfileStore(location), new Profile(ProfileStore.DefaultId, null), location, null);
        AppLog.Write("profiles", $"Opened profile {profile.Profile.Id} in process {Environment.ProcessId}.");
        services.AddSingleton(sp => new ProfilesViewModel(
            profile.Store, profile.Profile, keyring, SingleInstanceGuard.TryReserve,
            id => _profileSwitchTo?.Invoke(id)));
        services.AddSingleton(sp => new DeepLinkHandler(
            sp.GetRequiredService<NavigationService>(), sp.GetRequiredService<ICatalog>(),
            sp.GetRequiredService<PlaybackCoordinator>(), sp.GetRequiredService<QueueModel>(),
            sp.GetRequiredService<TrackActionsViewModel>(), sp.GetRequiredService<NetworkStatus>()));

        return services.BuildServiceProvider();

        OutputDeviceSettingsViewModel OutputDevices(IServiceProvider sp)
        {
            var player = sp.GetRequiredService<EngineAudioPlayer>();
            var output = new OutputDeviceSettingsViewModel(
                sp.GetRequiredService<AppSettings>(), player.OutputDevices,
                name => player.OutputDevice = name, () => RequestPersist(sp));
            _ = output.RefreshAsync();
            return output;
        }

        static SettingsPageViewModel WithTrayCheck(SettingsPageViewModel settings)
        {
            _ = settings.CheckTrayAsync(TrayShowsAsync);
            return settings;
        }
    }

    private async System.Threading.Tasks.Task<bool> OpenInBrowserAsync(string url)
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } window }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        try
        {
            return await window.Launcher.LaunchUriAsync(uri);
        }
        catch (Exception ex)
        {
            AppLog.Write("lastfm", $"Couldn't open the browser: {ex.Message}");
            return false;
        }
    }

    private static async System.Threading.Tasks.Task CheckForUpdatesSafeAsync(UpdateService updates)
    {
        try
        {
            await updates.CheckAndStageAsync();
        }
        catch
        {
            // Offline or feed unreachable — try again next launch.
        }
    }

    /// <summary>Coalesces bursts of settings edits (slider drags fire per tick)
    /// into one disk write; OnExit still flushes immediately.</summary>
    private void RequestPersist(IServiceProvider services)
    {
        if (_persistDebounce is null)
        {
            _persistDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _persistDebounce.Tick += (_, _) =>
            {
                _persistDebounce.Stop();
                PersistSettings(services);
            };
        }

        _persistDebounce.Stop();
        _persistDebounce.Start();
    }

    private static void PersistSettings(IServiceProvider services)
    {
        var settings = services.GetRequiredService<AppSettings>();
        var playback = services.GetRequiredService<PlaybackSettings>();
        settings.CrossfadeEnabled = playback.CrossfadeEnabled;
        settings.CrossfadeSeconds = playback.CrossfadeSeconds;
        settings.CrossfadeOnSkip = playback.CrossfadeOnSkip;
        settings.NormalizeVolume = playback.NormalizeVolume;
        settings.Autoplay = playback.Autoplay;
        settings.AudioQuality = AudioQualityNames.Format(playback.AudioQuality);
        settings.Volume = playback.Volume;
        var appearance = services.GetRequiredService<AppearanceSettings>();
        settings.AmbientBackground = appearance.AmbientBackground;
        settings.ArtworkMeter = appearance.ArtworkMeter;
        settings.VisualizerFrameRate = appearance.VisualizerFrameRate;
        settings.VisualizerStyle = AppearanceSettings.FormatStyle(appearance.VisualizerStyle);
        settings.VisualizerAdvanced = appearance.VisualizerAdvanced;
        settings.VisualizerCycle = AppearanceSettings.FormatCycle(appearance.VisualizerCycle);
        settings.VisualizerColor = AppearanceSettings.FormatColorSource(appearance.VisualizerColorSource);
        settings.VisualizerCustomColor = AppearanceSettings.ParseCustomColor(appearance.VisualizerCustomColor);
        settings.AccentColor = AppearanceSettings.FormatAccentSource(appearance.AccentSource);
        settings.AccentCustomColor = AppearanceSettings.ParseCustomColor(appearance.AccentCustomColor, AppearanceSettings.DefaultAccentColor);
        settings.Motion = AppearanceSettings.FormatMotion(appearance.Motion);
        services.GetRequiredService<AppSettingsStore>().Save(settings);

        var cache = services.GetRequiredService<AudioDiskCache>();
        var cap = settings.AudioCacheSizeMb * 1024L * 1024;
        if (cache.MaxSizeBytes != cap)
        {
            cache.MaxSizeBytes = cap;
            cache.EnforceCap();
        }
    }

    /// <summary>
    /// Follows the OS reduce-motion setting: read once at start, off
    /// the UI thread, and on Windows again whenever the window comes back to the
    /// front, which is when a change made in Settings would show.
    /// </summary>
    private static void SetupMotion(MainWindow window, AppearanceSettings appearance)
    {
        _ = ReadAsync();
        window.Activated += (_, _) =>
        {
            if (SystemMotion.ReadWindowsNow() is { } reduced)
                appearance.SystemReducesMotion = reduced;
        };

        async System.Threading.Tasks.Task ReadAsync()
        {
            if (await SystemMotion.ReadAsync() is { } reduced)
                Dispatcher.UIThread.Post(() => appearance.SystemReducesMotion = reduced);
        }
    }

    private void SetupCloseToTray(MainWindow window)
    {
        window.Closing += (_, e) =>
        {
            if (_exitRequested || _services is null)
                return;

            if (_services.GetRequiredService<AppSettings>().CloseToTray)
            {
                e.Cancel = true;
                var trayShows = TrayShowsAsync();
                if (trayShows.Wait(TimeSpan.FromSeconds(2)) && trayShows.Result)
                {
                    window.Hide();
                }
                else
                {
                    // Hidden with no tray icon to click, it could never come back.
                    AppLog.Write("tray", "No tray on this desktop; minimizing instead of hiding.");
                    window.WindowState = WindowState.Minimized;
                }
            }
        };
    }

    /// <summary>Whether the tray icon can be seen: always on Windows; on Linux,
    /// only with a StatusNotifier host, which many desktops lack.</summary>
    private static Task<bool> TrayShowsAsync() => OperatingSystem.IsLinux()
        ? Rambutan.Integrations.Linux.StatusNotifierHost.IsPresentAsync()
        : Task.FromResult(true);

    private void SetupTray(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var coordinator = _services!.GetRequiredService<PlaybackCoordinator>();

        var menu = new NativeMenu();
        menu.Items.Add(MenuItem("tray.show", () => ShowMainWindow(window)));
        menu.Items.Add(MiniPlayerItem(window));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("tray.playPause", coordinator.TogglePlayPause));
        menu.Items.Add(MenuItem("tray.next", () => _ = coordinator.NextAsync()));
        menu.Items.Add(MenuItem("tray.previous", () => _ = coordinator.PreviousAsync()));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(MenuItem("tray.exit", () =>
        {
            _exitRequested = true;
            desktop.Shutdown();
        }));

        _tray = new TrayIcon
        {
            ToolTipText = "Rambutan",
            Icon = window.Icon,
            Menu = menu,
        };
        _tray.Clicked += (_, _) => ShowMainWindow(window);
        TrayIcon.SetIcons(this, [_tray]);

        // The tray lives as long as the app, so a plain subscription re-labels it.
        static NativeMenuItem MenuItem(string key, Action action)
        {
            var item = new NativeMenuItem(Strings.Get(key));
            item.Click += (_, _) => action();
            Strings.Changed += () => item.Header = Strings.Get(key);
            return item;
        }
    }

    /// <summary>The tray's mini player switch, checked while the window is the
    /// mini player. It shows the window too: a hidden window changing shape shows nothing.</summary>
    private static NativeMenuItem MiniPlayerItem(MainWindow window)
    {
        var item = new NativeMenuItem(Strings.Get("tray.miniPlayer")) { ToggleType = MenuItemToggleType.CheckBox };
        Strings.Changed += () => item.Header = Strings.Get("tray.miniPlayer");
        if (window.DataContext is not MainViewModel vm)
            return item;
        item.IsChecked = vm.IsMiniPlayer;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.IsMiniPlayer))
                item.IsChecked = vm.IsMiniPlayer;
        };
        item.Click += (_, _) =>
        {
            vm.IsMiniPlayer = !vm.IsMiniPlayer;
            ShowMainWindow(window);
        };
        return item;
    }

    private void SetupMediaSession(IClassicDesktopStyleApplicationLifetime desktop, MainWindow window)
    {
        var session = _services!.GetRequiredService<IMediaSessionService>();
        var coordinator = _services!.GetRequiredService<PlaybackCoordinator>();

        try
        {
            session.Initialize(window.TryGetPlatformHandle()?.Handle ?? 0);
        }
        catch (Exception)
        {
            return; // media session unavailable — playback works without it
        }

        // Coordinator events can fire under its lock and on engine threads —
        // marshal like every other subscriber; WinRT calls stay off those threads.
        // Like the player bar: the loaded track, else the queue's current one,
        // which Play starts. So a queue restored at launch shows, and media keys
        // can start it.
        // Every queue edit raises Changed; only a different track is pushed.
        var queue = coordinator.Queue;
        var covers = _services!.GetRequiredService<KeptCovers>();
        TrackInfo? shown = null;
        string? shownCover = null;
        void Show()
        {
            var track = coordinator.NowPlaying ?? queue.Current;
            if (track == shown)
                return;
            shown = track;
            shownCover = track is null ? null : covers.PathFor(track.VideoId);
            session.UpdateNowPlaying(track);
        }
        // A scan can give the local file that plays another cover, or take it away.
        _services!.GetRequiredService<LocalLibraryScans>().Changed += () => Dispatcher.UIThread.Post(() =>
        {
            if (shown is not { } track || !Rambutan.Core.Local.LocalIds.IsLocal(track.VideoId))
                return;
            var cover = covers.PathFor(track.VideoId);
            if (cover == shownCover)
                return;
            shownCover = cover;
            session.UpdateArtwork();
        });
        // The session drops repeats of the same mode, so every queue edit can push both.
        void ShowModes()
        {
            session.UpdateShuffle(queue.Shuffle);
            session.UpdateRepeat(queue.Repeat);
        }
        coordinator.NowPlayingChanged += _ => Dispatcher.UIThread.Post(Show);
        queue.Changed += () => Dispatcher.UIThread.Post(() =>
        {
            Show();
            ShowModes();
        });
        Show();
        ShowModes();
        coordinator.StateChanged += state =>
            Dispatcher.UIThread.Post(() => session.UpdatePlaybackState(SessionStatus(state)));
        session.UpdatePlaybackState(SessionStatus(coordinator.State));
        coordinator.VolumeChanged += volume =>
            Dispatcher.UIThread.Post(() => session.UpdateVolume(volume));
        session.UpdateVolume(coordinator.Volume);
        coordinator.ProgressChanged += progress =>
            Dispatcher.UIThread.Post(() => session.UpdateTimeline(progress.Position, progress.Duration));

        session.PlayPauseRequested += () =>
            Dispatcher.UIThread.Post(coordinator.TogglePlayPause);
        session.NextRequested += () =>
            Dispatcher.UIThread.Post(() => _ = coordinator.NextAsync());
        session.PreviousRequested += () =>
            Dispatcher.UIThread.Post(() => _ = coordinator.PreviousAsync());
        session.PlayRequested += () => Dispatcher.UIThread.Post(() =>
        {
            // Only from paused or stopped: toggling while loading would pause.
            if (coordinator.State is PlaybackState.Paused or PlaybackState.Stopped)
                coordinator.TogglePlayPause();
        });
        session.PauseRequested += () => Dispatcher.UIThread.Post(coordinator.Pause);
        session.StopRequested += () => Dispatcher.UIThread.Post(coordinator.Stop);
        session.SeekRequested += position =>
            Dispatcher.UIThread.Post(() => _ = coordinator.SeekAsync(position));
        session.VolumeRequested += volume =>
            Dispatcher.UIThread.Post(() => coordinator.Volume = volume);
        // As the player bar's buttons, without their toast: the asker isn't looking at the app.
        session.ShuffleRequested += on => Dispatcher.UIThread.Post(() => queue.SetShuffle(on));
        session.RepeatRequested += repeat => Dispatcher.UIThread.Post(() => queue.Repeat = repeat);
        session.RaiseRequested += () => Dispatcher.UIThread.Post(() => ShowMainWindow(window));
        session.QuitRequested += () => Dispatcher.UIThread.Post(() =>
        {
            _exitRequested = true;
            desktop.Shutdown();
        });
    }

    /// <summary>A track that is loading was asked to play, so it counts as playing.
    /// One that failed counts as stopped: Play starts it again from the beginning,
    /// which is what Stopped tells the OS.</summary>
    internal static SessionPlaybackStatus SessionStatus(PlaybackState state) => state switch
    {
        PlaybackState.Playing or PlaybackState.Buffering or PlaybackState.Loading => SessionPlaybackStatus.Playing,
        PlaybackState.Paused => SessionPlaybackStatus.Paused,
        _ => SessionPlaybackStatus.Stopped,
    };

    /// <summary>
    /// A link the app was started with: handled once the stored cookies are in,
    /// so a private playlist opens signed in, but not after the network check
    /// that follows. A slow cookie store delays the link by a few seconds at most.
    /// </summary>
    private static async Task HandleStartupLinkAsync(DeepLinkHandler links, SessionViewModel session, string[] args)
    {
        if (Core.Links.DeepLinkArgs.Find(args) is null)
            return;
        try
        {
            await session.CookiesLoaded.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            AppLog.Write("link", "cookies not loaded in time; opening the startup link anyway");
        }
        await links.HandleArgsAsync(args);
    }

    private Action<string>? _profileSwitchTo;

    /// <summary>
    /// A profile switch ends this run normally, so everything is saved, and
    /// <c>Program.Main</c> then starts the app in the chosen profile. With an update
    /// staged, Velopack installs it and does the starting, so the two don't race.
    /// Also says when <c>--profile</c> named no profile, and which one opened.
    /// </summary>
    private void SetupProfileSwitch(IClassicDesktopStyleApplicationLifetime desktop, ProfilesViewModel profiles)
    {
        var services = _services!;
        var updates = services.GetRequiredService<UpdateService>();
        _profileSwitchTo = id =>
        {
            string[] args = ["--profile", id];
            AfterExit = () =>
            {
                if (updates.StagedVersion is not null)
                {
                    updates.RestartNow(args);
                    return;
                }
                if (Environment.ProcessPath is { } exe)
                {
                    var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
                    foreach (var arg in args)
                        start.ArgumentList.Add(arg);
                    System.Diagnostics.Process.Start(start)?.Dispose();
                }
            };
            _exitRequested = true;
            desktop.Shutdown();
        };

        if (Profile?.UnknownRequest is { } unknown)
        {
            services.GetRequiredService<ToastHostViewModel>().Show(Strings.Format(
                "toast.profile.unknown", ("name", unknown), ("opened", profiles.CurrentName)), ToastKind.Error);
        }
    }

    private static byte[] ReadAppIcon()
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://Rambutan.App/Assets/rambutan.ico"));
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static void ShowMainWindow(MainWindow window)
    {
        // Wayland lets no app un-minimize or raise itself.
        if (WaylandToplevel.IsWayland(window))
        {
            WaylandToplevel.Raise(window);
            return;
        }
        window.Show();
        window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void OnExit()
    {
        if (_services is null)
            return;

        _tray?.Dispose();
        _persistDebounce?.Stop();
        PersistSettings(_services);
        _queuePersistence?.Flush();
        var log = _services.GetRequiredService<FileLog>();
        _services.GetRequiredService<IMediaSessionService>().Dispose();
#if WINDOWS
        _taskbar?.Dispose();
#endif
        _services.GetRequiredService<DiscordPresenceService>().Dispose();
        // Queued scrobbles are already on disk; this only stops the worker.
        _lastFm?.Dispose();
        // Off the UI thread, and bounded: a phone holding the event stream must
        // not hold up the exit (the host drops its streams before stopping).
        if (!Task.Run(_services.GetRequiredService<PhoneRemoteService>().StopAsync).Wait(TimeSpan.FromSeconds(3)))
            AppLog.Write("remote", "still stopping at exit; leaving it");
        if (!Task.Run(_services.GetRequiredService<ObsOverlayService>().StopAsync).Wait(TimeSpan.FromSeconds(3)))
            AppLog.Write("overlay", "still stopping at exit; leaving it");
        // Downloads removed from a page while its Undo toast was up: the user was
        // told they are gone, so they go now rather than come back next launch.
        _services.GetRequiredService<TrackActionsViewModel>().FlushPendingRemovals();
        // Saves the jobs still waiting, so they resume at the next start.
        _services.GetRequiredService<DownloadStore>().Dispose();
        _services.GetRequiredService<LocalLibraryScans>().Dispose();
        _services.GetRequiredService<PlaybackCoordinator>().Dispose();
        (_services.GetRequiredService<IAudioPlayer>() as IDisposable)?.Dispose();
        _services.Dispose();

        // Last, and by hand — the container never disposes pre-built instances,
        // and everything above may still be writing evidence on its way down.
        AppLog.Sink = null;
        log.Dispose();
    }
}
