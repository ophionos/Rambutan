using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Catalog;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Import;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;
using Rambutan.Integrations.Spotify;

namespace Rambutan.App.ViewModels;

public enum ImportPhase
{
    /// <summary>No file read yet.</summary>
    Start,
    Reading,

    /// <summary>A file's lists are shown, to choose, match and review.</summary>
    Ready,
    Matching,
    Creating,
}

/// <summary>
/// Importing a Spotify library, from Library: read a file (the data export or
/// a CSV) or public lists' share links, choose the lists and where they go, match their songs on YouTube
/// Music (one signed-out search per distinct song), review the picks, then
/// create the playlists. Nothing is written until Create. A list imported
/// before to the same place adds only the songs new since, to the playlist
/// it made then (Update from Spotify); a link's list is remembered by its
/// link, to read again.
/// </summary>
public sealed partial class SpotifyImportViewModel : ViewModelBase
{
    private readonly SpotifyImports _imports;
    private readonly ICatalog _catalog;
    private readonly LocalPlaylistLibrary? _localPlaylists;
    private readonly Func<bool> _isSignedIn;
    private readonly NavigationService? _navigation;
    private readonly TrackActionsViewModel? _actions;

    private ImportReview? _review;
    private List<ImportRecord> _records = [];
    private CancellationTokenSource? _work;

    public SpotifyImportViewModel(
        SpotifyImports imports, ICatalog catalog, LocalPlaylistLibrary? localPlaylists, Func<bool> isSignedIn,
        NavigationService? navigation = null, TrackActionsViewModel? actions = null)
    {
        _imports = imports;
        _catalog = catalog;
        _localPlaylists = localPlaylists;
        _isSignedIn = isSignedIn;
        _navigation = navigation;
        _actions = actions;
        _toYouTubeMusic = isSignedIn();
    }

