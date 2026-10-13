using System.Linq;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class DownloadsPage : UserControl
{
    public DownloadsPage()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not DownloadsPageViewModel page)
                return;
            page.PickFolder = async start =>
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
        };
    }
}
