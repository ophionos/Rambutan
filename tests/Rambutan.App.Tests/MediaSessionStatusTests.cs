using Rambutan.Core.Playback;
using Rambutan.Integrations;

namespace Rambutan.App.Tests;

/// <summary>What the OS media session is told for each playback state.</summary>
public sealed class MediaSessionStatusTests
{
    [Theory]
    [InlineData(PlaybackState.Playing, SessionPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Buffering, SessionPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Loading, SessionPlaybackStatus.Playing)]
    [InlineData(PlaybackState.Paused, SessionPlaybackStatus.Paused)]
    [InlineData(PlaybackState.Stopped, SessionPlaybackStatus.Stopped)]
    [InlineData(PlaybackState.Faulted, SessionPlaybackStatus.Stopped)]
    public void Each_state_maps_to_what_the_session_reports(PlaybackState state, SessionPlaybackStatus expected) =>
        Assert.Equal(expected, App.SessionStatus(state));
}
