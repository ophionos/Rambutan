using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>A track list's album column: it shows while the title keeps its room,
/// links the album where YouTube Music gave one, and joins the row's spoken name
/// in the lists that have it.</summary>
public class TrackColumnsTests
{
    /// <summary>The playlist page's fixed parts (its template's FixedWidth).</summary>
    private const double PlaylistFixed = 318;

    /// <summary>The playlist list's padding (pagescroll: 12 on the right).</summary>
    private const double PlaylistPadding = 12;

    // No durations: the headless font draws every glyph a full em wide, so "5:20"
    // would be wider than the duration's 40 the app's font keeps it within.
    private static TrackInfo WithAlbum => new()
    {
        VideoId = "omt", Title = "One More Time", Artists = [new ArtistRef("Daft Punk", "UCdaft")],
        Album = "Discovery", AlbumBrowseId = "MPREb_disc",
    };

    private static TrackInfo WithoutAlbum => new()
    {
        VideoId = "single", Title = "A Single", Artists = [new ArtistRef("Daft Punk", "UCdaft")],
    };

    private static void Settle(Window window)
    {
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static Window Show(Control content, double width)
    {
        var window = new Window { Width = width, Height = 700, DataContext = new MainViewModel(), Content = content };
        window.Show();
        Settle(window);
        return window;
    }

    private static void Resize(Window window, double width)
    {
        window.Width = width;
        Settle(window);
    }

    private static Panel AlbumCell(Control row) =>
        row.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("rowalbum"));

