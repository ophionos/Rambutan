using Rambutan.Core.Links;
using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

/// <summary>Every link Share copies opens again, in YouTube Music and here.</summary>
public class ShareLinksTests
{
    private const string Channel = "UC_kRDKYrUlrbtrSiyu5Tflg";

    [Fact]
    public void ATrack_LinksToItsWatchPage()
    {
        var link = ShareLinks.ForTrack(TestTracks.Track("khnokW3Mw24"));

        Assert.Equal("https://music.youtube.com/watch?v=khnokW3Mw24", link);
        Assert.Equal(new PlayLink("khnokW3Mw24"), DeepLinkParser.Parse(link).Link);
    }

    [Fact]
    public void ATrackWhoseIdIsNotYouTubes_HasNoLink() =>
        Assert.Null(ShareLinks.ForTrack(TestTracks.Track("local:1")));

    public static TheoryData<CatalogEntry, string, DeepLink> Entries => new()
    {
        {
            new AlbumEntry { Title = "Discovery", BrowseId = "MPREb_4pL8gzRtw1p" },
            "https://music.youtube.com/browse/MPREb_4pL8gzRtw1p",
            new AlbumLink("MPREb_4pL8gzRtw1p")
        },
        {
            new PlaylistEntry { Title = "Mellow", PlaylistId = "RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs" },
            "https://music.youtube.com/playlist?list=RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs",
            new PlaylistLink("RDCLAK5uy_kmPRjHDECIcuVwnKsx2Ng7fyNgFKWNJFs")
        },
        {
            new ArtistEntry { Title = "Daft Punk", BrowseId = Channel },
            "https://music.youtube.com/channel/" + Channel,
            new ArtistLink(Channel)
        },
        // A Library row names the artist's library page; the link names the channel.
        {
            new ArtistEntry { Title = "Daft Punk", BrowseId = "MPLA" + Channel },
            "https://music.youtube.com/channel/" + Channel,
            new ArtistLink(Channel)
        },
    };

    [Theory]
    [MemberData(nameof(Entries))]
    public void ACard_LinksToItsPage_AndTheLinkOpensIt(CatalogEntry entry, string expected, DeepLink opens)
    {
        var link = ShareLinks.ForEntry(entry);

        Assert.Equal(expected, link);
        Assert.Equal(opens, DeepLinkParser.Parse(link).Link);
    }

    [Fact]
    public void NoLink_ForASongCard_OrAnIdThatWouldNotOpen()
    {
        Assert.Null(ShareLinks.ForEntry(new SongEntry { Title = "a", Track = TestTracks.Track("khnokW3Mw24") }));
        Assert.Null(ShareLinks.ForEntry(new ArtistEntry { Title = "Someone", BrowseId = "FEmusic_library_artist" }));
        Assert.Null(ShareLinks.ForEntry(new AlbumEntry { Title = "Odd", BrowseId = "VLPL123" }));
    }
}
