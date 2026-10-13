using Rambutan.Core.Playback;

namespace Rambutan.Core.Tests;

public class PlayerStateMachineTests
{
    [Fact]
    public void StartsStopped()
    {
        Assert.Equal(PlaybackState.Stopped, new PlayerStateMachine().State);
    }

    [Fact]
    public void HappyPath_LoadPlayPauseResumeStop()
    {
        var machine = new PlayerStateMachine();
        Assert.True(machine.TransitionTo(PlaybackState.Loading));
        Assert.True(machine.TransitionTo(PlaybackState.Playing));
        Assert.True(machine.TransitionTo(PlaybackState.Paused));
        Assert.True(machine.TransitionTo(PlaybackState.Playing));
        Assert.True(machine.TransitionTo(PlaybackState.Stopped));
    }

    [Fact]
    public void SameState_IsNoOp()
    {
        var machine = new PlayerStateMachine();
        Assert.False(machine.TransitionTo(PlaybackState.Stopped));
    }

    [Fact]
    public void StoppedToPlaying_IsInvalid()
    {
        var machine = new PlayerStateMachine();
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(PlaybackState.Playing));
    }

    [Fact]
    public void PausedToBuffering_IsInvalid()
    {
        var machine = new PlayerStateMachine();
        machine.TransitionTo(PlaybackState.Loading);
        machine.TransitionTo(PlaybackState.Playing);
        machine.TransitionTo(PlaybackState.Paused);
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(PlaybackState.Buffering));
    }

    [Fact]
    public void AnyState_CanStartLoading()
    {
        var machine = new PlayerStateMachine();
        machine.TransitionTo(PlaybackState.Loading);
        machine.TransitionTo(PlaybackState.Playing);
        machine.TransitionTo(PlaybackState.Buffering);
        Assert.True(machine.TransitionTo(PlaybackState.Loading));
    }

    [Fact]
    public void StateChanged_FiresOnTransitionOnly()
    {
        var machine = new PlayerStateMachine();
        var events = new List<PlaybackState>();
        machine.StateChanged += events.Add;

        machine.TransitionTo(PlaybackState.Loading);
        machine.TransitionTo(PlaybackState.Loading);

        Assert.Equal([PlaybackState.Loading], events);
    }
}
