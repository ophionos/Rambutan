using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Rambutan.Core.Stations;

namespace Rambutan.App.Tests;

/// <summary>Every page hero has one labelled accent primary, at most three
/// icon-only circles, and the rest of its actions in the More menu.</summary>
public class HeroActionsTests
{
    /// <summary>Stands for the download button, whose name and tooltip follow the
    /// collection's state and so differ from each other.</summary>
    private const string Download = "<download>";

    private sealed record Hero(string Primary, string[] Circles, string[] Menu);

    private static string T(string key) => Strings.Get(key);

    private static Window Show(Control view)
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        var window = new Window { Width = 1000, Height = 700, DataContext = new MainViewModel(), Content = view };
        window.Show();
        window.UpdateLayout();
        return window;
    }

    private static ActionRow RowOf(Control view) =>
        view.GetLogicalDescendants().OfType<ActionRow>().First();

    private static bool IsDownload(Button button) => button.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Arc>().Any();

    private static string NameOf(Button button) => IsDownload(button) ? Download : AutomationProperties.GetName(button) ?? string.Empty;

    private static bool HasLabel(Button button) =>
        button.GetLogicalDescendants().OfType<TextBlock>().Any(t => !string.IsNullOrEmpty(t.Text));

    private static void AssertPattern(Control view, Hero expected)
    {
        var buttons = RowOf(view).Items.OfType<Button>().ToList();
        Assert.DoesNotContain(buttons, b => b.Classes.Contains("cardplay"));

        var primary = Assert.Single(buttons, ActionRow.GetKeep);
        Assert.Contains("accent", primary.Classes);
        Assert.Contains("heroprimary", primary.Classes);
        Assert.True(HasLabel(primary));
        Assert.Contains(primary.GetLogicalDescendants().OfType<TextBlock>(), t => t.Text == expected.Primary);
        Assert.NotEmpty(primary.GetLogicalDescendants().OfType<PathIcon>());
        Assert.Equal(expected.Primary, AutomationProperties.GetName(primary));
        Assert.Equal(expected.Primary, ToolTip.GetTip(primary));

        var circles = buttons.Where(b => !ActionRow.GetKeep(b) && !ActionRow.GetInMenu(b)).ToList();
        Assert.True(circles.Count <= 3, $"{circles.Count} circles");
        Assert.Equal(expected.Circles, circles.Select(NameOf));
        foreach (var circle in circles)
        {
            Assert.Contains("icon", circle.Classes);
            Assert.Contains("outline", circle.Classes);
            Assert.False(HasLabel(circle));
            if (IsDownload(circle))
                continue;
            Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(circle)));
            Assert.Equal(AutomationProperties.GetName(circle), ToolTip.GetTip(circle));
        }

        var menu = buttons.Where(b => ActionRow.GetInMenu(b)).ToList();
        Assert.Equal(expected.Menu, menu.Select(NameOf));
        Assert.All(menu, b => Assert.False(string.IsNullOrWhiteSpace(AutomationProperties.GetName(b))));
    }

    [Fact]
    public void TheAlbumHero_HasPlayThenShuffleDownloadAndSave() => Headless.Run(() =>
    {
        var page = new AlbumPageViewModel(new FakeCatalog(), null!, "MPRE");
        var view = new Views.AlbumPage { DataContext = page };
        var window = Show(view);

        AssertPattern(view, new Hero(T("common.play"), [T("common.shuffle"), Download, T("album.save")], []));
        window.Close();
    });

    [Fact]
    public void TheArtistHero_HasPlayTopSongsThenShuffleMixAndSubscribe_AndLastFmInMore() => Headless.Run(() =>
    {
        var page = new ArtistPageViewModel(new FakeCatalog(), null!, null!, "UCdp");
        var view = new Views.ArtistPage { DataContext = page };
        var window = Show(view);

        AssertPattern(view, new Hero(
            T("artist.playTopSongs"),
            [T("artist.shuffleSongs"), T("menu.startMix"), T("artist.subscribe")],
            [T("artist.lastFmStation")]));
        window.Close();
    });

    [Fact]
    public void ThePlaylistHero_HasPlayThenShuffleDownloadAndCopy_AndRenameDeleteInMore() => Headless.Run(() =>
    {
        var page = new PlaylistPageViewModel(new FakeCatalog(), null!, null!, null!, "PL1");
        var view = new Views.PlaylistPage { DataContext = page };
        var window = Show(view);

        AssertPattern(view, new Hero(
            T("common.play"),
            [T("common.shuffle"), Download, T("menu.copyToLocalPlaylist")],
            [T("common.rename"), T("common.delete")]));
        window.Close();
    });

    [Fact]
    public void TheLocalPlaylistHero_HasPlayThenShuffleAndExport_AndRenameDeleteInMore() => Headless.Run(() =>
    {
        var view = new Views.LocalPlaylistPage();
        var window = Show(view);

        AssertPattern(view, new Hero(
            T("common.play"),
            [T("common.shuffle"), T("localPlaylist.export")],
            [T("common.rename"), T("common.delete")]));
        window.Close();
    });

    [Fact]
    public void TheStationHero_HasPlayThenShuffleAndRefresh() => Headless.Run(() =>
    {
        var view = new Views.StationPage();
        var window = Show(view);

        AssertPattern(view, new Hero(T("common.play"), [T("common.shuffle"), T("station.page.refreshTip")], []));
        window.Close();
    });

    [Fact]
    public void OnANarrowPane_TheCirclesFoldIntoMoreAndThePrimaryStays() => Headless.Run(() =>
    {
        var view = new Views.AlbumPage { DataContext = new AlbumPageViewModel(new FakeCatalog(), null!, "MPRE") };
        var window = Show(view);
        var row = RowOf(view);
        var primary = row.Items.OfType<Button>().Single(ActionRow.GetKeep);
        Assert.False(row.ShowsMore);

        window.Width = 330;
        window.UpdateLayout();
        window.UpdateLayout();

        Assert.True(primary.IsEffectivelyVisible);
        Assert.True(row.ShowsMore);
        Assert.Contains(T("common.shuffle"), row.BuildMenu().Select(e => (string?)e.Header));
        window.Close();
    });

    private static (Views.ArtistPage View, ArtistPageViewModel Page, Window Window) Artist(bool? subscribed)
    {
        var catalog = new FakeCatalog
        {
            Artist = new ArtistPage
            {
                BrowseId = "UCdp", Name = "Daft Punk", TopSongs = [], Shelves = [], Subscribed = subscribed,
            },
        };
        var page = new ArtistPageViewModel(catalog, null!, null!, "UCdp");
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Views.ArtistPage { DataContext = page };
        return (view, page, Show(view));
    }

    [Fact]
    public void Subscribe_DiffersByIconAndNameBetweenItsTwoStates() => Headless.Run(() =>
    {
        var (names, icons) = (new List<string?>(), new List<object?>());
        foreach (var state in new bool?[] { false, true })
        {
            var (view, page, window) = Artist(state);
            var button = RowOf(view).Items.OfType<Button>().Single(b => ReferenceEquals(b.Command, page.ToggleSubscribedCommand));

            Assert.True(button.IsEffectivelyVisible);
            Assert.Equal(AutomationProperties.GetName(button), ToolTip.GetTip(button));
            Assert.False(HasLabel(button));
            names.Add(AutomationProperties.GetName(button));
            icons.Add(button.GetVisualDescendants().OfType<PathIcon>().Single(i => i.IsVisible).Data);
            window.Close();
        }

        Assert.Equal([T("artist.subscribe"), T("artist.unsubscribe")], names);
        Assert.NotNull(icons[0]);
        Assert.NotNull(icons[1]);
        Assert.NotSame(icons[0], icons[1]);
    });

    [Fact]
    public void Subscribe_IsHiddenWhileTheStateIsUnknown() => Headless.Run(() =>
    {
        var (view, page, window) = Artist(null);

        var button = RowOf(view).Items.OfType<Button>().Single(b => ReferenceEquals(b.Command, page.ToggleSubscribedCommand));
        Assert.False(button.IsEffectivelyVisible);
        window.Close();
    });

    [Fact]
    public void TheLastFmStation_IsInMoreOnlyWhileTheStationIsOffered() => Headless.Run(() =>
    {
        var source = new HomeStationsTests.FakeStations();
        var stations = new HomeStations(source, new StationResolver((_, _) => Task.FromResult<IReadOnlyList<TrackInfo>>([])), () => "listener")
        {
            Enabled = true,
        };
        var catalog = new FakeCatalog
        {
            Artist = new ArtistPage { BrowseId = "UCdp", Name = "Daft Punk", TopSongs = [], Shelves = [] },
        };
        var page = new ArtistPageViewModel(catalog, FakePlayback.Coordinator(new QueueModel()), null!, "UCdp") { Stations = stations };
        page.LoadAsync().GetAwaiter().GetResult();
        var view = new Views.ArtistPage { DataContext = page };
        var window = Show(view);
        var row = RowOf(view);

        var entry = Assert.Single(row.BuildMenu());
        Assert.Equal(T("artist.lastFmStation"), entry.Header);
        Assert.Same(page.OpenStationCommand, entry.Command);
        Assert.True(row.ShowsMore);

        stations.Enabled = false;
        window.UpdateLayout();

        Assert.Empty(row.BuildMenu());
        Assert.False(row.ShowsMore);
        window.Close();
    });
}