    private static StackPanel TitleBlock(Control row) =>
        row.GetVisualDescendants().OfType<StackPanel>()
            .First(p => p.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("rowtitle")));

    [Fact]
    public void AListWithTheColumn_ShowsItOnlyWhenWide_AndFollowsAResize() => Headless.Run(() =>
    {
        var list = new ListBox { ItemsSource = new[] { "a" } };
        TrackColumns.SetFixedWidth(list, PlaylistFixed);
        var window = Show(list, 600);

        Assert.Equal(0, TrackColumns.GetAlbumWidth(list));
        Assert.False(TrackColumns.GetShowsAlbum(list));

        Resize(window, 1400);
        Assert.Equal(TrackRowColumns.MaxAlbum, TrackColumns.GetAlbumWidth(list));
        Assert.True(TrackColumns.GetShowsAlbum(list));

        Resize(window, 600);
        Assert.Equal(0, TrackColumns.GetAlbumWidth(list));
        Assert.False(TrackColumns.GetShowsAlbum(list));
        window.Close();
    });

    [Fact]
    public void AListWithoutTheColumn_NeverShowsIt() => Headless.Run(() =>
    {
        var list = new ListBox { ItemsSource = new[] { "a" } };
        var window = Show(list, 1400);

        Assert.Equal(0, TrackColumns.GetAlbumWidth(list));
        Assert.False(TrackColumns.GetShowsAlbum(list));
        window.Close();
    });

    private static (Window Window, ListBox List) PlaylistPage(double width)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Playlist = new PlaylistPage { PlaylistId = "PL1", Title = "Road trip", Tracks = [WithAlbum, WithoutAlbum] },
        };
        var page = new PlaylistPageViewModel(catalog, null!, null!, null!, "PL1");
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.PlaylistPage { DataContext = page };
        var window = Show(view, width);
        return (window, view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "TrackList"));
    }

    [Fact]
    public void APlaylistRow_LinksItsAlbum_WhenTheListIsWide() => Headless.Run(() =>
    {
        var (window, list) = PlaylistPage(1400);
        var first = list.ContainerFromIndex(0)!;
        var second = list.ContainerFromIndex(1)!;

        var cell = AlbumCell(first);
        Assert.True(cell.IsEffectivelyVisible);
        Assert.Equal(TrackRowColumns.MaxAlbum, cell.Bounds.Width);
        var link = cell.GetVisualDescendants().OfType<Button>().Single();
        Assert.True(link.IsEffectivelyVisible);
        Assert.False(link.IsTabStop);
        Assert.Equal("Discovery", link.GetVisualDescendants().OfType<TextBlock>().Single().Text);
        Assert.Same(list.Items[0], link.CommandParameter);
        Assert.Equal("Go to album", ToolTip.GetTip(link));

        Assert.False(AlbumCell(second).IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void APlaylistRow_DropsTheAlbum_WhenNarrow_AndTheTitleKeepsItsRoom() => Headless.Run(() =>
    {
        var (window, list) = PlaylistPage(700);
        var first = list.ContainerFromIndex(0)!;

        Assert.False(AlbumCell(first).IsVisible);
        // Without the column the row is as it was: the title takes all that the
        // number, cover, actions and duration leave.
        var grid = (Grid)TitleBlock(first).Parent!;
        Assert.Equal(grid.Bounds.Width - 40 - 52 - 138 - 56, TitleBlock(first).Bounds.Width, 0.5);
        window.Close();
    });

    [Fact]
    public void JustWideEnoughForTheColumn_TheTitleStillHasItsMinimum() => Headless.Run(() =>
    {
        // The first width that shows the column, on the list's own rows.
        var width = PlaylistFixed + TrackRowColumns.MinTitle + TrackRowColumns.MinAlbum + PlaylistPadding;
        var (window, list) = PlaylistPage(width);
        var first = list.ContainerFromIndex(0)!;

        Assert.True(AlbumCell(first).IsVisible);
        Assert.Equal(TrackRowColumns.MinAlbum, AlbumCell(first).Bounds.Width);
        Assert.True(TitleBlock(first).Bounds.Width >= TrackRowColumns.MinTitle - 0.5,
            $"title {TitleBlock(first).Bounds.Width}");
        window.Close();
    });

    [Fact]
    public void APlaylistRow_NamesItsAlbum_AtAnyWidth() => Headless.Run(() =>
    {
        var (window, list) = PlaylistPage(700);

        Assert.Equal("One More Time, Daft Punk, Discovery", AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        Assert.Equal("A Single, Daft Punk", AutomationProperties.GetName(list.ContainerFromIndex(1)!));

        Resize(window, 1400);
        Assert.Equal("One More Time, Daft Punk, Discovery", AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        window.Close();
    });

    [Fact]
    public void AnAlbumPageRow_DoesNotNameTheAlbum() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var catalog = new FakeCatalog
        {
            Album = new AlbumPage
            {
                BrowseId = "MPREb_disc", Title = "Discovery", Artist = "Daft Punk", ArtistBrowseId = "UCdaft",
                Tracks = [WithAlbum],
            },
        };
        var page = new AlbumPageViewModel(catalog, null!, "MPREb_disc");
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Rambutan.App.Views.AlbumPage { DataContext = page };
        var window = Show(view, 1400);

        var list = view.GetVisualDescendants().OfType<ListBox>().Single();
        Assert.Equal("One More Time, Daft Punk", AutomationProperties.GetName(list.ContainerFromIndex(0)!));
        window.Close();
    });

    private static (Window Window, ListBox List) SearchShelf(double width)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var file = new TrackInfo
        {
            VideoId = "local:1", Title = "Da Funk", Artists = [new ArtistRef("Daft Punk")],
            Album = "Homework", Duration = TimeSpan.FromSeconds(330),
        };
        var vm = new SearchPageViewModel();
        vm.LocalResults.Add(new LocalTrackRow(null, file));
        vm.HasLocalResults = true;
        var view = new Rambutan.App.Views.SearchPage { DataContext = vm };
        var window = Show(view, width);
        var list = view.GetVisualDescendants().OfType<ListBox>()
            .Single(l => AutomationProperties.GetName(l) == Rambutan.App.Localization.Strings.Get("search.local.listName"));
        return (window, list);
    }

    private static string?[] SecondLine(Control row) =>
        TitleBlock(row).GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.Classes.Contains("rowmeta") && t.IsEffectivelyVisible)
            .Select(t => t.Text).ToArray();

    [Fact]
    public void ALocalFile_ShowsItsAlbumAsText_AndItsSecondLineNamesItOnlyWithoutTheColumn() => Headless.Run(() =>
    {
        var (window, list) = SearchShelf(1400);
        var row = list.ContainerFromIndex(0)!;

        var cell = AlbumCell(row);
        Assert.True(cell.IsEffectivelyVisible);
        Assert.DoesNotContain(cell.GetVisualDescendants().OfType<Button>(), b => b.IsEffectivelyVisible);
        Assert.Contains(cell.GetVisualDescendants().OfType<TextBlock>(),
            t => t.IsEffectivelyVisible && t.Text == "Homework");
        Assert.Equal(["Daft Punk"], SecondLine(row));
        Assert.Equal("Da Funk, Daft Punk, Homework", AutomationProperties.GetName(row));

        Resize(window, 700);
        Assert.False(AlbumCell(row).IsVisible);
        Assert.Equal(["Daft Punk · Homework"], SecondLine(row));
        Assert.Equal("Da Funk, Daft Punk, Homework", AutomationProperties.GetName(row));
        window.Close();
    });
}
