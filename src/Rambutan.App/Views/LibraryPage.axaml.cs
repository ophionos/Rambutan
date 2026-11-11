using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Views;

public partial class LibraryPage : UserControl
{
    private LibraryPageViewModel? _vm;

    public LibraryPage()
    {
        InitializeComponent();

        // Each tab pages from whichever scroller it shows in: the cards from the
        // page's, Songs and History from their own lists'.
        Scroller.ScrollChanged += (_, _) => LoadMoreNearEnd(Scroller, Scroller.DataContext as LibrarySection);
        foreach (var list in new[] { SongList, HistoryList })
        {
            list.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) =>
            {
                if (list.Scroll is ScrollViewer scroll)
                    LoadMoreNearEnd(scroll, list.DataContext as LibrarySection);
            });
        }

        HistoryList.ContainerPrepared += OnHistoryContainerPrepared;
        HistoryList.SelectionChanged += OnHistorySelectionChanged;
    }

    /// <summary>Continuation-driven paging as on playlist pages. Extent changes
    /// raise this too, so a first page too short to scroll keeps loading until it
    /// fills the view. A failed page waits for its Try again instead of re-firing
    /// on every scroll tick. A hidden tab's list never pages.</summary>
    private void LoadMoreNearEnd(ScrollViewer scroll, LibrarySection? section)
    {
        if (section is { HasMore: true, IsLoadingMore: false, LoadError: null } &&
            ReferenceEquals(section, _vm?.SelectedSection) &&
            ScrollPaging.IsNearEnd(scroll.Offset.Y, scroll.Viewport.Height, scroll.Extent.Height))
        {
            section.LoadMoreCommand.Execute(null);
        }
    }

    // Containers are recycled between headings and rows, so each one is set
    // both ways. A heading takes no pointer (no hover, selection or double-click
    // play) and no focus (the arrow keys pass over it); a screen reader hears it
    // as a heading, named by AccessibleNames from its IHasAccessibleName.
    private void OnHistoryContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var container = e.Container;
        if (HistoryList.Items[e.Index] is HistoryHeading)
        {
            container.Focusable = false;
            container.IsHitTestVisible = false;
            AutomationProperties.SetHeadingLevel(container, 2);
        }
        else
        {
            container.ClearValue(FocusableProperty);
            container.ClearValue(IsHitTestVisibleProperty);
            container.ClearValue(AutomationProperties.HeadingLevelProperty);
        }
    }

    // Whatever selects a heading (a key that lands on it, the code) moves on to
    // the row beyond it in the same direction, else the row before it: Enter
    // then always has a song to play.
    private void OnHistorySelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not HistoryHeading)
            return;
        var items = HistoryList.Items;
        var at = HistoryList.SelectedIndex;
        var from = e.RemovedItems.Count > 0 ? items.IndexOf(e.RemovedItems[0]) : -1;
        var step = from > at ? -1 : 1;
        HistoryList.SelectedIndex = NextRow(at, step) ?? NextRow(at, -step) ?? -1;

        int? NextRow(int index, int direction)
        {
            for (var i = index + direction; i >= 0 && i < items.Count; i += direction)
            {
                if (items[i] is HistoryEntry)
                    return i;
            }
            return null;
        }
    }

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as LibraryPageViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;
        if (_vm is not null)
            _vm.PickPlaylistFile = async () =>
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
                    return null;
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Strings.Get("library.local.importPickTitle"),
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType(Strings.Get("library.local.playlistFileType")) { Patterns = ["*.m3u8", "*.m3u"] },
                        FilePickerFileTypes.All,
                    ],
                });
                return files.Count == 0 ? null : new PickedFile(files[0].TryGetLocalPath());
            };
        if (_vm?.Local?.Downloads is { } downloads)
            downloads.PickFolder = async start =>
            {
                if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
                    return null;
                var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = Strings.Get("downloads.export.pickTitle"),
                    AllowMultiple = false,
                    SuggestedStartLocation = start is null ? null : await storage.TryGetFolderFromPathAsync(start),
                });
                return picked.FirstOrDefault()?.TryGetLocalPath();
            };
    }

    // Each card tab starts at its top; the scroller is shared between them.
    // Songs and History keep their own lists' positions.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPageViewModel.SelectedSection))
            Scroller.Offset = new Vector(Scroller.Offset.X, 0);
    }
}
