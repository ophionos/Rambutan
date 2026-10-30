using System;
using System.Threading.Tasks;
using Avalonia.Threading;
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(YouTubeHistoryStatus))]
    private bool _addToYouTubeHistory;

    /// <summary>Why plays aren't reaching the YouTube Music history while it is
    /// on (signed out, or the last report failed); null when nothing is wrong.</summary>
    public string? YouTubeHistoryStatus =>
        !AddToYouTubeHistory || _youTube is null ? null
        : !_youTube.IsSignedIn ? Strings.Get("settings.history.youtubeSignedOut")
        : _youTube.LastFailed ? Strings.Get("settings.history.youtubeFailed")
        : null;

    /// <summary>What the last Clear did; null before one.</summary>
    [ObservableProperty] private string? _clearResult;

    /// <summary>Set by the view: asks to confirm (message, accept label).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <param name="post">Runs a refresh on the UI thread; tests pass their own.</param>
    public ListeningHistorySettingsViewModel(
        AppSettings app, PlayLog log, Action persist, WatchHistory? youTube = null, Action<Action>? post = null)
    {
        _app = app;
        _log = log;
        _persist = persist;
        _youTube = youTube;
        _recordPlays = app.RecordPlays;
        _addToYouTubeHistory = app.AddPlaysToYouTubeHistory;
        post ??= action => Dispatcher.UIThread.Post(action);
        if (youTube is not null)
            youTube.Changed += () => post(() => OnPropertyChanged(nameof(YouTubeHistoryStatus)));
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
