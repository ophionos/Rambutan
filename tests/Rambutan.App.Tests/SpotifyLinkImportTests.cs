using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Import;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;
using Rambutan.Integrations.Spotify;

namespace Rambutan.App.Tests;

/// <summary>Importing public lists from their Spotify links, and reading them again.</summary>
public sealed class SpotifyLinkImportTests : IDisposable
{
    private const string RockId = "37i9dQZF1DXcBWIGoYBM5M";
    private const string PrivateId = "0000000000000000000000";
    private const string SlowId = "2222222222222222222222";

    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-spotify-link-");
    private readonly FakeCatalog _catalog = new();
    private readonly List<SpotifyListLink> _read = [];
    private ImportedTrack[] _rock = [Panama];
    private bool _incomplete;

    public void Dispose() => _dir.Delete(recursive: true);

    private sealed class MemoryStore : ILocalPlaylistStore
    {
        public LoadedLocalPlaylists Load() => new([]);
        public void Save(IReadOnlyList<LocalPlaylist> playlists) { }
    }

    private static readonly ImportedTrack Panama = new("Panama", ["Van Halen"]);
    private static readonly ImportedTrack Jump = new("Jump", ["Van Halen"]);

    private Task<IReadOnlyList<TrackInfo>> Search(string query, CancellationToken ct)
    {
        IReadOnlyList<TrackInfo> results = query switch
        {
            "Van Halen Panama" => [Song("Panama", "panama")],
            "Van Halen Jump" => [Song("Jump", "jump")],
            _ => [],
        };
        return Task.FromResult(results);
    }

    private static TrackInfo Song(string title, string id) =>
        new() { VideoId = id, Title = title, Artists = [new ArtistRef("Van Halen")] };

