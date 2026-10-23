# CLAUDE.md

Guidance for Claude Code (claude.ai/code) working in this repository.

## Project

Rambutan is a native desktop client for YouTube Music — .NET 10 + Avalonia 12,
**no webview anywhere in the process** — for Windows and Linux. It owes no other
client (including Kodama, the maintainer's earlier WebView2 client, whose source
is not reachable from here) compatibility or migration.

## Where work is tracked

**GitHub is the single source of truth for work and direction.** Repository
documentation (this file, the README, comments) describes only what the code is
and the rules for changing it: no plans, roadmaps, phases, status, release
history or decision history. Don't create a PRD or plan document, and don't add
status tables or trackers to Markdown files. What is built is in the code; what
is not is an issue.

- **Issues:** requirements not yet built, bugs, follow-ups, UX findings, checks and
  decisions. A design note for a feature goes in its issue. Open a new issue for
  anything found and not fixed in the change at hand; close issues with the PR
  that fixes them (`Fixes #n`).

**Decisions belong to the maintainer.** Don't settle an open question in an issue by
implementing an answer. If a task depends on one, say so and ask. When a decision is
made, comment it on the issue (what was decided, by whom, when) before the issue
closes, and move any lasting rule into this file, without an issue number.

## Commands

```bash
dotnet build Rambutan.slnx -warnaserror   # what CI runs; note .slnx, not .sln
dotnet test --solution Rambutan.slnx
dotnet run --project src/Rambutan.App
dotnet publish src/Rambutan.App -c Release -r linux-x64   # AOT smoke (CI runs win-x64 + linux-x64)

# InnerTube fixtures: record / diff against live YTM (live-diff also checks Last.fm stations)
dotnet run --project tools/Rambutan.FixtureRecorder -- --help

# Web apps (web/remote, web/overlay; each its own npm project, output → src/Rambutan.LocalApi/wwwroot/)
npm install && npm run build
```

**Diagnostics env vars:** `RAMBUTAN_STARTUP_TRACE` (startup timing),
`RAMBUTAN_STARTUP_EXIT` (exit after startup, for measuring), `RAMBUTAN_VIZ_TRACE`
and `RAMBUTAN_KARAOKE_TRACE` (visualizer and karaoke frame times),
`RAMBUTAN_LOCALE` (override the UI language), `RAMBUTAN_DATA_DIR` (keep settings,
cookies, caches and logs in another folder; each folder is its own instance, so
copies in different worktrees run side by side; sidecars stay shared). That
folder is the root of its profiles (below); `--profile <name or id>` picks one.
Every data folder but the installed app's has its own pair of preferred LocalApi
ports (`AppDataLocation.PreferredPorts`). `RAMBUTAN_TOOLS_DIR` moves the sidecars
too, so a branch with another ffmpeg pin can't replace the installed app's.

**Demo mode:** `RAMBUTAN_DEMO=1` (or `anonymous`) runs the app on the recorded
fixtures with generated audio and placeholder art, no network needed;
`RAMBUTAN_DEMO_LATENCY` and `RAMBUTAN_DEMO_FAIL` produce loading and failure states
on demand. Details in `Services/Demo/DemoOptions.cs`. A demo never installs or
updates yt-dlp; it still prepares ffmpeg, in the shared tools folder unless
`RAMBUTAN_TOOLS_DIR` says otherwise. In cloud sessions, use the
**`demo` skill** (`tools/ui-driver/ui.py` on Xvfb) to launch, click, type and
screenshot. That's how UI changes get looked at here (definition of done, item 3).

**Cloud sessions:** `.claude/hooks/session-start.sh` installs `dotnet-sdk-10.0`
from Ubuntu's archive and restores packages. `dotnet-install.sh` does not work
there, because the network policy blocks Microsoft's download hosts. If `dotnet`
still isn't available, say you couldn't verify; don't imply you did. The Linux container can build and
test everything, and can show the UI in demo mode on Xvfb (the `demo` skill). It
can't reach YouTube Music, play audible sound, or exercise Windows-only code (SMTC),
so say which checks were impossible.

## Definition of done

1. `dotnet build Rambutan.slnx -warnaserror` passes. **Zero warnings is the
   baseline**; a new warning is a regression.
2. `dotnet test --solution Rambutan.slnx` passes. Logic changes come with tests; Core logic and parsers
   always get tests.
3. UI changes are looked at (screenshots via `dotnet run`), not inferred from XAML.
   If you couldn't look, say so.
