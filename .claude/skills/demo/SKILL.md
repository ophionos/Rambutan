---
name: demo
description: Run Rambutan in demo mode on a virtual display and drive it (click, type, screenshot) to see UI changes with real-looking content, without YouTube Music access. Use for any UI change, UX review, or "does this work in the app" check in a cloud session, and before claiming a UI change was looked at.
---

# Demo mode + UI driver

Cloud sessions can't reach YouTube Music, and have no screen or sound card. Demo mode
(`RAMBUTAN_DEMO`, see `src/Rambutan.App/Services/Demo/DemoOptions.cs`) runs the real app
on the recorded InnerTube fixtures, with generated audio and placeholder artwork.
`tools/ui-driver/ui.py` launches it on Xvfb and drives it.

## Workflow

```bash
export UI_SHOTS=<scratchpad>/ui-shots        # where screenshots go (default ./ui-shots, gitignored)
python3 tools/ui-driver/ui.py start          # builds, starts Xvfb + the app, waits for the window
python3 tools/ui-driver/ui.py run "shot home" "click 70 100" "type daft" "key Return" "wait 1" "shot search"
python3 tools/ui-driver/ui.py stop
```

Several sessions on one machine each need their own display, state directory and demo
data: `UI_DISPLAY=:92 UI_STATE=/tmp/rambutan-ui-92 XDG_DATA_HOME=/tmp/rambutan-data-92`.
`start` launches Xvfb only when that display has no server yet (`/tmp/.X92-lock`), and
`stop` stops only the app whose pid is in `$UI_STATE/app.pid`, never another session's.

Then Read the PNGs to look at them. Coordinates are window-relative (the 1280×800 window,
the same frame the screenshots show), so a point read off a screenshot can be clicked as is.

Start options:
- `--anonymous`: signed out (default is signed in as "Demo Listener").
- `--latency MS`: delay per catalog and lyrics response (default 300). Use ~3000 to see skeletons and spinners.
- `--fail home,search,…`: those areas fail as if offline. Areas: `home, search, album, artist,
  playlist, library, radio, account, mutations, lyrics, all`. `playlist-more` / `library-more` fail only
  the next page, which is how to see a failure beside content that already loaded. `session`
  (named only, not part of `all`) refuses every signed-in request with 401, as an expired
  session does. In the sign-in dialog, cookies whose SAPISID is `expired` are refused and any
  other SAPISID signs in as the demo account.
- `--no-build`: skip the build.

Steps: `shot NAME`, `click X Y [--double] [--right] [--button 8|9]` (8/9: mouse back/forward),
`scroll X Y CLICKS` (wheel; positive is down), `move X Y` (hover), `drag X1 Y1 X2 Y2`, `key Control_L+f`
(X keysym names joined by `+`), `type TEXT` (US layout, printable ASCII; quote it, e.g. `"type 'SID=a; SAPISID=x'"`), `wait SECONDS`.

Useful landmarks in the default layout: sidebar Home (70,65), Search (70,100),
Library (70,140), Settings (70,175); first Home card (290,200); player bar play/pause (640,766),
centred at any width; the artwork thumbnail at the player bar's left (36,766) opens Now Playing
(or `key Control_L+p`).

## What the recordings contain

- Every search returns the recorded "daft punk" results for the selected filter.
  A query starting with `empty` returns no results.
- Every album opens *Random Access Memories*, every artist *Daft Punk*, every playlist
  *'80s Pop* (with continuation), every radio the *Instant Crush* mix.
- Library is the synthetic playlist grid. Likes and playlist edits succeed but aren't remembered
  past the session. Signed in, the album's first track is liked and the playlist's first two are
  liked and disliked (the recordings themselves are anonymous and unrated).
- Moods & genres opens the recorded grid. Every tile opens *Chill*, except *Soundtracks & musicals*
  and Chill's *Coffee shop blends* "More"; every Home mood chip opens the *Relax* recording.
- Pages with no recording (e.g. charts) fail with a 404, as an unexpected real response would.
- Lyrics (Now Playing's pane) are invented, served in place of LRCLIB: *Give Life Back to Music*,
  *The Game of Love*, *Within* and *Instant Crush* are synced with Japanese, Korean, Chinese and
  emoji lines, and the last three are word-timed (the karaoke sweep); *Giorgio by Moroder* is plain (unsynced); *Motherboard* is instrumental; *Touch* has
  none; anything else gets a generic synced verse. `--fail lyrics` shows "Couldn't reach lyrics"
  (for tracks not already in the lyrics cache, which lives in the demo data directory).
- Audio plays in real time into a silent device; the visualizer, crossfade and seek bar all work.
  Each track is a generated chord loop as long as the track's listed duration.

Demo runs keep their own data under `~/.local/share/Rambutan/demo` (settings, cookies, caches),
sharing only the ffmpeg/yt-dlp sidecars. The app log is at `.../Rambutan/demo/logs/rambutan.log`;
catalog requests show up as `[demo] endpoint → area` lines.

## Limits

Say which checks weren't possible: real YouTube Music data beyond the fixtures, real artwork,
audible output, and anything Windows-only (caption buttons, SMTC). The virtual display has
no window manager, so there are no window decorations.