    private readonly TaskCompletionSource _slowStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task<ImportedList> ReadSlow(CancellationToken ct)
    {
        _slowStarted.SetResult();
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    private Task<ImportedList> Read(SpotifyListLink link, CancellationToken ct)
    {
        _read.Add(link);
        return link.Id switch
        {
            SlowId => ReadSlow(ct),
            RockId => Task.FromResult(new ImportedList(ImportedListKind.Playlist, "Rock", _rock, Source: link.Uri,
                Incomplete: _incomplete)),
            PrivateId => throw new SpotifyListUnavailableException("not public"),
            _ => throw new HttpRequestException("offline"),
        };
    }

    private SpotifyImportViewModel Page(bool readsLinks = true) => new(
        new SpotifyImports(_dir.FullName, Search, readsLinks ? Read : null, TimeSpan.Zero),
        _catalog, new LocalPlaylistLibrary(new MemoryStore()), () => true, account: () => "account-me");

    [Fact]
    public async Task Nothing_is_sent_for_text_that_isnt_a_list_link()
    {
        var page = Page();

        page.LinkText = "https://open.spotify.com/track/" + RockId;
        await page.ReadLinksCommand.ExecuteAsync(null);
        Assert.Equal(Localization.Strings.Get("import.problem.noLink"), page.Problem);

        page.LinkText = "https://spotify.link/AbCdEf";
        await page.ReadLinksCommand.ExecuteAsync(null);
        Assert.Equal(Localization.Strings.Get("import.problem.shortLink"), page.Problem);

        Assert.Empty(_read);
        Assert.Equal(ImportPhase.Start, page.Phase);
    }

    [Fact]
    public async Task Each_pasted_link_is_read_once_and_those_that_fail_are_named()
    {
        var page = Page();
        page.LinkText = $"https://open.spotify.com/playlist/{RockId}?si=x\nspotify:playlist:{RockId}, " +
                        $"https://open.spotify.com/playlist/{PrivateId} https://open.spotify.com/album/1111111111111111111111";

        await page.ReadLinksCommand.ExecuteAsync(null);

        Assert.Equal([RockId, PrivateId, "1111111111111111111111"], _read.Select(l => l.Id));
        Assert.Equal(ImportPhase.Ready, page.Phase);
        Assert.Equal("Rock", Assert.Single(page.Lists).DisplayName);
        Assert.Equal("", page.LinkText);
        Assert.Contains("open.spotify.com/playlist/" + PrivateId, page.Problem);
        Assert.Contains("offline", page.Problem);
        Assert.Equal(Localization.Strings.Get("import.chooseFile"), page.ChooseFileText);
    }

    [Fact]
    public async Task When_no_link_can_be_read_the_page_stays_where_it_was()
    {
        var page = Page();
        page.LinkText = "https://open.spotify.com/playlist/" + PrivateId;

        await page.ReadLinksCommand.ExecuteAsync(null);

        Assert.Equal(ImportPhase.Start, page.Phase);
        Assert.Empty(page.Lists);
        Assert.NotNull(page.Problem);
        Assert.Equal("https://open.spotify.com/playlist/" + PrivateId, page.LinkText);
    }

    [Fact]
    public async Task Cancelling_keeps_the_lists_read_before_it()
    {
        var page = Page();
        page.LinkText = $"spotify:playlist:{RockId} spotify:playlist:{SlowId} spotify:album:{PrivateId}";

        var reading = page.ReadLinksCommand.ExecuteAsync(null);
        await _slowStarted.Task;
        Assert.True(page.IsReadingLinks);
        Assert.Equal(Localization.Strings.Format("import.link.reading", ("done", 2), ("total", 3)), page.LinkProgress);
        page.CancelCommand.Execute(null);
        await reading;

        Assert.Equal([RockId, SlowId], _read.Select(l => l.Id));
        Assert.False(page.IsReadingLinks);
        Assert.Equal(ImportPhase.Ready, page.Phase);
        Assert.Equal("Rock", Assert.Single(page.Lists).DisplayName);
        Assert.Equal(Localization.Strings.Get("import.link.cancelled"), page.Result);
        Assert.Null(page.Problem);
    }

    [Fact]
    public async Task Cancelling_before_any_list_is_read_returns_to_the_start()
    {
        var page = Page();
        page.LinkText = "spotify:playlist:" + SlowId;

        var reading = page.ReadLinksCommand.ExecuteAsync(null);
        await _slowStarted.Task;
        page.CancelCommand.Execute(null);
        await reading;

        Assert.Equal(ImportPhase.Start, page.Phase);
        Assert.Empty(page.Lists);
        Assert.Null(page.Problem);
        Assert.Equal("spotify:playlist:" + SlowId, page.LinkText);
    }

    [Fact]
    public async Task An_incomplete_list_says_so()
    {
        _incomplete = true;
        var page = Page();
        page.LinkText = "spotify:playlist:" + RockId;

        await page.ReadLinksCommand.ExecuteAsync(null);

        Assert.Contains(Localization.Strings.Format("import.list.incomplete", ("count", 1)), page.Lists[0].Summary);
    }

    [Fact]
    public async Task A_link_imported_before_is_offered_again_and_adds_only_what_is_new()
    {
        var first = Page();
        first.LinkText = "spotify:playlist:" + RockId;
        await first.ReadLinksCommand.ExecuteAsync(null);
        first.Lists[0].IsChosen = true;
        await first.FindSongsCommand.ExecuteAsync(null);
        await first.CreateCommand.ExecuteAsync(null);
        Assert.Equal("spotify:playlist:" + RockId, Assert.Single(new SpotifyImportRecordStore(_dir.FullName).Load()).Source);
        _catalog.Added.Clear();

        _rock = [Panama, Jump];
        var again = Page();
        again.Shown();
        await WaitFor(() => again.HasEarlierLinks);
        var earlier = Assert.Single(again.EarlierLinks);
        Assert.Equal("Rock", earlier.Name);

        await earlier.ReadAgainCommand.ExecuteAsync(null);
        Assert.True(again.Lists[0].HasEarlier);
        again.Lists[0].IsChosen = true;
        await again.FindSongsCommand.ExecuteAsync(null);
        await again.CreateCommand.ExecuteAsync(null);

        Assert.Single(_catalog.CreatedPlaylists);
        var added = Assert.Single(_catalog.Added);
        Assert.Equal(["jump"], added.VideoIds);
    }

    [Fact]
    public void Without_a_reader_the_page_offers_no_links()
    {
        var page = Page(readsLinks: false);

        Assert.False(page.CanReadLinks);
        Assert.False(page.HasEarlierLinks);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }
}
