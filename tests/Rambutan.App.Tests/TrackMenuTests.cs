using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Tests;

public class TrackMenuTests
{
    private static readonly TrackActionsViewModel Actions = new(null!, new QueueModel(), null!, null!, new ToastHostViewModel());

    private static TrackInfo Track(
        string id, string? artistBrowseId = "UC1", string? setVideoId = null,
        string? albumBrowseId = "MPREb1", Rating? rating = null) => new()
    {
        VideoId = id,
        Title = id,
        Artists = [new("Artist", artistBrowseId)],
        SetVideoId = setVideoId,
        AlbumBrowseId = albumBrowseId,
        Rating = rating,
    };

    private static string[][] Headers(IReadOnlyList<IReadOnlyList<TrackMenuEntry>> groups) =>
        groups.Select(g => g.Select(e => e.Header).ToArray()).ToArray();

    [Fact]
    public void EveryPage_GetsTheSameMenu()
    {
        var groups = TrackMenuModel.Build(Track("a"), Actions);

        Assert.Equal(
            [
                ["Play next", "Add to queue", "Start a mix"],
                ["Like", "Save to playlist"],
                ["Go to album", "Go to artist", "Share"],
            ],
            Headers(groups));
        Assert.All(groups.SelectMany(g => g), e => Assert.True(e.IsEnabled));
    }

    [Fact]
    public void GoToArtist_IsDisabled_WithoutAnArtistLink()
    {
        var entry = TrackMenuModel.Build(Track("a", artistBrowseId: null), Actions)
            .SelectMany(g => g).Single(e => e.Header == "Go to artist");
        Assert.False(entry.IsEnabled);
    }

    [Fact]
    public void GoToAlbum_OpensTheTracksAlbum_AndIsDisabledWithoutOne()
    {
        string? opened = null;
        var actions = new TrackActionsViewModel(null!, new QueueModel(), null!, null!, new ToastHostViewModel())
        {
            OpenAlbum = id => opened = id,
        };

        var entry = TrackMenuModel.Build(Track("a"), actions).SelectMany(g => g).Single(e => e.Header == "Go to album");
        Assert.True(entry.IsEnabled);
        entry.Command.Execute(entry.Parameter);
        Assert.Equal("MPREb1", opened);

        var unlinked = TrackMenuModel.Build(Track("a", albumBrowseId: null), actions)
            .SelectMany(g => g).Single(e => e.Header == "Go to album");
        Assert.False(unlinked.IsEnabled);
    }

    [Fact]
    public void Like_ReadsTheSharedRating()
    {
        var catalog = new FakeCatalog();
        var ratings = new TrackRatings(catalog);
        var actions = new TrackActionsViewModel(catalog, new QueueModel(), null!, ratings, new ToastHostViewModel());

        string LikeHeader(TrackInfo t) => TrackMenuModel.Build(t, actions)[1][0].Header;

        Assert.Equal("Like", LikeHeader(Track("a")));
        Assert.Equal("Remove like", LikeHeader(Track("a", rating: Rating.Like)));
        Assert.Equal("Like", LikeHeader(Track("a", rating: Rating.Dislike)));
    }

    [Fact]
    public async Task Like_FollowsAPressElsewhere()
    {
        var catalog = new FakeCatalog();
        var ratings = new TrackRatings(catalog);
        var actions = new TrackActionsViewModel(catalog, new QueueModel(), null!, ratings, new ToastHostViewModel());
        var track = Track("a");

        // A thumb pressed in the player bar: the menu opened afterwards agrees.
        await ratings.ToggleAsync(track, Rating.Like);

        var like = TrackMenuModel.Build(track, actions)[1][0];
        Assert.Equal("Remove like", like.Header);
        Assert.Equal("Icon.ThumbUp", like.IconKey);
    }

    private static readonly LibraryToggle NotSaved = new(false, "ADD_a", "REMOVE_a");

    private static TrackActionsViewModel LibraryActions(FakeCatalog catalog, ToastHostViewModel? toasts = null) =>
        new(catalog, new QueueModel(), null!, null!, toasts ?? new ToastHostViewModel())
        {
            SongLibrary = new SongLibrary(catalog),
        };

    private static TrackMenuEntry? LibraryEntry(TrackInfo track, TrackActionsViewModel actions) =>
        TrackMenuModel.Build(track, actions)[1].SingleOrDefault(e => e.Command == actions.ToggleInLibraryCommand);

    [Fact]
    public void SaveToLibrary_IsLeftOut_WhenTheRowDidNotSay()
    {
        var actions = LibraryActions(new FakeCatalog());

        Assert.Null(LibraryEntry(Track("a"), actions));
        // Nor where nothing could send it.
        Assert.Null(LibraryEntry(Track("a") with { Library = NotSaved }, Actions));
    }

    [Fact]
    public void SaveToLibrary_IsLabelledForWhatItDoesNow()
    {
        var actions = LibraryActions(new FakeCatalog());

        var save = LibraryEntry(Track("a") with { Library = NotSaved }, actions)!;
        var remove = LibraryEntry(Track("a") with { Library = NotSaved with { InLibrary = true } }, actions)!;

        Assert.Equal(("Save to library", "Icon.LibraryAdd"), (save.Header, save.IconKey));
        Assert.Equal(("Remove from library", "Icon.LibraryAdded"), (remove.Header, remove.IconKey));
        // After Like and Save to playlist, before the download toggle.
        Assert.Equal(["Like", "Save to playlist", "Save to library"],
            TrackMenuModel.Build(Track("a") with { Library = NotSaved }, actions)[1].Select(e => e.Header));
    }

