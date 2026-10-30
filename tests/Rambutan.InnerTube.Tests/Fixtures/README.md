# InnerTube fixtures

Real YouTube Music responses that pin the parsers. A parser
change that no longer matches them fails a test, not a user.

- `*.json` are recorded with `tools/Rambutan.FixtureRecorder`.
- `*.synthetic.json` are hand-authored to the live shape, for surfaces that need a
  signed-in session or couldn't be reached when the parser was written. Each test
  that reads one says what it mirrors. Replace them with recorded ones when you can:
  record, review the diff, delete the synthetic file and point the test at the new
  name. Expect to adjust the test's exact values (counts, titles), not its shape.

Commands run from the repo root. Authed recordings need `--cookies <file>` (the raw
`Cookie` header from music.youtube.com); the recorder then scrubs identity fields
and the account name. Review the diff before committing anyway.

## Replacing the synthetic fixtures

| Synthetic fixture | Record with (`dotnet run --project tools/Rambutan.FixtureRecorder -- …`) |
|---|---|
| `search-suggestions.daft-punk.synthetic.json` | `suggestions "daft punk" --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/search-suggestions.daft-punk.json` (signed in, so history rows appear) |
| `search-songs-continuation.daft-punk.synthetic.json` | `search "daft punk" --filter songs --continuation <token from search-songs.daft-punk.json's shelf> --out tests/Rambutan.InnerTube.Tests/Fixtures/search-songs-continuation.daft-punk.json` (a fresh first page's token; they expire) |
| `browse-library-playlists.synthetic.json` | `browse FEmusic_liked_playlists --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-playlists.json` |
| `browse-library-playlists-continuation.synthetic.json` | `continuation <token from the recording above> --cookies cookies.txt` |
| `browse-library-albums.synthetic.json` | `browse FEmusic_liked_albums --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-albums.json` |
| `browse-library-albums-continuation.synthetic.json` | `continuation <token from the albums recording> --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-albums-continuation.json` (needs 25+ saved albums) |
| `browse-library-artists.synthetic.json` | `browse FEmusic_library_corpus_artists --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-artists.json` |
| `browse-library-artists-continuation.synthetic.json` | `continuation <token from the artists recording> --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-artists-continuation.json` (needs 25+ subscriptions) |
| `browse-library-songs.synthetic.json` | `browse FEmusic_liked_videos --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-songs.json` (the recorder redacts every `feedbackToken`, so a row's add and remove tokens both read `REDACTED`: compare the tests' in-library state, not their token values) |
| `browse-library-songs-continuation.synthetic.json` | `continuation <token from the songs recording> --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-songs-continuation.json` (needs 25+ saved songs) |
| `browse-history.synthetic.json` | Not to be replaced by a recording: a real one is the account's own listening. `browse FEmusic_history --cookies cookies.txt --out <scratch>/history.json` records one to compare the shape against. The synthetic mirrors a signed-in recording of October 2026 (one shelf per period, no continuation, "Remove from history" before "Pin to Listen again" in each row menu) with the search fixtures' public Daft Punk rows in place of the account's songs. |
| `browse-home-continuation.synthetic.json` | `home --cookies cookies.txt --out <scratch>/home.json` records Home and every later page in one session (tokens only work for the visitor they were issued to). The synthetic mirrors a signed-in middle page: its shelves are copied from an anonymous recording, because a real one is the account's own taste; the podcast shelf is hand-made. A signed-out Home with three or more pages would give a real middle page. |
| `browse-playlist.owned.synthetic.json` | `browse VL<id of a playlist you made> --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-playlist.owned.json`. The synthetic is the 80s-pop recording cut to 12 rows, with its header wrapped the way ytmusicapi's get_playlist reads an owned one (`musicEditablePlaylistDetailHeaderRenderer.header`, privacy before the year in the subtitle). Demo mode opens the demo account's own playlists with it. |
| `browse-playlist.rated.synthetic.json`, `next-rated.synthetic.json` | a playlist and a track you've rated: `browse VL<playlistId> --cookies cookies.txt`, `next <videoId> --cookies cookies.txt` |

`browse-lyrics.k8qwmwvqxgi.json` is a recording (`browse MPLYt_K8qWMWVqXGi-5`,
the Lyrics tab of `next-radio.khnokw3mw24.json`) whose lyrics are replaced with
invented lines: the real text is the publisher's, and this repository is public.
Re-record the same way and replace the text again; keep the blank line between
stanzas and the footer as recorded. `browse-lyrics.not-available.json` is a
recording as it came, YouTube Music's answer for a track without lyrics.

`live-diff` runs every anonymous parser against live YTM without writing anything;
CI runs it weekly.

Mutations (rate, save album or song, subscribe, playlist edits including reorder) have no
fixtures: the recorder doesn't send them, since recording one changes the account.
Their request bodies are pinned by payload tests (`RecordingHandler`) against
ytmusicapi's equivalents instead.
