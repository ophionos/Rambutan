using Rambutan.App.Services;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>The player bar's lyrics button: one press shows the lyrics, a second
/// puts them away, and turning them on is the listener's remembered choice.</summary>
public class NowPlayingTests
{
    private static (NowPlayingViewModel NowPlaying, AppSettings Settings) Create()
    {
        var settings = new AppSettings();
        return (new NowPlayingViewModel(new PlayerBarViewModel(), settings: settings), settings);
    }

    [Fact]
    public void TheLyricsButton_OpensNowPlaying_WithTheLyricsOn()
    {
        var (nowPlaying, settings) = Create();
        Assert.False(nowPlaying.AreLyricsOnScreen);

        nowPlaying.OpenLyricsCommand.Execute(null);

        Assert.True(nowPlaying.IsOpen);
        Assert.True(nowPlaying.ShowLyrics);
        Assert.True(nowPlaying.AreLyricsOnScreen);
        Assert.True(settings.ShowLyrics);
    }

    [Fact]
    public void TheLyricsButton_AgainClosesNowPlaying_AndKeepsTheChoice()
    {
        var (nowPlaying, settings) = Create();
        nowPlaying.OpenLyricsCommand.Execute(null);

        nowPlaying.OpenLyricsCommand.Execute(null);

        Assert.False(nowPlaying.IsOpen);
        Assert.False(nowPlaying.AreLyricsOnScreen);
        Assert.True(settings.ShowLyrics);
    }

    [Fact]
    public void TheLyricsButton_OnAnOpenPaneWithoutLyrics_TurnsThemOn()
    {
        var (nowPlaying, _) = Create();
        nowPlaying.IsOpen = true;
        var raised = new List<string?>();
        nowPlaying.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        nowPlaying.OpenLyricsCommand.Execute(null);

        Assert.True(nowPlaying.IsOpen);
        Assert.True(nowPlaying.AreLyricsOnScreen);
        Assert.Contains(nameof(NowPlayingViewModel.AreLyricsOnScreen), raised);
        Assert.Contains(nameof(NowPlayingViewModel.LyricsButtonTip), raised);
    }
}
