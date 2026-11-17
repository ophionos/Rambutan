using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Rambutan.App.Controls;

/// <summary>
/// Stops a control's endless animation while the control can't be seen.
/// Avalonia only pauses a style's animation while its control isn't effectively
/// visible, and a paused endless animation still asks for a frame every frame:
/// one "loading" bar or skeleton that is hidden, not removed, once its page has
/// loaded keeps the UI thread waking 60 times a second for as long as the page
/// stays (Avalonia 12.1.3, in an otherwise empty window: 11 to 23% of a core
/// for nothing anyone sees). An animation whose style no longer matches is
/// gone, not paused, so this makes the style stop matching:
/// <list type="bullet">
/// <item>a <see cref="ProgressBar"/> isn't indeterminate while hidden, whatever
/// its own value or binding says, and is what they say again when shown;</item>
/// <item>any other control has the <see cref="UnseenClass"/> class while
/// hidden, for its animation's selector to leave out
/// (<c>Border.skeleton:not(.unseen)</c>).</item>
/// </list>
/// Hidden is whichever ancestor hides it, and out of a visual tree counts as
/// hidden too: a page is styled before it is shown, and keeps its styles after
/// it is taken away. A control hidden only by its own <c>IsVisible</c> needs
/// none of this: its animation's selector can ask for <c>[IsVisible=True]</c>.
/// Set <see cref="StopProperty"/> from the style that declares the animation.
/// UI thread only.
/// </summary>
public static class HiddenAnimations
{
    public const string UnseenClass = "unseen";

    public static readonly AttachedProperty<bool> StopProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Stop", typeof(HiddenAnimations));

    /// <summary>What holds a hidden bar determinate, over its own value: a value
    /// at animation priority, so the bar's value or binding goes on underneath
    /// and is what shows once this is disposed.</summary>
    private static readonly AttachedProperty<IDisposable?> HeldProperty =
        AvaloniaProperty.RegisterAttached<ProgressBar, IDisposable?>("Held", typeof(HiddenAnimations));

    // The controls to keep an eye on: those with Stop set that are in a visual
    // tree. Weak, because a closed dialog's controls are never detached from it.
    private static readonly ConditionalWeakTable<Control, object?> Watched = new();

    // Sweep's working list; empty between sweeps, so it holds nothing alive.
    private static readonly List<Control> Swept = [];

    private static bool _queued;

    static HiddenAnimations()
    {
        StopProperty.Changed.AddClassHandler<Control>(OnStopChanged);
        // Avalonia tells nobody outside it when effective visibility changes, so
        // every change of any IsVisible asks the watched controls again, once
        // per burst.
        Visual.IsVisibleProperty.Changed.AddClassHandler<Visual>(static (_, _) => Queue());
    }

    public static bool GetStop(Control control) => control.GetValue(StopProperty);

    public static void SetStop(Control control, bool value) => control.SetValue(StopProperty, value);

    private static void OnStopChanged(Control control, AvaloniaPropertyChangedEventArgs change)
    {
        control.AttachedToVisualTree -= OnAttached;
        control.DetachedFromVisualTree -= OnDetached;
        if (change.GetNewValue<bool>())
        {
            control.AttachedToVisualTree += OnAttached;
            control.DetachedFromVisualTree += OnDetached;
            if (control.IsAttachedToVisualTree())
                Watch(control);
            // Styled before it is in a tree (a page being built), or in one and
            // hidden from the start: its animation would begin paused.
            Apply(control, IsHidden(control));
        }
        else
        {
            // No longer asked for (its style left it): as declared again if it
            // shows, since nothing will say so later. Hidden, it stays stopped;
            // a control leaving the tree loses its styles on the way.
            Watched.Remove(control);
            if (!IsHidden(control))
                Apply(control, hidden: false);
        }
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e) => Watch((Control)sender!);

    // Out of the tree nobody sees it, and its styles stay on it: left as it
    // was declared, its animation would be paused for good, asking for frames
    // from a page that is gone. It is asked again when it is back in a tree.
    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        var control = (Control)sender!;
        if (Watched.Remove(control))
            Apply(control, hidden: true);
    }

    private static void Watch(Control control)
    {
        Watched.TryAdd(control, null);
        Queue();
    }

    private static bool IsHidden(Control control) =>
        !control.IsAttachedToVisualTree() || !control.IsEffectivelyVisible;

    // Posted, not done in place: a page switch changes many IsVisible at once,
    // and the answer is wanted once they have all changed.
    private static void Queue()
    {
        if (_queued)
            return;
        _queued = true;
        Dispatcher.UIThread.Post(Sweep);
    }

    private static void Sweep()
    {
        _queued = false;
        // Copied first: applying can run handlers that watch or forget a control.
        foreach (var (control, _) in Watched)
            Swept.Add(control);
        foreach (var control in Swept)
        {
            if (Watched.TryGetValue(control, out _))
                Apply(control, IsHidden(control));
        }
        Swept.Clear();
    }

    private static void Apply(Control control, bool hidden)
    {
        if (control is not ProgressBar bar)
        {
            control.Classes.Set(UnseenClass, hidden);
            return;
        }

        var held = bar.GetValue(HeldProperty);
        if (hidden && held is null)
        {
            bar.SetValue(HeldProperty, bar.SetValue(
                ProgressBar.IsIndeterminateProperty, false, Avalonia.Data.BindingPriority.Animation));
        }
        else if (!hidden && held is not null)
        {
            bar.ClearValue(HeldProperty);
            held.Dispose();
        }
    }
}
