using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Downloads;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;

namespace Rambutan.App.ViewModels;

/// <summary>A job in On this computer's Downloading section: waiting,
/// downloading with its progress, or failed with Retry.</summary>
public partial class DownloadJobRow(TrackInfo track) : ObservableObject
{
    public TrackInfo Track { get; } = track;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFailed), nameof(IsWaiting))]
    private DownloadJobState _state;

    /// <summary>0..100; the bar is indeterminate while <see cref="HasProgress"/> is false.</summary>
    [ObservableProperty] private double _percent;
    [ObservableProperty] private bool _hasProgress;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _error;

    public bool IsFailed => State is DownloadJobState.Failed;
    public bool IsWaiting => State is DownloadJobState.Queued;

    internal void Update(DownloadJob job)
    {
        State = job.State;
        Error = job.Error;
        HasProgress = job.State is DownloadJobState.Downloading && job.Progress.Fraction is not null;
        Percent = (job.Progress.Fraction ?? 0) * 100;
        Status = job.State switch
        {
            DownloadJobState.Queued => Strings.Get("downloads.waiting"),
            DownloadJobState.Failed => Strings.Get("downloads.failed"),
            _ when job.Progress is { Fraction: { } f, Total: { } total } => Strings.Format("downloads.progress",
                ("percent", (int)(f * 100)), ("size", DownloadsViewModel.FormatSize(total))),
            _ => Strings.Get("downloads.starting"),
        };
    }
}

/// <summary>
/// The downloads as On this computer shows them (<see cref="LocalLibraryViewModel"/>):
/// the jobs still running or failed, the finished downloads with their space
/// used, and what acts on them (remove, download again, export). One instance
/// for the app's lifetime, so it follows the queue while it isn't on screen
/// too; queue events arrive on job threads and are posted to the UI thread here.
/// </summary>
public partial class DownloadsViewModel : ViewModelBase
{
    private readonly DownloadQueue _queue;
    private readonly TrackActionsViewModel? _actions;
    private readonly Action<Action> _post;
    private readonly ExportSettings? _exportSettings;
    private readonly DownloadExport? _export;
    private readonly Func<string, ExportSource?>? _exportSource;
    private CancellationTokenSource? _exportCts;

    public ObservableCollection<DownloadJobRow> Jobs { get; } = [];

    /// <summary>The finished downloads, newest first, without those a page's
    /// "Remove downloads" is offering to undo. Replaced, never edited.</summary>
    public IReadOnlyList<DownloadedTrack> Downloaded { get; private set; } = [];

