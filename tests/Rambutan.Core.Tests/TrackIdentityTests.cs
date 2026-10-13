using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

/// <summary>
/// The ids are Instant Crush's from the recorded fixtures: the album page's music
/// video (OMV) names the audio track (ATV) that search and radio hand out.
/// </summary>
public class TrackIdentityTests
{
    private const string Omv = "a5uQMwRMHcs";
    private const string Atv = "khnokW3Mw24";

    private static readonly TrackIdentity AlbumRow = new(Omv, Atv);      // album page
    private static readonly TrackIdentity SearchRow = new(Atv, Atv);     // song search, radio
    private static readonly TrackIdentity VideoRow = new(Omv, null);     // video search, OMV radio
    private static readonly TrackIdentity RestoredRow = new(Atv, null);  // a row with no credits item

    [Fact]
    public void AlbumMusicVideo_MatchesTheAudioTrackItNames_BothWays()
    {
        Assert.True(AlbumRow.SameSong(SearchRow));
        Assert.True(SearchRow.SameSong(AlbumRow));
        Assert.True(AlbumRow.SameSong(RestoredRow));
        Assert.True(RestoredRow.SameSong(AlbumRow));
    }

    [Fact]
    public void SameVideo_StillMatches_WhenOnlyOneSurfaceKnowsTheCounterpart()
    {
        // The regression a single "SongId ?? VideoId" key would cause: the video
        // played from a video search must still mark its own album row.
        Assert.True(VideoRow.SameSong(AlbumRow));
        Assert.True(AlbumRow.SameSong(VideoRow));
    }

    [Fact]
    public void UnknownCounterpart_FallsBackToTheExactVideo()
    {
        Assert.False(VideoRow.SameSong(SearchRow));
        Assert.False(VideoRow.SameSong(RestoredRow));
        Assert.True(VideoRow.SameSong(new TrackIdentity(Omv, null)));
    }

    [Fact]
    public void DifferentSongs_NeverMatch()
    {
        var other = new TrackIdentity("4D7u5KF7SP8", "4D7u5KF7SP8");
        var otherVideo = new TrackIdentity("5NV6Rdv1a3I", "4D7u5KF7SP8");

        Assert.False(AlbumRow.SameSong(other));
        Assert.False(AlbumRow.SameSong(otherVideo));
        Assert.False(new TrackIdentity("a", null).SameSong(new TrackIdentity("b", null)));
    }

    [Fact]
    public void EmptyIds_MatchNothing()
    {
        Assert.False(new TrackIdentity("", null).SameSong(new TrackIdentity("", null)));
        Assert.False(new TrackIdentity("", "").SameSong(new TrackIdentity("x", "")));
        Assert.False(new TrackIdentity("x", "").SameSong(new TrackIdentity("y", "")));
    }

    [Fact]
    public void TrackInfo_ExposesItsIdentity()
    {
        var track = new TrackInfo { VideoId = Omv, Title = "Instant Crush", Artists = [], SongId = Atv };

        Assert.Equal(AlbumRow, track.Identity);
    }
}
