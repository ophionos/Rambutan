using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class SpotifyImportPage : UserControl
{
    public SpotifyImportPage() => InitializeComponent();

    protected override void OnDataContextChanged(System.EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not SpotifyImportViewModel vm)
            return;
        vm.PickFile = async () =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
                return null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("import.pickTitle"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType(Strings.Get("import.fileType")) { Patterns = ["*.zip", "*.json", "*.csv", "*.tsv", "*.txt"] },
                    FilePickerFileTypes.All,
                ],
            });
            return files.Count == 0 ? null : new PickedFile(files[0].TryGetLocalPath());
        };
    }
}
