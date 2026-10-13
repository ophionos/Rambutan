using System;
using System.Collections.ObjectModel;
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
            foreach (var section in page.Sections)
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

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private void OpenCategory(MoodCategory category) => _navigation.OpenMoodCategory(category);
}
