using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class LibraryPage : UserControl
{
    private LibraryPageViewModel? _vm;

    public LibraryPage()
    {
        InitializeComponent();

        // Continuation-driven paging as on playlist pages. Extent
        // changes raise this too, so a first page too short to scroll keeps
        // loading until it fills the view. A failed page waits for its Try again
        // instead of re-firing on every scroll tick.
        Scroller.ScrollChanged += (_, _) =>
        {
            if (_vm?.SelectedSection is { HasMore: true, IsLoadingMore: false, LoadError: null } section &&
                ScrollPaging.IsNearEnd(Scroller.Offset.Y, Scroller.Viewport.Height, Scroller.Extent.Height))
            {
                section.LoadMoreCommand.Execute(null);
            }
        };
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

    // Each tab starts at its top; the scroller is shared between them.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryPageViewModel.SelectedSection))
            Scroller.Offset = new Vector(Scroller.Offset.X, 0);
    }
}
