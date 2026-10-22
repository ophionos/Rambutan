using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;

namespace Rambutan.Lyrics.Tests;

/// <summary>A local track's own lyrics, and where they stand among the others.</summary>
public sealed class LocalLyricsTests : IDisposable
{
    private const string Id = "local:7";
    private static readonly LyricsQuery Query = new(Id, "Title", "Artist");

    private readonly TempDirectory _dir = new();
    private readonly string _audio;
    private readonly List<string> _tags = [];
    private int _tagReads;

    public LocalLyricsTests()
    {
        _audio = Path.Combine(_dir.Path, "music", "song.flac");
        Directory.CreateDirectory(Path.GetDirectoryName(_audio)!);
        File.WriteAllBytes(_audio, []);
    }

    public void Dispose() => _dir.Dispose();

    private LocalLyrics Local() => new(
        id => id == Id ? _audio : null,
        (_, _) =>
        {
            _tagReads++;
            return Task.FromResult<IReadOnlyList<string>>(_tags.ToList());
        });

    private LyricsService Service(params ILyricsProvider[] providers) =>
        LyricsService.Create(_dir.Path, providers, Local());

    private void Sidecar(string text) => File.WriteAllText(Path.ChangeExtension(_audio, ".lrc"), text);

    private static string[] Texts(LyricsLookup lookup) => lookup.Document!.Lines.Select(l => l.Text).ToArray();

    [Fact]
    public async Task The_lrc_file_beside_the_song_comes_before_its_tags()
    {
        Sidecar("[00:01.00]From the file\n[00:02.00]Second");
        _tags.Add("From the tags");

        var doc = await Local().TryGetAsync(Id, default);

        Assert.Equal(LocalLyrics.SidecarSourceName, doc!.Source);
        Assert.True(doc.IsSynced);
        Assert.Equal(["From the file", "Second"], doc.Lines.Select(l => l.Text));
        Assert.Equal(0, _tagReads);
    }

    [Fact]
    public async Task An_enhanced_lrc_file_keeps_its_word_timing()
    {
        Sidecar("[00:01.00]<00:01.00>Two <00:01.50>words");

        var doc = await Local().TryGetAsync(Id, default);

        Assert.Equal(2, doc!.Lines[0].Words!.Count);
    }

