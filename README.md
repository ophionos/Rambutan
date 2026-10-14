# Rambutan Music Player

Rambutan is a desktop app for YouTube Music, for Windows and Linux. It is a
native app, with no web browser inside. On a desktop PC it starts in about
0.4 seconds and uses about 210 MB of memory while it plays.

Rambutan is not affiliated with, or endorsed by, Google or YouTube. You need
your own YouTube Music account to use your library.

## What it does

- **Browse and play:** search, Home, Moods & genres, albums, artists,
  playlists, radio, and your library, with save, like and playlist editing.
- **Playback:** gapless playback, crossfade, volume normalization, a 10-band
  equalizer, a queue you can reorder, and the last queue restored at startup.
  You can choose the output device.
- **Lyrics:** synced lyrics, with a word-by-word karaoke sweep where word
  timing exists, and lyrics you import yourself.
- **Downloads:** downloaded songs, albums and playlists play with no
  connection. You can also export downloads as ordinary audio files.
- **Phone remote:** control playback, the volume and the queue from your
  phone's browser, and search for songs to play.
- **OBS overlay:** show what is playing in a stream, in a style you edit in
  the app.
- **Last.fm and Discord:** scrobbling, your Last.fm stations on Home and on
  artist pages, and Discord Rich Presence.
- **Your desktop:** media keys and system media controls, a tray icon, a
  mini player that can stay on top of other windows (not on Wayland, which
  doesn't allow it), keyboard shortcuts (press F1 to see them), screen-reader
  names, and reduced motion.

Everything that sends data off your computer is off until you turn it on:
Last.fm, Discord, online lyrics lookups and the phone remote. Each setting
says exactly what it sends.

**Limits:**
- You sign in by copying a request from your browser's developer tools. There
  is no Google sign-in page.
- The app is in English only.
- On Linux, the app has been checked on real hardware with one desktop so far
  (COSMIC on Wayland).

## Install

Download the newest build from
[Releases](https://github.com/ophionos/Rambutan/releases).

**Windows 10 and 11:** run the `Setup.exe`, or unzip the portable build. The
installer is not code-signed, so Windows SmartScreen warns first: choose
**More info**, then **Run anyway**.

**Linux (x64):** download the `.AppImage`, make it executable
(`chmod +x` on the file) and run it. It needs the PulseAudio client library
(`libpulse`, which also works with PipeWire), fontconfig, and fonts with CJK
and emoji coverage, such as Noto. Your sign-in goes into the system keyring (GNOME
Keyring, KWallet or another Secret Service). **If no keyring is running, the
sign-in is stored unencrypted**, in a file that only your user account can
read. The sign-in screen tells you which one applies.

**Sign in:** open **Sign in** in the app and follow its steps. In short: sign
in to music.youtube.com in a private browser window, open the developer
tools, and copy one request as cURL. Rambutan picks it up from the clipboard.
Then close the private window without signing out.

**Updates:** the app checks this repository for new builds and offers them in
the status strip at the bottom of the window.

## Report a problem or an idea

Use the [issue forms](https://github.com/ophionos/Rambutan/issues/new/choose):
**Bug report** for something that does not work, **Feedback or idea** for
everything else. Please search the open issues first.

A bug report helps most with the end of the log file:

- Windows: `%LOCALAPPDATA%\Rambutan\logs\rambutan.log`
- Linux: `~/.local/share/Rambutan/logs/rambutan.log`

The log holds no cookies or keys, but it can name tracks you played.

If Rambutan crashes, it keeps a crash file on your computer. At the next start
it offers to open a bug report with that file filled in. Nothing is sent
until you submit the report yourself.

## Contribute

See [CONTRIBUTING.md](CONTRIBUTING.md) for how to build, test and send a
change. Everyone who takes part follows the
[code of conduct](CODE_OF_CONDUCT.md).

## License

Copyright (C) 2026 Ophion and contributors.

Rambutan Music Player is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by the Free
Software Foundation, either version 3 of the License, or (at your option) any
later version. See [LICENSE](LICENSE).

The components it includes keep their own licenses; see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