    /// <summary>Picks the file to import; null when nothing was picked. Set by
    /// the view, which has the window's storage provider.</summary>
    public Func<Task<PickedFile?>>? PickFile { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStart), nameof(IsReading), nameof(IsReady), nameof(IsBusy), nameof(IsMatching),
        nameof(IsCreating), nameof(CanStartWork))]
    private ImportPhase _phase;

    public bool IsStart => Phase == ImportPhase.Start;
    public bool IsReading => Phase == ImportPhase.Reading;

    public string ChooseFileText => Strings.Get(_fromFile ? "import.chooseOther" : "import.chooseFile");
    private bool _fromFile;
    public bool IsReady => Phase is ImportPhase.Ready or ImportPhase.Matching or ImportPhase.Creating;
    public bool IsBusy => Phase is ImportPhase.Reading or ImportPhase.Matching or ImportPhase.Creating;
    public bool IsMatching => Phase == ImportPhase.Matching;
    public bool IsCreating => Phase == ImportPhase.Creating;
    public bool CanStartWork => Phase == ImportPhase.Ready;

    /// <summary>What was read: the file, by its name, or how many links.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseFileText))]
    private string? _fileName;

    /// <summary>Why the file couldn't be read, or why creating stopped; null when nothing is wrong.</summary>
    [ObservableProperty] private string? _problem;

    /// <summary>What the last step did, said once it is done.</summary>
    [ObservableProperty] private string? _result;

    public ObservableCollection<ImportListItem> Lists { get; } = [];

    /// <summary>Spotify links pasted, one or more; nothing is sent until Read.</summary>
    [ObservableProperty] private string _linkText = "";

    public bool CanReadLinks => _imports.ReadLink is not null;

    /// <summary>Lists imported from links before, newest first, to read again.</summary>
    public ObservableCollection<EarlierLinkItem> EarlierLinks { get; } = [];

    public bool HasEarlierLinks => EarlierLinks.Count > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedList))]
    private ImportListItem? _selectedList;

    public bool HasSelectedList => SelectedList is not null;

    /// <summary>The selected list's rows, as the review shows them.</summary>
    [ObservableProperty] private IReadOnlyList<ImportRowItem> _rows = [];

    /// <summary>Only the rows that add nothing, or that the user changed.</summary>
    [ObservableProperty] private bool _onlyProblems;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ToThisComputer))]
    private bool _toYouTubeMusic;

    public bool ToThisComputer
    {
        get => !ToYouTubeMusic;
        set => ToYouTubeMusic = !value;
    }

    /// <summary>Creating on YouTube Music needs the account.</summary>
    public bool CanUseYouTubeMusic => _isSignedIn();

    public bool CanUseThisComputer => _localPlaylists is { IsReadOnly: false };

    [ObservableProperty] private int _progressDone;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private string? _progressText;

    /// <summary>Songs of the chosen lists not searched yet, or whose search failed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnsearched), nameof(FindText))]
    private int _unsearched;

    public bool HasUnsearched => Unsearched > 0;

    public string FindText => Strings.Format("import.find", ("count", Unsearched));

    /// <summary>The playlists Create would make or add to, said on its button.</summary>
    [ObservableProperty] private string _createText = Strings.Get("import.create.none");

    [ObservableProperty] private bool _canCreate;

    /// <summary>The page opens again: the sign-in may have changed since.</summary>
    public void Shown()
    {
        if (ToYouTubeMusic && !CanUseYouTubeMusic)
            ToYouTubeMusic = false;
        else if (Phase == ImportPhase.Start)
            ToYouTubeMusic = CanUseYouTubeMusic;
        Refresh();
        if (Phase == ImportPhase.Start)
            _ = LoadEarlierLinksAsync();
    }

    private async Task LoadEarlierLinksAsync()
    {
        var records = await Task.Run(_imports.LoadRecords);
        if (Phase == ImportPhase.Start)
            ShowEarlierLinks(records);
    }

    private void ShowEarlierLinks(IEnumerable<ImportRecord> records)
    {
        EarlierLinks.Clear();
        if (CanReadLinks)
        {
            foreach (var record in records
                         .Where(r => r.Source is not null)
                         .OrderByDescending(r => r.ImportedAt)
                         .DistinctBy(r => r.Source))
            {
                if (SpotifyLinks.Parse(record.Source) is { } link)
                    EarlierLinks.Add(new EarlierLinkItem(record.ListName.Length > 0 ? record.ListName : record.PlaylistName,
                        link, ReadAgainAsync));
            }
        }
        OnPropertyChanged(nameof(HasEarlierLinks));
    }

    /// <summary>Reads the lists of the pasted links, one at a time; shows those
    /// read, and says which couldn't be.</summary>
    [RelayCommand]
    private async Task ReadLinksAsync()
    {
        if (IsBusy)
            return;
        var words = SpotifyLinks.Words(LinkText);
        var links = words.Select(SpotifyLinks.Parse).OfType<SpotifyListLink>().Distinct().ToList();
        var shortLinks = words.Any(SpotifyLinks.IsShortLink);
        if (links.Count == 0)
        {
            Result = null;
            Problem = Strings.Get(shortLinks ? "import.problem.shortLink" : "import.problem.noLink");
            return;
        }
        if (await ReadAsync(links) && shortLinks)
            Problem = string.Join("\n", new[] { Problem, Strings.Get("import.problem.shortLink") }.OfType<string>());
    }

    private Task ReadAgainAsync(SpotifyListLink link) => IsBusy ? Task.CompletedTask : ReadAsync([link]);

    // False when nothing could be read.
    private async Task<bool> ReadAsync(IReadOnlyList<SpotifyListLink> links)
    {
        if (_imports.ReadLink is not { } read)
            return false;
        var previous = Phase;
        Phase = ImportPhase.Reading;
        Problem = null;
        Result = null;
        var lists = new List<ImportedList>();
        var problems = new List<string>();
        try
        {
            foreach (var link in links)
            {
                try
                {
                    lists.Add(await read(link, CancellationToken.None));
                }
                catch (SpotifyListUnavailableException)
                {
                    problems.Add(Strings.Format("import.problem.linkUnavailable", ("link", link.WebUrl)));
                }
                // Whatever one link's answer was, the other links still count.
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLog.Write("import", $"Couldn't read {link.Uri}: {ex.GetType().Name}: {ex.Message}");
                    problems.Add(Strings.Format("import.problem.linkFailed", ("link", link.WebUrl), ("error", ex.Message)));
                }
            }

            if (lists.Count > 0)
            {
                var records = await Task.Run(_imports.LoadRecords);
                Show(Strings.Format("import.fromLinks", ("count", lists.Count)), lists, records, fromFile: false);
                LinkText = "";
            }
        }
        finally
        {
            if (Phase == ImportPhase.Reading)
                Phase = previous == ImportPhase.Reading ? ImportPhase.Start : previous;
        }
        Problem = problems.Count > 0 ? string.Join("\n", problems) : Problem;
        return lists.Count > 0;
    }

    /// <summary>The name a list is created under: its own, or Liked Songs'.</summary>
    public static string PlaylistNameOf(ImportedList list) =>
        list.Kind == ImportedListKind.LikedSongs || list.Name.Length == 0
            ? Strings.Get(list.Kind == ImportedListKind.LikedSongs ? "import.likedSongs" : "import.unnamed")
            : list.Name;

    [RelayCommand]
    private async Task ChooseFileAsync()
    {
        if (PickFile is null || IsBusy || await PickFile() is not { } picked)
            return;
        if (picked.LocalPath is not { } path)
        {
            Problem = Strings.Get("import.problem.notLocal");
            return;
        }

        var previous = Phase;
        Phase = ImportPhase.Reading;
        Problem = null;
        Result = null;
        try
        {
            var (lists, records) = await Task.Run(() => (SpotifyImports.Read(path), _imports.LoadRecords()));
            Show(Path.GetFileName(path), lists, records);
        }
        catch (ImportFormatException ex)
        {
            Problem = Strings.Format(ex.Problem == ImportProblem.NoTitleColumn ? "import.problem.noTitle" : "import.problem.unreadable",
                ("name", Path.GetFileName(path)));
            Phase = previous == ImportPhase.Reading ? ImportPhase.Start : previous;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Problem = Strings.Format("import.problem.failed", ("name", Path.GetFileName(path)), ("error", ex.Message));
            Phase = previous == ImportPhase.Reading ? ImportPhase.Start : previous;
        }
    }

    /// <summary>Shows a file's lists, none of them chosen yet, against the
    /// earlier imports (read from the disk when null).</summary>
    internal void Show(string fileName, IReadOnlyList<ImportedList> lists, IReadOnlyList<ImportRecord>? records = null,
        bool fromFile = true)
    {
        _records = (records ?? _imports.LoadRecords()).ToList();
        ShowEarlierLinks(_records);
        if (_review is not null)
            _review.Changed -= OnReviewChanged;
        _review = new ImportReview(lists);
        _review.Changed += OnReviewChanged;
        _fromFile = fromFile;
        FileName = fileName;
        OnPropertyChanged(nameof(ChooseFileText));
        foreach (var item in Lists)
            item.PropertyChanged -= OnListChanged;
        Lists.Clear();
        foreach (var list in lists)
        {
            var item = new ImportListItem(list, _review);
            item.PropertyChanged += OnListChanged;
            Lists.Add(item);
        }
        ApplyTarget();
        SelectedList = Lists.FirstOrDefault();
        Phase = ImportPhase.Ready;
        if (lists.Count == 0)
            Problem = Strings.Format("import.problem.empty", ("name", fileName));
        Refresh();
    }

    partial void OnToYouTubeMusicChanged(bool value)
    {
        if (value && !CanUseYouTubeMusic)
        {
            ToYouTubeMusic = false;
            return;
        }
        ApplyTarget();
        Refresh();
    }

    private ImportTarget Target => ToYouTubeMusic ? ImportTarget.YouTubeMusic : ImportTarget.ThisComputer;

    // Which lists were imported to this place before, and so add only what is new.
    private void ApplyTarget()
    {
        foreach (var item in Lists)
        {
            var earlier = ImportPlan.EarlierImport(_records, item.List, Target);
            // A local playlist deleted since can't take the new songs.
            if (earlier is not null && Target == ImportTarget.ThisComputer &&
                _localPlaylists?.Find(earlier.Parts[^1].PlaylistId) is null)
                earlier = null;
            item.Earlier = earlier;
        }
        RebuildRows();
    }

    partial void OnSelectedListChanged(ImportListItem? value) => RebuildRows();

    partial void OnOnlyProblemsChanged(bool value) => RebuildRows();

    private void OnListChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImportListItem.IsChosen) or nameof(ImportListItem.AddToEarlier))
        {
            if (ReferenceEquals(sender, SelectedList) && e.PropertyName == nameof(ImportListItem.AddToEarlier))
                RebuildRows();
            Refresh();
        }
    }

    private void OnReviewChanged(string? key)
    {
        foreach (var row in Rows)
            if (key is null || row.Key == key)
                row.Refresh();
        Refresh();
    }

    private void RebuildRows()
    {
        if (SelectedList is not { } list || _review is null)
        {
            Rows = [];
            return;
        }
        var rows = list.InScope.Select(t => new ImportRowItem(t, _review, ChooseAnotherAsync));
        Rows = (OnlyProblems ? rows.Where(r => r.IsProblem) : rows).ToList();
    }

    partial void OnRowsChanged(IReadOnlyList<ImportRowItem> value) => OnPropertyChanged(nameof(NoRowsText));

    /// <summary>Why the review shows no rows; null while it shows some.</summary>
    public string? NoRowsText =>
        Rows.Count > 0 || SelectedList is not { } list ? null
        : list.InScope.Count == 0
            ? Strings.Get(list.HasEarlier && list.AddToEarlier ? "import.rows.noneNew" : "import.rows.none")
            : Strings.Get("import.rows.noProblems");

    // Counts, the button's text, and what is left to search.
    private void Refresh()
    {
        foreach (var item in Lists)
            item.Refresh();
        if (_review is null)
            return;
        var chosen = Lists.Where(l => l.IsChosen).ToList();
        Unsearched = _review.Unsearched(chosen.Select(l => l.InScopeList)).Count;
        var adds = chosen.Sum(l => _review.SongsOf(l.InScope).Count);
        CanCreate = Phase == ImportPhase.Ready && adds > 0 && chosen.Count > 0 &&
                    (ToYouTubeMusic ? CanUseYouTubeMusic : CanUseThisComputer);
        CreateText = chosen.Count == 0
            ? Strings.Get("import.create.none")
            : Strings.Format("import.create.label", ("songs", adds), ("lists", chosen.Count));
        OnPropertyChanged(nameof(CanUseYouTubeMusic));
    }

    /// <summary>Searches YouTube Music for the chosen lists' songs not searched
    /// yet (or whose search failed), one at a time.</summary>
    [RelayCommand]
    private async Task FindSongsAsync()
    {
        if (_review is null || Phase != ImportPhase.Ready)
            return;
        var tracks = _review.Unsearched(Lists.Where(l => l.IsChosen).Select(l => l.InScopeList));
        if (tracks.Count == 0)
            return;

        using var work = _work = new CancellationTokenSource();
        Phase = ImportPhase.Matching;
        Problem = null;
        Result = null;
        var progress = new Progress<ImportProgress>(p =>
        {
            (ProgressDone, ProgressTotal) = (p.Done, p.Total);
            ProgressText = Strings.Format("import.matching.progress", ("done", p.Done), ("total", p.Total));
        });
        try
        {
            var matches = await _imports.Matcher.MatchAsync(tracks, progress, work.Token);
            _review.Add(matches);
            var failed = matches.Values.Count(m => m.State == ImportMatchState.Failed);
            Result = failed > 0
                ? Strings.Format("import.matching.someFailed", ("count", failed))
                : Strings.Get("import.matching.done");
        }
        catch (OperationCanceledException)
        {
            Result = Strings.Get("import.matching.cancelled");
        }
        finally
        {
            _work = null;
            Phase = ImportPhase.Ready;
            await Task.Run(_imports.SaveMatches);
            Refresh();
        }
    }

    [RelayCommand]
    private void Cancel() => _work?.Cancel();

    /// <summary>Another song for a row: the search's first results, to pick from.</summary>
    private async Task<IReadOnlyList<TrackInfo>> ChooseAnotherAsync(ImportedTrack track)
    {
        var query = ImportMatcher.Query(track);
        var text = query.Artist.Length > 0 ? $"{query.Artist} {query.Title}" : query.Title;
        var results = await _imports.SearchSongs(text, CancellationToken.None);
        return results.Take(Core.Stations.StationResolver.CandidateCount).ToList();
    }

    /// <summary>
    /// Creates the chosen lists' playlists, or adds what is new to the ones an
    /// earlier import made, in the chosen place. Stops at the first list that
    /// fails and says what was made of it; lists made before it stay made.
    /// </summary>
    [RelayCommand]
    private async Task CreateAsync()
    {
        if (_review is null || !CanCreate)
            return;
        var target = Target;
        var chosen = Lists.Where(l => l.IsChosen).ToList();
        Phase = ImportPhase.Creating;
        Problem = null;
        Result = null;
        var made = new List<(string Name, string Id)>();
        var songsAdded = 0;
        ProgressTotal = chosen.Count;
        try
        {
            for (var i = 0; i < chosen.Count; i++)
            {
                var item = chosen[i];
                ProgressDone = i;
                ProgressText = Strings.Format("import.creating.progress", ("name", item.DisplayName), ("done", i + 1), ("total", chosen.Count));
                var earlier = item.AddToEarlier ? item.Earlier : null;
                var progress = new ListProgress(earlier);
                Exception? failure = null;
                try
                {
                    await ImportListAsync(item, earlier, _review.SongsOf(item.InScope), target, progress, made);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failure = ex;
                }
                songsAdded += progress.Added;
                // Recorded even when it stopped partway, so the next import
                // adds the rest to the same playlists and nothing twice.
                if (RecordOf(item, earlier, target, progress, complete: failure is null) is { } record)
                {
                    if (earlier is not null)
                        _records.Remove(earlier);
                    _records.Add(record);
                }
                if (failure is not null)
                {
                    AppLog.Write("import", $"Importing '{item.DisplayName}' stopped: {failure.GetType().Name}: {failure.Message}");
                    Problem = failure is NotSignedInException
                        ? Strings.Get("import.problem.signedOut")
                        : Strings.Format("import.problem.createFailed", ("name", item.DisplayName), ("error", failure.Message));
                    break;
                }
            }
        }
        finally
        {
            var records = _records.ToList();
            await Task.Run(() => _imports.SaveRecords(records));
            ShowEarlierLinks(records);
            Phase = ImportPhase.Ready;
            ApplyTarget();
            Refresh();
        }

        if (made.Count == 0 && songsAdded == 0)
            return;
        Result = Strings.Format("import.created", ("songs", songsAdded), ("lists", made.Count));
        if (_actions is not null && made.Count > 0)
        {
            var (name, id) = made[0];
            _actions.Toasts.Offer(Strings.Format("import.createdToast", ("name", name), ("more", made.Count - 1)),
                Strings.Get("common.open"), () =>
                {
                    if (target == ImportTarget.YouTubeMusic)
                        _navigation?.OpenPlaylist(id);
                    else
                        _navigation?.OpenLocalPlaylist(id);
                    return Task.CompletedTask;
                });
        }
    }

    /// <summary>What one list's import has done so far: its playlists, with
    /// their counts, and the songs it added.</summary>
    private sealed class ListProgress(ImportRecord? earlier)
    {
        public List<ImportedPart> Parts { get; } = earlier?.Parts.ToList() ?? [];

        public int Added { get; set; }
    }

    // New playlists, or the new songs added to the earlier import's last
    // playlist while it has room and then to new parts.
    private async Task ImportListAsync(
        ImportListItem item, ImportRecord? earlier, IReadOnlyList<TrackInfo> songs, ImportTarget target,
        ListProgress progress, List<(string Name, string Id)> made)
    {
        var cap = ImportPlan.CapOf(target);
        if (earlier is null)
        {
            foreach (var planned in ImportPlan.Split(item.DisplayName, songs, cap))
                await CreatePartAsync(planned, target, progress, made);
            return;
        }
        var (toLast, newParts) = ImportPlan.Append(earlier, songs, cap);
        if (toLast.Count > 0)
            await AddAsync(progress, progress.Parts.Count - 1, toLast, target);
        foreach (var planned in newParts)
            await CreatePartAsync(planned, target, progress, made);
    }

    /// <summary>The record of a list's import; null when it changed nothing
    /// (it stopped before any playlist or song), and the earlier record stays.</summary>
    private ImportRecord? RecordOf(
        ImportListItem item, ImportRecord? earlier, ImportTarget target, ListProgress progress, bool complete)
    {
        if (progress.Parts.Count == 0 ||
            (!complete && progress.Added == 0 && progress.Parts.Count == (earlier?.Parts.Count ?? 0)))
            return null;
        IReadOnlyList<string> keys = complete
            ? item.List.Tracks.Select(ImportMatcher.KeyOf).ToList()
            : [.. earlier?.Keys ?? [],
               .. ImportPlan.Covered(item.InScope, t => _review!.SongOf(t) is not null, progress.Added).Select(ImportMatcher.KeyOf)];
        return new ImportRecord(item.List.Name, item.List.Kind, target, earlier?.PlaylistName ?? item.DisplayName,
            progress.Parts.ToList(), keys, DateTimeOffset.UtcNow, item.List.Source);
    }

    private async Task CreatePartAsync(
        PlannedPlaylist planned, ImportTarget target, ListProgress progress, List<(string Name, string Id)> made)
    {
        if (target == ImportTarget.ThisComputer)
        {
            var id = _localPlaylists!.Import(planned.Name, planned.Songs.Select(LocalPlaylistItem.Of)).Id;
            made.Add((planned.Name, id));
            progress.Parts.Add(new ImportedPart(id, planned.Songs.Count));
            progress.Added += planned.Songs.Count;
            return;
        }
        var created = await _catalog.CreatePlaylistAsync(planned.Name, privacy: PlaylistPrivacy.Private);
        made.Add((planned.Name, created));
        progress.Parts.Add(new ImportedPart(created, 0));
        await AddAsync(progress, progress.Parts.Count - 1, planned.Songs, target);
    }

    // In batches, duplicates kept: a Spotify playlist may hold a song twice.
    // Each batch that lands is counted at once, in case a later one fails.
    private async Task AddAsync(ListProgress progress, int part, IReadOnlyList<TrackInfo> songs, ImportTarget target)
    {
        var playlistId = progress.Parts[part].PlaylistId;
        if (target == ImportTarget.ThisComputer)
        {
            if (!_localPlaylists!.Append(playlistId, songs.Select(LocalPlaylistItem.Of)))
                throw new InvalidOperationException(Strings.Get("import.problem.localGone"));
            Count(songs.Count);
            return;
        }
        for (var i = 0; i < songs.Count; i += ImportPlan.AddBatch)
        {
            var batch = songs.Skip(i).Take(ImportPlan.AddBatch).Select(s => s.VideoId).ToList();
            await _catalog.AddToPlaylistAsync(playlistId, batch, keepDuplicates: true);
            Count(batch.Count);
        }

        void Count(int n)
        {
            progress.Parts[part] = progress.Parts[part] with { Count = progress.Parts[part].Count + n };
            progress.Added += n;
        }
    }
}

