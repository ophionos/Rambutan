using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Layout;

namespace Rambutan.App.Views;

public partial class SearchPage : UserControl
{
    /// <summary>The search box's width at most: a results item at its widest (the
    /// list's FixedWidth, the title's maximum, the liked mark, the album with its
    /// gap, and the item's padding).</summary>
    public const double FieldMaxWidth =
        234 + TrackRowColumns.MaxTitle + TrackRowColumns.Liked + TrackRowColumns.AlbumGap + TrackRowColumns.MaxAlbum + 16;

    /// <summary>The side column's left margin: the gap between the two columns.</summary>
    public static readonly Thickness SideColumnMargin = new(SideColumnFit.Gap, 0, 0, 0);

    /// <summary>What the results' scroller keeps of the page's width: the page's
    /// 4 at the left, and the scroller's 16 padding at the right.</summary>
    private const double PaneInsets = 20;

    private SearchPageViewModel? _vm;
    private Window? _window;

    public SearchPage()
    {
        InitializeComponent();

        // Arriving on Search means you're about to type. Ctrl+F reads
        // as "find". A fresh view focuses
        // itself on load; Ctrl+F on a Search page that's already showing creates no
        // view, so the page asks instead.
        Loaded += (_, _) => FocusSearchBox();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null)
            {
                _vm.FocusRequested -= FocusSearchBox;
                _vm.PropertyChanged -= OnViewModelPropertyChanged;
            }
            _vm = DataContext as SearchPageViewModel;
            if (_vm is not null)
            {
                _vm.FocusRequested += FocusSearchBox;
                _vm.PropertyChanged += OnViewModelPropertyChanged;
            }
            ApplyColumns();
        };
        // The page's width, not the results scroller's: that one has none
        // before the first search, whose skeletons already need the answer.
        SizeChanged += (_, _) => ApplyColumns();

        // The drop-down shows while the box or the list under it
        // has focus. LostFocus fires before the new element gets focus, so the
        // check waits a turn of the dispatcher.
        SearchBox.GotFocus += (_, _) => _vm?.SetFieldActive(true);
        SearchBox.LostFocus += (_, _) => Dispatcher.UIThread.Post(CheckFieldActive);
        SuggestionsPanel.LostFocus += (_, _) => Dispatcher.UIThread.Post(CheckFieldActive);

        // Tunnel, so Up on the first row and Enter reach us before the ListBox
        // moves its selection or swallows the key.
        SuggestionList.AddHandler(KeyDownEvent, OnSuggestionKeyDown, RoutingStrategies.Tunnel);
        SuggestionList.Tapped += OnSuggestionTapped;

        RecentList.AddHandler(KeyDownEvent, OnRecentKeyDown);
        RecentList.AddHandler(Button.ClickEvent, OnRecentClick);

        ResultsList.ContainerPrepared += OnResultContainerPrepared;
        ResultsList.SelectionChanged += OnResultSelectionChanged;

        // A filtered search's next page, as the list nears its end. Extent changes
        // raise this too, so a first page too short to scroll asks for the next.
        ResultsScroller.ScrollChanged += (_, _) =>
        {
            if (_vm is { HasMore: true, MoreError: null } vm &&
                ScrollPaging.IsNearEnd(
                    ResultsScroller.Offset.Y, ResultsScroller.Viewport.Height, ResultsScroller.Extent.Height))
            {
                vm.LoadMoreCommand.Execute(null);
            }
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // The list is its own top-level window; it must not float over other apps.
        _window = TopLevel.GetTopLevel(this) as Window;
        if (_window is not null)
            _window.Deactivated += OnWindowDeactivated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_window is not null)
            _window.Deactivated -= OnWindowDeactivated;
        _window = null;
    }

    protected override void OnUnloaded(RoutedEventArgs e)
    {
        base.OnUnloaded(e);
        // The VM outlives this view (NavigationService caches it); don't let it
        // keep a detached view alive.
        if (_vm is not null)
        {
            _vm.FocusRequested -= FocusSearchBox;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
            _vm.SetFieldActive(false);
        }
        _vm = null;
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SearchPageViewModel.ShowsSideColumn))
            ApplyColumns();
    }

    /// <summary>Tells the view model whether the pane has room for the side
    /// column, and gives the two columns their widths while it shows. Without
    /// it the first column has the pane, as before there was a second.</summary>
    private void ApplyColumns()
    {
        if (Bounds.Width <= 0)
            return;
        var fit = SideColumnFit.For(Bounds.Width - PaneInsets, FieldMaxWidth);
        if (_vm is not null)
            _vm.HasSideRoom = fit.Show;
        var show = fit.Show && _vm is { ShowsSideColumn: true };
        var definitions = Columns.ColumnDefinitions;
        definitions[0].Width = show ? new GridLength(fit.Main) : GridLength.Star;
        definitions[1].Width = show ? new GridLength(SideColumnFit.Gap + fit.Side) : new GridLength(0);
    }

    private void OnWindowDeactivated(object? sender, System.EventArgs e) => _vm?.CloseDropdown();

    // As History's headings (LibraryPage), except that a heading stays
    // hit-testable for its See all, so its class takes the rows' hover and
    // selection highlight from it (Styles/Browse.axaml). Containers are recycled between headings and
    // rows, so each one is set both ways. A heading takes no focus (the arrow
    // keys pass over it); a screen reader hears it as a heading, named by
    // AccessibleNames from its IHasAccessibleName.
    private void OnResultContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        var container = e.Container;
        if (ResultsList.Items[e.Index] is SearchResultHeading)
        {
            container.Focusable = false;
            container.Classes.Add("heading");
            AutomationProperties.SetHeadingLevel(container, 2);
        }
        else
        {
            container.Classes.Remove("heading");
            container.ClearValue(FocusableProperty);
            container.ClearValue(AutomationProperties.HeadingLevelProperty);
        }
    }

    // Whatever selects a heading (a press beside its See all, a key that lands
    // on it) moves on to the row beyond it in the same direction, else the row
    // before it: Enter then always has an entry to open.
    private void OnResultSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ResultsList.SelectedItem is not SearchResultHeading)
            return;
        var items = ResultsList.Items;
        var at = ResultsList.SelectedIndex;
        var from = e.RemovedItems.Count > 0 ? items.IndexOf(e.RemovedItems[0]) : -1;
        var step = from > at ? -1 : 1;
        ResultsList.SelectedIndex = NextRow(at, step) ?? NextRow(at, -step) ?? -1;

        int? NextRow(int index, int direction)
        {
            for (var i = index + direction; i >= 0 && i < items.Count; i += direction)
            {
                if (items[i] is not SearchResultHeading)
                    return i;
            }
            return null;
        }
    }

    private void CheckFieldActive()
    {
        if (_vm is not null && !SearchBox.IsFocused && !SuggestionsPanel.IsKeyboardFocusWithin)
            _vm.SetFieldActive(false);
    }

    /// <summary>Focus with the old query selected, so typing replaces it and
    /// Enter re-runs it.</summary>
    private void FocusSearchBox()
    {
        SearchBox.Focus(NavigationMethod.Tab);
        SearchBox.SelectAll();
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not SearchPageViewModel vm)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                vm.SearchCommand.Execute(null);
                break;
            case Key.Down when vm.IsDropdownOpen && SuggestionList.ItemCount > 0:
                SuggestionList.SelectedIndex = 0;
                SuggestionList.ContainerFromIndex(0)?.Focus(NavigationMethod.Directional);
                e.Handled = true;
                break;
            case Key.Escape when vm.IsDropdownOpen:
                vm.CloseDropdown();
                e.Handled = true;
                break;
        }
    }

    private void OnRecentKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && e.KeyModifiers == KeyModifiers.None &&
            e.Source is Button { DataContext: RecentQueryItem recent })
        {
            RemoveRecent(recent);
            e.Handled = true;
        }
    }

    private void OnRecentClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { DataContext: RecentQueryItem recent } button && button.Classes.Contains("recentremove"))
        {
            RemoveRecent(recent);
            e.Handled = true;
        }
    }

    /// <summary>Removes a chip and keeps keyboard focus in the list: on the chip
    /// that takes its place, or in the box when none is left.</summary>
    private void RemoveRecent(RecentQueryItem recent)
    {
        if (_vm is null)
            return;

        var index = _vm.Recent.IndexOf(recent);
        _vm.RemoveRecentCommand.Execute(recent.Query);
        Dispatcher.UIThread.Post(() =>
        {
            var chips = RovingFocus.Stops(RecentList).OfType<Button>()
                .Where(b => b.Classes.Contains("recentrun")).ToList();
            if (chips.Count == 0)
                SearchBox.Focus(NavigationMethod.Directional);
            else
                chips[System.Math.Min(System.Math.Max(index, 0), chips.Count - 1)].Focus(NavigationMethod.Directional);
        });
    }

    private void OnSuggestionKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null)
            return;

        switch (e.Key)
        {
            case Key.Enter:
                _vm.PickCommand.Execute(SuggestionList.SelectedItem);
                SearchBox.Focus(NavigationMethod.Directional);
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Escape:
                _vm.CloseDropdown();
                SearchBox.Focus(NavigationMethod.Directional);
                e.Handled = true;
                break;
            case Key.Up when SuggestionList.SelectedIndex <= 0:
                SuggestionList.SelectedIndex = -1;
                SearchBox.Focus(NavigationMethod.Directional);
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Delete when SuggestionList.SelectedItem is RecentQueryItem recent:
                var index = SuggestionList.SelectedIndex;
                _vm.RemoveRecentCommand.Execute(recent.Query);
                if (SuggestionList.ItemCount == 0)
                    SearchBox.Focus(NavigationMethod.Directional);
                else
                {
                    index = System.Math.Min(index, SuggestionList.ItemCount - 1);
                    SuggestionList.SelectedIndex = index;
                    SuggestionList.ContainerFromIndex(index)?.Focus(NavigationMethod.Directional);
                }
                e.Handled = true;
                break;
        }
    }

    /// <summary>One click picks, as in any search drop-down; a click on a row's
    /// own button (remove) is that button's.</summary>
    private void OnSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is null)
            return;

        for (var v = e.Source as Visual; v is not null && v != SuggestionList; v = v.GetVisualParent())
        {
            if (v is Button)
                return;
            if (v is ListBoxItem item)
            {
                _vm.PickCommand.Execute(item.DataContext);
                SearchBox.Focus(NavigationMethod.Pointer);
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
                return;
            }
        }
    }
}
