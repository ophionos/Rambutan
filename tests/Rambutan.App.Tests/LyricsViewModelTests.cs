using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Providers;

namespace Rambutan.App.Tests;

/// <summary>The lyrics pane's states and line tracking.</summary>
public class LyricsViewModelTests
{
    private static TrackInfo Track(string id, string title = "Instant Crush") => new()
    {
        VideoId = id,
        Title = title,
        Artists = [new("Daft Punk", "UCdp")],
        Album = "Random Access Memories",
        Duration = TimeSpan.FromMinutes(5),
    };

    private static LyricsDocument Synced(params int[] startSeconds) => new()
    {
        Source = LrcLibProvider.SourceName,
        IsSynced = true,
        Lines = startSeconds.Select((s, i) => new LyricLine(i == 2 ? "" : $"line {i}", TimeSpan.FromSeconds(s))).ToArray(),
    };

    /// <summary>Lookups that complete when the test says, and honour cancellation.</summary>
    private sealed class Lookups
    {
        public List<(LyricsQuery Query, CancellationToken Token, TaskCompletionSource<LyricsLookup> Answer)> Calls { get; } = [];
        public List<TimeSpan> Seeks { get; } = [];

        public LyricsViewModel ViewModel() => new(Fetch, position =>
        {
            Seeks.Add(position);
            return Task.CompletedTask;
        });

        private Task<LyricsLookup> Fetch(LyricsQuery query, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<LyricsLookup>();
            ct.Register(() => answer.TrySetCanceled(ct));
            Calls.Add((query, ct, answer));
            return answer.Task;
        }

        public void Answer(LyricsLookup result, int call = -1) =>
            Calls[call < 0 ? Calls.Count - 1 : call].Answer.TrySetResult(result);
    }

    [Fact]
    public void NothingLoaded_FollowsTheQueuesTrack_UntilATrackLoads()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();