/// <summary>A list of the file, with whether it is chosen and what it adds.</summary>
public sealed partial class ImportListItem(ImportedList list, ImportReview review) : ObservableObject
{
    public ImportedList List { get; } = list;

    public string DisplayName { get; } = SpotifyImportViewModel.PlaylistNameOf(list);

    [ObservableProperty] private bool _isChosen;

    /// <summary>The earlier import of this list to the chosen place; null when there is none.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEarlier), nameof(EarlierText), nameof(AddToEarlierText))]
    private ImportRecord? _earlier;

    public string? AddToEarlierText => Earlier is { } e
        ? Strings.Format("import.list.addToEarlier", ("name", e.PlaylistName))
        : null;

    /// <summary>Add the songs new since the earlier import to its playlist,
    /// rather than make a new one.</summary>
    [ObservableProperty] private bool _addToEarlier = true;

    public bool HasEarlier => Earlier is not null;

    public string? EarlierText => Earlier is { } e
        ? Strings.Format("import.list.earlier", ("name", e.PlaylistName), ("date", e.ImportedAt.ToLocalTime().ToString("d")))
        : null;

    /// <summary>The rows this list adds from: all of them, or those new since the earlier import.</summary>
    public IReadOnlyList<ImportedTrack> InScope =>
        AddToEarlier && Earlier is { } e ? ImportPlan.NewSince(List, e) : List.Tracks;

