using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Moods &amp; genres: YouTube Music's grid of tiles, one per mood and
/// per genre, opened from Home's header. A tile opens that mood's page.
/// </summary>
public partial class MoodsPageViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly NavigationService _navigation;

    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private LoadError? _loadError;
    /// <summary>The page loaded with no tiles on it.</summary>
    [ObservableProperty] private bool _isEmpty;

    public ObservableCollection<MoodSection> Sections { get; } = [];

    /// <summary>Leaves out a tile that an earlier group already shows, and a group
    /// left with none. YouTube Music's first group ("For you") repeats tiles of
    /// the groups after it, which reads as noise under the Search page's recent
    /// searches; the Moods &amp; genres page keeps the groups as sent.</summary>
    public bool LeavesOutRepeats { get; set; }

    public MoodsPageViewModel(ICatalog catalog, NavigationService navigation)
    {
        _catalog = catalog;
        _navigation = navigation;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        IsEmpty = false;
        try
        {
            var page = await _catalog.GetMoodsAndGenresAsync();
            Sections.Clear();
            foreach (var section in LeavesOutRepeats ? WithoutRepeats(page.Sections) : page.Sections)
                Sections.Add(section);
            IsEmpty = Sections.Count == 0;
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.Moods, ReloadCommand);
        }
        finally
        {
            IsLoading = false;
        }
    }

    /// <summary>Each group without the tiles of the groups before it (a tile is
    /// its params, or its title when it has none); repeats inside one group stay.</summary>
    private static IEnumerable<MoodSection> WithoutRepeats(IReadOnlyList<MoodSection> sections)
    {
        var shown = new HashSet<string>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            var kept = section.Categories.Where(c => !shown.Contains(Key(c))).ToList();
            foreach (var category in kept)
                shown.Add(Key(category));
            if (kept.Count > 0)
                yield return kept.Count == section.Categories.Count ? section : section with { Categories = kept };
        }

        static string Key(MoodCategory category) =>
            string.IsNullOrEmpty(category.Params) ? "title:" + category.Title : "params:" + category.Params;
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private void OpenCategory(MoodCategory category) => _navigation.OpenMoodCategory(category);
}
