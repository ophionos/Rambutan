using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.ViewModels;

/// <summary>
/// One mood or genre: its shelves, opened from a Moods &amp; genres tile
/// or from a shelf's "See all" on another of these pages.
/// </summary>
public partial class MoodCategoryPageViewModel : ViewModelBase
{
    private readonly ICatalog _catalog;
    private readonly NavigationService _navigation;
    private readonly MoodCategory _category;

    /// <summary>The tile's name until the page says its own.</summary>
    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private LoadError? _loadError;
    /// <summary>The page loaded with no shelves on it.</summary>
    [ObservableProperty] private bool _isEmpty;

    public ObservableCollection<MoodShelf> Shelves { get; } = [];

    public MoodCategoryPageViewModel(ICatalog catalog, NavigationService navigation, MoodCategory category)
    {
        _catalog = catalog;
        _navigation = navigation;
        _category = category;
        _title = category.Title;
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        LoadError = null;
        IsEmpty = false;
        try
        {
            var page = await _catalog.GetMoodCategoryAsync(_category);
            if (!string.IsNullOrEmpty(page.Title))
                Title = page.Title;
            Shelves.Clear();
            foreach (var shelf in page.Shelves)
                Shelves.Add(new MoodShelf(shelf, shelf.More is null ? null : OpenMoreCommand));
            IsEmpty = Shelves.Count == 0;
        }
        catch (Exception ex)
        {
            LoadError = LoadError.From(ex, LoadSubject.MoodCategory, ReloadCommand);
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    [RelayCommand]
    private void OpenMore(Shelf shelf)
    {
        if (shelf.More is { } more)
            _navigation.OpenMoodCategory(more);
    }

    [RelayCommand]
    private Task OpenEntryAsync(CatalogEntry entry) => _navigation.OpenEntryAsync(entry);

    /// <summary>A card's hover play button: plays without opening the page.</summary>
    [RelayCommand]
    private Task PlayEntryAsync(CatalogEntry entry) => _navigation.PlayEntryAsync(entry);
}

/// <summary>A shelf with its "See all", which is null where YouTube Music links no page.</summary>
public sealed record MoodShelf(Shelf Shelf, ICommand? SeeAll);
