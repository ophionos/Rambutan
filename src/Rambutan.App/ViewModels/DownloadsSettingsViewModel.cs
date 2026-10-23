using System;
using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.LocalLibrary;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings → Downloads: where new downloads go, and how much space the
/// downloads use. A new folder applies to downloads from then on; existing ones
/// stay where they are and keep playing (maintainer's decision). Export
/// as ordinary files is a setting here too, off by default.
/// </summary>
public partial class DownloadsSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly DownloadStore _store;
    private readonly string _defaultFolder;
    private readonly Action _persist;
    private readonly ExportSettings? _export;

    [ObservableProperty] private string _folder = "";
    [ObservableProperty] private bool _isDefaultFolder;
    [ObservableProperty] private string _usage = "";
    [ObservableProperty] private string? _folderError;

    /// <summary>Export as ordinary files (shown only when <see cref="CanExport"/>).</summary>
    [ObservableProperty] private bool _exportEnabled;

    /// <summary>0 = original quality (.ogg/.m4a), 1 = MP3.</summary>
    [ObservableProperty] private int _exportFormatIndex;

    public bool CanExport => _export is not null;

    /// <summary>Set by the view: the platform folder picker, null when cancelled.</summary>
    public Func<Task<string?>>? PickFolder { get; set; }

    public DownloadsSettingsViewModel(
        AppSettings app, DownloadStore store, string defaultFolder, Action persist, ExportSettings? export = null)
    {
        _app = app;
        _store = store;
        _defaultFolder = defaultFolder;
        _persist = persist;
        _export = export;
        _exportEnabled = export?.Enabled ?? false;
        _exportFormatIndex = export?.Format is Core.Downloads.ExportFormat.Mp3 ? 1 : 0;
        Refresh();
    }

    partial void OnExportEnabledChanged(bool value)
    {
        if (_export is not null)
            _export.Enabled = value;
    }

    partial void OnExportFormatIndexChanged(int value)
    {
        if (_export is not null && value >= 0)
            _export.Format = value == 1 ? Core.Downloads.ExportFormat.Mp3 : Core.Downloads.ExportFormat.Original;
    }

    private void Refresh()
    {
        Folder = DownloadsFolders.Effective(_app, _defaultFolder);
        IsDefaultFolder = string.IsNullOrWhiteSpace(_app.DownloadsFolder);
        Usage = Strings.Format("settings.downloads.usageValue",
            ("count", _store.List().Count), ("size", DownloadsPageViewModel.FormatSize(_store.TotalBytes)));
    }

    [RelayCommand]
    private async Task ChangeFolderAsync()
    {
        if (PickFolder is null || await PickFolder() is not { Length: > 0 } folder)
            return;
        SetFolder(folder);
    }

    /// <summary>A folder that can't be written to is refused here, not at the first download.</summary>
    internal void SetFolder(string folder)
    {
        if (!CanWrite(folder))
        {
            FolderError = Strings.Get("settings.downloads.folderNotWritable");
            return;
        }

        FolderError = null;
        _app.DownloadsFolder = folder;
        _persist();
        Refresh();
    }

    [RelayCommand]
    private void UseDefaultFolder()
    {
        FolderError = null;
        _app.DownloadsFolder = null;
        _persist();
        Refresh();
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, $".rambutan-write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