4. Issues the change fixes are closed by it (`Fixes #n`), and anything it leaves
   open is an issue. README and this file are updated in the same change when it
   makes them wrong.
5. Commit style follows the history: an imperative, plain-English subject with no
   `feat:`-style prefix, and a body explaining *why*, ending with what was verified
   (e.g. "build -warnaserror clean; 143 tests pass"). Work goes on a branch and
   merges through a PR to `main`.

## Architecture

Nine projects under `src/`. **Feature libraries reference `Rambutan.Core` only;
`Rambutan.App` composes everything.** Never add a reference between feature
libraries; a type two of them share belongs in Core.

| Project | Role |
|---|---|
| `Rambutan.Core` | Playback coordinator + state machine, queue, ports (`ICatalog`, `IStreamSource`, `IAudioPlayer`), `AppLog`. **Pure C#, zero I/O, no package refs** without discussion. |
| `Rambutan.InnerTube` | YTM API client implementing `ICatalog`. Parsers tested against recorded fixtures. |
| `Rambutan.Streams` | yt-dlp + ffmpeg sidecars, stream resolution, audio disk cache, the downloads' fetcher (`GoogleVideoFetcher`, Core's `IAudioFetcher`), export through the ffmpeg executable. The **only** project that touches stream extraction. |
| `Rambutan.Audio` | Engine: ffmpeg decode → ring buffer → crossfade mixer → SoundFlow output, FFT analyzer. Reads googlevideo URLs directly (no proxy). Also reads local files' tags, length and cover (`FfmpegMediaFileReader`), so libav stays in one project. |
| `Rambutan.LocalLibrary` | The local library: scans the watched folders into the per-profile index (`local-index.json`) and cover cache (`local-covers`), through Core's `IMediaFileReader`, and plays them (`LocalFirstSource`). Also the downloads (`DownloadStore`, played by `DownloadsFirstSource`; kept apart from the cache: no cap, no eviction), recorded on their files' entries in that index, and the offline copies of pages (`KeptCollectionStore`). |
| `Rambutan.Lyrics` | Lyrics model + timing rules, LRC parser/writer, Lyricsfile (YAML subset) parser for LRCLIB's word-synced records, LRCLIB provider + chain, per-track cache, custom-lyrics store, `LyricsService`. |
| `Rambutan.Integrations` | SMTC (Windows), MPRIS and the tray-host check (Linux, over D-Bus), single-instance, Velopack updates, Discord Rich Presence (hand-rolled IPC), Last.fm scrobbling and stations. |
| `Rambutan.LocalApi` | Kestrel host started on demand: `/remote` (the phone remote, on the LAN only while its setting is on; token pairing, state over SSE) and `/overlay` (the OBS overlay, in its own host on loopback only, port 41742 for the installed app's data folder, with its own read-only key; style file `overlay-style.json`, versioned). |
| `Rambutan.App` | Avalonia UI (CommunityToolkit.Mvvm), custom controls, **plus app services**: settings store, cookie vault, artwork cache, logging, sidecar bootstrap, tray. |

**Ports and adapters:** Core owns orchestration and
defines the interfaces it needs; feature libraries implement them thinly
("translate, don't decide"); App binds them. Logic that can be tested without I/O
belongs in Core. Libraries log through `Core.Diagnostics.AppLog` (App installs the
file sink), never to files directly.

**Where things actually live** (not always where you'd guess):
- Settings: `App/Services/AppSettingsStore.cs`, a single versioned `AppSettings`
  class persisted as JSON. Add fields with sensible defaults; a rename or change of
  meaning needs a migration. The model lives in App; a feature library that needs
  a setting gets the value passed in by App, not the model.
- Credentials: `App/Services/CookieVault.cs`, `LastFmSessionVault.cs` and
  `RemoteTokenVault.cs` (the phone remote's pairing), all through `StoredCredential`: DPAPI on Windows; on Linux the Secret Service
  (`Integrations/Linux/SecretServiceStore`, D-Bus via Tmds.DBus.Protocol, no
  native libsecret), items keyed by the data folder. **With no keyring running,
  Linux falls back to an unencrypted 0600 file**, and the sign-in screen says so.
  `CookieVault` also keeps the optional Google sign-in (`InnerTube/GoogleSignIn`,
  the accounts.google.com cookies). When a check finds the session refused and
  rotating fails, `InnerTubeClient.ReissueSessionAsync` replays Google's sign-in
  redirects with it to get new youtube.com cookies. Those cookies open the whole
  Google account: they go to accounts.google.com and nowhere else.
- Profiles: `App/Services/Profiles.cs`. A profile is a data folder:
  `default` is the root data folder itself, others are `profiles/<id>` (the
  folder comes from the id, never from the file), listed in the versioned
  `profiles.json` in the root. Downloads are per profile. Switching restarts the
  app (`App.AfterExit`); deleting removes the folder and the keyring items
  keyed by it, never while that profile runs.
- Styling: stock `FluentTheme`; our tokens (colours, radii, type sizes) in
  `App/Styles/Tokens.axaml`, control styles in `App/Styles/Controls.axaml` and
  `Browse.axaml`, icons as geometry in `App/Styles/Icons.axaml` (not a font; glyph
  fonts differ per platform). No third-party theme packages. The UI font is
  Figtree, bundled under `Assets/Fonts` (OFL); it is Latin-only, so Inter comes
  first in the fallback chain (`Program.cs`). Album art is blurred **once** into a
  page-background layer with translucent panels above it (Avalonia has no
  backdrop blur).
- Accent: the user's choice (Rambutan red, the cover's colour, or custom).
  `Services/ThemeColors` resolves it, `Core/Appearance/AccentPalette` makes it
  readable and derives the rest (the Fluent ramp, button states, white or dark
  text on it; the default red stays a fixed table equal to `Tokens.axaml`), and
  `Services/AccentResources` writes it into the app's resources, changing the
  accent brushes in place. So reach the accent only through those brushes or
  DynamicResource, and draw on it with `Token.Brush.OnAccent`, never with a hex
  literal or white. The phone remote gets it in its state.
- Visualizer: `FftAnalyzer` emits raw bands, and a decimated stereo waveform
  for the styles that draw one; smoothing and peaks live in
  `Controls/VisualizerControl`, which draws on the composition thread. Keep it
  that way: the OBS overlay smooths the raw bands itself. Now Playing draws
  through an `IVisualizerRenderer` on Skia's canvas (Avalonia's Skia lease),
  one per style (`VisualizerRenderers.Create`), which asks only for the inputs
  it uses; beats come from `Core/Visualizer/OnsetDetector`. A renderer is
  built on the UI thread and from then on belongs to the render thread, which
  disposes it; it allocates nothing per frame (`VisualizerRendererTests`),
  except the shader presets, which build a SkiaSharp shader each frame (a few
  hundred bytes, bounded by `VisualizerPresetTests`; SkiaSharp can't update a
  built shader's uniforms).
  The animated presets are our own: SkSL shaders under `App/Visualizer/*.sksl`
  (`ShaderRenderer`) and the feedback loop (`FeedbackRenderer`), not projectM.
  They show only while the "Animated presets" setting is on, whose help says
  they cost more processor and graphics card. They draw offscreen at
  `VisualizerRenderScale` (half the device pixels at most, within a GPU and a
  smaller software budget) and scale up; without a GPU context they draw in
  software and Now Playing says so. They move only with the feed's activity, so
  they come to rest when it stops. A new preset passes the photosensitivity
  test in `VisualizerPresetTests` (at most 3 flashes a second, from WCAG 2.3.1).
  The visualizer always fills the Now Playing pane (the stage), dimmed under
  the artwork and more under the lyrics. Two header toggles (`ShowArtwork`,
  `ShowLyrics`) put the artwork and the lyrics over it: side by side from
  `NowPlayingView.SideBySideWidth`, else stacked with a few lines under the
  artwork. Fullscreen follows the same toggles. Changing the style
  automatically (`VisualizerCycler`) picks among the styles the picker offers.
  The radial style draws around the artwork in place of the stage, and the
  stage draws bars where there is no artwork (hidden, or no cover).
  `NowPlayingViewModel` decides which one shows and polls. The
  "artwork" colour is `Core/Visualizer/ArtworkColor`, read from a small
  decode of the cover by `Services/ThemeColors`. The compact meter and
  the overlay preview draw with Avalonia's primitives, always as bars. The overlay style
  editor's native preview reuses the control with `OverlayBars`, which groups
  bands as the overlay page does. Each redraw costs a compositor frame, so the
  control redraws only through `VisualizerRedrawGate` (visible change only, at
  most `MaxFrameRate` a second); the meter and Now Playing follow the user's
  "Visualizer frame rate" setting, 30 fps by default. A style that moves on its
  own (`IsAnimated`) draws at that cap instead, until the feed stops or goes
  silent (paused, the output runs on) and it comes to rest. Each analyzer output runs only while a reader calls it
  (`CopyLatest`, `CopyWaveform`) within its `ReadLease`, so a reader that
  isn't seen stops reading: the control stops while it is inactive or its
  window is minimized or hidden.
- Native Wayland: `App/Services/WaylandFramePacing` (frame pacing) and
  `App/Services/WaylandToplevel` (app id `rambutan`, raising a window)
  reach into Avalonia.Wayland's internals by name; `WaylandFramePacingTests` and
  `WaylandToplevelTests` pin those names, so an Avalonia update that renames them
  fails there. The dock icon comes from the hidden `rambutan.desktop` and hicolor
  PNGs that `Integrations/Linux/LinuxAppEntry` writes; the AppImage owns the
  entry, other runs on the installed data folder only fill a missing one.
- Output device: it runs while anything plays, and `EngineAudioPlayer` stops
  it, with the progress timer, after `OutputIdleTimer.DefaultDelay` (10 minutes)
  paused or stopped; play and resume start it again. A shorter delay makes
  quick resumes slow (a Bluetooth output can lose the start of a track), so
  don't shorten it without the maintainer.
- Mini player: the main window itself, not a second window.
  `MainWindow.ApplyMiniPlayer` changes its shape, the `mini` class hides what
  it has no room for (`fullOnly` / `miniOnly` in `Controls.axaml`), and
  `WindowStatePersistence` keeps its bounds apart from the full window's. The
  page and panels stay underneath, so leaving puts them back. Native Wayland
  can't keep a window on top, so there the pin is disabled and says why.
- Fullscreen: two modes in the main window. The drawn caption's fullscreen
  button gives the whole app the screen; Now Playing's fullscreen button
  (`NowPlayingViewModel.IsFullscreen`) gives it to Now Playing: the
  `stagefull` class hides the `frame` (title bar, sidebar, player bar, queue)
  and `MainWindow.ApplyStageFullscreen` sets the window state. Its controls
  fade out after 3 s without input, and the pointer with them, but only
  while the window is active: over an inactive window (a dialog has the
  focus) the pointer stays. Esc and F11 leave either mode, and
  leaving the window's fullscreen from outside ends the visualizer's too.
  Neither mode is saved (`WindowStatePersistence` writes only Normal or
  Maximized), and fullscreen and the mini player end each other.
- Taskbar (Windows): the buttons under the taskbar preview and the progress
  on the taskbar button are `Integrations/Taskbar` (`TaskbarToolbarState` holds
  the rules, `TaskbarThumbnailToolbar` the `ITaskbarList3` calls) and
  `App/Services/TaskbarControls`, which forwards the window's messages and
  draws the icons from `Icons.axaml` (`TaskbarGlyphs`). Windows takes the
  buttons once per taskbar button and never removes them, so the setting
  hides them. The bar stays in the normal state, paused or not: the paused
  and error states draw yellow and red, which read as warnings.
- Lyrics degrade in explicit states: word-synced → line-synced → plain → "no
  lyrics found". Imported custom lyrics always win over providers.
  A local track's own lyrics come next (`Lyrics/Store/LocalLyrics`): the
  `.lrc` beside the file (found ignoring case; its bytes decoded by
  `Lyrics/Parsing/LrcFileText`: a byte order mark, else UTF-8 if valid, else
  the system's ANSI code page, Windows-1252 off Windows), then an ID3v2
  tag's `SYLT` (`Lyrics/Parsing/SyltParser`; libav doesn't expose it), then
  its other tags (read through `FfmpegMediaFileReader.ReadLyrics`, which
  waits for ffmpeg); a text that parses as synced LRC shows synced. They are read on each lookup, never cached, and
  whatever the online-lookup setting; the cache and LRCLIB come after them.
- Artwork: hand-rolled `Controls/RemoteImage` + `Services/ArtworkCache`.
- Card menus: a song card has the track menu (`TrackMenuModel`); album,
  playlist and artist cards have their own (`CollectionMenuModel`, acting
  through `CollectionActionsViewModel`); `Controls/ContextMenuHost` opens both,
  from the keyboard too. A card doesn't know whether it is saved or subscribed,
  so its menu offers only to add, and to remove only in a list that says its
  cards are in the library (`CollectionMenu.InLibrary`, the Library tabs).
  Signed out, neither shows. No Rename or Delete on a card: they stay on the
  playlist's page.
- Offline pages: `Core/Downloads/OfflineCatalog` wraps the `ICatalog` that App
  registers. When an album or playlist page, or the Library's Albums or Playlists
  tab, can't load, it serves the copy that "Download all" kept
  (`downloads-collections.json`, `LocalLibrary/KeptCollectionStore`), marked
  `KeptAt` / `IsOfflineCopy` so the page says so.
- Which id plays: a row's `TrackInfo.SongId` (YTM's credits link) names the
  album audio track. Album pages play the `SongId` when a row has one and fall
  back to the `VideoId` if it doesn't resolve; every other surface plays the
  video it lists. Lyrics, downloads and the now-playing marker follow the id that
  plays. `Core/Models/AlbumAudio` swaps the ids and `PlaybackCoordinator`
  falls back to the listed video. A download plays under its YouTube id; the
  `local:<n>` id of its entry in the local index stays inside LocalLibrary.
  Builds before downloads were in the index kept them in `downloads.json`: the
  first start takes its records in once and leaves the file as it was.
- Local library: the watched folders are a setting (`AppSettings.WatchedFolders`,
  per profile like every setting). `LocalLibrary/LocalFileIndex` scans them at
  startup, when a folder is added, and on Rescan. While the app runs,
  `LocalLibrary/LocalFolderWatcher` watches them (the outermost ones only:
  on Linux each costs one of few inotify instances) and scans a folder again
  once it has been quiet for 2 s; the rules, which events count and what an
  error means, are `Core/Local/LocalWatch`. The app's own folders never
  count, or its log writes would start a scan after each scan. A scan of
  some folders leaves the others, and their unavailable state, as they were,
  and one that stops a running scan takes on its folders. A watcher that
  fails stops, and Settings says the folder isn't watched until a later scan
  arms it again.
  The rules are in Core (`Core/Local/LocalScan`): a file is read again only when
  its size or modified time changed; its `local:<n>` id stays with its path and
  is never given again; a file that is gone stays in the index as missing; a
  watched folder that can't be listed (an unplugged drive) leaves its files as
  they were, and so does one that lists no audio file while the index has
  files present under it (a Linux mount whose drive is down lists empty);
  files of a folder no longer watched stay in the index, unlisted.
  The reader detects the format from the content after any ID3v2 tag, never the
  extension, and reads stream info only when the open gives no length. Its tests
  need the ffmpeg shared libraries (`RAMBUTAN_FFMPEG_LIBS`, else the app's tools
  folder on Windows or the system's on Linux) and skip without them, as on CI.
  A local track is `Core/Local/LocalTracks.ToTrack` under its `local:<n>` id.
  `LocalFirstSource` sits outermost on `IStreamSource`: a local id plays its
  file and never reaches the streams, even on a fault retry; a file that is
  gone throws `LocalFileMissingException`, and the queue marks it. Playback and
  scans wait only for ffmpeg (`SidecarBootstrapper.AudioReady`), not yt-dlp,
  so they work offline on a first start. ReplayGain's track gain becomes
  YouTube's loudness (`LocalTracks.LoudnessDb`). An embedded cover always
  wins; a track without one takes its folder's picture (`cover`, `folder`,
  `front` or `AlbumArt*`, hidden ones too: `Core/Local/LocalFolderCovers`),
  copied into the cover cache, and a changed picture updates it without
  reading the track again. Covers come by id through
  `Services/KeptCovers`, never as a URL in the track, so no file path reaches
  Discord or `queue.json`; when the index changes, the player bar, the queue
  rows and the media session read the playing file's cover again
  (`IMediaSessionService.UpdateArtwork`). The YouTube-only actions (radio, like, library,
  playlists, download, share) are off for such a track
  (`TrackActionsViewModel.IsOnYouTube`), and autoplay stops with a notice,
  unless the track has a YouTube Music match.
  Matching is an outbound integration, off by default
  (`AppSettings.MatchLocalOnYouTubeMusic`): `Core/Local/LocalMatcher` searches
  a file's title and artist tags (never its file name; none without a title
  tag) through the stations' signed-out `InnerTubeClient`, when it plays, when
  its menu opens and when autoplay needs its radio, never the whole library.
  It picks with `TrackMatcher` without the last resort, so another version
  never matches. `App/Services/YouTubeMatches` keeps the answers by local id
  and tag key (`local-matches.json`, versioned, dropped on a new
  `TrackMatcher.RulesVersion`). A match drives the menu's YouTube Music group
  (a line naming the song or saying why there is none, then Start a mix, Like
  and Save to playlist acting on the song), autoplay's radio
  (`PlaybackCoordinator.RadioSeedFor`, which leaves the matched song out of
  what follows), and what the tags lack as shown, never written: the artist and
  album names in the player bar, Now Playing and the media session
  (`YouTubeMatches.Fill`, without the song's links) and, for a file with no
  cover of its own or its folder's, the song's cover, kept in
  `local-match-covers` and found through `KeptCovers`; a kept cover stays only
  while a current match of a file without a cover of its own uses it (a
  missing file's too). The queue keeps the file's own track, so turning
  matching off takes all of it back, and deletes the kept covers; the saved
  matches stay.
  Library has two sources, YouTube Music and On this computer
  (`LocalLibraryViewModel`), signed out too; with no watched folder it offers Settings.
  A file opened (Ctrl+O) or dropped on the window (`Services/LocalFileOpener`,
  `LocalFileIndex.OpenAsync`) keeps the id the index has for its path, else
  gets a new `local:<n>`; one outside the watched folders is kept as opened
  (`LocalFile.IsOpened`), never listed, and forgotten at the next start when
  the queue no longer holds it (`ForgetOpened`). A drop on the queue pane adds
  to its end; elsewhere the files play after the current track
  (`PlaybackCoordinator.PlayAfterCurrentAsync`); the queue is never replaced.
  Its lyrics cache file is `local.<n>.json`; its own lyrics (below, under
  Lyrics) come before it. Reading opened files for longer than
  `LocalFileOpener.ShowAfter` shows on the status strip, with the number of
  files once they are listed, until the last open ends. Search's All tab puts the
  index's matches (`Core/Local/LocalSearch`) on a shelf above YouTube Music's
  results, at most `SearchPageViewModel.LocalKept` rows, since that list
  doesn't virtualize; the shelf stays when YouTube Music can't be reached.
- Ids sent to YouTube: InnerTube's track calls (radio, mix, rating, playlist
  edits) and the stream resolver refuse any id that isn't YouTube-shaped
  (`Core/Models/YouTubeIds`, `NotAYouTubeTrackException`) before any request,
  so a track from elsewhere never reaches YouTube Music. Copy link and MPRIS's
  `xesam:url` go through `Core/Links/ShareLinks.ForTrack`, which has no link
  for such an id; MPRIS gives a local file's `file://` URL instead.
- Last.fm stations on Home, and artists' stations from their pages:
  `Integrations/LastFm/LastFmStationClient` reads the web player's
  undocumented station endpoint (`user/<name>/…`, `music/<artist>/+similar`;
  its tag stations sit behind a JavaScript challenge, so there are none);
  `Core/Stations/TrackMatcher`
  matches each track to a YTM song (tested against recorded tracks in
  `tests/Rambutan.Core.Tests/Fixtures/Stations`); `App/Services/HomeStations`
  wires it up. The matching searches go through a signed-out `InnerTubeClient`
  of their own, never the account's, so they stay out of its search history.
  Bump `TrackMatcher.RulesVersion` when a rule change could change a pick; it
  invalidates the saved matches (`App/Services/StationStores`).
- `rambutan://` links: the grammar is `Core/Links/DeepLinkParser`, acting on a
  link is `ViewModels/DeepLinkHandler`, and the Windows registration (HKCU,
  from Velopack's install, update and uninstall hooks only) is
  `Integrations/DeepLinkRegistration`; on Linux the AppImage registers itself at
  startup with a hidden desktop file (`Integrations/Linux/LinuxDeepLinkRegistration`).
  A web page can fire a link, so a link
  only reads and navigates: it never deletes, signs out, changes a setting,
  writes the search history or replaces the queue.
- Logo: `Assets/rambutan.ico` is generated by `tools/logo/logo.py` (Pillow), which
  also writes SVG copies. Change the script, not the icon.

`web/remote` and `web/overlay` are Svelte 5 + Vite projects whose builds embed
into LocalApi. Web exists only because their clients are phones and OBS. CI
doesn't run npm, so the built output under `LocalApi/wwwroot` is committed: a
change under `web/` comes with its `npm run build` output (`git add -A` on the
folder, so replaced hashed files go too). Browser baselines: the remote keeps
Vite's default (Chrome 111, Safari 16.4); the overlay targets Chrome 103
for OBS 30's browser source.

**Tests:** xunit v3, one project per library,
plus `tests/Rambutan.App.Tests` for view-model logic and custom controls (controls run
under `Avalonia.Headless`; see `HeadlessSession.cs`). xunit v3 runs on
Microsoft.Testing.Platform; `global.json` opts `dotnet test` into that mode,
because the .NET 10 SDK refuses the old VSTest mode for these projects.
InnerTube fixtures are real recorded responses under `tests/Rambutan.InnerTube.Tests/Fixtures`
(`*.synthetic.json` are hand-authored where the surface needs auth; replace them with recorded, scrubbed captures when possible). Scrub
personal data before committing any fixture.

**Comments** say only what the code can't: why (YouTube Music's behaviour, a
race, a platform quirk, a maintainer's decision, a format's rules) and the
contract (null when, throws when, which thread). Don't restate a member's name
or the line below it, and don't cite issue numbers or planning ids.

## Hard constraints

When two of these conflict, the one higher in the list wins.

- **Native or nothing.** If an element can't be built well in Avalonia, redesign
  the element; don't embed web content.
- **In-process by default.** HTTP only where the client is another device (phone
  remote, OBS overlay). The audio engine reads googlevideo URLs directly, with no
  localhost proxy. LocalApi binds localhost by default and the LAN only while the
  remote is on; pairing is always tokenized.
- **Own the breakage.** InnerTube churn is caught by recorded-fixture tests and
  the weekly live-diff; yt-dlp (auto-updated) is the *only* component that
  touches stream extraction.
- **NativeAOT.** Release publishes with `PublishAot`; every
  library builds with `IsAotCompatible` analyzers (Directory.Build.props), and CI
  runs an AOT publish on both platforms. So: source-generated `System.Text.Json`
  only, no reflection-based serializers or runtime codegen, and **vet every new
  NuGet package for trim/AOT safety before adding it**. Say how you vetted it
  (AOT publish with no new warnings, at minimum).
- **License:** GPL-3.0-or-later. Every component that ships in the build (NuGet
  packages and their native libraries, fonts, the Svelte runtime) has its notice
  in `THIRD-PARTY-NOTICES.md` or under `licenses/`, and both ship beside the
  executable. A new or updated shipped dependency updates them in the same change.
- **No webview, ever.** No WebView2/CEF/Photino.
- **Own formats, versioned from day one:** anything a user invests effort in
  (settings, overlay configs, custom lyrics, caches with a schema) is versioned
  JSON from the first commit that writes it. i18n stays flat key→string JSON, not
  `.resx`, and every user-visible string must go through it.
- **Version:** single source of truth is `<Version>` in `Directory.Build.props`;
  the release workflow refuses a `v*` tag that doesn't match it. Never hardcode a
  version string.
- **Honest degradation:** every feature state has a visual. No blank panes,
  silent no-ops, or spinners that outlive their request. Errors are non-modal,
  dismissible, and expire when no longer true.
- **Outbound integrations** (Discord, Last.fm, anything that sends data off the
  machine) are off by default, and their settings say exactly what is sent. Lyrics
  lookups count: showing the lyrics pane and looking lyrics up online are two
  settings, and lookups are off by default. When Last.fm ends the connection
  itself, plays are still recorded locally and sent on reconnect; only the
  user's own Disconnect discards them.
- **Word-level lyrics** come only from the user's own enhanced LRC and from
  LRCLIB's word-synced records. Everything else is line-level, and the app says so.
- **Export is opt-in:** writing downloads out as ordinary files sits behind a
  setting that is off by default.
- **Linux is supported** beside Windows, not "experimental". Where no keyring is
  running, the Linux sign-in screen and the release notes say that cookies are
  stored unencrypted.
- **No telemetry** without an opt-in. Crash data never leaves the machine on its
  own: the app keeps a local crash file, and the user chooses to report it as a
  GitHub issue. The file is `crash.json` in the profile's folder
  (`App/Services/CrashFile`, scrubbed by `Core/Diagnostics/CrashReport`); the
  status strip offers it on the next start.
- **Accessibility:** everything must be keyboard-navigable, every control must
  have a screen-reader name, and reduced motion is honoured (no karaoke sweep,
  visualizer or decorative animation; line sync stays).
