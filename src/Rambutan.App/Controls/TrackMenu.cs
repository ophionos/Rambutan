using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Controls;

/// <summary>
/// The one track context menu. A row opts in with its track, and
/// a surface with extra entries (the queue, an editable playlist) adds itself as
/// the host:
/// <code>
/// &lt;Grid controls:TrackMenu.Track="{Binding}"
///       controls:TrackMenu.Host="{Binding #Root.((vm:PlaylistPageViewModel)DataContext)}"&gt;
/// </code>
/// Entries come from <see cref="TrackMenuModel"/> and are rebuilt each time the
/// menu is requested, so enabled states are never stale.
/// </summary>
public static class TrackMenu
{
    /// <summary>Composition-time hook, like <see cref="RemoteImage.Cache"/>: the
    /// shared actions every menu invokes. Without it the menu has nothing to show.</summary>
    public static TrackActionsViewModel? Actions { get; set; }

    public static readonly AttachedProperty<TrackInfo?> TrackProperty =
        AvaloniaProperty.RegisterAttached<Control, TrackInfo?>("Track", typeof(TrackMenu));

    public static readonly AttachedProperty<ITrackMenuHost?> HostProperty =
        AvaloniaProperty.RegisterAttached<Control, ITrackMenuHost?>("Host", typeof(TrackMenu));

    static TrackMenu()
    {
        TrackProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is null || control.ContextFlyout is not null)
                return;

            // Filled on the way *down* to the row, before the control's own
            // handler shows the flyout. Filling it from the flyout's Opening event
            // instead produced a menu that reported open and never appeared (X11).
            var flyout = new MenuFlyout();
            control.ContextFlyout = flyout;
            control.AddHandler(Control.ContextRequestedEvent, (_, e) =>
            {
                Populate(flyout, control);
                // A recycled container keeps its flyout when its item has no
                // track (a card that was a song and is now an album): nothing opens.
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
        });
    }

    private static readonly AttachedProperty<Action?> UnforwardProperty =
        AvaloniaProperty.RegisterAttached<Control, Action?>("Unforward", typeof(TrackMenu));

    private static void ForwardFromContainer(Control control, MenuFlyout flyout)
    {
        StopForwarding(control);
        var row = control.FindAncestorOfType<ListBoxItem>();
        var container = (Control?)row ?? control;

        bool Open()
        {
            if (GetTrack(control) is null)
                return false;
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
                (row is null || ReferenceEquals(e.Source, container)) && GetTrack(control) is not null)
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

    public static TrackInfo? GetTrack(Control control) => control.GetValue(TrackProperty);
    public static void SetTrack(Control control, TrackInfo? value) => control.SetValue(TrackProperty, value);
    public static ITrackMenuHost? GetHost(Control control) => control.GetValue(HostProperty);
    public static void SetHost(Control control, ITrackMenuHost? value) => control.SetValue(HostProperty, value);

    private static void Populate(MenuFlyout flyout, Control owner)
    {
        flyout.Items.Clear();
        if (GetTrack(owner) is not { } track || Actions is not { } actions)
            return;

        var extras = GetHost(owner)?.MenuExtrasFor(owner.DataContext, track);
        foreach (var group in TrackMenuModel.Build(track, actions, extras))
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
