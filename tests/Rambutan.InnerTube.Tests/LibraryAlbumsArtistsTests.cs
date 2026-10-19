using System.Text.Json;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.InnerTube.Parsing;

namespace Rambutan.InnerTube.Tests;

/// <summary>
/// Saved albums and subscribed artists. The browse-library-albums* and
/// browse-library-artists* fixtures are hand-authored: the surfaces need a signed-in
/// session and the sandbox couldn't reach YTM. They follow ytmusicapi's
/// get_library_contents (sectionListRenderer → itemSectionRenderer → gridRenderer
/// for parse_library_albums, musicShelfRenderer for parse_library_artists) and page
/// the way those functions call get_continuations ("gridContinuation",
/// "musicShelfContinuation"). The album cards are real musicTwoRowItemRenderers from
/// browse-artist.daft-punk.json with library-style subtitles; the artist rows are
/// real rows from search-artists.daft-punk.json, one relinked to the library's
/// "MPLA…" artist view. The mutation payloads mirror rate_playlist,
/// subscribe_artist and unsubscribe_artists. Replace the fixtures with the recorder
/// commands in Fixtures/README.md.
/// </summary>
public class LibraryAlbumsArtistsTests
{
    private static JsonDocument LoadFixture(string name) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)));

    [Fact]
    public void LibraryAlbums_GridOfAlbums_WithTheirPlaylistsAndAToken()
    {
        using var doc = LoadFixture("browse-library-albums.synthetic.json");

        var page = LibraryParsers.ParseLibraryAlbums(doc.RootElement);

        Assert.Equal(5, page.Items.Count);
        var albums = page.Items.Select(i => Assert.IsType<AlbumEntry>(i)).ToList();
        Assert.Equal("Random Access Memories", albums[1].Title);
        Assert.Equal("MPREb_K8qWMWVqXGi", albums[1].BrowseId);
        Assert.Equal("Album • Daft Punk • 2013", albums[1].Subtitle);
        Assert.All(albums, a => Assert.StartsWith("OLAK5uy_", a.AudioPlaylistId));
        Assert.Equal("OLAK5uy_k47QV7r7DblltHRB-1yRp-ZyrKYN4gkt0", albums[0].AudioPlaylistId);
        Assert.NotNull(page.Continuation);
    }

    [Fact]
    public void LibraryAlbums_Continuation_YieldsTheRestAndEnds()
    {
        using var doc = LoadFixture("browse-library-albums-continuation.synthetic.json");

        var page = LibraryParsers.ParseLibraryGridContinuation(doc.RootElement);

        Assert.Equal(3, page.Items.Count);
        Assert.All(page.Items, i => Assert.IsType<AlbumEntry>(i));
        Assert.Equal("GLBTM (Studio Outtakes)", page.Items[0].Title);
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void LibraryArtists_ShelfRows_AreChannels()
    {
        using var doc = LoadFixture("browse-library-artists.synthetic.json");

        var page = LibraryParsers.ParseLibraryArtists(doc.RootElement);

        var artists = page.Items.Select(i => Assert.IsType<ArtistEntry>(i)).ToList();
        Assert.Equal(["Daft Punk", "Thomas Bangalter", "Daft Punk Experience"], artists.Select(a => a.Title));
        Assert.Equal("10.4M subscribers", artists[0].Subtitle);
        Assert.Equal("UCRr1xG_2WIDs18a6cIiCxeA", artists[0].BrowseId);
        // The library's own artist view (MPLA…) resolves to the channel.
        Assert.Equal("UCZ2J4f4052BX-NcUh55AFgg", artists[2].BrowseId);
        Assert.All(artists, a => Assert.False(string.IsNullOrEmpty(a.ThumbnailUrl)));
        Assert.NotNull(page.Continuation);
    }

    [Fact]
    public void LibraryArtists_Continuation_YieldsTheRestAndEnds()
    {
        using var doc = LoadFixture("browse-library-artists-continuation.synthetic.json");

        var page = LibraryParsers.ParseLibraryArtistsContinuation(doc.RootElement);

        Assert.Equal(["Pharrell", "Daft Punk's Karaoke Band"], page.Items.Select(i => i.Title));
        Assert.Null(page.Continuation);
    }

    [Fact]
    public void LibraryArtists_AppendActionsShape_IsReadToo()
    {
        using var doc = JsonDocument.Parse("""
            {"onResponseReceivedActions": [{"appendContinuationItemsAction": {"continuationItems": [
              {"musicResponsiveListItemRenderer": {
                "flexColumns": [{"musicResponsiveListItemFlexColumnRenderer": {"text": {"runs": [{"text": "Justice"}]}}}],
                "navigationEndpoint": {"browseEndpoint": {"browseId": "UCjustice",
                  "browseEndpointContextSupportedConfigs": {"browseEndpointContextMusicConfig": {"pageType": "MUSIC_PAGE_TYPE_ARTIST"}}}}}},
              {"continuationItemRenderer": {"continuationEndpoint": {"continuationCommand": {"token": "MORE"}}}}
            ]}}]}
            """);

        var page = LibraryParsers.ParseLibraryArtistsContinuation(doc.RootElement);

        Assert.Equal("UCjustice", Assert.IsType<ArtistEntry>(Assert.Single(page.Items)).BrowseId);
        Assert.Equal("MORE", page.Continuation?.Value);
    }

    [Theory]
    [InlineData("""{"contents": {}}""")]
    [InlineData("""{"contents": {"singleColumnBrowseResultsRenderer": {"tabs": [{"tabRenderer": {"content": {"sectionListRenderer": {"contents": [{"itemSectionRenderer": {"contents": [{"messageRenderer": {}}]}}]}}}}]}}}""")]
    public void EmptyLibrary_IsEmptyNotThrow(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Empty(LibraryParsers.ParseLibraryAlbums(doc.RootElement).Items);
        Assert.Empty(LibraryParsers.ParseLibraryArtists(doc.RootElement).Items);
    }

    [Fact]
    public void AlbumPage_ReadsItsPlaylistAndSavedState()
    {
        using var doc = LoadFixture("browse-album.ram.json");

        var album = BrowseParsers.ParseAlbum("MPREb_K8qWMWVqXGi", doc.RootElement);

        Assert.Equal("OLAK5uy_kNhM2yaBTOVwrcZJepB1C9P3-n5_Sfy5c", album.AudioPlaylistId);
        Assert.False(album.InLibrary);   // the parser reports the toggle; the client nulls it signed out
    }

    [Theory]
    [InlineData(true, "like/like")]
    [InlineData(false, "like/removelike")]
    public async Task SaveAlbum_RatesItsPlaylist(bool save, string endpoint)
    {
        var (client, handler) = RecordingHandler.Client();

        await client.SetAlbumInLibraryAsync("OLAK5uy_album", save);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, sent.Endpoint);
        var target = sent.Body.GetProperty("target");
        Assert.Equal("OLAK5uy_album", target.GetProperty("playlistId").GetString());
        Assert.False(target.TryGetProperty("videoId", out _));
    }

    [Theory]
    [InlineData("RDCLAK5uy_mellow", true, "like/like")]
    [InlineData("VLRDCLAK5uy_mellow", true, "like/like")]
    [InlineData("RDCLAK5uy_mellow", false, "like/removelike")]
    public async Task SavePlaylist_RatesIt_ByItsIdWithoutVL(string id, bool save, string endpoint)
    {
        var (client, handler) = RecordingHandler.Client();

        await client.SetPlaylistInLibraryAsync(id, save);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, sent.Endpoint);
        var target = sent.Body.GetProperty("target");
        Assert.Equal("RDCLAK5uy_mellow", target.GetProperty("playlistId").GetString());
        Assert.False(target.TryGetProperty("videoId", out _));
    }

    [Theory]
    [InlineData(true, "subscription/subscribe")]
    [InlineData(false, "subscription/unsubscribe")]
    public async Task Subscribe_SendsTheChannel(bool subscribe, string endpoint)
    {
        var (client, handler) = RecordingHandler.Client();

        await client.SetArtistSubscribedAsync("MPLAUCRr1xG_2WIDs18a6cIiCxeA", subscribe);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(endpoint, sent.Endpoint);
        Assert.Equal(["UCRr1xG_2WIDs18a6cIiCxeA"],
            sent.Body.GetProperty("channelIds").EnumerateArray().Select(c => c.GetString()));
    }

    [Fact]
    public async Task SignedOut_LibraryCalls_ThrowWithoutARequest()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);

        await Assert.ThrowsAsync<NotSignedInException>(() => client.GetLibraryAlbumsAsync());
        await Assert.ThrowsAsync<NotSignedInException>(() => client.GetLibraryArtistsAsync());
        await Assert.ThrowsAsync<NotSignedInException>(() => client.SetAlbumInLibraryAsync("OLAK", true));
        await Assert.ThrowsAsync<NotSignedInException>(() => client.SetPlaylistInLibraryAsync("RDCLAK", true));
        await Assert.ThrowsAsync<NotSignedInException>(() => client.SetArtistSubscribedAsync("UC", true));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AlbumPage_SignedOut_SavedStateIsUnknown()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "browse-album.ram.json"));

        var album = await client.GetAlbumAsync("MPREb_K8qWMWVqXGi");

        Assert.Null(album.InLibrary);
        Assert.NotNull(album.AudioPlaylistId);
    }

    [Fact]
    public void ArtistPage_ReadsItsSubscribedState()
    {
        using var doc = LoadFixture("browse-artist.daft-punk.json");

        var artist = BrowseParsers.ParseArtist("UC_kRDKYrUlrbtrSiyu5Tflg", doc.RootElement);

        Assert.False(artist.Subscribed);   // the parser reports the button; the client nulls it signed out
    }

    [Fact]
    public async Task ArtistPage_SignedOut_SubscribedStateIsUnknown()
    {
        var (client, handler) = RecordingHandler.Client(signedIn: false);
        handler.Response = _ => File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "browse-artist.daft-punk.json"));

        var artist = await client.GetArtistAsync("UC_kRDKYrUlrbtrSiyu5Tflg");

        Assert.Null(artist.Subscribed);
    }
}
