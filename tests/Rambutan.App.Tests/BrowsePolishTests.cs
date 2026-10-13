using System.Globalization;
using Rambutan.App.Controls;
using Rambutan.App.Converters;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

public class DurationFormatTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(7, "0:07")]
    [InlineData(338, "5:38")]           // rows used to say "05:38"
    [InlineData(600, "10:00")]
    [InlineData(3725, "1:02:05")]       // and wrapped anything over an hour
    [InlineData(-3, "0:00")]
    public void Clock_OneFormatForRowsAndThePlayerBar(int seconds, string expected) =>
        Assert.Equal(expected, DurationFormat.Clock(TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(0, "0 min")]
    [InlineData(20, "1 min")]
    [InlineData(48 * 60 + 59, "48 min")]
    [InlineData(74 * 60 + 40, "1 hr 14 min")] // rounded down, as YTM's headers are
    [InlineData(120 * 60, "2 hr")]
    public void Total_ReadsAsARunningTime(int seconds, string expected) =>
        Assert.Equal(expected, DurationFormat.Total(TimeSpan.FromSeconds(seconds)));

    private static TrackInfo Track(int? seconds) => new()
    {
        VideoId = Guid.NewGuid().ToString(),
        Title = "t",
        Artists = [],
        Duration = seconds is { } s ? TimeSpan.FromSeconds(s) : null,
    };

    [Fact]
    public void Summary_CountsAndTotalsTheRows()
    {
        Assert.Equal("", DurationFormat.Summary([]));
        Assert.Equal("1 song • 4 min", DurationFormat.Summary([Track(276)]));
        Assert.Equal("3 songs • 12 min", DurationFormat.Summary([Track(240), Track(240), Track(240)]));
    }

    [Fact]
    public void Summary_LeavesOutATotalItCantKnow()
    {
        // A sum of the known lengths would understate the list.
        Assert.Equal("2 songs", DurationFormat.Summary([Track(240), Track(null)]));
    }

    [Fact]
    public void Converter_BlankForAMissingDuration()
    {
        var convert = (object? v) => DurationFormat.Converter.Convert(v, typeof(string), null, CultureInfo.InvariantCulture);
        Assert.Equal("4:36", convert(TimeSpan.FromSeconds(276)));
        Assert.Equal("", convert(null));
    }
}

public class ScrollPagingTests
{
    // Cards are 156 wide with 10 between: a stride of 166.
    private const double Stride = 166;

    [Fact]
    public void Forward_PagesByWholeVisibleCards_SoTheCutOffCardLeads()
    {
        // 750 wide shows 4 whole cards (664) and part of the 5th.
        Assert.Equal(4 * Stride, ScrollPaging.PageTarget(0, 750, 20 * Stride, Stride, forward: true));
    }

    [Fact]
    public void Back_ReturnsToThePreviousPage()
    {
        Assert.Equal(0, ScrollPaging.PageTarget(4 * Stride, 750, 20 * Stride, Stride, forward: false));
        Assert.Equal(4 * Stride, ScrollPaging.PageTarget(8 * Stride, 750, 20 * Stride, Stride, forward: false));
    }

    [Fact]
    public void PagingFromMidCard_LandsOnACardBoundary()
    {
        // Left mid-card by Shift+wheel: forward from 1.5 cards, back from 5.5.
        Assert.Equal(5 * Stride, ScrollPaging.PageTarget(1.5 * Stride, 750, 20 * Stride, Stride, forward: true));
        Assert.Equal(2 * Stride, ScrollPaging.PageTarget(5.5 * Stride, 750, 20 * Stride, Stride, forward: false));
    }

    [Fact]
    public void Targets_ClampToTheScrollableRange()
    {
        var extent = 10 * Stride;
        Assert.Equal(extent - 750, ScrollPaging.PageTarget(8 * Stride, 750, extent, Stride, forward: true));
        Assert.Equal(0, ScrollPaging.PageTarget(Stride, 750, extent, Stride, forward: false));
    }

