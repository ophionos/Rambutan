# Rambutan Music Player

Rambutan Music Player is a native desktop client for YouTube Music, for
Windows and Linux — .NET 10 + Avalonia, no webview anywhere in the process.
Word-by-word karaoke lyrics, crossfade, visualizer, phone remote, OBS overlay —
compiled and Skia-rendered.

Work, bugs and decisions are tracked in
[GitHub issues](https://github.com/ophionos/Rambutan/issues).

## Why

Other YouTube Music desktop clients render in a web engine (WebView2 or
Electron). Rambutan has none in the process, so it is lighter and faster; the
numbers are below.

## Measured

NativeAOT `win-x64` build, single 38 MB executable. The numbers *are* the
marketing, so here they are — reproduce them with `build/pack.ps1` plus the
`RAMBUTAN_STARTUP_TRACE` env var.

| | Measured | Budget |
|---|---|---|
| Cold start to interactive shell | **401 ms** (median of 5) | < 1.5 s |
| Working set, idle-playing, audio buffers included | **210 MB** peak | < 250 MB |
| Visualizer and karaoke frame rate | **~100 fps** (`RAMBUTAN_VIZ_TRACE`, `RAMBUTAN_KARAOKE_TRACE`) | 60 fps |

Reference machine: Ryzen 9 5900X, 32 GB, NVMe, Windows 11 — a desktop, so it
flatters the startup figure. The budgets target a mid-range 2023 laptop with an
SSD; a number from faster hardware says so. Memory
is measured playing a track after a home-feed browse and a search (the artwork
cache at work), sampled over 30 s of steady-state playback.

## What it does

**Works on Windows:**
- Search, Home (with its mood chips), Moods & genres, album, artist and playlist
  pages, radio, and your library (playlists, songs, albums, artists), with save,
  like and playlist editing.
- Gapless playback, crossfade (off by default), volume normalization, a
  10-band equalizer (off by default), a queue you can reorder, and the last queue restored at startup. Sound follows the
  system's default output device, and Settings can choose another.
- Synced lyrics, with a word-by-word karaoke sweep where word timing exists
  (your own enhanced LRC, or LRCLIB's word-synced records), and lyrics you import
  yourself. Looking lyrics up online is off by default.
- A visualizer (off under reduced motion) and a background tinted by the
  album art.
- Downloads, which play with no connection; downloaded albums and playlists also
  open offline from the Library. Export of downloads as ordinary audio files is
  off by default.
- Media keys and controls (Windows; MPRIS on Linux), the tray, keyboard shortcuts (F1 lists them),
  and screen-reader names (checked with Narrator on Windows).
- Discord Rich Presence and Last.fm scrobbling, both off by default; each
  setting says exactly what it sends.
- Your Last.fm mix and recommendations as shelves on Home (off by default). Each
  track plays from YouTube Music when it has the song, and otherwise from the
  YouTube link Last.fm gives. "See all" lists every track, with the ones that
  can't play marked, and the last lists stay available offline. With the same
  setting, an artist's page offers a Last.fm station of that artist and similar
  ones.
- The phone remote (Settings → Phone remote) plays, pauses, skips, seeks,
  sets the volume, shows and edits the queue, and searches for songs to play now,
  play next or add.
- The OBS overlay (Settings → OBS overlay) is a browser source on this computer
  only, styled in the app's style editor (with a live preview) or through an exported and
  imported style file.
- `rambutan://` links (`play/<videoId>`, `album/<id>`, `playlist/<id>`,
  `artist/<channelId>`, `search?q=…`) and music.youtube.com links given on the
  command line open in the app. The Windows installer registers the scheme for
  your user; a portable or dev run never registers it. On Linux the
  AppImage registers it for your user each time it starts, with a hidden entry
  in `~/.local/share/applications`, unless another application already handles
  the scheme; the entry stays if you delete the AppImage. A link only
  opens pages and plays; with songs already queued, a played song goes next
  instead of replacing the queue.

**Limits:**
- Sign-in is by pasting cookies copied from your browser while signed in to
  music.youtube.com; there is no Google sign-in page.
- The installer isn't code-signed, so Windows SmartScreen warns before it runs.
- **Linux** (the x64 AppImage) has been checked on real hardware with one
  desktop so far (COSMIC on Wayland). Media keys and the panel's media controls
  work through MPRIS. Desktops that show no tray icons (GNOME without
  the AppIndicator extension) get no tray; closing to the tray then minimizes. Your YouTube cookies, Last.fm session and phone
  remote pairing go into the system keyring (GNOME Keyring, KWallet, or another Secret Service). When no
  keyring is running, they are stored **unencrypted**, in a file only your user
  account can read, and the sign-in screen says so.
- English only.

## Layout

| Project | Role |
|---|---|
| `src/Rambutan.Core` | Domain: playback coordinator + state machine, queue, ports. Pure C#, zero I/O. |
| `src/Rambutan.InnerTube` | YouTube Music API client (search, browse, library, auth). |
| `src/Rambutan.Streams` | yt-dlp + ffmpeg sidecar management, stream resolution, disk cache. |
| `src/Rambutan.Audio` | Playback engine: decode → buffer → mix → output, crossfade, FFT analyzer. |
| `src/Rambutan.Lyrics` | Lyrics model, LRC and Lyricsfile parsing, LRCLIB provider chain, cache, custom lyrics. |
| `src/Rambutan.Integrations` | Media controls (SMTC on Windows, MPRIS on Linux), keyring storage on Linux, single-instance, self-update, Discord Rich Presence, Last.fm. |
| `src/Rambutan.App` | Avalonia UI (CommunityToolkit.Mvvm) and the app services that compose everything. |
| `src/Rambutan.LocalApi` | In-process server for clients on other devices: the phone remote while its setting is on, and the OBS overlay (this computer only) while its setting is on. |
| `web/remote`, `web/overlay` | Svelte phone remote and OBS overlay, built into LocalApi's `wwwroot`. |
| `tests/` | xUnit per library: Core logic, InnerTube parsers against recorded fixtures, Streams, Lyrics, Integrations; App view models and controls under Avalonia.Headless. |
| `tools/Rambutan.FixtureRecorder` | Records InnerTube fixtures; the weekly CI live-diff runs it. |
| `tools/ui-driver`, `tools/logo` | Drives the app in demo mode on Xvfb; generates the icon and its SVG copies. |

Dependency rule: libraries reference `Core` only; `App` composes everything.
A feature cannot sprawl across layers — `Rambutan.Lyrics` cannot see `Rambutan.App`.

## Build

Requires the .NET 10 SDK. On Linux, AOT publishing also needs `clang` and
`zlib1g-dev`, and running the app needs PulseAudio, the Wayland client libraries (a Wayland
session gets a native window) and the X11 ones (the fallback), fontconfig, and
Noto fonts with CJK and colour emoji (on Arch: `libpulse wayland libxkbcommon
libx11 libice libsm fontconfig noto-fonts noto-fonts-cjk noto-fonts-emoji`).

```bash
dotnet build Rambutan.slnx -warnaserror   # what CI runs; zero warnings is the baseline
dotnet test --solution Rambutan.slnx
dotnet run --project src/Rambutan.App
dotnet publish src/Rambutan.App -c Release -r linux-x64   # NativeAOT (or win-x64)

# web apps (each of web/remote, web/overlay):
npm install && npm run build # outputs into src/Rambutan.LocalApi/wwwroot/
```

### Demo mode

`RAMBUTAN_DEMO=1 dotnet run --project src/Rambutan.App` runs the app with no network:
the catalog comes from the recorded InnerTube fixtures, audio is generated, and
artwork is placeholder. Everything above those three edges (parsers, playback,
audio engine, UI) is the real code. It's for screenshots, demos and UI work
without an account or a connection. Demo runs keep their own settings and caches.
Set `RAMBUTAN_DEMO=anonymous` to start signed out, `RAMBUTAN_DEMO_LATENCY=<ms>` to slow
responses, and `RAMBUTAN_DEMO_FAIL=home,search,…` (or `all`) to make areas fail as
if offline. `RAMBUTAN_DATA_DIR=<folder>` keeps a run's data in that folder, so
several copies (a demo per worktree, say) can run at once, each with its own
phone-remote and overlay ports. `RAMBUTAN_TOOLS_DIR=<folder>` moves the ffmpeg and
yt-dlp sidecars as well (a demo never touches yt-dlp). Profiles live under
that folder; `--profile <name>` starts the app in one. `tools/ui-driver/ui.py` launches it on a virtual display and clicks,
types and screenshots.

CI (`.github/workflows/ci.yml`) builds, tests and AOT-publishes on Windows and
Linux for every push and PR. A weekly job diffs live YouTube Music responses
against the recorded fixtures.

### Claude Code on the web

`.claude/hooks/session-start.sh` prepares cloud sessions. It installs
`dotnet-sdk-10.0` from Ubuntu's archive if it's missing, then restores packages.
It uses Ubuntu's package rather than `dotnet-install.sh` because the cloud
network policy blocks Microsoft's download hosts. It also installs Xvfb and the
UI driver's Python packages, so a session can look at the app in demo mode (the
`demo` skill in `.claude/skills/`). It does nothing in local sessions. Guidance for agents working in the repo is in [CLAUDE.md](CLAUDE.md).

NativeAOT: `Release` publishes with `PublishAot`, and every
library builds with AOT-compatibility analyzers on. Keep dependencies trim-safe and
use source-generated `System.Text.Json` everywhere.

## Packaging

```powershell
./build/pack.ps1                         # AOT publish + vpk pack → build/releases (channel win)
./build/pack.ps1 -Channel win-beta       # beta channel
./build/pack.ps1 -Rid linux-x64          # Linux AppImage (channel linux; run on Linux)
```

Tagging `v<version>` (matching `<Version>` in `Directory.Build.props`) drafts a
GitHub release with the Windows installer, portable zip and delta packages, plus
the Linux AppImage. A tag with a prerelease suffix
(`v0.1.0-beta.1`) goes to the beta channels (`win-beta`, `linux-beta`) and is
marked a prerelease. Installed builds update from the published releases of this
repository, in the channel they were packed with. The release workflow reads the integration keys from the
secrets `RAMBUTAN_DISCORD_APP_ID`, `RAMBUTAN_LASTFM_API_KEY` and
`RAMBUTAN_LASTFM_SHARED_SECRET`; a build without them shows those integrations as
unavailable.

## Glossary

| Term | Meaning |
|---|---|
| InnerTube | YouTube's internal JSON API; undocumented and unstable |
| googlevideo | The CDN hosts serving audio; URLs expire after a few hours |
| PO token / SABR | YouTube anti-scraping measures that yt-dlp tracks |
| Sidecar | A binary Rambutan downloads and manages (yt-dlp, ffmpeg) |
| LRC / enhanced LRC | Lyrics with `[mm:ss.xx]` line stamps / plus `<mm:ss.xx>` word stamps |
| Lyricsfile | LRCLIB's YAML lyrics document, which can carry word timings |
| SMTC / MPRIS | Windows / Linux media-control interfaces |
| LocalApi | The in-process HTTP server for the remote and overlay |