    internal ImportedList InScopeList => List with { Tracks = InScope };

    [ObservableProperty] private string _summary = "";

    partial void OnEarlierChanged(ImportRecord? value) => OnPropertyChanged(nameof(InScope));

    partial void OnAddToEarlierChanged(bool value) => OnPropertyChanged(nameof(InScope));

    internal void Refresh()
    {
        var scope = InScope;
        var counts = review.CountsOf(scope);
        var parts = new List<string>
        {
            HasEarlier && AddToEarlier
                ? Strings.Format("import.list.newSongs", ("count", scope.Count))
                : Strings.Format("import.list.songs", ("count", scope.Count)),
        };
        if (counts.Found > 0 || counts.NotFound > 0)
            parts.Add(Strings.Format("import.list.found", ("count", counts.Found)));
        if (counts.NotFound > 0)
            parts.Add(Strings.Format("import.list.notFound", ("count", counts.NotFound)));
        if (counts.LeftOut > 0)
            parts.Add(Strings.Format("import.list.leftOut", ("count", counts.LeftOut)));
        if (List.Skipped > 0)
            parts.Add(Strings.Format("import.list.skipped", ("count", List.Skipped)));
        if (List.Incomplete)
            parts.Add(Strings.Format("import.list.incomplete", ("count", List.Tracks.Count + List.Skipped)));
        Summary = string.Join(" · ", parts);
    }
}

