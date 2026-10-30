using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Providers;
using Rambutan.Lyrics.Store;

namespace Rambutan.App.Tests;

/// <summary>
/// The pane's choice of online source: LRCLIB first, remembered once changed,
/// asked again at once on a change, and shown only where it decides what the
/// pane shows.
/// </summary>
public class LyricsSourceSwitchTests
{
    private static TrackInfo Track(string id) => new()
    {
        VideoId = id,
        Title = "Instant Crush",
        Artists = [new("Daft Punk", "UCdp")],
        Duration = TimeSpan.FromMinutes(5),
    };

    private sealed class Pane
    {
        public List<LyricsQuery> Asked { get; } = [];
        public List<string> Remembered { get; } = [];
        public Func<LyricsQuery, LyricsLookup> Answer { get; set; } = _ => LyricsLookup.NotFound;

        public LyricsViewModel ViewModel(string? source = null, bool canChoose = true) => new(
            (query, _) =>
            {
                Asked.Add(query);
                return Task.FromResult(Answer(query));
            },
            _ => Task.CompletedTask,
            source: source,
            applySource: canChoose ? Remembered.Add : null);
    }

    private static LyricsLookup Found(string source, string? credit = null) => new(
        LyricsStatus.Found, LyricsDocument.FromPlainText("words", source) with { Credit = credit });

    private static LyricsSourceChoice YouTubeMusic(LyricsViewModel vm) =>
        vm.SourceChoices.Single(c => c.Name == YouTubeMusicLyricsProvider.SourceName);

    [Fact]
    public void LRCLIB_is_chosen_first_and_an_unknown_choice_reads_as_LRCLIB()
    {
        var pane = new Pane();

        Assert.Equal(["LRCLIB", "YouTube Music"], pane.ViewModel().SourceChoices.Select(c => c.Label));
        Assert.Equal(LrcLibProvider.SourceName, pane.ViewModel().SelectedSource.Name);
        Assert.Equal(LrcLibProvider.SourceName, pane.ViewModel("a newer build's").SelectedSource.Name);
        Assert.Equal(YouTubeMusicLyricsProvider.SourceName,
            pane.ViewModel(YouTubeMusicLyricsProvider.SourceName).SelectedSource.Name);
    }

    [Fact]
    public void Choosing_a_source_remembers_it_and_asks_again_at_once()
    {
        var pane = new Pane();
        var vm = pane.ViewModel();
        vm.OnTrackChanged(Track("khnokW3Mw24"));
        Assert.Equal(LyricsState.None, vm.State);

        pane.Answer = _ => Found(YouTubeMusicLyricsProvider.SourceName, "Source: Musixmatch");
        vm.SelectedSource = YouTubeMusic(vm);

        Assert.Equal([YouTubeMusicLyricsProvider.SourceName], pane.Remembered);
        Assert.Equal(2, pane.Asked.Count);
        Assert.Equal(LyricsState.Unsynced, vm.State);
        Assert.Equal("Lyrics from YouTube Music · Source: Musixmatch", vm.SourceLabel);
        Assert.True(vm.ShowsSourceSwitch);
    }

    [Fact]
    public void Choosing_the_source_already_chosen_asks_nothing()
    {
        var pane = new Pane();
        var vm = pane.ViewModel();
        vm.OnTrackChanged(Track("khnokW3Mw24"));

        vm.SelectedSource = vm.SourceChoices[0];

        Assert.Single(pane.Asked);
        Assert.Empty(pane.Remembered);
    }

    [Theory]
    [InlineData(CustomLyricsStore.SourceName)]
    [InlineData(LocalLyrics.SidecarSourceName)]
    [InlineData(LocalLyrics.TagsSourceName)]
    public void The_switch_hides_while_the_lyrics_are_the_users_own_or_the_tracks(string source)
    {
        var pane = new Pane { Answer = _ => Found(source) };
        var vm = pane.ViewModel();

        vm.OnTrackChanged(Track("khnokW3Mw24"));

        Assert.False(vm.ShowsSourceSwitch);
    }