    /// <summary><see cref="Downloaded"/> changed; raised on the UI thread.</summary>
    public event Action? DownloadedChanged;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _jobsSummary = "";
    [ObservableProperty] private bool _hasJobs;
    [ObservableProperty] private bool _hasFailed;
    [ObservableProperty] private bool _hasDownloaded;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isEmpty = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanExport))]
    private bool _isExporting;

    /// <summary>"Exporting 3 of 11…" while an export runs.</summary>
    [ObservableProperty] private string _exportStatus = "";

    /// <summary>The export setting is on (export controls show); off by default.</summary>
    public bool ExportEnabled => _exportSettings?.Enabled == true && _export is not null;

    /// <summary>Export buttons work: the setting is on, there is something to
    /// export, and no export is running.</summary>
    public bool CanExport => ExportEnabled && HasDownloaded && !IsExporting;

    /// <summary>Set by the view: the platform folder picker, starting at the given
    /// folder when there is one; null when cancelled.</summary>
    public Func<string?, Task<string?>>? PickFolder { get; set; }

    /// <param name="post">Runs an action on the UI thread; tests pass one that runs it at once.</param>
    /// <param name="exportSource">The download's files for export (the store's
    /// <c>ExportSourceFor</c>).</param>
    public DownloadsViewModel(
        DownloadQueue queue, TrackActionsViewModel? actions,
        Action<Action>? post = null, ExportSettings? exportSettings = null, DownloadExport? export = null,
        Func<string, ExportSource?>? exportSource = null)
    {
        _queue = queue;
        _actions = actions;
        _post = post ?? (a => Dispatcher.UIThread.Post(a));
        _exportSettings = exportSettings;
        _export = export;
        _exportSource = exportSource;
        if (exportSettings is not null)
            exportSettings.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ExportSettings.Enabled))
                {
                    OnPropertyChanged(nameof(ExportEnabled));
                    OnPropertyChanged(nameof(CanExport));
                }
            };

        queue.JobsChanged += () => _post(RefreshJobs);
        queue.ProgressChanged += job => _post(() => Jobs.FirstOrDefault(r => r.Track.VideoId == job.Track.VideoId)?.Update(job));
        queue.DownloadsChanged += _ => QueueRefreshDownloaded();
        // A page's "Remove downloads" hides its tracks here too while Undo is offered.
        if (actions is not null)
            actions.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TrackActionsViewModel.DownloadedIds))
                    RefreshDownloaded();
            };
        RefreshJobs();
        RefreshDownloaded();
    }

    private int _downloadedRefreshQueued;

    // Once per track; a bulk removal's hundreds come together as one rebuild.
    private void QueueRefreshDownloaded()
    {
        if (Interlocked.Exchange(ref _downloadedRefreshQueued, 1) == 0)
            _post(() =>
            {
                Volatile.Write(ref _downloadedRefreshQueued, 0);
                RefreshDownloaded();
            });
    }

    /// <summary>Rows are kept per track, so a state change doesn't rebuild the
    /// list under the keyboard focus.</summary>
    private void RefreshJobs()
    {
        var jobs = _queue.Jobs;
        var rows = Jobs.ToDictionary(r => r.Track.VideoId);
        for (var i = 0; i < jobs.Count; i++)
        {
            if (!rows.TryGetValue(jobs[i].Track.VideoId, out var row))
                row = new DownloadJobRow(jobs[i].Track);
            row.Update(jobs[i]);

            var at = Jobs.IndexOf(row);
            if (at < 0)
                Jobs.Insert(i, row);
            else if (at != i)
                Jobs.Move(at, i);
        }
        while (Jobs.Count > jobs.Count)
            Jobs.RemoveAt(Jobs.Count - 1);

        HasJobs = Jobs.Count > 0;
        var hadFailed = HasFailed;
        HasFailed = Jobs.Any(r => r.IsFailed);
        // The page may not be on screen, so the first failure says so where the
        // user is. Only the first: a playlist failing offline would otherwise
        // bury the window in toasts, and the page lists every one.
        if (HasFailed && !hadFailed && Jobs.First(r => r.IsFailed) is { } failed)
            _actions?.ReportError(Strings.Format("toast.download.failed", ("title", failed.Track.Title)));
        JobsSummary = HasJobs ? Strings.Format("downloads.activeCount", ("count", Jobs.Count)) : "";
        IsEmpty = !HasJobs && !HasDownloaded;
    }

    private void RefreshDownloaded()
    {
        var downloads = _queue.Downloaded
            .Where(d => _actions?.IsPendingRemoval(d.Track.VideoId) != true).ToList();
        Downloaded = downloads;

        HasDownloaded = Downloaded.Count > 0;
        OnPropertyChanged(nameof(CanExport));
        Summary = HasDownloaded
            ? Strings.Format("downloads.summary",
                ("count", downloads.Count), ("size", FormatSize(downloads.Sum(d => d.SizeBytes))))
            : "";
        IsEmpty = !HasJobs && !HasDownloaded;
        DownloadedChanged?.Invoke();
    }

    /// <summary>"84 MB", "1.2 GB".</summary>
    public static string FormatSize(long bytes)
    {
        const double mb = 1024 * 1024;
        var megabytes = bytes / mb;
        // Numbers, not text: the string table formats them for the UI language.
        return megabytes >= 1000
            ? Strings.Format("downloads.sizeGb", ("value", Math.Round(megabytes / 1024, 1)))
            : Strings.Format("downloads.sizeMb", ("value", Math.Round(Math.Max(megabytes, bytes > 0 ? 1 : 0))));
    }

    [RelayCommand]
    private void Remove(DownloadedTrack? row)
    {
        if (row is null)
            return;
        _queue.Delete(row.Track.VideoId);
        _actions?.Report(Strings.Format("toast.download.removed", ("title", row.Track.Title)));
    }

    [RelayCommand]
    private void DownloadAgain(DownloadedTrack? row)
    {
        if (row is null)
            return;
        // The record goes first: the queue skips a track it still counts as downloaded.
        _queue.Delete(row.Track.VideoId);
        _queue.Enqueue([row.Track]);
    }

    [RelayCommand]
    private void Cancel(DownloadJobRow? row)
    {
        if (row is not null)
            _queue.Cancel(row.Track.VideoId);
    }

    [RelayCommand]
    private void Retry(DownloadJobRow? row)
    {
        if (row is not null)
            _queue.Retry(row.Track.VideoId);
    }

    [RelayCommand]
    private void RetryAll() => _queue.RetryAllFailed();

    [RelayCommand]
    private void CancelAll() => _queue.CancelAll();

    /// <summary>Exports every download whose file is there, in list order.</summary>
    [RelayCommand]
    private Task ExportAllAsync() =>
        ExportAsync(Downloaded.Where(r => !r.IsMissing).Select(r => r.Track.VideoId).ToList());

    [RelayCommand]
    private Task ExportOneAsync(DownloadedTrack? row) =>
        row is null || row.IsMissing ? Task.CompletedTask : ExportAsync([row.Track.VideoId]);

    [RelayCommand]
    private void CancelExport() => _exportCts?.Cancel();

    /// <summary>Asks for a folder, then writes the downloads there one at a time.
    /// The outcome is a toast: the page may be left while it runs.</summary>
    internal async Task ExportAsync(IReadOnlyList<string> videoIds)
    {
        if (!CanExport || _export is null || _exportSource is null || videoIds.Count == 0 || PickFolder is null)
            return;
        if (await PickFolder(_exportSettings!.LastFolder) is not { Length: > 0 } folder)
            return;
        _exportSettings.LastFolder = folder;

        var sources = videoIds.Select(_exportSource).OfType<ExportSource>().ToList();
        using var cts = _exportCts = new CancellationTokenSource();
        IsExporting = true;
        ExportStatus = Strings.Format("downloads.export.progress", ("done", 0), ("count", sources.Count));
        try
        {
            var progress = new Progress<int>(done => _post(() =>
            {
                if (IsExporting)
                    ExportStatus = Strings.Format("downloads.export.progress",
                        ("done", Math.Min(done + 1, sources.Count)), ("count", sources.Count));
            }));
            var format = _exportSettings.Format;
            var result = await Task.Run(() => _export.RunAsync(sources, folder, format, progress, cts.Token));
            ReportExport(result, folder);
        }
        catch (Exception ex)
        {
            Core.Diagnostics.AppLog.Write("export", $"export failed: {ex.Message}");
            _actions?.ReportError(Strings.Get("toast.export.failed"));
        }
        finally
        {
            _exportCts = null;
            IsExporting = false;
            ExportStatus = "";
        }
    }

    private void ReportExport(ExportResult result, string folder)
    {
        var where = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
        if (string.IsNullOrEmpty(where))
            where = folder;
        if (result.Cancelled)
            _actions?.Report(Strings.Format("toast.export.stopped", ("count", result.Written), ("folder", where)));
        else if (result.Failed.Count == 0)
            _actions?.Report(Strings.Format("toast.export.done", ("count", result.Written), ("folder", where)));
        else if (result.Written == 0)
            _actions?.ReportError(Strings.Get("toast.export.failed"));
        else
            _actions?.ReportError(Strings.Format("toast.export.partial",
                ("count", result.Written), ("failed", result.Failed.Count), ("folder", where)));
    }
}
