using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Controls;

/// <summary>
/// Keeps the queue's current row at the top of the queue list, with what was
/// played above it out of view and what comes next below it. Opening the pane
/// (or a queue appearing in an open one) always puts the current row at the top.
/// A new track moves it there only while the list is where this class last put
/// it and no pointer is down on it: a list the user scrolled stays where they put
/// it until the pane opens again.
/// </summary>
public sealed class QueueFollow
{
    private readonly ListBox _list;
    private readonly Control _pane;
    private readonly QueueViewModel _queue;

    // The offset read back after the last placement (a short queue's end clamps
    // what was asked for); null before the first, when the list is at its top.
    private double? _placed;
    private bool _pressed;

    private QueueFollow(ListBox list, Control pane, QueueViewModel queue)
    {
        _list = list;
        _pane = pane;
        _queue = queue;

        queue.CurrentChanged += _ => Dispatcher.UIThread.Post(FollowCurrent, DispatcherPriority.Loaded);
        pane.PropertyChanged += OnVisibilityChanged;
        list.PropertyChanged += OnVisibilityChanged;
        // Attached before the window shows, nothing changes visibility at start:
        // the list loading is the start.
        list.Loaded += (_, _) => PostPlace();
        // ListBoxItem handles the press, so only handledEventsToo hears it.
        list.AddHandler(InputElement.PointerPressedEvent, (_, _) => _pressed = true,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerReleasedEvent, (_, _) => _pressed = false,
            RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        list.AddHandler(InputElement.PointerCaptureLostEvent, (_, _) => _pressed = false,
            RoutingStrategies.Bubble, handledEventsToo: true);

        PostPlace();
    }

    /// <param name="list">The queue's list, whose items are <see cref="QueueViewModel.Items"/>.</param>
    /// <param name="pane">What shows and hides the list (the queue pane).</param>
    public static QueueFollow Attach(ListBox list, Control pane, QueueViewModel queue) => new(list, pane, queue);

    private bool IsShown => _pane.IsEffectivelyVisible && _list.IsEffectivelyVisible;

    private QueueItemViewModel? Current => _queue.Items.FirstOrDefault(i => i.IsCurrent);

    private ScrollViewer? Viewer => _list.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

    private void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && e.NewValue is true)
            PostPlace();
    }

    // Posted: a list just shown, or a row just placed, isn't laid out in this turn.
    private void PostPlace() => Dispatcher.UIThread.Post(() =>
    {
        if (!IsShown)
            return;
        // A release this list never heard (capture lost elsewhere) ends here.
        _pressed = false;
        if (Current is { } item)
            Place(item);
    }, DispatcherPriority.Loaded);

    private void FollowCurrent()
    {
        if (!IsShown || _pressed || Current is not { } item || Viewer is not { } viewer)
            return;
        if (Math.Abs(viewer.Offset.Y - (_placed ?? 0)) > 0.5)
            return;
        Place(item);
    }

    /// <summary>Scrolls so <paramref name="item"/>'s row starts at the top of the
    /// viewport, as far as the list's end allows.</summary>
    public void Place(QueueItemViewModel item)
    {
        if (Viewer is not { } viewer)
            return;

        // Into view first, so the row exists; then to the top. Rows not yet
        // realized have estimated heights, so a second pass corrects the first.
        _list.ScrollIntoView(item);
        _list.UpdateLayout();
        for (var pass = 0; pass < 2; pass++)
        {
            if (_list.ContainerFromItem(item) is not Control row || row.TranslatePoint(default, viewer) is not { } top)
                break;
            var max = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
            var target = Math.Clamp(viewer.Offset.Y + top.Y, 0, max);
            if (Math.Abs(target - viewer.Offset.Y) < 0.5)
                break;
            viewer.Offset = viewer.Offset.WithY(target);
            _list.UpdateLayout();
        }
        _placed = viewer.Offset.Y;
    }
}
