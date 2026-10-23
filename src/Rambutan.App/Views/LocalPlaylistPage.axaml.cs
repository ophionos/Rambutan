using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class LocalPlaylistPage : UserControl
{
    public LocalPlaylistPage()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is LocalPlaylistPageViewModel vm)
            vm.PickExportFile = PickExportFileAsync;
    }

    private async Task<PickedFile?> PickExportFileAsync(string suggested)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
            return null;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Strings.Get("localPlaylist.exportPickTitle"),
            SuggestedFileName = suggested,
            DefaultExtension = "m3u8",
            FileTypeChoices = [new FilePickerFileType(Strings.Get("library.local.playlistFileType")) { Patterns = ["*.m3u8"] }],
        });
        return file is null ? null : new PickedFile(file.TryGetLocalPath());
    }
}
