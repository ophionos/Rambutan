using System.Linq;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();

        // The folder picker needs a TopLevel, which only the view has.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is SettingsPageViewModel { Remote: { } remote })
            {
                remote.CopyToClipboard = text =>
                    TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? System.Threading.Tasks.Task.CompletedTask;
            }
            if (DataContext is SettingsPageViewModel { Overlay: { } overlay })
                AttachOverlay(overlay);
            if (DataContext is SettingsPageViewModel { History: { } history })
                history.Confirm = async (message, accept) => TopLevel.GetTopLevel(this) is Window window
                    && await new ConfirmDialog(message, accept).ShowDialog<bool>(window);
            if (DataContext is SettingsPageViewModel { Local: { } local })
                local.PickFolder = () => PickFolderAsync("settings.local.pickTitle");
            if (DataContext is not SettingsPageViewModel { Downloads: { } downloads })
                return;
            downloads.PickFolder = () => PickFolderAsync("settings.downloads.pickTitle");
        };
    }

    private async System.Threading.Tasks.Task<string?> PickFolderAsync(string titleKey)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            return null;
        var picked = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Strings.Get(titleKey),
            AllowMultiple = false,
        });
        return picked.FirstOrDefault()?.TryGetLocalPath();
    }

    /// <summary>The clipboard and the style file pickers, which need a TopLevel.</summary>
    private void AttachOverlay(OverlaySettingsViewModel overlay)
    {
        var styleType = new FilePickerFileType(Strings.Get("settings.overlay.style.fileType")) { Patterns = ["*.json"] };
        overlay.CopyToClipboard = text =>
            TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text) ?? System.Threading.Tasks.Task.CompletedTask;
        overlay.PickStyleToImport = async () =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storage)
                return null;
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Strings.Get("settings.overlay.style.import"),
                AllowMultiple = false,
                FileTypeFilter = [styleType],
            });
            if (files.Count == 0)
                return null;
            // One character past the cap is enough for the parser to call it too
            // large, and a stream that can't tell its length is never read whole.
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new System.IO.StreamReader(stream);
            var buffer = new char[LocalApi.Overlay.OverlayConfigFormat.MaxBytes + 1];
            var read = await reader.ReadBlockAsync(buffer);
            return new string(buffer, 0, read);
        };
        overlay.OpenEditor = editor =>
            TopLevel.GetTopLevel(this) is Window owner
                ? new OverlayStyleEditorDialog(editor).ShowDialog(owner)
                : System.Threading.Tasks.Task.CompletedTask;
        overlay.SaveStyleAs = async json =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanSave: true } storage)
                return false;
            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Strings.Get("settings.overlay.style.export"),
                SuggestedFileName = "rambutan-overlay-style.json",
                DefaultExtension = "json",
                FileTypeChoices = [styleType],
            });
            if (file is null)
                return false;
            await using var stream = await file.OpenWriteAsync();
            if (stream.CanSeek)
                stream.SetLength(0);
            await using var writer = new System.IO.StreamWriter(stream);
            await writer.WriteAsync(json);
            return true;
        };
    }
}
