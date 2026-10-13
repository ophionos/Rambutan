using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

/// <summary>
/// Pauses a toast's expiry while the pointer is over it or keyboard focus is inside
/// it, so nobody loses an Undo while reaching for it or tabbing to it. A new toast
/// rises and fades in, except under reduced motion, where it
/// simply appears.
/// </summary>
public partial class ToastHost : UserControl
{
    /// <summary>How long a toast takes to rise into place.</summary>
    public static readonly TimeSpan EntranceDuration = TimeSpan.FromMilliseconds(180);

    /// <summary>How far below its place a toast starts, in pixels.</summary>
    private const double EntranceRise = 12;

    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<ToastHost, bool>(nameof(ReduceMotion));

    public ToastHost()
    {
        InitializeComponent();
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    /// <summary>The entrance for one toast: a fade and a short rise, or nothing
    /// under reduced motion (null: no transitions, the toast is drawn in place).</summary>
    public static Transitions? EntranceTransitions(bool reduceMotion)
    {
        var duration = AppearanceSettings.Duration(EntranceDuration, reduceMotion);
        if (duration == TimeSpan.Zero)
            return null;

        return
        [
            new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = duration, Easing = new Avalonia.Animation.Easings.CubicEaseOut() },
        ];
    }

    private void OnToastAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not Control toast)
            return;

        toast.Transitions = EntranceTransitions(ReduceMotion);
        if (toast.Transitions is null)
        {
            toast.Opacity = 1;
            toast.RenderTransform = null;
            return;
        }

        // Start below and clear, then let the transitions carry it to rest once
        // it has been laid out where it will stay.
        toast.Opacity = 0;
        toast.RenderTransform = TransformOperations.Parse($"translateY({EntranceRise}px)");
        Dispatcher.UIThread.Post(() =>
        {
            toast.Opacity = 1;
            toast.RenderTransform = TransformOperations.Identity;
        }, DispatcherPriority.Render);
    }

    private void Hold(object? sender)
    {
        if (DataContext is ToastHostViewModel host && (sender as Control)?.DataContext is Toast toast)
            host.Hold(toast);
    }

    private void Release(object? sender)
    {
        if (DataContext is ToastHostViewModel host && (sender as Control)?.DataContext is Toast toast)
            host.Release(toast);
    }

    private void OnToastPointerEntered(object? sender, PointerEventArgs e) => Hold(sender);

    private void OnToastPointerExited(object? sender, PointerEventArgs e) => Release(sender);

    private void OnToastGotFocus(object? sender, FocusChangedEventArgs e) => Hold(sender);

    private void OnToastLostFocus(object? sender, FocusChangedEventArgs e) => Release(sender);
}
