using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.LogicalTree;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Rambutan.App.Localization;
using Rambutan.Core.Models;

namespace Rambutan.App.Controls;

/// <summary>A row's own spoken name, for items whose name changes with their state
/// (a queue row that becomes "now playing"). Raise PropertyChanged for
/// <see cref="AccessibleName"/> when it changes.</summary>
public interface IHasAccessibleName : INotifyPropertyChanged
{
    string AccessibleName { get; }
}

/// <summary>A row that names itself (<see cref="IHasAccessibleName"/>) and stands
/// for one track, so the track's downloaded state can join its name.</summary>
public interface ITrackRow
{
    TrackInfo Track { get; }
}

/// <summary>
/// Screen-reader names for the controls that would otherwise have none.
/// Every transport and row button is a <see cref="PathIcon"/> in a bare
/// button, which a screen reader announced as just "button". Two rules, installed
/// once for the whole app, so a control added later is covered without anyone
/// remembering to name it:
/// <list type="bullet">
/// <item>A button with no text content takes its name from its tooltip, which is
/// already written for people. A trailing shortcut in brackets, "Next (Ctrl+→)",
/// becomes the accelerator key instead of part of the name. A tooltip that
/// changes renames the button with it.</item>
/// <item>A list row takes its item's name: "title, artists" for a track, "title,
/// subtitle" for a card. Unnamed, a row read out its record's ToString().</item>
/// </list>
/// A track row that has a download says so after its name: the mark is visual only.
/// An explicit <c>AutomationProperties.Name</c> always wins: these rules write at
/// style priority, below any local value or binding, so a bound name ("Play
/// {Title}") keeps its value even when the tip arrives before the data does.
/// </summary>
public static class AccessibleNames
{
    /// <summary>Undoes the derived name; null when this class set none.</summary>
    private static readonly AttachedProperty<IDisposable?> DerivedProperty =
        AvaloniaProperty.RegisterAttached<Control, IDisposable?>("Derived", typeof(AccessibleNames));

    private static readonly AttachedProperty<IDisposable?> DerivedKeyProperty =
        AvaloniaProperty.RegisterAttached<Control, IDisposable?>("DerivedKey", typeof(AccessibleNames));

    private static readonly AttachedProperty<Watcher?> WatchedProperty =
        AvaloniaProperty.RegisterAttached<Control, Watcher?>("Watched", typeof(AccessibleNames));

    /// <summary>Subscribed to <see cref="TrackRowsChanged"/> while the row is loaded.</summary>
    private static readonly AttachedProperty<Action?> RefreshProperty =
        AvaloniaProperty.RegisterAttached<ListBoxItem, Action?>("Refresh", typeof(AccessibleNames));

    private static bool _installed;

    /// <summary>Whether a videoId has a download; set at composition. Null leaves
    /// names as they are.</summary>
    public static Func<string, bool>? IsDownloaded { get; set; }

    private static event Action? TrackRowsChanged;

    /// <summary>Renames the rows on screen; call (on the UI thread) when what
    /// <see cref="IsDownloaded"/> answers changes.</summary>
    public static void RefreshTrackRows() => TrackRowsChanged?.Invoke();

    /// <summary>For XAML: the same name a row gets, for controls that stand for an
    /// item without being a list row (a card's button).</summary>
    public static readonly IValueConverter ItemConverter =
        new FuncValueConverter<object?, string?>(For);

