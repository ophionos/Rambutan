using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

/// <summary>
/// Library's Songs and History tabs are lists that scroll themselves, so only the
/// rows in view are built however many the account has. History is one list of
/// its headings and rows, the headings neither selectable nor played.
/// </summary>
public sealed class LibraryListsTests : IDisposable
{
    private readonly FakeCatalog _catalog = new();
    private readonly QueueModel _queue = new();
    private readonly PlaybackCoordinator _coordinator;

    public LibraryListsTests() => _coordinator = FakePlayback.Coordinator(_queue);

    public void Dispose() => _coordinator.Dispose();

    private static SongEntry Song(int i) => new()
    {
        Title = "Song " + i,
        Track = new TrackInfo
        {
            VideoId = "v" + i, Title = "Song " + i, Artists = [new ArtistRef("Artist")],
            Album = "Album", AlbumBrowseId = "MPREb_" + i,
        },
    };

    private static HistoryEntry Played(string id, string played) => new()
    {
        Title = "Title " + id,
        Track = new TrackInfo { VideoId = id, Title = "Title " + id, Artists = [], Album = "Album" },
        Played = played,
        RemoveToken = "T_" + id,
    };

    private static void Settle(Window window)
    {
        for (var i = 0; i < 3; i++)
        {
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private LibraryPageViewModel Library(LibrarySectionKind kind)
    {
        var library = new LibraryPageViewModel(_catalog, null!, null!, _coordinator);
        library.SelectedSection = library.Section(kind);
        return library;
    }

    /// <summary>Answers the section's first page; the page's continuation, if any, is "next".</summary>
    private void Load(LibrarySection section, string? next, IReadOnlyList<CatalogEntry> entries)
    {
        var loading = section.RefreshAsync();
        _catalog.LibraryCalls.Last().Complete(next, entries);
        Dispatcher.UIThread.RunJobs();
        Assert.True(loading.IsCompletedSuccessfully);
    }

    private static (Window Window, ListBox List) Show(LibraryPageViewModel library, string listName, double width = 1000)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var view = new Rambutan.App.Views.LibraryPage { DataContext = library };
        var window = new Window { Width = width, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        Settle(window);
        var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == listName);
        return (window, list);
    }

    private static ScrollViewer ScrollerOf(ListBox list) =>
        Assert.IsType<ScrollViewer>(list.Scroll);

    [Fact]
    public void Songs_BuildsOnlyTheRowsInView() => Headless.Run(() =>
    {
        var library = Library(LibrarySectionKind.Songs);
        Load(library.Songs, null, [.. Enumerable.Range(0, 2000).Select(Song)]);

        var (window, list) = Show(library, "SongList");

        Assert.True(list.IsEffectivelyVisible);
        var realized = list.GetRealizedContainers().Count();
        Assert.InRange(realized, 1, 99);
        Assert.Equal("Song 0", AutomationProperties.GetName(list.ContainerFromIndex(0)!)?.Split(',')[0]);
        window.Close();
    });

    [Fact]
    public void Songs_ShowTheAlbumColumn_WhenWide() => Headless.Run(() =>
    {
        var library = Library(LibrarySectionKind.Songs);
        Load(library.Songs, null, [Song(1)]);

        var (window, list) = Show(library, "SongList", width: 1400);

        Assert.True(TrackColumns.GetShowsAlbum(list));
        var cell = list.ContainerFromIndex(0)!.GetVisualDescendants().OfType<Panel>().Single(p => p.Classes.Contains("rowalbum"));
        Assert.True(cell.IsEffectivelyVisible);
        Assert.Equal(TrackRowColumns.MaxAlbum, cell.Bounds.Width);
        window.Close();
    });

    [Fact]
    public void Songs_ScrollingTheListToItsEnd_AsksForTheNextPage() => Headless.Run(() =>
    {
        var library = Library(LibrarySectionKind.Songs);
        // Long enough to scroll, so the first layout alone isn't near the end.
        Load(library.Songs, "next", [.. Enumerable.Range(0, 200).Select(Song)]);
        var (window, list) = Show(library, "SongList");
        Assert.DoesNotContain(_catalog.LibraryCalls, c => c.What == "songs" && c.Token is not null);

        var scroll = ScrollerOf(list);
        scroll.Offset = new Vector(0, scroll.Extent.Height - scroll.Viewport.Height);
        Settle(window);

        var more = Assert.Single(_catalog.LibraryCalls, c => c.What == "songs" && c.Token is not null);
        Assert.Equal("next", more.Token!.Value);
        window.Close();
    });

    private LibraryPageViewModel HistoryOf(int groups, int perGroup)
    {
        var library = Library(LibrarySectionKind.History);
        var entries = Enumerable.Range(0, groups)
            .SelectMany(g => Enumerable.Range(0, perGroup).Select(i => Played($"g{g}r{i}", "Heading " + g)))
            .ToArray();
        Load(library.History, null, entries);
        return library;
    }

    private static string Shape(object? row) => row switch
    {
        HistoryHeading heading => "# " + heading.Played,
        HistoryEntry entry => entry.Track.VideoId,
        _ => "?",
    };

    [Fact]
    public void History_IsOneListOfHeadingsAndRows_BuildingOnlyThoseInView() => Headless.Run(() =>
    {
        var library = HistoryOf(groups: 20, perGroup: 100);

        var (window, list) = Show(library, "HistoryList");

        Assert.True(list.IsEffectivelyVisible);
        Assert.Equal(2020, list.ItemCount);
        Assert.InRange(list.GetRealizedContainers().Count(), 1, 99);
        Assert.Equal(["# Heading 0", "g0r0", "g0r1"], list.Items.Take(3).Select(Shape));
        Assert.Equal(["g0r99", "# Heading 1", "g1r0"], list.Items.Skip(100).Take(3).Select(Shape));
        window.Close();
    });

    [Fact]
    public void History_AHeadingIsSpokenAsAHeading_AndIsNoRow() => Headless.Run(() =>
    {
        var library = HistoryOf(groups: 2, perGroup: 2);
        var (window, list) = Show(library, "HistoryList", width: 1400);

        var heading = list.ContainerFromIndex(0)!;
        Assert.Equal(2, AutomationProperties.GetHeadingLevel(heading));
        Assert.Equal("Heading 0", AutomationProperties.GetName(heading));
        Assert.False(heading.Focusable);
        Assert.False(heading.IsHitTestVisible);

        var row = list.ContainerFromIndex(1)!;
        Assert.Equal(0, AutomationProperties.GetHeadingLevel(row));
        Assert.True(row.Focusable);
        Assert.True(row.IsHitTestVisible);
        Assert.StartsWith("Title g0r0", AutomationProperties.GetName(row));
        // The album column, as on the Songs tab.
        Assert.True(TrackColumns.GetShowsAlbum(list));

        list.SelectedIndex = 0;
        Assert.NotEqual(0, list.SelectedIndex);
        Assert.IsType<HistoryEntry>(list.SelectedItem);
        window.Close();
    });

    [Fact]
    public void History_ArrowKeysPassOverAHeading() => Headless.Run(() =>
    {
        var library = HistoryOf(groups: 2, perGroup: 2);
        var (window, list) = Show(library, "HistoryList");

        // The last row of the first group.
        list.SelectedIndex = 2;
        list.ContainerFromIndex(2)!.Focus();

        Press(Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("g1r0", Shape(list.SelectedItem));
        Press(Key.Down, PhysicalKey.ArrowDown);
        Assert.Equal("g1r1", Shape(list.SelectedItem));
        Press(Key.Up, PhysicalKey.ArrowUp);
        Press(Key.Up, PhysicalKey.ArrowUp);
        Assert.Equal("g0r1", Shape(list.SelectedItem));
        Press(Key.Home, PhysicalKey.Home);
        Assert.Equal("g0r0", Shape(list.SelectedItem));
        Press(Key.End, PhysicalKey.End);
        Assert.Equal("g1r1", Shape(list.SelectedItem));
        window.Close();

        // Focus follows the selection, so the next key starts from the row selected.
        void Press(Key key, PhysicalKey physical)
        {
            window.KeyPress(key, RawInputModifiers.None, physical, null);
            Settle(window);
            Assert.Same(list.ContainerFromIndex(list.SelectedIndex), window.FocusManager!.GetFocusedElement());
        }
    });

    [Fact]
    public void History_DoubleClickingAHeading_PlaysNothing() => Headless.Run(() =>
    {
        var library = HistoryOf(groups: 2, perGroup: 2);
        var (window, list) = Show(library, "HistoryList");

        Point At(int index)
        {
            var container = list.ContainerFromIndex(index)!;
            return container.TranslatePoint(new Point(20, container.Bounds.Height / 2), window)!.Value;
        }

        void DoubleClick(Point at)
        {
            for (var i = 0; i < 2; i++)
            {
                window.MouseDown(at, MouseButton.Left, RawInputModifiers.None);
                window.MouseUp(at, MouseButton.Left, RawInputModifiers.None);
            }
            Settle(window);
        }

        // A throwaway pair pays the first click's cost (ItemActivationTests), then a
        // row's double-click plays, so the heading's below is a real double-click.
        DoubleClick(At(5));
        _queue.SetQueue([]);
        list.SelectedIndex = -1;
        DoubleClick(At(4));
        Assert.Equal("g1r0", _queue.Current?.VideoId);
        _queue.SetQueue([]);

        DoubleClick(At(3));

        Assert.Empty(_queue.Items);
        Assert.Equal("g1r0", Shape(list.SelectedItem));
        window.Close();
    });

    [Fact]
    public void History_ARowPlaysWithTheWholeHistory_OnEnter() => Headless.Run(() =>
    {
        var library = HistoryOf(groups: 2, perGroup: 2);
        var (window, list) = Show(library, "HistoryList");

        list.SelectedIndex = 4;
        list.ContainerFromIndex(4)!.Focus();
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, "\r");
        Settle(window);

        Assert.Equal(["g0r0", "g0r1", "g1r0", "g1r1"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal("g1r0", _queue.Current?.VideoId);
        window.Close();
    });

    [Fact]
    public void History_RemovingARow_KeepsTheListWhereItWas() => Headless.Run(() =>
    {
        var library = HistoryOf(groups: 20, perGroup: 100);
        var (window, list) = Show(library, "HistoryList");
        var scroll = ScrollerOf(list);
        scroll.Offset = new Vector(0, 50_000);
        Settle(window);
        var before = scroll.Offset.Y;
        Assert.True(before > 10_000, $"offset {before}");

        library.History.RemoveEntry(library.History.Items[1500]);
        Settle(window);

        Assert.Equal(2019, list.ItemCount);
        Assert.InRange(scroll.Offset.Y, before - 100, before + 100);
        window.Close();
    });

    [Fact]
    public void Flattening_PutsAHeadingWhereverThePlayedHeadingChanges()
    {
        HistoryEntry[] entries =
        [
            Played("a", "Today"), Played("b", "Today"), Played("c", "Yesterday"), Played("d", "Today"),
        ];

        var rows = LibrarySection.HistoryRowsOf(entries);

        Assert.Equal(["# Today", "a", "b", "# Yesterday", "c", "# Today", "d"], rows.Select(Shape));
        Assert.Same(entries[2], rows[4]);
    }

    [Fact]
    public void Flattening_NoEntries_IsNoRows() =>
        Assert.Empty(LibrarySection.HistoryRowsOf([]));
}
