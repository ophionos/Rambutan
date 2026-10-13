using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Rambutan.App.Controls;

/// <summary>
/// One tab stop for a whole collection, arrows inside it. A Home
/// page was two stops per card (the card and its ▶) across four shelves of a
/// dozen: close to a hundred Tabs before the queue. With this on an items
/// control, Tab enters it once, at the element last focused there, and the next
/// Tab leaves it; the arrow keys move between its focusable elements by position,
/// so the same rule serves a shelf (left/right), the Library grid (all four) and
/// the artist page's rows (up/down between rows, left/right along one).
/// Home and End jump to the first and last. An arrow with nowhere to go in the
/// collection is left alone, so ↑/↓ in a one-row shelf still scroll the page.
/// <code>
/// &lt;ItemsControl controls:RovingFocus.IsEnabled="True" … /&gt;
/// </code>
/// ListBox rows don't need this: a ListBox is already one stop with arrow keys.
/// </summary>
public static class RovingFocus
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<InputElement, bool>("IsEnabled", typeof(RovingFocus));

    static RovingFocus()
    {
        IsEnabledProperty.Changed.AddClassHandler<InputElement>((control, e) =>
        {
            control.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            control.RemoveHandler(InputElement.GotFocusEvent, OnGotFocus);
            if (e.NewValue is true)
            {
                KeyboardNavigation.SetTabNavigation(control, KeyboardNavigationMode.Once);
                control.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
                control.AddHandler(InputElement.GotFocusEvent, OnGotFocus);
            }
            else
            {
                control.ClearValue(KeyboardNavigation.TabNavigationProperty);
            }
        });
    }

    public static bool GetIsEnabled(InputElement control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(InputElement control, bool value) => control.SetValue(IsEnabledProperty, value);

    /// <summary>The elements the arrows move between: focusable, shown, enabled,
    /// in visual order.</summary>
    public static IReadOnlyList<InputElement> Stops(Visual container) =>
        container.GetVisualDescendants()
            .OfType<InputElement>()
            .Where(e => e.Focusable && e.IsEffectivelyVisible && e.IsEffectivelyEnabled &&
                        KeyboardNavigation.GetIsTabStop(e))
            .ToList();

    /// <summary>
    /// The stop to move to from <paramref name="from"/> for <paramref name="key"/>,
    /// or null when there's none that way. Left/right/up/down pick the nearest stop
    /// whose centre lies in that direction, weighing drift across the direction
    /// twice as heavily as distance along it, so ↓ in a row of buttons lands on the
    /// one below rather than a nearer one diagonally.
    /// </summary>
    public static InputElement? Target(Visual container, InputElement from, Key key)
    {
        var stops = Stops(container);
        if (stops.Count == 0)
            return null;

        switch (key)
        {
            case Key.Home:
                return stops[0];
            case Key.End:
                return stops[^1];
        }

        if (Centre(from, container) is not { } origin)
            return null;

        InputElement? best = null;
        var bestScore = double.MaxValue;
        foreach (var stop in stops)
        {
            if (ReferenceEquals(stop, from) || Centre(stop, container) is not { } c)
                continue;

            var (along, across) = key switch
            {
                Key.Right => (c.X - origin.X, c.Y - origin.Y),
                Key.Left => (origin.X - c.X, c.Y - origin.Y),
                Key.Down => (c.Y - origin.Y, c.X - origin.X),
                Key.Up => (origin.Y - c.Y, c.X - origin.X),
                _ => (0d, 0d),
            };
            if (along < 1)
                continue; // not that way

            var score = along + 2 * System.Math.Abs(across);
            if (score < bestScore)
            {
                bestScore = score;
                best = stop;
            }
        }
        return best;
    }

    private static Point? Centre(Visual element, Visual container) =>
        element.TranslatePoint(new Point(element.Bounds.Width / 2, element.Bounds.Height / 2), container);

    private static void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.KeyModifiers != KeyModifiers.None || sender is not InputElement container ||
            e.Key is not (Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End))
        {
            return;
        }

        // Only moves that start inside the collection.
        if (e.Source is not InputElement from || !container.IsVisualAncestorOf(from))
            return;

        // The focused stop, or the stop the source sits in (a key on a button's
        // content reports the content as its source).
        var current = from.Focusable ? from : from.FindAncestorOfType<InputElement>();
        if (current is null || Target(container, current, e.Key) is not { } next)
            return;

        next.Focus(NavigationMethod.Directional);
        (next as Control)?.BringIntoView();
        e.Handled = true;
    }

    private static void OnGotFocus(object? sender, FocusChangedEventArgs e)
    {
        // Tab back into the collection returns to where the user left it.
        if (sender is InputElement container && e.Source is IInputElement focused &&
            !ReferenceEquals(focused, container))
        {
            KeyboardNavigation.SetTabOnceActiveElement(container, focused);
        }
    }
}
