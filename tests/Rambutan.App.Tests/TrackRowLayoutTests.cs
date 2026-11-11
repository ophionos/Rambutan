using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>Track lists whose columns sit at fixed positions: on a wide list the
/// rows end at one edge, well before the list's, and the album page leaves out
/// the artist line that only repeats the album's artist.</summary>
public sealed class TrackRowLayoutTests : IDisposable
{
    private readonly FakeCatalog _catalog = new();
    private readonly QueueModel _queue = new();
    private readonly PlaybackCoordinator _coordinator;

    public TrackRowLayoutTests() => _coordinator = FakePlayback.Coordinator(_queue);

    public void Dispose() => _coordinator.Dispose();

    private static readonly ArtistRef DaftPunk = new("Daft Punk", "UCdaft");

    // Durations of one length: the headless font draws every glyph a full em
    // wide, so the duration column is wider than the app's, but the same on every row.
    private static TrackInfo Track(string id, string title, int seconds, params ArtistRef[] artists) => new()
    {
        VideoId = id, Title = title, Artists = artists, Duration = TimeSpan.FromSeconds(seconds),
        Album = "Discovery", AlbumBrowseId = "MPREb_disc",
    };

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Window Show(Control content, double width)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var window = new Window { Width = width, Height = 700, DataContext = new MainViewModel(), Content = content };
        window.Show();
        Settle(window);
        return window;
    }

    private (Window Window, ListBox List) AlbumPage(double width, params TrackInfo[] tracks)
    {
        _catalog.Album = new AlbumPage
        {
            BrowseId = "MPREb_disc", Title = "Discovery", Artist = "Daft Punk", ArtistBrowseId = "UCdaft",
            Tracks = tracks,
        };
        var page = new AlbumPageViewModel(_catalog, null!, "MPREb_disc");
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.AlbumPage { DataContext = page };
        var window = Show(view, width);
        return (window, view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList"));
    }

    private static TextBlock DurationOf(Control row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .Single(t => t.Text is { } text && System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+:\d\d$"));

    private static double RightEdge(Control child, Visual list) =>
        child.TranslatePoint(new Point(child.Bounds.Width, 0), list)!.Value.X;

    private static double LeftEdge(Control child, Visual list) =>
        child.TranslatePoint(default, list)!.Value.X;

    [Fact]
    public void OnAWideAlbumPage_EveryDurationSharesOneEdge_FarFromTheListsEdge() => Headless.Run(() =>
    {
        var (window, list) = AlbumPage(1800,
            Track("a", "One More Time", 320, DaftPunk),
            Track("b", "Aerodynamic", 212, DaftPunk),
            Track("c", "Digital Love, the long version with a much longer title", 301, DaftPunk));

        var edges = Enumerable.Range(0, 3).Select(i => RightEdge(DurationOf(list.ContainerFromIndex(i)!), list)).ToArray();

        Assert.All(edges, e => Assert.Equal(edges[0], e, 0.5));
        // The row ends where the rule says, not at the far edge of a wide list.
        Assert.True(TrackColumns.GetShowsLiked(list));
        var rowWidth = TrackColumns.GetRowWidth(list);
        Assert.Equal(208 + 520 + 24, rowWidth, 0.5);
        Assert.True(edges[0] < list.Bounds.Width / 2, $"duration at {edges[0]} of {list.Bounds.Width}");
        window.Close();
    });

    [Fact]
    public void AnAlbumPage_HasNoAlbumColumn_AndItsRowsDoNotNameTheAlbum() => Headless.Run(() =>
    {
        var (window, list) = AlbumPage(1800, Track("a", "One More Time", 320, DaftPunk));

        Assert.False(TrackColumns.IsOn(list));
        Assert.False(TrackColumns.GetShowsAlbum(list));
        Assert.Equal("One More Time, Daft Punk", AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        window.Close();
    });

    private static TextBlock[] ArtistLines(Control row) =>
        row.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("rowmeta") && t.IsEffectivelyVisible && t.Text is { Length: > 0 } text
                        && !System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+:\d\d$"))
            .ToArray();

    [Fact]
    public void AlbumRows_ByTheAlbumsArtist_LeaveOutTheirArtistLine_AndAreShorter() => Headless.Run(() =>
    {
        var (window, list) = AlbumPage(1200,
            Track("a", "One More Time", 320, DaftPunk),
            Track("b", "Aerodynamic", 212, new ArtistRef("daft punk")),
            Track("c", "Get Lucky", 369, DaftPunk, new ArtistRef("Pharrell Williams", "UCpharrell")));

        var plain = list.ContainerFromIndex(0)!;
        var byName = list.ContainerFromIndex(1)!;
        var feature = list.ContainerFromIndex(2)!;

        Assert.Empty(ArtistLines(plain));
        Assert.Empty(ArtistLines(byName));
        Assert.Equal(["Daft Punk, Pharrell Williams"], ArtistLines(feature).Select(t => t.Text));
        Assert.True(plain.Bounds.Height < feature.Bounds.Height,
            $"{plain.Bounds.Height} not shorter than {feature.Bounds.Height}");
        // The spoken name still says who.
        Assert.Equal("One More Time, Daft Punk", AutomationProperties.GetName(plain));
        window.Close();
    });

    [Fact]
    public void AnAlbumWithoutAnArtist_KeepsEveryArtistLine() => Headless.Run(() =>
    {
        _catalog.Album = new AlbumPage
        {
            BrowseId = "MPREb_va", Title = "Various", Tracks = [Track("a", "One More Time", 320, DaftPunk)],
        };
        var page = new AlbumPageViewModel(_catalog, null!, "MPREb_va");
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.AlbumPage { DataContext = page };
        var window = Show(view, 1200);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList");

        Assert.Equal(["Daft Punk"], ArtistLines(list.ContainerFromIndex(0)!).Select(t => t.Text));
        window.Close();
    });

    private static SongEntry Song(string id, string title) => new()
    {
        Title = title,
        Track = new TrackInfo
        {
            VideoId = id, Title = title, Artists = [new ArtistRef("Artist")], Duration = TimeSpan.FromSeconds(200),
            Album = "Album " + id, AlbumBrowseId = "MPREb_" + id,
        },
    };

    [Fact]
    public void OnAWideSongsList_TheAlbumAndDurationColumnsLineUp_AndTheRowEnds() => Headless.Run(() =>
    {
        var library = new LibraryPageViewModel(_catalog, null!, null!, _coordinator);
        library.SelectedSection = library.Section(LibrarySectionKind.Songs);
        var loading = library.Songs.RefreshAsync();
        _catalog.LibraryCalls.Last().Complete(null,
            [Song("1", "Short"), Song("2", "A considerably longer song title than the first"), Song("3", "Mid title")]);
        Dispatcher.UIThread.RunJobs();
        Assert.True(loading.IsCompletedSuccessfully);

        var view = new Rambutan.App.Views.LibraryPage { DataContext = library };
        var window = Show(view, 1720);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SongList");
        var rows = Enumerable.Range(0, 3).Select(i => list.ContainerFromIndex(i)!).ToArray();

        var albums = rows.Select(r => LeftEdge(r.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("rowalbum")), list)).ToArray();
        var durations = rows.Select(r => RightEdge(DurationOf(r), list)).ToArray();

        Assert.All(albums, x => Assert.Equal(albums[0], x, 0.5));
        Assert.All(durations, x => Assert.Equal(durations[0], x, 0.5));
        Assert.True(TrackColumns.GetShowsLiked(list));
        Assert.Equal(Rambutan.Core.Layout.TrackRowColumns.MaxAlbum, TrackColumns.GetAlbumWidth(list));
        Assert.Equal(234 + 520 + 24 + 16 + 320, TrackColumns.GetRowWidth(list), 0.5);
        Assert.True(durations[0] < list.Bounds.Width - 400, $"duration at {durations[0]} of {list.Bounds.Width}");
        window.Close();
    });

    [Fact]
    public void OnAWideArtistPage_TopSongsLineUpTheirAlbumAndDuration_AndTheRowEnds() => Headless.Run(() =>
    {
        _catalog.Artist = new ArtistPage
        {
            BrowseId = "UCdaft", Name = "Daft Punk",
            TopSongs =
            [
                Track("a", "One", 320, DaftPunk),
                Track("b", "A much longer top song title than the one above", 212, DaftPunk),
            ],
            Shelves = [],
        };
        var vm = new ArtistPageViewModel(_catalog, _coordinator, null!, "UCdaft");
        vm.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.ArtistPage { DataContext = vm };
        var window = Show(view, 1800);
        var list = view.GetVisualDescendants().OfType<ItemsControl>()
            .Single(i => AutomationProperties.GetName(i) == "Top songs");
        var rows = list.GetVisualDescendants().OfType<Grid>().Where(g => TrackMenu.GetTrack(g) is not null).ToArray();
        Assert.Equal(2, rows.Length);

        var albums = rows.Select(r => r.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("rowalbum"))).ToArray();
        Assert.All(albums, a => Assert.True(a.IsEffectivelyVisible));
        Assert.Equal(LeftEdge(albums[0], list), LeftEdge(albums[1], list), 0.5);
        Assert.Equal(RightEdge(DurationOf(rows[0]), list), RightEdge(DurationOf(rows[1]), list), 0.5);
        Assert.Equal(234 + 520 + 24 + 16 + 320, TrackColumns.GetRowWidth(list), 0.5);
        Assert.Equal(TrackColumns.GetRowWidth(list), rows[0].Bounds.Width, 0.5);
        window.Close();
    });

    [Fact]
    public void TheLikedMark_ShowsOnlyForALike_AndFollowsThePresses() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var ratings = new TrackRatings(_catalog);
        var liked = new TrackInfo { VideoId = "l", Title = "L", Artists = [], Rating = Rating.Like };
        var unknown = new TrackInfo { VideoId = "u", Title = "U", Artists = [] };
        var likedMark = new LikedMark { Ratings = ratings, Track = liked };
        var unknownMark = new LikedMark { Ratings = ratings, Track = unknown };
        var window = new Window { Content = new StackPanel { Children = { likedMark, unknownMark } } };
        window.Show();
        Settle(window);

        Assert.True(likedMark.IsVisible);
        Assert.False(unknownMark.IsVisible);
        Assert.False(likedMark.Focusable);

        ratings.ToggleAsync(unknown, Rating.Like).GetAwaiter().GetResult();
        Assert.True(unknownMark.IsVisible);
        ratings.ToggleAsync(liked, Rating.Like).GetAwaiter().GetResult();
        Assert.False(likedMark.IsVisible);
        window.Close();
    });
}
