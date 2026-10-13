using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class SearchPage : UserControl
{
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
                _vm.FocusRequested -= FocusSearchBox;
            _vm = DataContext as SearchPageViewModel;
            if (_vm is not null)
                _vm.FocusRequested += FocusSearchBox;
        };

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
            _vm.SetFieldActive(false);
        }
        _vm = null;
    }

    private void OnWindowDeactivated(object? sender, System.EventArgs e) => _vm?.CloseDropdown();

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
