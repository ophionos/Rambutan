# Contributing to Rambutan

Thanks for helping. This file says how to build Rambutan, how the code is laid
out, and what a change needs before it can merge. Everyone who takes part
follows the [code of conduct](CODE_OF_CONDUCT.md).

## Before you start

- **Start with an issue.** Work, bugs and decisions are tracked in
  [GitHub issues](https://github.com/ophionos/Rambutan/issues). For anything
  larger than a small fix, open or comment on an issue first, so we agree on
  the change before you write it.
- **Decisions belong to the maintainer.** An issue labelled `decision` is
  open until the maintainer decides it; please don't settle it in a pull
  request.
- **Labels:** `P0` blocks its milestone, `P1` is needed for 1.0, `P2` may
  never ship. `maintainer-check` needs the maintainer's accounts or hardware.

## Build and run

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Then:

```bash
dotnet build Rambutan.slnx -warnaserror   # what CI runs; note .slnx, not .sln
dotnet test --solution Rambutan.slnx
dotnet run --project src/Rambutan.App
```

On Linux, running the app also needs PulseAudio's client library, the Wayland
and X11 client libraries, fontconfig, and Noto fonts with CJK and colour emoji
(on Arch: `libpulse wayland libxkbcommon libx11 libice libsm fontconfig
noto-fonts noto-fonts-cjk noto-fonts-emoji`). Release builds are NativeAOT,
which also needs `clang` and `zlib1g-dev`:

```bash
dotnet publish src/Rambutan.App -c Release -r linux-x64   # or win-x64
```

The phone remote and the OBS overlay are small Svelte apps under `web/remote`
and `web/overlay`. CI does not run npm, so their built output under
`src/Rambutan.LocalApi/wwwroot` is committed. If you change anything under
`web/`, run `npm install && npm run build` in that folder and commit the whole
output folder, so replaced files go too.

### Demo mode

`RAMBUTAN_DEMO=1 dotnet run --project src/Rambutan.App` runs the app with no
network and no account. The catalog comes from recorded YouTube Music
responses, the audio is generated, and the artwork is placeholder; everything
else is the real code. Use it for UI work and screenshots.

- `RAMBUTAN_DEMO=anonymous` starts signed out.
- `RAMBUTAN_DEMO_LATENCY=<ms>` slows responses down.
- `RAMBUTAN_DEMO_FAIL=home,search,…` (or `all`) makes areas fail as if offline.
- `RAMBUTAN_DATA_DIR=<folder>` keeps settings, caches and logs in that
  folder, so several copies can run at once. `RAMBUTAN_TOOLS_DIR=<folder>`
  moves the downloaded ffmpeg and yt-dlp too. `--profile <name>` picks a
  profile inside the data folder.

`Services/Demo/DemoOptions.cs` lists every option. Other diagnostics
variables are in [CLAUDE.md](CLAUDE.md#commands).

## How the code is laid out

| Folder | What it holds |
|---|---|
| `src/Rambutan.Core` | Playback, the queue, and the interfaces the other projects implement. Plain C# with no I/O. |
| `src/Rambutan.InnerTube` | The YouTube Music API client. |
| `src/Rambutan.Streams` | yt-dlp and ffmpeg management, stream lookup, the audio cache and downloads. |
| `src/Rambutan.Audio` | The audio engine: decode, buffer, crossfade, output, and the visualizer's analyzer. |
| `src/Rambutan.Lyrics` | Lyrics: LRC and LRCLIB parsing, lookups, cache, custom lyrics. |
| `src/Rambutan.Integrations` | Media controls, keyring, single instance, updates, Discord, Last.fm. |
| `src/Rambutan.LocalApi` | The local web server for the phone remote and the OBS overlay. |
| `src/Rambutan.App` | The Avalonia UI and the app services that join everything together. |
| `web/remote`, `web/overlay` | The phone remote and the OBS overlay pages. |
| `tests/` | One xUnit project per library, plus `Rambutan.App.Tests` for view models and controls. |
| `tools/` | The fixture recorder, the UI driver for demo mode, and the logo generator. |

Libraries reference `Rambutan.Core` only; `Rambutan.App` joins them. A type
that two libraries share belongs in Core.

## What a change needs

1. `dotnet build Rambutan.slnx -warnaserror` passes. Zero warnings is the
   baseline.
2. `dotnet test --solution Rambutan.slnx` passes. Logic changes come with
   tests; parsers are tested against recorded responses. Remove personal data
   from any recorded response before you commit it.
3. UI changes are looked at in the running app (demo mode is fine), not
   judged from the XAML. A screenshot in the pull request helps.
4. Every user-visible string goes through `src/Rambutan.App/Locales/en.json`.
   Every control is reachable by keyboard and has a screen-reader name.
5. Release builds are NativeAOT. Use source-generated `System.Text.Json`, no
   reflection-based serializers, and check that a new NuGet package publishes
   with AOT without new warnings. A package that ships in the app also gets
   its notice in `THIRD-PARTY-NOTICES.md`.
6. Comments say only what the code can't: why, and the contract.
7. Commits have a plain imperative subject (no `feat:` prefix) and a body that
   says why, ending with what you verified. The pull request names the issue
   it fixes (`Fixes #n`).

Some rules are fixed: no web view in the app, ever; anything that sends data
off the computer is off by default; and settings and other files users invest
in are versioned JSON. The full list, and where things live in the code, is
in [CLAUDE.md](CLAUDE.md). It is written for AI coding agents, and it is the
most complete description of the project's rules.

## Working with Claude Code on the web

`.claude/hooks/session-start.sh` prepares cloud sessions: it installs the .NET
SDK from Ubuntu's archive, restores packages, and installs Xvfb and the UI
driver, so a session can run the app in demo mode (the `demo` skill in
`.claude/skills/`). It does nothing in local sessions.

## Releases

`./build/pack.ps1` publishes and packs a build with Velopack
(`-Rid linux-x64` for the AppImage, `-Channel win-beta` for the beta channel).
Pushing a tag `v<version>` that matches `<Version>` in `Directory.Build.props`
drafts a GitHub release for Windows and Linux. A tag with a prerelease suffix,
such as `v0.1.0-beta.1`, goes to the beta channels. The release workflow reads
the Discord and Last.fm keys from repository secrets; a build without them
shows those integrations as unavailable.

## License

Rambutan is licensed under the GNU General Public License, version 3 or later.
By sending a contribution, you agree that it is licensed under the same terms.