    [Fact]
    public void NarrowerThanOneCard_StillMovesOneCard() =>
        Assert.Equal(Stride, ScrollPaging.PageTarget(0, 100, 20 * Stride, Stride, forward: true));

    [Fact]
    public void Ends_DisableTheirChevron()
    {
        Assert.False(ScrollPaging.CanPageBack(0));
        Assert.True(ScrollPaging.CanPageBack(166));
        Assert.True(ScrollPaging.CanPageForward(0, 750, 1660));
        Assert.False(ScrollPaging.CanPageForward(910, 750, 1660));
    }

    [Fact]
    public void NearEnd_FiresWithinTheThreshold()
    {
        Assert.False(ScrollPaging.IsNearEnd(offset: 0, viewport: 600, extent: 2000));
        Assert.True(ScrollPaging.IsNearEnd(offset: 1100, viewport: 600, extent: 2000));
        // A first page too short to scroll counts as at its end.
        Assert.True(ScrollPaging.IsNearEnd(offset: 0, viewport: 600, extent: 300));
    }
}

public class EntryPlaybackTests
{
    private static TrackInfo Track(string id) => new() { VideoId = id, Title = id, Artists = [] };

    [Fact]
    public void OnlyAlbumAndPlaylistCardsGetAPlayButton()
    {
        Assert.True(EntryPlayback.HasPlayButton(new AlbumEntry { Title = "a", BrowseId = "MPRE" }));
        Assert.True(EntryPlayback.HasPlayButton(new PlaylistEntry { Title = "p", PlaylistId = "PL" }));
        Assert.False(EntryPlayback.HasPlayButton(new ArtistEntry { Title = "x", BrowseId = "UC" }));
        Assert.False(EntryPlayback.HasPlayButton(new SongEntry { Title = "s", Track = Track("v") }));
        Assert.False(EntryPlayback.HasPlayButton(null));
    }

    [Fact]
    public async Task Resolve_FetchesTheAlbumOrPlaylistTracks()
    {
        var catalog = new FakeCatalog
        {
            Album = new AlbumPage { BrowseId = "MPRE", Title = "a", Tracks = [Track("a1"), Track("a2")] },
            Playlist = new PlaylistPage { PlaylistId = "PL", Title = "p", Tracks = [Track("p1")] },
        };

        var album = await EntryPlayback.ResolveAsync(catalog, new AlbumEntry { Title = "a", BrowseId = "MPRE" });
        var playlist = await EntryPlayback.ResolveAsync(catalog, new PlaylistEntry { Title = "p", PlaylistId = "PL" });
        var artist = await EntryPlayback.ResolveAsync(catalog, new ArtistEntry { Title = "x", BrowseId = "UC" });

        Assert.Equal(["a1", "a2"], album.Select(t => t.VideoId));
        Assert.Equal(["p1"], playlist.Select(t => t.VideoId));
        Assert.Empty(artist);
    }

    [Fact]
    public async Task Resolve_PlaysAnAlbumsAudioTracks_ButAPlaylistAsListed()
    {
        // A card plays an album as its page does; a playlist plays as listed.
        var catalog = new FakeCatalog
        {
            Album = new AlbumPage
            {
                BrowseId = "MPRE", Title = "a",
                Tracks = [Track("omv1") with { SongId = "atv1" }, Track("omv2") with { SongId = "atv2" }],
            },
            Playlist = new PlaylistPage { PlaylistId = "PL", Title = "p", Tracks = [Track("omv1") with { SongId = "atv1" }] },
        };

        var album = await EntryPlayback.ResolveAsync(catalog, new AlbumEntry { Title = "a", BrowseId = "MPRE" }, id => id == "omv2");
        var playlist = await EntryPlayback.ResolveAsync(catalog, new PlaylistEntry { Title = "p", PlaylistId = "PL" });

        Assert.Equal(["atv1", "omv2"], album.Select(t => t.VideoId));
        Assert.Equal(["omv1"], playlist.Select(t => t.VideoId));
    }