    [Fact]
    public async Task An_lrc_file_without_stamps_shows_as_plain_lyrics()
    {
        Sidecar("Just words\nNo times");

        var doc = await Local().TryGetAsync(Id, default);

        Assert.False(doc!.IsSynced);
        Assert.Equal(["Just words", "No times"], doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task An_lrc_file_named_in_another_case_is_found()
    {
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(_audio)!, "SONG.LRC"), "[00:01.00]Shouted");

        var doc = await Local().TryGetAsync(Id, default);

        Assert.Equal(["Shouted"], doc!.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task An_lrc_file_that_isnt_utf8_is_read_without_replacement_characters()
    {
        File.WriteAllBytes(Path.ChangeExtension(_audio, ".lrc"), [.. "[00:01.00]Caf"u8, 0xE9]);

        var doc = await Local().TryGetAsync(Id, default);

        Assert.DoesNotContain('\uFFFD', doc!.Lines[0].Text);
    }

    [Fact]
    public async Task Synced_lyrics_in_an_id3_tag_come_after_the_lrc_file_and_before_the_other_tags()
    {
        var tag = Id3.Tag(4, 0, Id3.Frame(4, "SYLT", Id3.Sylt(3, [("From SYLT", 1000)])));
        File.WriteAllBytes(_audio, [.. tag, .. new byte[100]]);
        _tags.Add("[00:01.00]From USLT");

        var fromTag = await Local().TryGetAsync(Id, default);
        Assert.Equal(LocalLyrics.TagsSourceName, fromTag!.Source);
        Assert.True(fromTag.IsSynced);
        Assert.Equal(["From SYLT"], fromTag.Lines.Select(l => l.Text));
        Assert.Equal(0, _tagReads);

        Sidecar("[00:01.00]From the file");
        Assert.Equal(["From the file"], (await Local().TryGetAsync(Id, default))!.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task An_id3_tag_without_synced_lyrics_falls_through_to_the_other_tags()
    {
        File.WriteAllBytes(_audio, Id3.Tag(3, 0, Id3.Frame(3, "TIT2", [3, .. "Title"u8])));
        _tags.Add("From USLT");

        Assert.Equal(["From USLT"], (await Local().TryGetAsync(Id, default))!.Lines.Select(l => l.Text));
    }

    [Fact]
    public async Task An_empty_lrc_file_falls_through_to_the_tags()
    {
        Sidecar("  \n");
        _tags.Add("From the tags");

        var doc = await Local().TryGetAsync(Id, default);

        Assert.Equal(LocalLyrics.TagsSourceName, doc!.Source);
    }

    [Fact]
    public void A_tag_with_lrc_in_it_is_synced_and_wins_over_a_plain_one()
    {
        var doc = LocalLyrics.Interpret(["Plain words", "[00:01.00]Timed"], LocalLyrics.TagsSourceName);

        Assert.True(doc!.IsSynced);
        Assert.Equal("Timed", Assert.Single(doc.Lines).Text);
    }

    [Fact]
    public void Tags_without_stamps_are_plain_and_blank_ones_are_none()
    {
        Assert.False(LocalLyrics.Interpret(["", "Plain words"], "tags")!.IsSynced);
        Assert.Null(LocalLyrics.Interpret(["  ", "\n\n"], "tags"));
        Assert.Null(LocalLyrics.Interpret([], "tags"));
    }

    [Fact]
    public async Task A_track_that_isnt_local_is_never_looked_for()
    {
        var asked = false;
        var local = new LocalLyrics(_ => { asked = true; return _audio; }, (_, _) => throw new InvalidOperationException());

        Assert.Null(await local.TryGetAsync("dQw4w9WgXcQ", default));
        Assert.False(asked);
    }

    [Fact]
    public async Task Tags_that_cant_be_read_are_none()
    {
        var local = new LocalLyrics(_ => _audio, (_, _) => throw new IOException("locked"));

        Assert.Null(await local.TryGetAsync(Id, default));
    }

    [Fact]
    public async Task Custom_lyrics_still_win()
    {
        Sidecar("[00:01.00]From the file");
        new CustomLyricsStore(Path.Combine(_dir.Path, "lyrics", "custom")).Save(Id, new CustomLyrics("mine"));
        using var service = Service();

        Assert.Equal(["mine"], Texts(await service.GetAsync(Query)));
    }

    [Fact]
    public async Task Own_lyrics_come_before_the_cache_and_LRCLIB_and_are_never_cached()
    {
        var provider = ScriptedProvider.Returning("p", LyricsResult.Found(LyricsDocument.FromPlainText("provided", "p")));
        using (var first = Service(provider))
            Assert.Equal(["provided"], Texts(await first.GetAsync(Query))); // cached now

        _tags.Add("From the tags");
        using var service = Service(provider);
        var lookup = await service.GetAsync(Query);

        Assert.Equal(LocalLyrics.TagsSourceName, lookup.Document!.Source);
        Assert.Equal(1, provider.Calls);

        // An edit shows on the next look, with no cache in the way.
        Sidecar("[00:01.00]Edited");
        Assert.Equal(["Edited"], Texts(await service.ReloadAsync(Query)));
    }

    [Fact]
    public async Task Own_lyrics_show_with_online_lookups_off()
    {
        _tags.Add("From the tags");
        using var service = Service();
        service.LookUpOnline = false;

        var lookup = await service.GetAsync(Query);

        Assert.Equal(LyricsStatus.Found, lookup.Status);
        Assert.Equal(["From the tags"], Texts(lookup));
    }

    [Fact]
    public async Task Without_own_lyrics_LRCLIB_is_asked_as_before()
    {
        var provider = ScriptedProvider.Returning("p", LyricsResult.Found(LyricsDocument.FromPlainText("provided", "p")));
        using var service = Service(provider);

        Assert.Equal(["provided"], Texts(await service.GetAsync(Query)));
        Assert.Equal(1, _tagReads);
    }
}
