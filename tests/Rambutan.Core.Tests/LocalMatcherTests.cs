using Rambutan.Core.Local;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

public class LocalMatcherTests
{
    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static TrackInfo Song(string title, string artist, string id, int seconds = 200, string? album = null) =>
        new()
        {
            VideoId = id, Title = title, Artists = [new ArtistRef(artist, "UC" + id)], Album = album,
            AlbumBrowseId = album is null ? null : "MPRE" + id, Duration = TimeSpan.FromSeconds(seconds),
        };

    private static LocalFileTags Tags(string? title, string? artist = null, int seconds = 200, string? albumArtist = null) =>
        new("mp3", Title: title, Artist: artist, AlbumArtist: albumArtist, Duration: TimeSpan.FromSeconds(seconds));

    private readonly ManualTime _time = new();
    private readonly List<string> _searches = [];
    private Func<string, IReadOnlyList<TrackInfo>> _answer = _ => [];

    private LocalMatcher Matcher() => new((query, _) =>
    {
        lock (_searches) _searches.Add(query);
        return Task.FromResult(_answer(query));
    }, _time);

    [Fact]
    public void Only_the_title_and_artist_tags_are_searched_and_never_the_file_name()
    {
        Assert.Null(LocalMatchQuery.Of(null));
        Assert.Null(LocalMatchQuery.Of(Tags(title: null, artist: "Pulp")));
        Assert.Null(LocalMatchQuery.Of(Tags(title: "  ", artist: "Pulp")));
        Assert.Equal("Pulp Disco 2000", LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"))!.SearchText);
        Assert.Equal("Pulp Disco 2000", LocalMatchQuery.Of(Tags("Disco 2000", albumArtist: "Pulp"))!.SearchText);
        Assert.Equal("Disco 2000", LocalMatchQuery.Of(Tags("Disco 2000"))!.SearchText);
    }

    [Fact]
    public async Task A_file_without_a_title_tag_is_not_searched()
    {
        var matcher = Matcher();

        Assert.Null(await matcher.MatchAsync("local:1", LocalMatchQuery.Of(Tags(null, "Pulp"))));
        Assert.Equal(LocalMatchState.NoTitle, matcher.StatusOf("local:1", null).State);
        Assert.Empty(_searches);
    }

    [Fact]
    public async Task The_same_song_is_found_and_kept_so_it_is_searched_once()
    {
        _answer = _ => [Song("Disco 2000 (Live)", "Pulp", "live"), Song("Disco 2000", "Pulp", "song")];
        var matcher = Matcher();
        var query = LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"));
        Assert.Equal(LocalMatchState.NotSearched, matcher.StatusOf("local:1", query).State);

        var song = await matcher.MatchAsync("local:1", query);
        var again = await matcher.MatchAsync("local:1", query);

        Assert.Equal("song", song?.VideoId);
        Assert.Same(song, again);
        Assert.Equal(["Pulp Disco 2000"], _searches);
        Assert.Equal(new LocalMatchStatus(LocalMatchState.Found, song), matcher.StatusOf("local:1", query));
    }

    [Fact]
    public async Task Another_version_is_not_a_match()
    {
        _answer = _ => [Song("Disco 2000 (Live)", "Pulp", "live")];
        var matcher = Matcher();
        var query = LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"));

        Assert.Null(await matcher.MatchAsync("local:1", query));
        Assert.Equal(LocalMatchState.NotFound, matcher.StatusOf("local:1", query).State);
    }

    [Fact]
    public async Task Without_an_artist_only_the_same_title_and_length_match()
    {
        _answer = _ => [Song("Disco 2000", "Pulp", "far", seconds: 260), Song("Disco 2000", "Pulp", "near", seconds: 201)];
        var matcher = Matcher();

        Assert.Equal("near", (await matcher.MatchAsync("local:1", LocalMatchQuery.Of(Tags("Disco 2000"))))?.VideoId);
    }

    [Fact]
    public async Task A_retagged_file_is_searched_again()
    {
        _answer = q => q.Contains("Babies") ? [Song("Babies", "Pulp", "babies")] : [Song("Disco 2000", "Pulp", "disco")];
        var matcher = Matcher();
        await matcher.MatchAsync("local:1", LocalMatchQuery.Of(Tags("Disco 2000", "Pulp")));

        var retagged = LocalMatchQuery.Of(Tags("Babies", "Pulp"));
        Assert.Equal(LocalMatchState.NotSearched, matcher.StatusOf("local:1", retagged).State);
        Assert.Equal("babies", (await matcher.MatchAsync("local:1", retagged))?.VideoId);
        Assert.Equal(2, _searches.Count);
    }

    [Fact]
    public async Task A_miss_expires_sooner_than_a_match()
    {
        var matcher = Matcher();
        var query = LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"));
        await matcher.MatchAsync("local:1", query);

        _time.Now += LocalMatcher.NotFoundLifetime + TimeSpan.FromMinutes(1);
        _answer = _ => [Song("Disco 2000", "Pulp", "added")];
        Assert.Equal("added", (await matcher.MatchAsync("local:1", query))?.VideoId);

        _time.Now += LocalMatcher.NotFoundLifetime + TimeSpan.FromMinutes(1);
        await matcher.MatchAsync("local:1", query);
        Assert.Equal(2, _searches.Count);
    }

    [Fact]
    public async Task A_failed_search_is_not_kept_and_says_so_until_the_next_try()
    {
        var fail = true;
        var matcher = new LocalMatcher((_, _) => fail
            ? Task.FromException<IReadOnlyList<TrackInfo>>(new HttpRequestException("offline"))
            : Task.FromResult<IReadOnlyList<TrackInfo>>([Song("Disco 2000", "Pulp", "song")]), _time);
        var changed = new List<string>();
        matcher.Changed += changed.Add;
        var query = LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"));

        await Assert.ThrowsAsync<HttpRequestException>(() => matcher.MatchAsync("local:1", query));
        Assert.Equal(LocalMatchState.Failed, matcher.StatusOf("local:1", query).State);
        Assert.Empty(matcher.Export());

        fail = false;
        Assert.Equal("song", (await matcher.MatchAsync("local:1", query))?.VideoId);
        Assert.Equal(["local:1", "local:1"], changed);
    }

    [Fact]
    public async Task Requests_while_a_search_runs_share_it()
    {
        var gate = new TaskCompletionSource<IReadOnlyList<TrackInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var searches = 0;
        var matcher = new LocalMatcher((_, _) =>
        {
            Interlocked.Increment(ref searches);
            return gate.Task;
        }, _time);
        var query = LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"));

        var first = matcher.MatchAsync("local:1", query);
        matcher.Request("local:1", query);
        var second = matcher.MatchAsync("local:1", query);
        Assert.Equal(LocalMatchState.Searching, matcher.StatusOf("local:1", query).State);
        gate.SetResult([Song("Disco 2000", "Pulp", "song")]);

        Assert.Equal("song", (await first)?.VideoId);
        Assert.Equal("song", (await second)?.VideoId);
        Assert.Equal(1, searches);
    }

    [Fact]
    public async Task Saved_answers_come_back_unless_they_expired()
    {
        _answer = _ => [Song("Disco 2000", "Pulp", "song")];
        var matcher = Matcher();
        var query = LocalMatchQuery.Of(Tags("Disco 2000", "Pulp"));
        await matcher.MatchAsync("local:1", query);
        var saved = matcher.Export();

        var restored = Matcher();
        restored.Import(saved);
        Assert.Equal("song", restored.StatusOf("local:1", query).Song?.VideoId);

        _time.Now += LocalMatcher.FoundLifetime + TimeSpan.FromDays(1);
        var late = Matcher();
        late.Import(saved);
        Assert.Equal(LocalMatchState.NotSearched, late.StatusOf("local:1", query).State);
    }

    [Fact]
    public void A_match_fills_only_what_the_tags_lack_and_without_its_links()
    {
        var song = Song("Disco 2000", "Pulp", "song", album: "Different Class");
        var bare = new TrackInfo { VideoId = "local:1", Title = "Disco 2000", Artists = [] };
        var tagged = new TrackInfo { VideoId = "local:2", Title = "Disco 2000", Artists = [new ArtistRef("Jarvis")], Album = "Mine" };

        var filled = LocalMatcher.Fill(bare, song);
        Assert.Equal("local:1", filled.VideoId);
        Assert.Equal("Pulp", Assert.Single(filled.Artists).Name);
        Assert.Null(filled.Artists[0].BrowseId);
        Assert.Equal("Different Class", filled.Album);
        Assert.Null(filled.AlbumBrowseId);

        Assert.Same(tagged, LocalMatcher.Fill(tagged, song));
        Assert.Same(bare, LocalMatcher.Fill(bare, null));
    }
}
