using System.Text;
using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.LocalApi;

namespace Rambutan.App.Tests;

/// <summary>Which image a cover request gets.</summary>
public sealed class CoverSourceTests : IDisposable
{
    private const string Url = "https://lh3.googleusercontent.com/abc=w544-h544-l90-rj";
    private readonly string _kept = Path.GetTempFileName();
    private readonly List<string> _fetched = [];
    private bool _offline;

    public CoverSourceTests() => File.WriteAllBytes(_kept, "kept"u8.ToArray());

    public void Dispose() => File.Delete(_kept);

    private static TrackInfo Track(string? url = Url) =>
        new() { VideoId = "v1", Title = "T", Artists = [], ThumbnailUrl = url };

    private CoverSource Covers(bool downloaded = true) => new(
        (url, _) =>
        {
            _fetched.Add(url);
            return _offline
                ? throw new HttpRequestException("offline")
                : Task.FromResult(Encoding.UTF8.GetBytes("fetched"));
        },
        _ => downloaded ? _kept : null);

    private static string Text(RemoteArtwork? art) => Encoding.UTF8.GetString(art!.Bytes);

    [Fact]
    public async Task TheFullCover_IsTheKeptCopyFirst()
    {
        Assert.Equal("kept", Text(await Covers().GetAsync(Track(), ArtworkSize.Full, "test", default)));
        Assert.Empty(_fetched);
    }

    [Fact]
    public async Task ARowsCover_IsASmallFetchedOne_NotTheLargeKeptCopy()
    {
        var art = await Covers().GetAsync(Track(), ArtworkSize.Row, "test", default);

        Assert.Equal("fetched", Text(art));
        Assert.Equal(["https://lh3.googleusercontent.com/abc=w120-h120-l90-rj"], _fetched);
    }

    [Fact]
    public async Task ARowsCover_Offline_FallsBackToTheKeptCopy()
    {
        _offline = true;

        Assert.Equal("kept", Text(await Covers().GetAsync(Track(), ArtworkSize.Row, "test", default)));
    }

    [Fact]
    public async Task ARowsCover_WithoutAUrl_IsTheKeptCopy_OrNone()
    {
        Assert.Equal("kept", Text(await Covers().GetAsync(Track(url: null), ArtworkSize.Row, "test", default)));
        Assert.Null(await Covers(downloaded: false).GetAsync(Track(url: null), ArtworkSize.Row, "test", default));
    }
}
