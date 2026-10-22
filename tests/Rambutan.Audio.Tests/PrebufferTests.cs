namespace Rambutan.Audio.Tests;

public class PrebufferTests
{
    [Fact]
    public async Task ATrackShorterThanThePrebuffer_IsReadyOnceItIsAllDecoded()
    {
        // A one-second file whose decoder has finished: all of it sits in the
        // ring, which never reaches three seconds and isn't drained yet.
        using var track = MixerTestKit.Dc("short", 0.5f, seconds: 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await track.PrebufferAsync(TimeSpan.FromSeconds(3), timeout.Token);

        Assert.False(track.Ended);
    }

    [Fact]
    public async Task AnOpenTrack_WaitsForEnoughAudio()
    {
        using var track = MixerTestKit.Dc("streaming", 0.5f, seconds: 1, complete: false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => track.PrebufferAsync(TimeSpan.FromSeconds(3), timeout.Token));
    }
}
