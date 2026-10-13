using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.Core.Downloads;

namespace Rambutan.App.ViewModels;

/// <summary>
/// An album's or playlist's Download button. It shows the collection's
/// state: not downloaded, downloading with progress, or all downloaded. Once all
/// is downloaded, a press removes the collection's downloads, with Undo.
/// </summary>
public sealed partial class CollectionDownloadButton : ObservableObject
{
    private readonly TrackActionsViewModel _actions;
    private readonly Func<(IEnumerable<string> Ids, bool Complete)> _tracks;
    private readonly Func<Task> _download;
    private readonly Func<string> _title;
    private readonly string _downloadTipKey;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDownloading), nameof(IsDownloaded), nameof(SweepAngle), nameof(Name), nameof(Tip))]
    private CollectionDownloadState _state = CollectionDownloadState.Empty;

    /// <param name="tracks">The collection's videoIds as the page has them, and
    /// whether that is all of them.</param>
    /// <param name="download">The page's Download all.</param>
    /// <param name="title">The collection's name, for the removal toast.</param>
    /// <param name="downloadTipKey">The tooltip before anything is downloaded.</param>
    public CollectionDownloadButton(
        TrackActionsViewModel actions, INotifyCollectionChanged trackList,
        Func<(IEnumerable<string> Ids, bool Complete)> tracks, Func<Task> download, Func<string> title,
        string downloadTipKey)
    {
        _actions = actions;
        _tracks = tracks;
        _download = download;
        _title = title;
        _downloadTipKey = downloadTipKey;

        trackList.CollectionChanged += (_, _) => Refresh();
        actions.WhenDownloadsChange(this, static b => b.Refresh());
        Strings.WhenChanged(this, static b =>
        {
            b.OnPropertyChanged(nameof(Name));
            b.OnPropertyChanged(nameof(Tip));
        });
        Refresh();
    }

    public bool IsDownloading => State.Kind == CollectionDownloadKind.Downloading;

    public bool IsDownloaded => State.Kind == CollectionDownloadKind.Downloaded;

    /// <summary>The progress ring's sweep while downloading, 0..360.</summary>
    public double SweepAngle => State.Fraction * 360;

    /// <summary>The button's spoken name: what a press does, or what is happening.</summary>
    public string Name => State.Kind switch
    {
        CollectionDownloadKind.Downloaded => Strings.Get("download.collection.remove"),
        CollectionDownloadKind.Downloading => Strings.Format("download.collection.downloading",
            ("done", State.Downloaded), ("total", State.Total)),
        _ => Strings.Get("common.download"),
    };

    public string Tip => State.Kind switch
    {
        CollectionDownloadKind.Downloaded => Strings.Get("download.collection.removeTip"),
        CollectionDownloadKind.Downloading => Name,
        _ when State.Failed > 0 => Strings.Format("download.collection.someFailed", ("count", State.Failed)),
        _ => Strings.Get(_downloadTipKey),
    };

    public void Refresh()
    {
        var (ids, complete) = _tracks();
        State = _actions.DownloadStateOf(ids, complete);
    }

    [RelayCommand]
    private Task PressAsync()
    {
        if (!IsDownloaded)
            return _download();
        _actions.RemoveDownloads(_tracks().Ids, _title());
        return Task.CompletedTask;
    }
}
