using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings → Listening history: whether the play log is kept (on by default; it
/// never leaves the machine), and clearing it, after a confirmation, for this
/// profile; and whether plays go to the account's YouTube Music history (off
/// by default: that tells Google what the user plays).
/// </summary>
public partial class ListeningHistorySettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly PlayLog _log;
    private readonly Action _persist;
    private readonly WatchHistory? _youTube;

    [ObservableProperty] private bool _recordPlays;

    [ObservableProperty] private bool _addToYouTubeHistory;

    /// <summary>What the last Clear did; null before one.</summary>
    [ObservableProperty] private string? _clearResult;

    /// <summary>Set by the view: asks to confirm (message, accept label).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    public ListeningHistorySettingsViewModel(AppSettings app, PlayLog log, Action persist, WatchHistory? youTube = null)
    {
        _app = app;
        _log = log;
        _persist = persist;
        _youTube = youTube;
        _recordPlays = app.RecordPlays;
        _addToYouTubeHistory = app.AddPlaysToYouTubeHistory;
    }

    partial void OnAddToYouTubeHistoryChanged(bool value)
    {
        _app.AddPlaysToYouTubeHistory = value;
        if (_youTube is not null)
            _youTube.Enabled = value;
        _persist();
    }

    partial void OnRecordPlaysChanged(bool value)
    {
        _app.RecordPlays = value;
        _log.Enabled = value;
        _persist();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (Confirm is null || !await Confirm(
                Strings.Get("settings.history.clearConfirm"), Strings.Get("settings.history.clearAccept")))
            return;
        try
        {
            await _log.ClearAsync();
            ClearResult = Strings.Get("settings.history.cleared");
        }
        catch (Exception ex)
        {
            AppLog.Write("plays", $"Couldn't clear the play log: {ex.Message}");
            ClearResult = Strings.Get("settings.history.clearFailed");
        }
    }
}