        vm.OnLoadedTrackChanged(null, Track("a"));
        Assert.Equal("a", Assert.Single(lookups.Calls).Query.VideoId);
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(0, 10, 20)));
        Assert.Equal(LyricsState.Synced, vm.State);

        // Play loads the same track: the lyrics stay, nothing is asked again.
        vm.OnLoadedTrackChanged(Track("a"), Track("a"));
        Assert.Single(lookups.Calls);
        Assert.Equal(LyricsState.Synced, vm.State);

        // Once a track is loaded, the queue's current track doesn't steer the pane.
        vm.OnQueueChanged(Track("b"));
        Assert.Single(lookups.Calls);
    }

    [Fact]
    public void NothingLoaded_AQueueEditThatKeepsTheTrack_AsksNothingAgain()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnLoadedTrackChanged(null, Track("a"));
        lookups.Answer(LyricsLookup.NotFound);

        vm.OnQueueChanged(Track("a"));
        Assert.Single(lookups.Calls);

        vm.OnQueueChanged(Track("b"));
        Assert.Equal("b", lookups.Calls[^1].Query.VideoId);

        vm.OnQueueChanged(null);
        Assert.Equal(LyricsState.Hidden, vm.State);
    }

    [Fact]
    public void NothingPlaying_IsHidden_AndAsksForNothing()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(null);

        Assert.Equal(LyricsState.Hidden, vm.State);
        Assert.False(vm.IsShown);
        Assert.Empty(lookups.Calls);
    }

    [Fact]
    public void ATrack_LoadsThenShowsSyncedLines_WithTheActiveLineFromPlayback()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();

        vm.OnTrackChanged(Track("a"));
        Assert.Equal(LyricsState.Loading, vm.State);
        Assert.True(vm.IsShown);
        Assert.Equal("a", lookups.Calls[0].Query.VideoId);
        Assert.Equal("Daft Punk", lookups.Calls[0].Query.Artist);

        vm.OnPosition(TimeSpan.FromSeconds(9)); // progress arrives before the lyrics
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(4, 8, 12, 16)));

        Assert.Equal(LyricsState.Synced, vm.State);
        Assert.Equal(4, vm.Lines.Count);
        Assert.Equal(1, vm.ActiveLineIndex);
        Assert.True(vm.Lines[1].IsActive);
        Assert.True(vm.Lines[0].IsNear);
        Assert.True(vm.Lines[2].IsNear);
        Assert.False(vm.Lines[3].IsNear || vm.Lines[3].IsActive);
        Assert.Equal("Lyrics from LRCLIB", vm.SourceLabel);

        vm.OnPosition(TimeSpan.FromSeconds(16.2));
        Assert.Equal(3, vm.ActiveLineIndex);
        Assert.False(vm.Lines[1].IsActive);
        Assert.True(vm.Lines[3].IsActive);
    }

    [Fact]
    public void ABlankStampedLine_IsAGap_DrawnAsANote()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(4, 8, 12, 16)));

        Assert.True(vm.Lines[2].IsGap);
        Assert.Equal("♪", vm.Lines[2].Text);
        Assert.StartsWith("Instrumental", vm.Lines[2].AccessibleName);
        Assert.Equal("line 0", vm.Lines[0].AccessibleName);
    }

    [Fact]
    public void PlainLyrics_AreUnsynced_AndPlaybackMovesNothing()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found,
            LyricsDocument.FromPlainText("first\nsecond\n\nthird", LrcLibProvider.SourceName)));

        Assert.Equal(LyricsState.Unsynced, vm.State);
        Assert.True(vm.HasLyrics);
        Assert.Equal("first\nsecond\n\nthird", vm.PlainText);
        Assert.Empty(vm.Lines);

        vm.OnPosition(TimeSpan.FromSeconds(30));
        Assert.Equal(-1, vm.ActiveLineIndex);
    }

    [Fact]
    public void Instrumental_HasItsOwnState()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(LyricsLookup.From(LyricsResult.Instrumental(LrcLibProvider.SourceName)));

        Assert.Equal(LyricsState.Instrumental, vm.State);
        Assert.True(vm.IsInstrumental);
        Assert.False(vm.HasLyrics);
    }

    [Fact]
    public void NotFound_AndAnEmptyDocument_AreNone()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(LyricsLookup.NotFound);
        Assert.Equal(LyricsState.None, vm.State);

        vm.OnTrackChanged(Track("b"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, LyricsDocument.Empty(LrcLibProvider.SourceName)));
        Assert.Equal(LyricsState.None, vm.State);
        Assert.True(vm.IsNone);
    }

    [Fact]
    public void Unavailable_OffersRetry_WhichAsksAgain()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(LyricsLookup.Unavailable);

        Assert.Equal(LyricsState.Unavailable, vm.State);
        Assert.NotNull(vm.LoadError);
        Assert.Equal("Couldn't reach lyrics", vm.LoadError!.Title);
        Assert.Same(vm.RetryCommand, vm.LoadError.Retry);

        vm.LoadError.Retry!.Execute(null);
        Assert.Equal(LyricsState.Loading, vm.State);
        Assert.Null(vm.LoadError);
        Assert.Equal(2, lookups.Calls.Count);
        Assert.Equal("a", lookups.Calls[1].Query.VideoId);

        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(1, 2)));
        Assert.Equal(LyricsState.Synced, vm.State);
    }

    [Fact]
    public void AFailingLookup_IsUnavailable_NotAStuckSkeleton()
    {
        var vm = new LyricsViewModel((_, _) => Task.FromException<LyricsLookup>(new InvalidOperationException("boom")),
            _ => Task.CompletedTask);
        vm.OnTrackChanged(Track("a"));
        Assert.Equal(LyricsState.Unavailable, vm.State);
    }

    [Fact]
    public void ATrackChange_CancelsTheLookupInFlight_AndItsLateAnswerIsIgnored()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        var first = lookups.Calls[0];

        vm.OnTrackChanged(Track("b", "Within"));
        Assert.True(first.Token.IsCancellationRequested);
        Assert.Equal(LyricsState.Loading, vm.State);

        // The first track's answer, arriving after the skip, must not paint.
        first.Answer.TrySetResult(new LyricsLookup(LyricsStatus.Found, Synced(1, 2, 3)));
        Assert.Equal(LyricsState.Loading, vm.State);

        lookups.Answer(LyricsLookup.NotFound);
        Assert.Equal(LyricsState.None, vm.State);
    }

    [Fact]
    public void StoppingPlayback_CancelsTheLookup_AndHidesThePane()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        vm.OnTrackChanged(null);

        Assert.True(lookups.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(LyricsState.Hidden, vm.State);
    }

    [Fact]
    public void TheSameTrackAgain_KeepsItsLyrics()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(1, 2)));

        vm.OnTrackChanged(Track("a")); // repeat-one announces it again
        Assert.Single(lookups.Calls);
        Assert.Equal(LyricsState.Synced, vm.State);
    }

    [Fact]
    public void TheSameTrackAgain_WhileLoading_StillGetsItsAnswer()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(1, 2)));

        Assert.Single(lookups.Calls);
        Assert.Equal(LyricsState.Synced, vm.State);
    }

    [Fact]
    public void ANewTrack_StartsFromItsFirstLine()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(1, 2, 3)));
        vm.OnPosition(TimeSpan.FromSeconds(100));

        vm.OnTrackChanged(Track("b"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(4, 8)));
        Assert.Equal(-1, vm.ActiveLineIndex); // the old track's position doesn't carry over
    }

    [Fact]
    public async Task ClickingASyncedLine_SeeksToIt_AndHighlightsItAtOnce()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        var sought = 0;
        vm.LineSought += () => sought++;
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(4, 8, 12, 16)));

        await vm.SeekToLineCommand.ExecuteAsync(vm.Lines[3]);

        Assert.Equal([TimeSpan.FromSeconds(16)], lookups.Seeks);
        Assert.Equal(3, vm.ActiveLineIndex);
        Assert.Equal(1, sought);
    }

    [Fact]
    public async Task UnsyncedLyrics_NeverSeek()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, LyricsDocument.FromPlainText("x\ny", "lrclib")));

        await vm.SeekToLineCommand.ExecuteAsync(
            new LyricLineViewModel(new LyricLine("x", TimeSpan.Zero), 0));
        Assert.Empty(lookups.Seeks);
    }

    [Fact]
    public void Disabled_LooksNothingUp_AndEnablingLooksUpTheTrackPlaying()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.IsEnabled = false;

        vm.OnTrackChanged(Track("a"));
        Assert.Empty(lookups.Calls);
        Assert.Equal(LyricsState.Hidden, vm.State);

        vm.IsEnabled = true;
        Assert.Single(lookups.Calls);
        Assert.Equal(LyricsState.Loading, vm.State);

        vm.IsEnabled = false; // mid-lookup
        Assert.True(lookups.Calls[0].Token.IsCancellationRequested);
        Assert.Equal(LyricsState.Hidden, vm.State);
    }

    [Fact]
    public void TheNowPlayingToggle_IsRemembered_AndSwitchesTheLookupsOff()
    {
        var lookups = new Lookups();
        var lyrics = lookups.ViewModel();
        var settings = new AppSettings { ShowLyrics = false };
        var nowPlaying = new NowPlayingViewModel(new PlayerBarViewModel(), lyrics, settings);

        Assert.False(nowPlaying.ShowLyrics);
        Assert.False(lyrics.IsEnabled);
        Assert.Equal("Show lyrics", nowPlaying.LyricsToggleTip);

        lyrics.OnTrackChanged(Track("a"));
        Assert.False(nowPlaying.IsLyricsPaneVisible);

        nowPlaying.ToggleLyricsCommand.Execute(null);
        Assert.True(settings.ShowLyrics);
        Assert.True(lyrics.IsEnabled);
        Assert.True(nowPlaying.IsLyricsPaneVisible);
        Assert.Equal("Hide lyrics", nowPlaying.LyricsToggleTip);
        Assert.Single(lookups.Calls);
    }

    [Fact]
    public void ShowLyrics_DefaultsOff_SoNothingIsLookedUpUntilAsked()
    {
        var lookups = new Lookups();
        var lyrics = lookups.ViewModel();
        var nowPlaying = new NowPlayingViewModel(new PlayerBarViewModel(), lyrics, new AppSettings());

        lyrics.OnTrackChanged(Track("a"));

        Assert.False(new AppSettings().ShowLyrics);
        Assert.False(nowPlaying.ShowLyrics);
        Assert.Empty(lookups.Calls);
    }

    [Fact]
    public void LookupsOff_ShowsWhyThereAreNoLyrics_AndTurningThemOnLooksUpAgain()
    {
        var lookups = new Lookups();
        var applied = new List<bool>();
        var vm = new LyricsViewModel(
            (query, ct) =>
            {
                var answer = new TaskCompletionSource<LyricsLookup>();
                lookups.Calls.Add((query, ct, answer));
                return answer.Task;
            },
            _ => Task.CompletedTask,
            applied.Add);
        vm.LookUpOnline = false;
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(LyricsLookup.NotLookedUp);

        Assert.Equal(LyricsState.NotLookedUp, vm.State);
        Assert.True(vm.IsNotLookedUp);
        Assert.True(vm.IsShown);

        vm.TurnOnLookUpCommand.Execute(null);

        Assert.True(vm.LookUpOnline);
        Assert.Equal([false, true], applied);
        Assert.Equal(LyricsState.Loading, vm.State);
        Assert.Equal(2, lookups.Calls.Count);
        Assert.Equal("a", lookups.Calls[1].Query.VideoId);
    }

    [Fact]
    public void TheSameTrackAgain_AfterNotLookedUp_AsksAgain()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(LyricsLookup.NotLookedUp);

        vm.OnTrackChanged(Track("a"));

        Assert.Equal(2, lookups.Calls.Count);
    }

    [Fact]
    public void TurningLookupsOff_KeepsTheLyricsOnScreen()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(1, 2)));

        vm.LookUpOnline = false;

        Assert.Equal(LyricsState.Synced, vm.State);
        Assert.Single(lookups.Calls);
    }

    [Fact]
    public async Task TheClock_FollowsTrackPositionAndPlayState()
    {
        var lookups = new Lookups();
        var vm = lookups.ViewModel();
        vm.OnTrackChanged(Track("a"));
        vm.OnPosition(TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(30), vm.Clock.Now);

        vm.OnPlaybackState(Rambutan.Core.Playback.PlaybackState.Playing);
        Assert.True(vm.Clock.IsRunning);
        vm.OnPlaybackState(Rambutan.Core.Playback.PlaybackState.Buffering);
        Assert.False(vm.Clock.IsRunning);

        lookups.Answer(new LyricsLookup(LyricsStatus.Found, Synced(1, 2, 3)));
        await vm.SeekToLineCommand.ExecuteAsync(vm.Lines[0]);
        Assert.Equal(TimeSpan.FromSeconds(1), vm.Clock.Now);

        vm.OnTrackChanged(Track("b"));
        Assert.Equal(TimeSpan.Zero, vm.Clock.Now);
    }
}
