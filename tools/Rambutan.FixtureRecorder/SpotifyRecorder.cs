using System.Text.Json;
using System.Text.RegularExpressions;
using Rambutan.Core.Import;
using Rambutan.Integrations.Spotify;

// Spotify's share-link reader: recording its fixtures and checking it against
// live Spotify. These requests are made only when the maintainer runs them on
// their machine, never by the weekly live-diff in CI.
internal static partial class SpotifyRecorder
{
    public const string FixturesDir = "tests/Rambutan.Integrations.Tests/Fixtures/Spotify";

    /// <summary>A public playlist of more than 100 songs: Spotify's own
    /// "All Out 2010s", which it keeps at about 150.</summary>
    public const string LongPlaylist = "https://open.spotify.com/playlist/37i9dQZF1DX5Ejj0EkURtP";

    /// <summary>
    /// Records a link's embed page (as <c>embed.&lt;kind&gt;.&lt;id&gt;.html</c>) and,
    /// for a playlist the page lists to its limit, the web player's next page
    /// (<c>pathfinder.playlist-contents.json</c>), with the anonymous key and
    /// trace ids scrubbed.
    /// </summary>
    public static async Task<int> RecordAsync(string text, string? outDir)
    {
        if (SpotifyLinks.Parse(text) is not { } link)
        {
            Console.Error.WriteLine("error: not a link to a Spotify playlist or album.");
            return 2;
        }
        var dir = outDir ?? RepoPath(FixturesDir);
        Directory.CreateDirectory(dir);
        var reader = new SpotifyLinkReader();

        var (html, status) = await reader.SendAsync(new HttpRequestMessage(HttpMethod.Get, link.EmbedUrl));
        var embed = SpotifyLinkReader.ParseEmbed(html, status, link);
        var kind = link.Kind.ToString().ToLowerInvariant();
        Write(Path.Combine(dir, $"embed.{kind}.{link.Id}.html"), ScrubEmbed(html));
        Console.WriteLine($"{embed.List.Name}: {embed.List.Tracks.Count} songs, {embed.List.Skipped} skipped on the embed page");

        if (link.Kind != SpotifyListKind.Playlist ||
            embed.List.Tracks.Count + embed.List.Skipped < SpotifyLinkReader.EmbedLimit)
            return 0;
        if (embed.AccessToken is not { Length: > 0 } token)
        {
            Console.Error.WriteLine("error: the embed page gave no anonymous key.");
            return 1;
        }

        var (json, pageStatus) = await reader.SendAsync(
            SpotifyLinkReader.PageRequest(link, token, SpotifyLinkReader.EmbedLimit));
        Write(Path.Combine(dir, "pathfinder.playlist-contents.json"), Indent(json));
        Console.WriteLine($"web player page: HTTP {pageStatus}");
        var page = SpotifyLinkReader.ParsePlaylistPage(json, pageStatus);
        Console.WriteLine($"  {page.Tracks.Count} songs, {page.Skipped} skipped, total {page.Total?.ToString() ?? "not given"}");
        Console.WriteLine("reminder: review the diff before committing.");
        return 0;
    }

    /// <summary>Reads a long public playlist through the app's reader and
    /// fails when the embed page or the web player's API no longer reads.</summary>
    public static async Task<int> LiveDiffAsync(string? text)
    {
        var link = SpotifyLinks.Parse(text ?? LongPlaylist);
        if (link is null)
        {
            Console.Error.WriteLine("error: not a link to a Spotify playlist or album.");
            return 2;
        }
        try
        {
            var list = await new SpotifyLinkReader().ReadAsync(link);
            var ok = list.Tracks.Count > 0 && !list.Incomplete;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} spotify link: {list.Name}: {list.Tracks.Count} songs, " +
                              $"{list.Skipped} skipped{(list.Incomplete ? ", INCOMPLETE (the web player's API refused the rest; see the log)" : "")}");
            if (list.Tracks.Count <= SpotifyLinkReader.EmbedLimit && !list.Incomplete)
                Console.WriteLine("note: the list fits on the embed page, so the web player's API wasn't asked.");
            return ok ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or SpotifyListUnavailableException or SpotifyApiException)
        {
            Console.WriteLine($"FAIL spotify link: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The embed page without its anonymous key, correlation id and Sentry trace.</summary>
    public static string ScrubEmbed(string html)
    {
        html = AccessToken().Replace(html, "\"accessToken\":\"REDACTED\"");
        html = CorrelationId().Replace(html, "\"correlationId\":\"00000000000000000000000000000000\"");
        html = SentryValue().Replace(html, "\"$1\":\"REDACTED\"");
        return SentryPair().Replace(html, "sentry-REDACTED");
    }

    [GeneratedRegex("\"accessToken\":\"[^\"]*\"")]
    private static partial Regex AccessToken();

    [GeneratedRegex("\"correlationId\":\"[0-9a-f]+\"")]
    private static partial Regex CorrelationId();

    [GeneratedRegex("\"(_sentryTraceData|_sentryBaggage)\":\"[^\"]*\"")]
    private static partial Regex SentryValue();

    [GeneratedRegex("sentry-[a-z_]+=[^,\"&]+")]
    private static partial Regex SentryPair();

    private static string Indent(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
                doc.RootElement.WriteTo(writer);
            return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static void Write(string path, string text)
    {
        File.WriteAllText(path, text);
        Console.WriteLine($"wrote: {path}");
    }

    private static string RepoPath(string relative)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Rambutan.slnx")))
            dir = dir.Parent;
        return dir is null ? relative : Path.Combine(dir.FullName, relative);
    }
}
