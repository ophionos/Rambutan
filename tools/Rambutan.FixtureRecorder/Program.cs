using System.Text.Json;
using System.Text.Json.Nodes;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube;
using Rambutan.InnerTube.Parsing;
using Rambutan.Integrations.LastFm;

// Records live InnerTube responses as parser test fixtures.
// Authed recordings are scrubbed automatically — identity fields are redacted
// and the account display name is replaced everywhere it appears. Review the
// diff before committing anyway; the scrubber is a net, not a guarantee.

const string FixturesDir = "tests/Rambutan.InnerTube.Tests/Fixtures";

// Properties whose values never feed a parser and may carry identity/session data.
string[] redactedKeys =
[
    "visitorData", "trackingParams", "clickTrackingParams", "serializedContextData",
    "accountName", "email", "channelHandle", "datasyncId", "delegatedSessionId",
    "feedbackToken",
];

if (args.Length == 0 || args[0] is "-h" or "--help")
    return Usage();

string? outPath = null;
string? cookiesPath = null;
var positional = new List<string>();
string? filterName = null;
string? continuationArg = null;
string? paramsArg = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--out": outPath = Next(args, ref i); break;
        case "--cookies": cookiesPath = Next(args, ref i); break;
        case "--filter": filterName = Next(args, ref i); break;
        case "--continuation": continuationArg = Next(args, ref i); break;
        case "--params": paramsArg = Next(args, ref i); break;
        default: positional.Add(args[i]); break;
    }
}

if (positional.Count < 1)
    return Usage();

using var client = new InnerTubeClient();

// Exercise every anonymous parser against live responses. IDs are not
// hardcoded — each page check navigates out of a live search result, so the
// run breaks only when a parser (or YTM itself) does.
if (positional[0] == "live-diff")
    return await LiveDiffAsync(client);
string? accountName = null;

if (cookiesPath is not null)
{
    var cookieString = File.ReadAllText(cookiesPath).Trim();
    if (InnerTubeSession.FromCookieString(cookieString) is not { } session)
    {
        Console.Error.WriteLine("error: no SAPISID in the cookies file — copy the full Cookie header from music.youtube.com.");
        return 1;
    }

    client.SetSession(session);
    var info = await client.GetSessionInfoAsync();
    if (info.State != SessionState.Valid)
    {
        Console.Error.WriteLine($"error: session is {info.State} — re-copy fresh cookies.");
        return 1;
    }

    accountName = info.AccountName;
    Console.WriteLine($"authed as: {accountName} (name will be scrubbed from output)");
}

// Home, page by page in one session: a token only works for the visitor it was
// issued to, so the later pages can't be recorded by separate runs.
if (positional[0] == "home")
{
    var first = outPath ?? Path.Combine(RepoRelative(FixturesDir), "browse-home.json");
    var page = await client.RecordBrowseAsync("FEmusic_home");
    var token = BrowseParsers.ParseHome(page.RootElement).Continuation;
    var visitor = InnerTubeClient.VisitorData(page.RootElement);
    Save(page, first);
    for (var n = 1; token is not null && n <= 10; n++)
    {
        page = await client.RecordSectionContinuationAsync(token.Value, visitor);
        token = BrowseParsers.ParseHomeContinuation(page.RootElement).Continuation;
        Save(page, Path.ChangeExtension(first, null) + $"-continuation-{n}.json");
    }

    if (cookiesPath is not null)
        Console.WriteLine("reminder: review the diff for leftover identity data before committing.");
    return 0;
}

JsonDocument doc;
string defaultName;

switch (positional[0])
{
    case "browse" when positional.Count >= 2:
        doc = await client.RecordBrowseAsync(positional[1], continuationArg, paramsArg);
        defaultName = $"browse-{Slug(positional[1])}.json";
        break;

    case "continuation" when positional.Count >= 2:
        doc = await client.RecordBrowseAsync(browseId: null, continuation: positional[1]);
        defaultName = "browse-continuation.json";
        break;

    case "next" when positional.Count >= 2:
        doc = await client.NextAsync(positional[1]);
        defaultName = $"next-radio.{Slug(positional[1])}.json";
        break;

    case "search" when positional.Count >= 2:
        SearchFilter? filter = filterName is null
            ? null
            : Enum.Parse<SearchFilter>(filterName, ignoreCase: true);
        // A later page needs the filter and query of the page that issued the token.
        doc = await client.RecordSearchAsync(positional[1], filter, continuationArg);
        defaultName = $"search-{filterName?.ToLowerInvariant() ?? "all"}" +
                      (continuationArg is null ? "" : "-continuation") +
                      $".{Slug(positional[1])}.json";
        break;

    case "suggestions" when positional.Count >= 2:
        doc = await client.RecordSearchSuggestionsAsync(positional[1]);
        defaultName = $"search-suggestions.{Slug(positional[1])}.json";
        break;

    default:
        return Usage();
}

