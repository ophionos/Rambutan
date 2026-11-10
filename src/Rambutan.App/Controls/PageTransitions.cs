using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;

namespace Rambutan.App.Controls;

public static class PageTransitions
{
    /// <summary>How long one page takes to give way to the next. The outgoing page
    /// stays in the tree for exactly this long.</summary>
    public static readonly TimeSpan Fade = TimeSpan.FromMilliseconds(120);

    /// <summary>On a <see cref="TransitioningContentControl"/>: the page that is leaving takes no
    /// pointer input while it fades, so a click lands on the page that is arriving.</summary>
    public static readonly AttachedProperty<bool> IgnoresOutgoingProperty =
        AvaloniaProperty.RegisterAttached<TransitioningContentControl, TransitioningContentControl, bool>(
            "IgnoresOutgoing");

    public static bool GetIgnoresOutgoing(TransitioningContentControl host) => host.GetValue(IgnoresOutgoingProperty);

    public static void SetIgnoresOutgoing(TransitioningContentControl host, bool value) =>
        host.SetValue(IgnoresOutgoingProperty, value);

    static PageTransitions()
    {
        IgnoresOutgoingProperty.Changed.AddClassHandler<TransitioningContentControl>((host, e) =>
        {
            if (e.GetNewValue<bool>())
                host.TemplateApplied += OnTemplateApplied;
            else
                host.TemplateApplied -= OnTemplateApplied;
        });
    }

    private static void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
    {
        var host = (TransitioningContentControl)sender!;
        var presenters = new ContentPresenter?[]
        {
            e.NameScope.Find<ContentPresenter>("PART_ContentPresenter"),
            e.NameScope.Find<ContentPresenter>("PART_ContentPresenter2"),
        };
        foreach (var presenter in presenters)
        {
            presenter?.GetObservable(ContentPresenter.ContentProperty).Subscribe(new Observer(() =>
            {
                foreach (var other in presenters)
                {
                    if (other is not null)
                        other.IsHitTestVisible = ReferenceEquals(other.Content, host.Content);
                }
            }));
        }
    }

    private sealed class Observer(Action changed) : IObserver<object?>
    {
        public void OnNext(object? value) => changed();
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
