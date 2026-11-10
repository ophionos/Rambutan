using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Localization;
using Rambutan.Core.Layout;
using Rambutan.Core.Text;

namespace Rambutan.App.Views;

/// <summary>
/// The section list beside the column and the filter above it. Both only read
/// the page and show or hide parts of it: nothing is rebuilt, so every
/// control keeps its state.
/// </summary>
public partial class SettingsPage
{
    /// <summary>The settings column's widest, as in the XAML.</summary>
    public const double ColumnWidth = 720;

    /// <summary>The narrowest page with room for the section list: the column
    /// (4 + 720), a 32 gap, the 200-wide list, and the scroller's 12 inset on the
    /// right. The list's left margin in the XAML is the first two.</summary>
    public const double SectionListMinWidth = 4 + ColumnWidth + 32 + 200 + 12;

    /// <summary>The column's side margins (4 each) and the scroller's inset.</summary>
    private const double ColumnInsets = 4 + 4 + 12;

    private static readonly TimeSpan ScrollDuration = TimeSpan.FromMilliseconds(200);

    private readonly List<Section> _sections = [];
    private readonly List<Section> _listed = [];
    private readonly ObservableCollection<string> _entries = [];
    private readonly List<double> _tops = [];
    private readonly Dictionary<Control, IDisposable?> _filteredOut = [];
    private readonly Stopwatch _clock = new();

    private bool _wide;
    private bool _applyQueued;
    private bool _animating;
    private double _animationFrom;
    private double _animationTo;
    private Section? _chosen;
    private double _chosenOffset;
    private (Control Heading, NavigationMethod Method)? _focusAfterScroll;

    /// <summary>A heading, its section's panel, and its rows grouped into the
    /// units the filter shows or hides together.</summary>
    private sealed record Section(TextBlock Heading, Control Panel, List<List<Control>> Units);

    private void InitializeSections()
    {
        foreach (var child in Column.Children)
        {
            if (child is not StackPanel panel || panel.Children.Count == 0 ||
                panel.Children[0] is not TextBlock heading || !heading.Classes.Contains("settingsheader"))
            {
                continue;
            }
            var rows = panel.Children.OfType<Border>().FirstOrDefault(b => b.Classes.Contains("settingsgroup"))
                ?.Child as Panel;
            _sections.Add(new Section(heading, panel, Units(rows)));
        }

        SectionList.ItemsSource = _entries;
        SectionList.AddHandler(TappedEvent, OnSectionTapped);
        // Handled events too: the list marks Enter and Space handled as it selects.
        SectionList.AddHandler(KeyDownEvent, OnSectionKeyDown, RoutingStrategies.Bubble, handledEventsToo: true);

        FilterBox.TextChanged += (_, _) => ApplyFilter();
        FilterBox.KeyDown += OnFilterKeyDown;
        ClearFilterButton.Click += (_, _) =>
        {
            FilterBox.Text = "";
            FilterBox.Focus(NavigationMethod.Tab);
        };

        Scroller.ScrollChanged += (_, _) => OnScrolled();
        // Tunnel, and handled events too: the scroller consumes the wheel, and
        // a wheel during the animated scroll means the user took over.
        Scroller.AddHandler(PointerWheelChangedEvent, (_, _) => StopAnimation(),
            RoutingStrategies.Tunnel, handledEventsToo: true);
        Scroller.AddHandler(ScrollGestureEvent, (_, _) => StopAnimation(),
            RoutingStrategies.Bubble, handledEventsToo: true);

        SizeChanged += (_, e) => OnWidthChanged(e.NewSize.Width);
        // Which sections show follows the rows' own bindings (sign-in, platform,
        // a view model arriving); a layout pass is when that has settled.
        LayoutUpdated += (_, _) => RefreshList();

        foreach (var member in _sections.SelectMany(s => s.Units.SelectMany(u => u).Prepend(s.Panel)))
            member.PropertyChanged += OnMemberPropertyChanged;

        Strings.WhenChanged(this, static page => page.QueueApply());
    }

