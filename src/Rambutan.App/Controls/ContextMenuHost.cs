using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Controls;

/// <summary>
/// The one context flyout behind <see cref="TrackMenu"/> and
/// <see cref="CollectionMenu"/>. A control gets a single flyout, whichever menu
/// asked first, and each time it is requested the first source with entries
/// fills it, so a recycled card that changes from a song to an album changes
/// menus with it. Entries are rebuilt on every request, so enabled states are
/// never stale.
/// </summary>
internal static class ContextMenuHost
{
    /// <summary>What a control's menu holds now, in groups; empty for none.</summary>
    public delegate IReadOnlyList<IReadOnlyList<MenuEntry>> Source(Control owner);

    private static readonly List<Source> Sources = [];

    private static readonly AttachedProperty<MenuFlyout?> FlyoutProperty =
        AvaloniaProperty.RegisterAttached<Control, MenuFlyout?>("Flyout", typeof(ContextMenuHost));

    private static readonly AttachedProperty<Action?> UnforwardProperty =
        AvaloniaProperty.RegisterAttached<Control, Action?>("Unforward", typeof(ContextMenuHost));

    /// <summary>Called once per menu kind, from its static constructor; earlier
    /// sources win.</summary>
    public static void AddSource(Source source) => Sources.Add(source);

    public static void Install(Control control)
    {
        if (control.GetValue(FlyoutProperty) is not null)
            return;

        // Filled on the way *down* to the row, before the control's own
        // handler shows the flyout. Filling it from the flyout's Opening event
        // instead produced a menu that reported open and never appeared (X11).
        var flyout = new MenuFlyout();
        control.SetValue(FlyoutProperty, flyout);
        control.ContextFlyout = flyout;
        control.AddHandler(Control.ContextRequestedEvent, (_, e) =>
        {
            Populate(flyout, control);
            // A recycled container keeps its flyout when its item has no menu
            // (a row whose track went away): nothing opens.
            if (flyout.Items.Count == 0)
                e.Handled = true;
        }, RoutingStrategies.Tunnel);

        // The keyboard route: the menu key and Shift+F10 raise
        // the request on the focused element. In a list that is the row's
        // ListBoxItem, *above* this control, so the request never passes
        // through it, and the row's container forwards it here instead. On a
        // card it is the card's button, *inside* this control, so the
        // request arrives by itself and only Shift+F10 needs handling.
        control.AttachedToVisualTree += (_, _) => ForwardFromContainer(control, flyout);
        control.DetachedFromVisualTree += (_, _) => StopForwarding(control);
        if (control.IsAttachedToVisualTree())
            ForwardFromContainer(control, flyout);
    }

    private static IReadOnlyList<IReadOnlyList<MenuEntry>> Build(Control owner)
    {
        foreach (var source in Sources)
        {
            if (source(owner) is { Count: > 0 } groups)
                return groups;
        }
        return [];
    }

    private static void ForwardFromContainer(Control control, MenuFlyout flyout)
    {
        StopForwarding(control);
        var row = control.FindAncestorOfType<ListBoxItem>();
        var container = (Control?)row ?? control;

        bool Open()
        {
            Populate(flyout, control);
            if (flyout.Items.Count == 0)
                return false;
            flyout.ShowAt(control);
            return true;
        }

        void OnRequested(object? sender, ContextRequestedEventArgs e)
        {
            // Only requests aimed at the row itself: one from inside the row (a
            // right-click on the row content) has already opened this menu.
            if (!e.Handled && ReferenceEquals(e.Source, container))
                e.Handled = Open();
        }

        // Shift+F10, the other keyboard route, which X11 doesn't turn into a
        // context request the way it does the menu key. The menu opens on the
        // release, not the press: on Windows an unhandled F10 release puts the
        // window into menu mode, which takes focus and so light-dismisses a
        // flyout opened on the press before it is ever seen.
        var f10Pressed = false;

        void OnKeyDown(object? sender, KeyEventArgs e)
        {
            if (!e.Handled && e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift &&
                (row is null || ReferenceEquals(e.Source, container)) && Build(control).Count > 0)
            {
                f10Pressed = true;
                e.Handled = true;
            }
        }

        void OnKeyUp(object? sender, KeyEventArgs e)
        {
            // Shift may already be up: the press decided, so modifiers don't matter here.
            if (e.Key != Key.F10 || !f10Pressed)
                return;
            f10Pressed = false;
            e.Handled = true;
            // Where the platform also maps Shift+F10 to a context request, the
            // focused element has raised it already, and the menu is open.
            if (!flyout.IsOpen)
                Open();
        }

        if (row is not null)
            container.AddHandler(Control.ContextRequestedEvent, OnRequested);
        container.AddHandler(InputElement.KeyDownEvent, OnKeyDown);
        container.AddHandler(InputElement.KeyUpEvent, OnKeyUp);
        control.SetValue(UnforwardProperty, () =>
        {
            if (row is not null)
                container.RemoveHandler(Control.ContextRequestedEvent, OnRequested);
            container.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
            container.RemoveHandler(InputElement.KeyUpEvent, OnKeyUp);
        });
    }

    private static void StopForwarding(Control control)
    {
        control.GetValue(UnforwardProperty)?.Invoke();
        control.ClearValue(UnforwardProperty);
    }

    private static void Populate(MenuFlyout flyout, Control owner)
    {
        flyout.Items.Clear();
        foreach (var group in Build(owner))
        {
            if (flyout.Items.Count > 0)
                flyout.Items.Add(new Separator());

            foreach (var entry in group)
            {
                var item = new MenuItem
                {
                    Header = entry.Header,
                    Command = entry.Command,
                    CommandParameter = entry.Parameter,
                    IsEnabled = entry.IsEnabled,
                };
                if (owner.TryFindResource(entry.IconKey, out var icon) && icon is Geometry geometry)
                    item.Icon = new PathIcon { Data = geometry, Width = 14, Height = 14 };
                flyout.Items.Add(item);
            }
        }
    }
}