    /// <summary>Hooks the two rules in. Idempotent; call once at startup (tests
    /// call it too).</summary>
    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        ToolTip.TipProperty.Changed.AddClassHandler<Button>((button, _) => NameFromTip(button));
        // XAML sets attributes (the tip) before child content, so a text button's
        // content can arrive after its tip was taken as its name.
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>((button, _) => NameFromTip(button));
        // An icon-and-label button ("▶ Play all", the sidebar) is named by its
        // label, read once its content is built and bound.
        Control.LoadedEvent.AddClassHandler<Button>((button, _) => NameFromTip(button));
        StyledElement.DataContextProperty.Changed.AddClassHandler<ListBoxItem>((item, _) => NameFromItem(item));
        Control.LoadedEvent.AddClassHandler<ListBoxItem>((item, _) => Subscribe(item));
        Control.UnloadedEvent.AddClassHandler<ListBoxItem>((item, _) => Unsubscribe(item));
    }

    private static void Subscribe(ListBoxItem item)
    {
        Unsubscribe(item);
        Action refresh = () => NameFromItem(item);
        item.SetValue(RefreshProperty, refresh);
        TrackRowsChanged += refresh;
    }

    private static void Unsubscribe(ListBoxItem item)
    {
        if (item.GetValue(RefreshProperty) is { } refresh)
            TrackRowsChanged -= refresh;
        item.ClearValue(RefreshProperty);
    }

    /// <summary>The item's name as its row says it: <see cref="For"/>, plus
    /// "downloaded" for a track that has a download.</summary>
    internal static string? RowName(object? item)
    {
        if (For(item) is not { } name)
            return null;
        var track = item switch
        {
            TrackInfo t => t,
            SongEntry song => song.Track,
            ITrackRow row => row.Track,
            _ => null,
        };
        return track is not null && IsDownloaded?.Invoke(track.VideoId) == true
            ? Strings.Format("downloads.row.downloaded", ("name", name))
            : name;
    }

    /// <summary>The spoken name of a list item, or null when it has none of its own.</summary>
    public static string? For(object? item) => item switch
    {
        IHasAccessibleName named => named.AccessibleName,
        TrackInfo track => Join(track.Title, track.ArtistDisplay),
        SongEntry song => For(song.Track),
        CatalogEntry entry => Join(entry.Title, entry.Subtitle),
        _ => null,
    };

    /// <summary>Splits "Next (Ctrl+→)" into ("Next", "Ctrl+→"). A tip without a
    /// trailing bracket is all name.</summary>
    public static (string Name, string? Shortcut) SplitTip(string tip)
    {
        tip = tip.Trim();
        var open = tip.LastIndexOf(" (", StringComparison.Ordinal);
        if (tip.EndsWith(')') && open > 0)
            return (tip[..open].TrimEnd(), tip[(open + 2)..^1]);
        return (tip, null);
    }

    private static void NameFromTip(Button button)
    {
        // Text buttons ("Sign in", a link's title) are already named by their text.
        if (button.Content is string || button.Content is TextBlock)
        {
            Clear(button);
            return;
        }

        if (ToolTip.GetTip(button) is string { Length: > 0 } tip)
        {
            var (name, shortcut) = SplitTip(tip);
            Set(button, name);
            SetKey(button, shortcut);
        }
        else if (LabelOf(button) is { } label)
        {
            Set(button, label);
            SetKey(button, null);
        }
        else
        {
            Clear(button);
        }
    }

    /// <summary>The text shown inside a button whose content is a panel of icon
    /// and label, or null. (Unnamed, such a button read out "StackPanel".)</summary>
    private static string? LabelOf(Button button)
    {
        if (button.Content is not Control content)
            return null;
        var texts = content.GetLogicalDescendants().Prepend(content)
            .OfType<TextBlock>()
            .Where(t => t.IsVisible && !string.IsNullOrWhiteSpace(t.Text))
            .Select(t => t.Text!.Trim());
        var label = string.Join(" ", texts);
        return label.Length > 0 ? label : null;
    }

    private static void NameFromItem(ListBoxItem item)
    {
        item.GetValue(WatchedProperty)?.Dispose();
        item.ClearValue(WatchedProperty);

        var data = item.DataContext;
        if (RowName(data) is { } name)
            Set(item, name);
        else
            Clear(item);

        if (data is IHasAccessibleName named)
            item.SetValue(WatchedProperty, new Watcher(item, named));
    }

    /// <summary>A local value (not this) replaces a binding, which is how a card's
    /// bound "Play {Title}" was lost to its "Play" tip. Style priority sits under
    /// both, and each write undoes the previous one rather than stacking.</summary>
    private static void Set(Control control, string name)
    {
        control.GetValue(DerivedProperty)?.Dispose();
        control.SetValue(DerivedProperty,
            control.SetValue(AutomationProperties.NameProperty, name, BindingPriority.Style));
    }

    private static void SetKey(Control control, string? shortcut)
    {
        control.GetValue(DerivedKeyProperty)?.Dispose();
        control.SetValue(DerivedKeyProperty, shortcut is null ? null :
            control.SetValue(AutomationProperties.AcceleratorKeyProperty, shortcut, BindingPriority.Style));
    }

    private static void Clear(Control control)
    {
        control.GetValue(DerivedProperty)?.Dispose();
        control.ClearValue(DerivedProperty);
        SetKey(control, null);
    }

    private static string Join(string title, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? title : $"{title}, {detail}";

    /// <summary>Renames a row when its item's name changes; dropped as soon as the
    /// (recycled) container moves on to another item.</summary>
    private sealed class Watcher : IDisposable
    {
        private readonly ListBoxItem _item;
        private readonly IHasAccessibleName _named;

        public Watcher(ListBoxItem item, IHasAccessibleName named)
        {
            _item = item;
            _named = named;
            named.PropertyChanged += OnChanged;
        }

        public void Dispose() => _named.PropertyChanged -= OnChanged;

        private void OnChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (!string.IsNullOrEmpty(e.PropertyName) &&
                e.PropertyName != nameof(IHasAccessibleName.AccessibleName))
            {
                return;
            }

            if (Dispatcher.UIThread.CheckAccess())
                Apply();
            else
                Dispatcher.UIThread.Post(Apply);
        }

        private void Apply()
        {
            if (ReferenceEquals(_item.DataContext, _named) && RowName(_named) is { } name)
                Set(_item, name);
        }
    }
}