    [Fact]
    public void Shuffled_IsAPermutation_AndLeavesTheSourceAlone()
    {
        var source = Enumerable.Range(1, 20).ToList();

        var shuffled = EntryPlayback.Shuffled(source, new Random(7));

        Assert.Equal(Enumerable.Range(1, 20), source);
        Assert.Equal(source, shuffled.Order());
        Assert.NotEqual(source, shuffled);
    }
}

public class AlbumHeaderTests
{
    private static TrackInfo Track(int number, int seconds) => new()
    {
        VideoId = $"v{number}",
        Title = $"t{number}",
        Artists = [new ArtistRef("Daft Punk", "UC1")],
        Duration = TimeSpan.FromSeconds(seconds),
        TrackNumber = number,
    };

    private static async Task<AlbumPageViewModel> Load(AlbumPage album)
    {
        // Loading never touches playback.
        var vm = new AlbumPageViewModel(new FakeCatalog { Album = album }, null!, album.BrowseId);
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task Header_LinksTheArtist_AndSummarisesTheRows()
    {
        var vm = await Load(new AlbumPage
        {
            BrowseId = "MPRE",
            Title = "Random Access Memories",
            Artist = "Daft Punk",
            ArtistBrowseId = "UC1",
            SubtitleInfo = "Album • 2013",
            Tracks = [Track(1, 276), Track(2, 323)],
        });

        Assert.Equal(new ArtistRef("Daft Punk", "UC1"), vm.Artist);
        Assert.Equal("Album • 2013", vm.Details);
        Assert.Equal("2 songs • 9 min", vm.Summary);
    }

    [Fact]
    public async Task Header_ShowsAnUnlinkedArtistAsText()
    {
        var vm = await Load(new AlbumPage
        {
            BrowseId = "MPRE", Title = "a", Artist = "Various Artists", Tracks = [Track(1, 60)],
        });

        Assert.Equal("Various Artists", vm.Artist?.Name);
        Assert.Null(vm.Artist?.BrowseId);
    }

    [Fact]
    public void TrackNumber_GivesWayToThePlayingMarker()
    {
        var notPlaying = IsPlayingTrackConverter.Inverted.Convert(
            [new TrackIdentity("v1", null), new TrackIdentity("v2", null)], typeof(bool), null, CultureInfo.InvariantCulture);
        var playing = IsPlayingTrackConverter.Inverted.Convert(
            [new TrackIdentity("v1", null), new TrackIdentity("v1", null)], typeof(bool), null, CultureInfo.InvariantCulture);
        var nothingPlaying = IsPlayingTrackConverter.Inverted.Convert(
            [new TrackIdentity("v1", null), null], typeof(bool), null, CultureInfo.InvariantCulture);

        Assert.Equal(true, notPlaying);
        Assert.Equal(false, playing);
        Assert.Equal(true, nothingPlaying);
    }

    [Fact]
    public void PlayingMarker_FindsTheAlbumRow_ForATrackPlayedFromSearch()
    {
        // The album page lists Instant Crush's music video; search played its
        // audio track. The album row's credits link names that audio track.
        var albumRow = new TrackIdentity("a5uQMwRMHcs", "khnokW3Mw24");
        var fromSearch = new TrackIdentity("khnokW3Mw24", "khnokW3Mw24");
        var otherSong = new TrackIdentity("4D7u5KF7SP8", "4D7u5KF7SP8");

        Assert.Equal(true, IsPlayingTrackConverter.Instance.Convert(
            [albumRow, fromSearch], typeof(bool), null, CultureInfo.InvariantCulture));
        Assert.Equal(false, IsPlayingTrackConverter.Instance.Convert(
            [albumRow, otherSong], typeof(bool), null, CultureInfo.InvariantCulture));
    }
}
