# Lyrics fixtures

`lrclib-*.json` without a `.synthetic` suffix are real LRCLIB responses, recorded
on 2026-10-05 and pretty-printed (field order and values unchanged). The songs
are public domain, so the recorded lyrics stay:

| File | Request | Record |
|---|---|---|
| `lrclib-get.json` | `/api/get`, Amazing Grace / Melvin West / Great Hymns of the Church / 277 | 37009621, line-synced |
| `lrclib-get.plain-only.json` | `/api/get`, Greensleeves / Dublin Ramblers / Whiskey In The Jar From Ireland / 97 | 37541876, plain only |
| `lrclib-get.instrumental.json` | `/api/get`, Greensleeves / the mantovani orchestra / Beautiful Moments / 189 | 37471959, instrumental |
| `lrclib-not-found.json` | `/api/get` for a track LRCLIB doesn't have | the 404 body |
| `lrclib-overloaded.json` | `/api/get` while LRCLIB was busy | the 503 body |

To record again: `curl -A "<user agent>" --get https://lrclib.net/api/get
--data-urlencode track_name=... --data-urlencode artist_name=...
--data-urlencode album_name=... --data-urlencode duration=...`. LRCLIB answers
503 `ServerOverloaded` now and then; retry after a moment.

Two files stay hand-authored:

- `lrclib-search.synthetic.json` is an engineered `/api/search` result: a radio
  edit outside the duration tolerance, a record with no lyrics, and a plain and
  a synced album record at different distances from the query. The tolerance and
  preference tests need exactly that mix, which no real search returns on
  demand. The lyrics text is invented.
- `lrclib-get.word-synced.synthetic.json` follows a record recorded on
  2026-10-02 (LRCLIB id 37903874): the same fields, a `lyricsfile` YAML string in
  the single-quoted style LRCLIB writes for word-synced records, `hasWordSync`
  true, and line-level `syncedLyrics` beside it. The lyrics are invented,
  because the recorded ones are copyrighted.
