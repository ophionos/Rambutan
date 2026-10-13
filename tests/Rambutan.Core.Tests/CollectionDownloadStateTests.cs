using Rambutan.Core.Downloads;

namespace Rambutan.Core.Tests;

/// <summary>The album and playlist Download button's state.</summary>
public class CollectionDownloadStateTests
{
    private static readonly (string, DownloadJobState, double?)[] NoJobs = [];

    private static CollectionDownloadState Of(
        string[] ids, string[] downloaded, (string, DownloadJobState, double?)[]? jobs = null, bool complete = true) =>
        CollectionDownloadState.Of(ids, downloaded.Contains, jobs ?? NoJobs, complete);

    [Fact]
    public void NothingDownloaded_IsNotDownloaded() =>
        Assert.Equal(CollectionDownloadKind.NotDownloaded, Of(["a", "b"], []).Kind);

    [Fact]
    public void EveryTrack_IsDownloaded()
    {
        var state = Of(["a", "b", "a"], ["a", "b"]);
        Assert.Equal(CollectionDownloadKind.Downloaded, state.Kind);
        Assert.Equal(2, state.Total);
        Assert.Equal(1.0, state.Fraction);
    }

    [Fact]
    public void SomeTracks_IsNotDownloaded() =>
        Assert.Equal(CollectionDownloadKind.NotDownloaded, Of(["a", "b"], ["a"]).Kind);

    [Fact]
    public void ARunningJob_IsDownloading_WithProgressOverTheWhole()
    {
        var state = Of(["a", "b", "c", "d"], ["a"],
            [("b", DownloadJobState.Downloading, 0.5), ("c", DownloadJobState.Queued, null), ("x", DownloadJobState.Downloading, 0.9)]);
        Assert.Equal(CollectionDownloadKind.Downloading, state.Kind);
        Assert.Equal(1, state.Downloaded);
        Assert.Equal(1.5 / 4, state.Fraction, 6);
    }

    [Fact]
    public void OnlyFailedJobsLeft_IsNotDownloading_AndCountsThem()
    {
        var state = Of(["a", "b"], ["a"], [("b", DownloadJobState.Failed, null)]);
        Assert.Equal(CollectionDownloadKind.NotDownloaded, state.Kind);
        Assert.Equal(1, state.Failed);
    }

    [Fact]
    public void APartList_NeverClaimsAllDownloaded() =>
        Assert.Equal(CollectionDownloadKind.NotDownloaded, Of(["a"], ["a"], complete: false).Kind);

    [Fact]
    public void NoTracks_IsEmpty() => Assert.Same(CollectionDownloadState.Empty, Of([], []));
}
