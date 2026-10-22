using System.ComponentModel;
using Rambutan.App.Converters;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>A scan that gives the playing local file another cover. These set
/// <see cref="ArtworkConverter.LocalArtwork"/>, which is static, so they run alone.</summary>
[Collection(nameof(LocalArtwork))]
public sealed class LocalCoverRefreshTests : IDisposable
{
    private readonly Dictionary<string, string> _covers = [];
    private readonly Func<string, string?>? _before = ArtworkConverter.LocalArtwork;

    public LocalCoverRefreshTests() =>
        ArtworkConverter.LocalArtwork = id => _covers.GetValueOrDefault(id);

    public void Dispose() => ArtworkConverter.LocalArtwork = _before;

    private static TrackInfo Track(string id) =>
        new() { VideoId = id, Title = id, Artists = [new("Artist")], ThumbnailUrl = "https://example.com/art.jpg" };

    private static string Uri(string path) => new Uri(path).AbsoluteUri;

    private static QueueModel Queue(params string[] ids)
    {
        var queue = new QueueModel();
        queue.SetQueue(ids.Select(Track));
        return queue;
    }

    [Fact]
    public void ThePlayerBar_ShowsTheNewCover() => Headless.Run(() =>
    {
        var old = Path.Combine(Path.GetTempPath(), "old.jpg");
        var newer = Path.Combine(Path.GetTempPath(), "new.jpg");
        _covers["local:1"] = old;
        var bar = new PlayerBarViewModel(null!, Queue("local:1"), null);
        Assert.Equal(Uri(old), bar.ArtworkUrl);

        _covers["local:1"] = newer;
        bar.RefreshArtwork();
        Assert.Equal(Uri(newer), bar.ArtworkUrl);

        _covers.Remove("local:1");
        bar.RefreshArtwork();
        Assert.Equal("https://example.com/art.jpg", bar.ArtworkUrl); // the track's own again
    });

    [Fact]
    public void ThePlayerBar_ShowsTheNamesAMatchFills_AndKeepsTheFilesOwnTrack() => Headless.Run(() =>
    {
        var queue = new QueueModel();
        queue.SetQueue([new TrackInfo { VideoId = "local:1", Title = "Instant Crush", Artists = [] }]);
        TrackInfo? match = null;
        var bar = new PlayerBarViewModel(null!, queue, null)
        {
            ShowAs = track => match is null ? track : track with { Artists = [new("Daft Punk")], Album = "RAM" },
        };
        Assert.Equal("", bar.Artist);

        match = Track("song");
        bar.RefreshArtwork();

        Assert.Equal("Daft Punk • RAM", bar.Artist);
        Assert.Equal("Daft Punk", bar.DisplayedTrack?.ArtistDisplay);
        Assert.Empty(bar.ShownTrack!.Artists);
    });

    [Fact]
    public void ThePlayerBar_LeavesAYouTubeTracksCoverAlone() => Headless.Run(() =>
    {
        var bar = new PlayerBarViewModel(null!, Queue("dQw4w9WgXcQ"), null);
        var changes = 0;
        bar.PropertyChanged += (_, e) => changes += e.PropertyName == nameof(PlayerBarViewModel.ArtworkUrl) ? 1 : 0;

        _covers["dQw4w9WgXcQ"] = Path.Combine(Path.GetTempPath(), "kept.jpg");
        bar.RefreshArtwork();

        Assert.Equal("https://example.com/art.jpg", bar.ArtworkUrl);
        Assert.Equal(0, changes);
    });

    [Fact]
    public void AQueueRow_TellsItsViewOnlyWhenItsCoverChanged() => Headless.Run(() =>
    {
        var cover = Path.Combine(Path.GetTempPath(), "cover.jpg");
        var row = new QueueViewModel(Queue("local:1"), null).Items[0];
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        row.RefreshArtwork(); // not shown yet: nothing read, nothing told
        Assert.Equal("https://example.com/art.jpg", row.ArtworkUrl);
        row.RefreshArtwork();
        Assert.Empty(changes);

        _covers["local:1"] = cover;
        row.RefreshArtwork();
        Assert.Equal([nameof(QueueItemViewModel.ArtworkUrl)], changes);
        Assert.Equal(Uri(cover), row.ArtworkUrl);
    });
}

[CollectionDefinition(nameof(LocalArtwork), DisableParallelization = true)]
public sealed class LocalArtwork;