Save(doc, outPath ?? Path.Combine(RepoRelative(FixturesDir), defaultName));
if (cookiesPath is not null)
    Console.WriteLine("reminder: review the diff for leftover identity data before committing.");
return 0;

// Scrubs an authed response, writes it, and disposes it.
void Save(JsonDocument response, string path)
{
    var root = JsonNode.Parse(response.RootElement.GetRawText())!;
    response.Dispose();

    if (cookiesPath is not null)
    {
        var redactions = Scrub(root, redactedKeys, accountName);
        Console.WriteLine($"scrubbed: {redactions} value(s) redacted");
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    File.WriteAllText(path, root.ToJsonString());
    Console.WriteLine($"wrote: {path}");
}

static async Task<int> LiveDiffAsync(InnerTubeClient client)
{
    const string query = "daft punk";
    var failures = 0;

    async Task Check(string name, Func<Task<string>> probe)
    {
        try
        {
            Console.WriteLine($"PASS {name}: {await probe()}");
        }
        catch (Exception ex)
        {
            failures++;
            Console.WriteLine($"FAIL {name}: {ex.Message}");
        }
    }

    static T First<T>(IReadOnlyList<CatalogEntry> entries) where T : CatalogEntry =>
        entries.OfType<T>().FirstOrDefault()
        ?? throw new InvalidOperationException($"no {typeof(T).Name} among {entries.Count} entries");

    await Check("search songs", async () =>
    {
        var songs = await client.SearchSongsAsync(query);
        return songs.Count > 0
            ? $"{songs.Count} songs, first: {songs[0].Title}"
            : throw new InvalidOperationException("0 results");
    });

    await Check("search suggestions", async () =>
    {
        var suggestions = await client.GetSearchSuggestionsAsync("daft pu");
        return suggestions.Queries.Count > 0
            ? $"{suggestions.Queries.Count} queries, {suggestions.Entries.Count} entities, " +
              $"first: {suggestions.Queries[0].Query}"
            : throw new InvalidOperationException("0 suggestions");
    });

    await Check("search all (top result + results)", async () =>
    {
        var overview = await client.SearchAllAsync(query);
        return overview.TopResult is { } top && overview.Results.Count > 0
            ? $"top: {top.Entry.Title} ({top.Entry.GetType().Name}, {top.Related.Count} related); " +
              $"{overview.Results.Count} results: " +
              string.Join(", ", overview.Results.GroupBy(e => e.GetType().Name).Select(g => $"{g.Count()} {g.Key}"))
            : throw new InvalidOperationException(
                $"top result {(overview.TopResult is null ? "missing" : "present")}, {overview.Results.Count} results");
    });

    await Check("search songs, page 2", async () =>
    {
        var first = await client.SearchPagedAsync(query, SearchFilter.Songs);
        if (first.Continuation is null)
            return $"{first.Items.Count} songs (no continuation offered)";
        var more = await client.GetSearchContinuationAsync(query, SearchFilter.Songs, first.Continuation);
        return more.Items.Count > 0
            ? $"{first.Items.Count} + {more.Items.Count} songs"
            : throw new InvalidOperationException("empty continuation page");
    });

    IReadOnlyList<CatalogEntry> artists = [], playlists = [];
    foreach (var filter in Enum.GetValues<SearchFilter>())
    {
        await Check($"search {filter}", async () =>
        {
            var entries = await client.SearchAsync(query, filter);
            switch (filter)
            {
                case SearchFilter.Artists: artists = entries; break;
                case SearchFilter.Playlists: playlists = entries; break;
            }

            return entries.Count > 0
                ? $"{entries.Count} entries, first: {entries[0].Title}"
                : throw new InvalidOperationException("0 results");
        });
    }

    await Check("radio (next)", async () =>
    {
        var songs = await client.SearchSongsAsync(query);
        var radio = await client.GetRadioAsync(songs[0].VideoId);
        return radio.Count > 1
            ? $"{radio.Count} tracks from “{songs[0].Title}”"
            : throw new InvalidOperationException($"{radio.Count} track(s) — automix missing");
    });

    await Check("home + continuations", async () =>
    {
        var page = await client.GetHomeAsync();
        var shelves = page.Shelves.ToList();
        var pages = 1;
        // Follow the tokens as a scrolling user would; the cap guards against a loop.
        while (page.Continuation is { } next && pages < 10)
        {
            page = await client.GetHomeContinuationAsync(next);
            if (page.Shelves.Count == 0 && page.Continuation is not null)
                throw new InvalidOperationException($"page {pages + 1}: no shelves, more promised");
            shelves.AddRange(page.Shelves);
            pages++;
        }

        return shelves.Count > 0 && shelves.All(s => s.Entries.Count > 0)
            ? $"{shelves.Count} shelves over {pages} page(s): " + string.Join(", ", shelves.Select(s => s.Title))
            : throw new InvalidOperationException(
                $"{shelves.Count} shelves, {shelves.Count(s => s.Entries.Count == 0)} empty");
    });

    await Check("home mood chip", async () =>
    {
        var chips = (await client.GetHomeAsync()).Chips;
        if (chips.Count == 0)
            throw new InvalidOperationException("no mood chips");
        var page = await client.GetHomeAsync(chips[0]);
        return page.Shelves.Count > 0 && page.Chips.Any(c => c.IsSelected && c.Title == chips[0].Title)
            ? $"{chips.Count} chips; {chips[0].Title}: {page.Shelves.Count} shelves"
            : throw new InvalidOperationException(
                $"{chips[0].Title}: {page.Shelves.Count} shelves, selected chip " +
                (page.Chips.FirstOrDefault(c => c.IsSelected)?.Title ?? "missing"));
    });

    await Check("moods & genres + a category + its More", async () =>
    {
        var grid = await client.GetMoodsAndGenresAsync();
        var categories = grid.Sections.SelectMany(s => s.Categories).ToList();
        if (categories.Count == 0)
            throw new InvalidOperationException("no tiles");
        var page = await client.GetMoodCategoryAsync(categories[0]);
        if (page.Shelves.Count == 0 || page.Shelves.Any(s => s.Entries.Count == 0))
            throw new InvalidOperationException($"{categories[0].Title}: {page.Shelves.Count} shelves, some empty");
        if (page.Shelves.FirstOrDefault(s => s.More is not null)?.More is not { } more)
            return $"{categories.Count} tiles; {page.Title}: {page.Shelves.Count} shelves (no More offered)";
        var morePage = await client.GetMoodCategoryAsync(more);
        return morePage.Shelves.Count > 0
            ? $"{categories.Count} tiles; {page.Title}: {page.Shelves.Count} shelves; {morePage.Title}: {morePage.Shelves.Count} shelves"
            : throw new InvalidOperationException($"{more.Title}: no shelves");
    });

    await Check("artist page", async () =>
    {
        var page = await client.GetArtistAsync(First<ArtistEntry>(artists).BrowseId);
        return page.TopSongs.Count > 0
            ? $"{page.Name}: {page.TopSongs.Count} top songs, {page.Shelves.Count} shelves"
            : throw new InvalidOperationException($"{page.Name}: no top songs");
    });

    await Check("album page", async () =>
    {
        // Not the first "daft punk" album: that is whatever ranks first today, and
        // some albums (tribute releases) carry no credits links at all. A
        // well-known album that has them on every row keeps the check meaningful.
        var known = await client.SearchAsync("random access memories", SearchFilter.Albums);
        var page = await client.GetAlbumAsync(First<AlbumEntry>(known).BrowseId);
        if (page.Tracks.Count == 0)
            throw new InvalidOperationException($"{page.Title}: no tracks");
        // The credits link is how the now-playing marker pairs an album row with
        // the audio track search hands out; losing it is silent otherwise.
        var withSong = page.Tracks.Count(t => t.SongId is not null);
        return withSong > 0
            ? $"{page.Title}: {page.Tracks.Count} tracks, {withSong} name their song"
            : throw new InvalidOperationException($"{page.Title}: no row has a song-credits link");
    });

    await Check("playlist page + continuation", async () =>
    {
        var page = await client.GetPlaylistAsync(First<PlaylistEntry>(playlists).PlaylistId);
        if (page.Tracks.Count == 0)
            throw new InvalidOperationException($"{page.Title}: no tracks");
        if (page.Continuation is null)
            return $"{page.Title}: {page.Tracks.Count} tracks (no continuation offered)";

        var more = await client.GetPlaylistContinuationAsync(page.PlaylistId, page.Continuation);
        // A trackless terminal chunk (related-content only) is a known shape;
        // trackless with MORE pages promised is a parser regression.
        return more.Tracks.Count > 0 || more.Continuation is null
            ? $"{page.Title}: {page.Tracks.Count} + {more.Tracks.Count} continued tracks"
            : throw new InvalidOperationException($"{page.Title}: empty non-terminal continuation");
    });

    // Last.fm's web player endpoint is undocumented too. An artist's
    // station answers JSON; its tag stations sit behind a JavaScript challenge,
    // and this would fail if that spread here.
    await Check("last.fm artist station", async () =>
    {
        var tracks = await new LastFmStationClient().GetArtistStationAsync("Daft Punk");
        var linked = tracks.Count(t => t.VideoId is not null);
        return tracks.Count > 0 && linked > 0
            ? $"{tracks.Count} tracks from {tracks.Select(t => t.Artist).Distinct().Count()} artists, {linked} with a YouTube link"
            : throw new InvalidOperationException($"{tracks.Count} tracks, {linked} with a YouTube link");
    });

    Console.WriteLine(failures == 0
        ? "live-diff: all parsers agree with live responses"
        : $"live-diff: {failures} check(s) FAILED");
    return failures == 0 ? 0 : 1;
}

static int Usage()
{
    Console.WriteLine(
        """
        Rambutan.FixtureRecorder — records InnerTube responses as parser fixtures.

        usage:
          dotnet run --project tools/Rambutan.FixtureRecorder -- browse <browseId> [--continuation <token>] [--params <params>] [options]
          dotnet run --project tools/Rambutan.FixtureRecorder -- continuation <token> [options]   (playlist pages)
          dotnet run --project tools/Rambutan.FixtureRecorder -- home [options]   (Home and every continuation, in one session: <out>, <out>-continuation-N)
          dotnet run --project tools/Rambutan.FixtureRecorder -- search <query> [--filter songs|videos|albums|artists|playlists] [--continuation <token>] [options]   (no --filter: the unfiltered "All" search)
          dotnet run --project tools/Rambutan.FixtureRecorder -- suggestions <partial query>   (search-box completions)
          dotnet run --project tools/Rambutan.FixtureRecorder -- next <videoId>   (radio/automix watch queue)
          dotnet run --project tools/Rambutan.FixtureRecorder -- live-diff   (run all anonymous parsers against live YTM and Last.fm)

        options:
          --out <file>      output path (default: tests/Rambutan.InnerTube.Tests/Fixtures/<derived>.json)
          --cookies <file>  file holding the raw Cookie header from music.youtube.com;
                            enables auth and turns on the identity scrub pass

        examples:
          … -- browse FEmusic_home
          … -- browse FEmusic_liked_playlists --cookies cookies.txt --out tests/Rambutan.InnerTube.Tests/Fixtures/browse-library-playlists.json
          … -- browse FEmusic_liked_albums --cookies cookies.txt          (saved albums)
          … -- browse FEmusic_library_corpus_artists --cookies cookies.txt   (subscribed artists)
        """);
    return 2;
}

static string Next(string[] args, ref int i)
{
    if (++i >= args.Length)
        throw new ArgumentException($"missing value after {args[i - 1]}");
    return args[i];
}

static string Slug(string value)
{
    var chars = value.ToLowerInvariant()
        .Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-')
        .ToArray();
    return new string(chars).Trim('-');
}

/// <summary>Walks up from the working directory to the repo root (contains Rambutan.slnx).</summary>
static string RepoRelative(string relative)
{
    var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rambutan.slnx")))
        dir = dir.Parent;
    return dir is null ? relative : Path.Combine(dir.FullName, relative);
}

/// <summary>
/// Redacts identity-bearing keys in place and replaces the account display name
/// wherever it appears in string values. Returns the number of redactions.
/// </summary>
static int Scrub(JsonNode node, string[] redactedKeys, string? accountName)
{
    var count = 0;

    switch (node)
    {
        case JsonObject obj:
            foreach (var key in obj.Select(p => p.Key).ToList())
            {
                var value = obj[key];
                if (redactedKeys.Contains(key))
                {
                    obj[key] = key == "accountName" ? "Redacted User" : "REDACTED";
                    count++;
                }
                else if (TryScrubString(value, accountName, out var replaced))
                {
                    obj[key] = replaced;
                    count++;
                }
                else if (value is not null)
                {
                    count += Scrub(value, redactedKeys, accountName);
                }
            }
            break;

        case JsonArray array:
            for (var i = 0; i < array.Count; i++)
            {
                var item = array[i];
                if (TryScrubString(item, accountName, out var replaced))
                {
                    array[i] = replaced;
                    count++;
                }
                else if (item is not null)
                {
                    count += Scrub(item, redactedKeys, accountName);
                }
            }
            break;
    }

    return count;
}

static bool TryScrubString(JsonNode? node, string? accountName, out string replaced)
{
    replaced = "";
    if (accountName is null ||
        node is not JsonValue value ||
        value.GetValueKind() != JsonValueKind.String)
    {
        return false;
    }

    var text = value.GetValue<string>();
    if (!text.Contains(accountName, StringComparison.Ordinal))
        return false;

    replaced = text.Replace(accountName, "Redacted User", StringComparison.Ordinal);
    return true;
}