    [Theory]
    [InlineData(LyricsStatus.NotFound)]
    [InlineData(LyricsStatus.Unavailable)]
    [InlineData(LyricsStatus.NotLookedUp)]
    [InlineData(LyricsStatus.Instrumental)]
    public void A_miss_keeps_the_switch_in_view(LyricsStatus status)
    {
        var pane = new Pane { Answer = _ => new LyricsLookup(status, LyricsDocument.Empty(LrcLibProvider.SourceName)) };
        var vm = pane.ViewModel();

        vm.OnTrackChanged(Track("khnokW3Mw24"));

        Assert.True(vm.ShowsSourceSwitch);
    }

    [Fact]
    public void Without_a_way_to_remember_the_choice_there_is_no_switch()
    {
        var vm = new Pane().ViewModel(canChoose: false);

        vm.OnTrackChanged(Track("khnokW3Mw24"));

        Assert.False(vm.ShowsSourceSwitch);
    }

    [Fact]
    public void The_hints_name_the_source_chosen()
    {
        var pane = new Pane();
        var vm = pane.ViewModel();
        vm.OnTrackChanged(Track("khnokW3Mw24"));
        Assert.Equal("LRCLIB doesn't have lyrics for this track yet.", vm.NoneHint);
        Assert.Contains("lrclib.net", vm.NotLookedUpHint);

        vm.SelectedSource = YouTubeMusic(vm);

        Assert.Equal("YouTube Music doesn't have lyrics for this track.", vm.NoneHint);
        Assert.Contains("video id to music.youtube.com", vm.NotLookedUpHint);
    }

    [Fact]
    public void A_local_file_on_YouTube_Music_says_why_there_are_none()
    {
        var pane = new Pane { Answer = _ => LyricsLookup.NotAsked };
        var vm = pane.ViewModel(YouTubeMusicLyricsProvider.SourceName);

        vm.OnTrackChanged(Track("local:4"));

        Assert.Equal(LyricsState.None, vm.State);
        Assert.Equal(
            "YouTube Music has lyrics only for its own songs, and this file isn't matched to one.",
            vm.NoneHint);
    }

    [Fact]
    public void A_matched_local_file_with_no_lyrics_on_YouTube_Music_says_so()
    {
        var pane = new Pane();
        var vm = pane.ViewModel(YouTubeMusicLyricsProvider.SourceName);

        vm.OnTrackChanged(Track("local:4"));

        Assert.Equal("YouTube Music doesn't have lyrics for this track.", vm.NoneHint);
    }

    [Fact]
    public void The_service_asks_the_source_the_pane_starts_with()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-lyrics-source-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var service = LyricsService.Create(dir,
            [
                new ScriptedLyricsProvider(LrcLibProvider.SourceName),
                new ScriptedLyricsProvider(YouTubeMusicLyricsProvider.SourceName),
            ]);
            var queue = new Rambutan.Core.Queue.QueueModel();
            using var coordinator = FakePlayback.Coordinator(queue);
            var remembered = new List<string>();

            var vm = new LyricsViewModel(service, coordinator, queue, lookUpOnline: false, _ => { },
                YouTubeMusicLyricsProvider.SourceName, remembered.Add);

            Assert.Equal(YouTubeMusicLyricsProvider.SourceName, service.Source);
            vm.SelectedSource = vm.SourceChoices[0];
            Assert.Equal(LrcLibProvider.SourceName, service.Source);
            Assert.Equal([LrcLibProvider.SourceName], remembered);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch (DirectoryNotFoundException) { }
        }
    }

    private sealed class ScriptedLyricsProvider(string name) : ILyricsProvider
    {
        public string Name => name;

        public Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct) =>
            Task.FromResult<LyricsResult?>(null);
    }
}
