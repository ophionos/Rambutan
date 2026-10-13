using System.Net;
using Rambutan.Core.Playback;

namespace Rambutan.Streams.Tests;

public sealed class OfflineAwareSourceTests
{
    private static readonly ResolvedMedia Media = new("https://cdn.test/a", "opus");

    [Fact]
    public async Task Online_ReturnsTheResolution()
    {
        var inner = new ScriptedSource(async ct =>
        {
            await Task.Delay(20, ct);
            return Media;
        });
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Online));

        Assert.Same(Media, await source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnAnswerAlreadyAtHand_CostsNoCheck()
    {
        var checks = 0;
        var inner = new ScriptedSource(_ => Task.FromResult(Media));
        var source = new OfflineAwareSource(inner, _ =>
        {
            checks++;
            return Task.FromResult(Connectivity.Online);
        });

        await source.ResolveAsync("a", ct: TestContext.Current.CancellationToken);

        Assert.Equal(0, checks);
    }

    [Fact]
    public async Task Offline_CutsASlowResolutionShort()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new ScriptedSource(async ct =>
        {
            ct.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct);
            return Media;
        });
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Offline));

        await Assert.ThrowsAsync<OfflineException>(
            () => source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AFailureWhileOnline_IsNotOffline()
    {
        var inner = new ScriptedSource(async _ =>
        {
            await Task.Yield();
            throw new InvalidOperationException("video unavailable");
        });
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Online));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
        Assert.Equal("video unavailable", ex.Message);
    }

    [Fact]
    public async Task AFailureAfterTheCheckTimedOut_IsOffline()
    {
        var inner = new ScriptedSource(async _ =>
        {
            await Task.Delay(20);
            throw new InvalidOperationException("Unable to download API page");
        });
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Unknown));

        var ex = await Assert.ThrowsAsync<OfflineException>(
            () => source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task ACheckThatTimesOut_NeverCutsAResolutionShort()
    {
        var inner = new ScriptedSource(async ct =>
        {
            await Task.Delay(50, ct);
            return Media;
        });
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Unknown));

        Assert.Same(Media, await source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void OfflineAvailability_ComesFromTheInnerSource()
    {
        var inner = new ScriptedSource(_ => Task.FromResult(Media)) { Offline = ["cached"] };
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Offline));

        Assert.True(source.IsAvailableOffline("cached"));
        Assert.False(source.IsAvailableOffline("other"));
    }

    [Fact]
    public async Task Probe_ConnectionFailure_IsOffline()
    {
        var probe = new ConnectivityProbe(new HttpClient(new Handler(
            (_, _) => throw new HttpRequestException("No such host is known."))));

        Assert.Equal(Connectivity.Offline, await probe.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Probe_AnyResponse_IsOnline()
    {
        var probe = new ConnectivityProbe(new HttpClient(new Handler(
            (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)))));

        Assert.Equal(Connectivity.Online, await probe.CheckAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Probe_NoAnswerInTime_IsUnknown()
    {
        var probe = new ConnectivityProbe(
            new HttpClient(new Handler(async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return new HttpResponseMessage(HttpStatusCode.NoContent);
            })),
            TimeSpan.FromMilliseconds(50));

        Assert.Equal(Connectivity.Unknown, await probe.CheckAsync(TestContext.Current.CancellationToken));
    }

    // yt-dlp offline: no answer until the check ends it.
    private static async Task<ResolvedMedia> Hangs(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        return Media;
    }

    private static readonly ResolvedMedia LowerQualityCopy = new("/cache/a.249.webm", "opus");

    [Fact]
    public async Task Offline_PlaysALocalCopyBelowTheQualityPreference()
    {
        var inner = new ScriptedSource(Hangs)
        {
            Local = { ["a"] = LowerQualityCopy },
        };
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Offline));

        Assert.Equal(LowerQualityCopy, await source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<OfflineException>(
            () => source.ResolveAsync("b", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Offline_FaultRetry_DoesntFallBackToTheFileThatFailed()
    {
        var inner = new ScriptedSource(Hangs)
        {
            Local = { ["a"] = LowerQualityCopy },
        };
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Offline));

        await Assert.ThrowsAsync<OfflineException>(
            () => source.ResolveAsync("a", bypassCache: true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Online_AFailedResolution_StaysAFailure_EvenWithALocalCopy()
    {
        var inner = new ScriptedSource(_ => Task.FromException<ResolvedMedia>(new InvalidOperationException("yt-dlp")))
        {
            Local = { ["a"] = LowerQualityCopy },
        };
        var source = new OfflineAwareSource(inner, _ => Task.FromResult(Connectivity.Online));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.ResolveAsync("a", ct: TestContext.Current.CancellationToken));
    }

    private sealed class ScriptedSource(Func<CancellationToken, Task<ResolvedMedia>> resolve)
        : IStreamSource, IOfflineAvailability, ILocalFallback
    {
        public HashSet<string> Offline { get; init; } = [];
        public Dictionary<string, ResolvedMedia> Local { get; init; } = [];

        public ResolvedMedia? TryLocal(string videoId) => Local.GetValueOrDefault(videoId);

        public Task<ResolvedMedia> ResolveAsync(
            string videoId, bool bypassCache = false, CancellationToken ct = default) => resolve(ct);

        public bool IsAvailableOffline(string videoId) => Offline.Contains(videoId);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }
}