    /// <summary>
    /// The group's rows as units: a row with a label starts one, and what follows
    /// it without a label of its own (a .sub row, the disclosure under it, the
    /// watched folders' list) belongs to it, so a match keeps a setting's
    /// details with it and a parent never shows without them.
    /// </summary>
    private static List<List<Control>> Units(Panel? rows)
    {
        var units = new List<List<Control>>();
        if (rows is null)
            return units;
        foreach (var child in rows.Children)
        {
            var starts = child is Border border && border.Classes.Contains("settingrow") &&
                         !border.Classes.Contains("sub") &&
                         border.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Classes.Contains("settinglabel"));
            if (starts || units.Count == 0)
                units.Add([]);
            units[^1].Add(child);
        }
        return units;
    }

    private void OnWidthChanged(double width)
    {
        var column = Math.Max(0, Math.Min(ColumnWidth, width - ColumnInsets));
        Header.Width = column;
        NoMatch.Width = column;
        _wide = width >= SectionListMinWidth;
        RefreshList();
    }

    // ---- Filter ----

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        // Esc clears a filter; on an empty field it is left to the window
        // (leaving fullscreen, closing Now Playing).
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None || string.IsNullOrEmpty(FilterBox.Text))
            return;
        FilterBox.Text = "";
        e.Handled = true;
    }

    /// <summary>A row's own visibility changed (its binding, not the filter's
    /// override): what the filter left shown may have changed with it.</summary>
    private void OnMemberPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IsVisibleProperty && e.Priority != BindingPriority.Animation && _filteredOut.Count > 0)
            QueueApply();
    }

    private void QueueApply()
    {
        if (_applyQueued)
            return;
        _applyQueued = true;
        // After the bindings that the same change updates have run.
        Dispatcher.UIThread.Post(() =>
        {
            _applyQueued = false;
            ApplyFilter();
        });
    }

    /// <summary>
    /// Shows the rows whose texts (in the UI language, as shown) match the
    /// filter, with their sections, and hides the rest; an empty filter
    /// shows the page as its bindings have it.
    /// </summary>
    internal void ApplyFilter()
    {
        var query = FilterBox.Text;
        var words = SettingsFilter.Words(query);
        var anyShown = false;

        foreach (var section in _sections)
        {
            var heading = section.Heading.Text;
            var sectionShown = false;
            foreach (var unit in section.Units)
            {
                var match = words.Length == 0 || SettingsFilter.Matches(words, Texts(unit, heading));
                foreach (var member in unit)
                    SetFilteredOut(member, !match);
                sectionShown |= match && unit.Any(OwnVisible);
            }
            SetFilteredOut(section.Panel, words.Length > 0 && !sectionShown);
            anyShown |= sectionShown && OwnVisible(section.Panel);
        }

        var nothing = words.Length > 0 && !anyShown;
        Column.IsVisible = !nothing;
        NoMatch.IsVisible = nothing;
        NoMatchHint.Text = nothing ? Strings.Format("settings.filter.noMatchHint", ("query", query!.Trim())) : null;
        RefreshList();
    }

    private static List<string?> Texts(List<Control> unit, string? heading)
    {
        var texts = new List<string?> { heading };
        foreach (var member in unit)
        {
            foreach (var text in member.GetLogicalDescendants().OfType<TextBlock>())
            {
                if (text.IsVisible && (text.Classes.Contains("settinglabel") || text.Classes.Contains("settinghelp")))
                    texts.Add(text.Text);
            }
        }
        return texts;
    }

    /// <summary>
    /// Hides a row or section above its own IsVisible binding without replacing
    /// it: the filter's false sits at animation priority, which outranks the
    /// binding's local value, and removing it hands the property back to the
    /// binding with whatever value it has now.
    /// </summary>
    private void SetFilteredOut(Control control, bool hidden)
    {
        if (hidden)
        {
            if (!_filteredOut.ContainsKey(control))
                _filteredOut[control] = control.SetValue(IsVisibleProperty, false, BindingPriority.Animation);
        }
        else if (_filteredOut.Remove(control, out var undo))
        {
            undo?.Dispose();
        }
    }

    /// <summary>Whether the control's own binding shows it, the filter apart.</summary>
    private static bool OwnVisible(Control control) =>
        control.GetBaseValue(IsVisibleProperty) is not { HasValue: true, Value: false };

    // ---- Section list ----

    /// <summary>Lists the sections shown now, by their headings' current text,
    /// and marks the one in view. Runs after every layout pass, so it allocates
    /// nothing unless the list changed.</summary>
    private void RefreshList()
    {
        if (!ListIsCurrent())
        {
            _listed.Clear();
            _listed.AddRange(_sections.Where(s => s.Heading.IsEffectivelyVisible));
            _entries.Clear();
            foreach (var section in _listed)
                _entries.Add(section.Heading.Text ?? "");
        }

        // Hidden, the list is out of the tab order and the automation tree.
        var show = _wide && _listed.Count > 0;
        if (SectionList.IsVisible != show)
            SectionList.IsVisible = show;
        MarkInView();
    }

    private bool ListIsCurrent()
    {
        var listed = 0;
        foreach (var section in _sections)
        {
            if (!section.Heading.IsEffectivelyVisible)
                continue;
            if (listed >= _listed.Count || _listed[listed] != section || _entries[listed] != (section.Heading.Text ?? ""))
                return false;
            listed++;
        }
        return listed == _listed.Count;
    }

    private void OnScrolled()
    {
        // A scroll the user made, not ours, ends the hold on the chosen section.
        if (_chosen is not null && !_animating && Math.Abs(Scroller.Offset.Y - _chosenOffset) > 0.5)
            _chosen = null;
        MarkInView();
    }

    /// <summary>Selects the entry of the section in view. Moves neither focus
    /// nor the page: the list doesn't scroll to its selection.</summary>
    private void MarkInView()
    {
        if (_listed.Count == 0 || Scroller.Content is not Visual content)
            return;

        var index = _chosen is not null ? _listed.IndexOf(_chosen) : -1;
        if (index < 0)
        {
            _tops.Clear();
            foreach (var section in _listed)
                _tops.Add(section.Heading.TranslatePoint(default, content)?.Y ?? 0);
            index = SectionInView.Index(_tops, Scroller.Offset.Y, Scroller.Viewport.Height, Scroller.Extent.Height);
        }
        if (SectionList.SelectedIndex != index)
            SectionList.SelectedIndex = index;
    }

    private void OnSectionTapped(object? sender, TappedEventArgs e)
    {
        if (IndexOf(e.Source) is { } index)
            Choose(index, NavigationMethod.Pointer);
    }

    private void OnSectionKeyDown(object? sender, KeyEventArgs e)
    {
        // Arrows only move along the list; Enter or Space chooses, so walking
        // the list doesn't scroll the page at each step.
        if (e.Key is not (Key.Enter or Key.Space) || e.KeyModifiers != KeyModifiers.None)
            return;
        if ((IndexOf(e.Source) ?? SectionList.SelectedIndex) is var index and >= 0)
        {
            Choose(index, NavigationMethod.Tab);
            e.Handled = true;
        }
    }

    private int? IndexOf(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is { } item
            ? SectionList.IndexFromContainer(item) is var i and >= 0 ? i : null
            : null;

    /// <summary>Scrolls the section's heading to the top of the view (animated
    /// unless motion is reduced) and then gives it the focus.</summary>
    internal void Choose(int index, NavigationMethod method)
    {
        if (index < 0 || index >= _listed.Count || Scroller.Content is not Visual content ||
            _listed[index].Heading.TranslatePoint(default, content) is not { } top)
        {
            return;
        }

        var section = _listed[index];
        var target = Math.Clamp(top.Y, 0, Math.Max(0, Scroller.Extent.Height - Scroller.Viewport.Height));
        _chosen = section;
        _chosenOffset = target;
        _focusAfterScroll = (section.Heading, method);
        MarkInView();

        var reduced = TopLevel.GetTopLevel(this)?.Classes.Contains("reducedmotion") ?? true;
        if (reduced || !IsEffectivelyVisible || Math.Abs(Scroller.Offset.Y - target) < 0.5)
        {
            _animating = false;
            Scroller.Offset = new Vector(Scroller.Offset.X, target);
            FocusChosen();
            return;
        }

        _animationFrom = Scroller.Offset.Y;
        _animationTo = target;
        _clock.Restart();
        if (!_animating)
        {
            _animating = true;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
        }
    }

    private void Step(TimeSpan _)
    {
        if (!_animating)
            return;

        var t = Math.Min(1, _clock.Elapsed / ScrollDuration);
        var eased = 1 - Math.Pow(1 - t, 3); // ease-out cubic
        Scroller.Offset = new Vector(Scroller.Offset.X, _animationFrom + (_animationTo - _animationFrom) * eased);

        if (t < 1)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(Step);
            return;
        }
        _animating = false;
        FocusChosen();
    }

    private void StopAnimation()
    {
        _animating = false;
        _focusAfterScroll = null;
        _chosen = null;
    }

    /// <summary>Focus moves once the heading has arrived: focusing it earlier
    /// could bring it into view on its own and jump the animation.</summary>
    private void FocusChosen()
    {
        if (_focusAfterScroll is not { } focus)
            return;
        _focusAfterScroll = null;
        focus.Heading.Focus(focus.Method);
    }
}