/// <summary>A row of the review: the song as Spotify listed it, and what it adds.</summary>
public sealed partial class ImportRowItem(
    ImportedTrack source, ImportReview review, Func<ImportedTrack, Task<IReadOnlyList<TrackInfo>>> searchOthers)
    : ObservableObject
{
    public ImportedTrack Source { get; } = source;

    public string Key { get; } = ImportMatcher.KeyOf(source);

    public string SourceTitle => Source.Title;

    public string SourceArtist => string.Join(", ", Source.Artists);

    /// <summary>The song the row adds; null when it adds none.</summary>
    public TrackInfo? Song => review.SongOf(Source);

    public bool HasSong => Song is not null;

    public bool IsLeftOut => review.IsLeftOut(Source);

    /// <summary>Adds nothing, or the user changed it.</summary>
    public bool IsProblem => Song is null || review.IsChosen(Source);

    /// <summary>What the row adds, in words: the song, or why there is none.</summary>
    public string Outcome => IsLeftOut
        ? Strings.Get("import.row.leftOut")
        : Song is { } song
            ? (review.IsChosen(Source) ? Strings.Format("import.row.chosen", ("song", Describe(song))) : Describe(song))
            : review.MatchOf(Source).State switch
            {
                ImportMatchState.NotFound => Strings.Get("import.row.notFound"),
                _ => Strings.Get("import.row.notSearched"),
            };

    public string AccessibleName => $"{SourceTitle}, {SourceArtist}: {Outcome}";

    public ObservableCollection<TrackInfo> Others { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChooseText))]
    private bool _isChoosing;

    public string ChooseText => Strings.Get(IsChoosing ? "import.row.hideOthers" : "import.row.chooseAnother");

    [ObservableProperty] private bool _isSearching;

    [ObservableProperty] private string? _othersNote;

    private static string Describe(TrackInfo song) =>
        song.ArtistDisplay is { Length: > 0 } artist ? $"{song.Title} · {artist}" : song.Title;

    [RelayCommand]
    private void LeaveOut() => review.SetLeftOut(Source, true);

    [RelayCommand]
    private void PutBack() => review.SetLeftOut(Source, false);

    /// <summary>Shows other songs to pick from, or hides them.</summary>
    [RelayCommand]
    private async Task ChooseAnotherAsync()
    {
        if (IsChoosing)
        {
            IsChoosing = false;
            return;
        }
        IsChoosing = true;
        IsSearching = true;
        OthersNote = null;
        Others.Clear();
        try
        {
            foreach (var song in await searchOthers(Source))
                Others.Add(song);
            if (Others.Count == 0)
                OthersNote = Strings.Get("import.row.noOthers");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            OthersNote = Strings.Format("import.row.othersFailed", ("error", ex.Message));
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private void Pick(TrackInfo? song)
    {
        if (song is null)
            return;
        review.Choose(Source, song);
        IsChoosing = false;
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(Song));
        OnPropertyChanged(nameof(HasSong));
        OnPropertyChanged(nameof(IsLeftOut));
        OnPropertyChanged(nameof(IsProblem));
        OnPropertyChanged(nameof(Outcome));
        OnPropertyChanged(nameof(AccessibleName));
    }
}

/// <summary>A list imported from a link before, to read again and add what is new.</summary>
public sealed partial class EarlierLinkItem(string name, SpotifyListLink link, Func<SpotifyListLink, Task> readAgain)
{
    public string Name { get; } = name;

    public SpotifyListLink Link { get; } = link;

    public string ReadAgainName => Strings.Format("import.readAgainName", ("name", Name));

    [RelayCommand]
    private Task ReadAgainAsync() => readAgain(Link);
}