    [Fact]
    public async Task SaveToLibrary_SendsTheToken_AndTheNextMenuOffersRemove()
    {
        var catalog = new FakeCatalog();
        var toasts = new ToastHostViewModel();
        var actions = LibraryActions(catalog, toasts);
        var changed = new List<LibrarySectionKind>();
        actions.LibraryChanged += changed.Add;
        var track = Track("a") with { Library = NotSaved };

        await actions.ToggleInLibraryCommand.ExecuteAsync(track);

        Assert.Equal(["add ADD_a"], catalog.LibraryEdits);
        Assert.Equal([LibrarySectionKind.Songs], changed);
        Assert.Equal("Saved “a” to your library", toasts.Toasts[^1].Message);
        // The same song from another page agrees.
        Assert.Equal("Remove from library", LibraryEntry(Track("a") with { Library = NotSaved }, actions)!.Header);
    }

    [Fact]
    public async Task SaveToLibrary_Refused_FlipsBack_AndSaysSo()
    {
        var catalog = new FakeCatalog { EditFailure = new HttpRequestException("offline") };
        var toasts = new ToastHostViewModel();
        var actions = LibraryActions(catalog, toasts);
        var removed = new List<(TrackInfo, bool)>();
        actions.SongLibraryChanged += (t, saved) => removed.Add((t, saved));
        var track = Track("a") with { Library = NotSaved };

        await actions.ToggleInLibraryCommand.ExecuteAsync(track);

        Assert.Equal("Save to library", LibraryEntry(track, actions)!.Header);
        Assert.True(toasts.Toasts[^1].IsError);
        Assert.Contains("offline", toasts.Toasts[^1].Message);
        Assert.Empty(removed);
    }

    [Fact]
    public void QueueRows_SwapQueueingForQueueMoves_AndCanBeRemoved() => Headless.Run(() =>
    {
        var queue = new QueueModel();
        queue.SetQueue([Track("a"), Track("b"), Track("c")]);
        var vm = new QueueViewModel(queue, null);

        var upcoming = vm.Items[2];
        var groups = TrackMenuModel.Build(upcoming.Track, Actions, vm.MenuExtrasFor(upcoming, upcoming.Track));

        Assert.Equal(
            [
                ["Play now", "Play next", "Start a mix"],
                ["Like", "Save to playlist"],
                ["Go to album", "Go to artist", "Share"],
                ["Remove from queue"],
            ],
            Headers(groups));
        Assert.All(groups.SelectMany(g => g), e => Assert.True(e.IsEnabled));

        // The playing row can't be played now, moved next or removed.
        var current = vm.Items[0];
        var currentEntries = TrackMenuModel.Build(current.Track, Actions, vm.MenuExtrasFor(current, current.Track))
            .SelectMany(g => g).ToDictionary(e => e.Header);
        Assert.False(currentEntries["Play now"].IsEnabled);
        Assert.False(currentEntries["Play next"].IsEnabled);
        Assert.False(currentEntries["Remove from queue"].IsEnabled);

        // Already next: "Play next" has nothing to do.
        var next = vm.Items[1];
        Assert.False(vm.MenuExtrasFor(next, next.Track).Play!.Single(e => e.Header == "Play next").IsEnabled);
    });

    [Fact]
    public void PlaylistRows_OfferRemove_OnlyWhenThePlaylistAllowsIt()
    {
        var page = new PlaylistPageViewModel(null!, null!, null!, Actions, "PL1") { IsEditable = true };

        var editable = TrackMenuModel.Build(Track("a", setVideoId: "S1"), Actions,
            page.MenuExtrasFor(null, Track("a", setVideoId: "S1")));
        var readOnly = TrackMenuModel.Build(Track("a"), Actions, page.MenuExtrasFor(null, Track("a")));

        Assert.Equal(["Remove from playlist"], Headers(editable)[^1]);
        Assert.DoesNotContain(readOnly.SelectMany(g => g), e => e.Header == "Remove from playlist");
    }

    [Fact]
    public void ARequestedMenu_IsFilledBeforeItOpens_WithSeparatorsBetweenGroups() => Headless.Run(() =>
    {
        TrackMenu.Actions = Actions;
        var list = new ListBox
        {
            ItemsSource = new[] { Track("a"), Track("b") },
            ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<TrackInfo>((_, _) =>
            {
                var row = new Border { Height = 40, Background = Avalonia.Media.Brushes.Transparent };
                row.Bind(TrackMenu.TrackProperty, new Avalonia.Data.Binding());
                return row;
            }),
        };
        var window = new Window { Width = 400, Height = 300, Content = list };
        window.Show();
        window.UpdateLayout();
        var row = list.ContainerFromIndex(1)!.GetVisualDescendants().OfType<Border>()
            .First(b => TrackMenu.GetTrack(b) is not null);
        var flyout = Assert.IsType<MenuFlyout>(row.ContextFlyout);
        Assert.Empty(flyout.Items); // nothing built per row until asked

        // What a right-click or the menu key raises on the row.
        row.RaiseEvent(new Avalonia.Input.ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });

        Assert.True(flyout.IsOpen);
        var items = flyout.Items.ToArray();
        Assert.Equal(10, items.Length); // 8 entries + 2 separators
        Assert.IsType<Separator>(items[3]);
        Assert.Equal("Play next", ((MenuItem)items[0]!).Header);
        Assert.Equal("b", ((MenuItem)items[0]!).CommandParameter is TrackInfo t ? t.VideoId : null);
        flyout.Hide();
    });
}
