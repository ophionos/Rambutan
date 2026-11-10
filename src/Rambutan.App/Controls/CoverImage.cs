using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Rambutan.App.Controls;

/// <summary>
/// The cover of the playing track: when <see cref="Url"/> changes, the new cover
/// decodes in a second layer and fades in over the old one only once its bitmap
/// is there. A slow or failed fetch leaves the old cover (or none) rather than
/// an empty frame, and a cover that is gone, or fails, fades out so a stale
/// cover never stays for another track.
///
/// Not for lists: every row would fade in from empty. Rows use
/// <see cref="RemoteImage"/> directly.
/// </summary>
public sealed class CoverImage : Panel
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<CoverImage, string?>(nameof(Url));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<CoverImage, int>(nameof(DecodeWidth), defaultValue: 320);

    public static readonly StyledProperty<Stretch> StretchProperty =
        Image.StretchProperty.AddOwner<CoverImage>();

    /// <summary>Under reduced motion a cover replaces the last at once.</summary>
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<CoverImage, bool>(nameof(ReduceMotion));

    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);

    private readonly RemoteImage[] _layers = [new(), new()];

    // One set per layer: a Transitions collection belongs to its owner.
    private readonly DoubleTransition[] _fades =
    [
        new() { Property = OpacityProperty, Duration = FadeDuration },
        new() { Property = OpacityProperty, Duration = FadeDuration },
    ];
    private readonly Transitions[] _transitions;

    // The layer that shows the cover, or will once its bitmap arrives.
    private int _current;

    // The opacity a layer is animating to, and the layer to empty when it gets
    // there. Watching the opacity lets the fade end by itself: no timer. The
    // layer's own value is read, not the event's: the change to the target
    // is announced first as a local value while the animation still shows 0.
    private RemoteImage? _settling;
    private RemoteImage? _emptyWhenSettled;
    private double _settleOpacity;

    public CoverImage()
    {
        _transitions = [[_fades[0]], [_fades[1]]];
        for (var i = 0; i < _layers.Length; i++)
        {
            var layer = _layers[i];
            layer.Opacity = 0;
            layer.HorizontalAlignment = HorizontalAlignment.Stretch;
            layer.VerticalAlignment = VerticalAlignment.Stretch;
            layer.Transitions = _transitions[i];
            layer.PropertyChanged += OnLayerChanged;
            layer.LoadFailed += OnLayerLoadFailed;
            Children.Add(layer);
        }
        DetachedFromVisualTree += (_, _) => FinishSettling();
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public Stretch Stretch
    {
        get => GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    /// <summary>The fade in use now: <see cref="FadeDuration"/>, or zero under
    /// reduced motion.</summary>
    public TimeSpan CurrentFade => _layers[0].Transitions is null ? TimeSpan.Zero : _fades[0].Duration;

    internal RemoteImage[] Layers => _layers;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty)
        {
            ShowCover(change.GetNewValue<string?>());
        }
        else if (change.Property == StretchProperty)
        {
            foreach (var layer in _layers)
                layer.Stretch = change.GetNewValue<Stretch>();
        }
        else if (change.Property == ReduceMotionProperty)
        {
            // No transition at all rather than a zero-length one, so a swap
            // completes within the call that starts it.
            var reduced = change.GetNewValue<bool>();
            for (var i = 0; i < _layers.Length; i++)
            {
                _fades[i].Duration = Services.AppearanceSettings.Duration(FadeDuration, reduced);
                _layers[i].Transitions = reduced ? null : _transitions[i];
            }
        }
    }

    private void ShowCover(string? url)
    {
        // A fade still running is cut short: its old cover is under the one
        // that came after it, and is about to be reused for this one.
        FinishSettling();

        var shown = _layers[_current];
        var other = _layers[1 - _current];

        if (url is null)
        {
            SetInstant(other, 0);
            other.Url = null;
            Remove(shown);
            return;
        }

        // Back to the cover that is still showing, before the other one decoded.
        if (shown.Url == url && shown.Source is not null)
        {
            SetInstant(other, 0);
            other.Url = null;
            return;
        }

        SetInstant(other, 0);
        other.DecodeWidth = DecodeWidth;
        other.Stretch = Stretch;
        // A load in flight for an earlier url is dropped by RemoteImage itself.
        if (other.Url == url)
            other.Url = null;
        other.Url = url;
    }

    private void OnLayerChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        var layer = (RemoteImage)sender!;
        if (e.Property == Image.SourceProperty && e.NewValue is not null &&
            ReferenceEquals(layer, _layers[1 - _current]))
        {
            Promote(layer);
        }
        else if (e.Property == OpacityProperty && ReferenceEquals(layer, _settling) &&
                 layer.Opacity == _settleOpacity)
        {
            FinishSettling();
        }
    }

    /// <summary>The cover of a layer has arrived: it goes over the old one and fades in.</summary>
    private void Promote(RemoteImage incoming)
    {
        var outgoing = _layers[_current];
        _current = 1 - _current;
        incoming.ZIndex = 1;
        outgoing.ZIndex = 0;
        _settling = incoming;
        _settleOpacity = 1;
        _emptyWhenSettled = outgoing;
        incoming.Opacity = 1;
    }

    /// <summary>The cover's fetch ended with nothing: whatever is shown belongs to another track.</summary>
    internal void OnLayerLoadFailed(object? sender, EventArgs e)
    {
        if (ReferenceEquals(sender, _layers[1 - _current]))
        {
            FinishSettling();
            Remove(_layers[_current]);
        }
    }

    /// <summary>Fades a layer out and empties it, releasing its bitmap.</summary>
    private void Remove(RemoteImage layer)
    {
        if (layer.Source is null && layer.Url is null)
        {
            SetInstant(layer, 0);
            return;
        }
        _settling = layer;
        _settleOpacity = 0;
        _emptyWhenSettled = layer;
        layer.Opacity = 0;
    }

    private void FinishSettling()
    {
        var done = _emptyWhenSettled;
        _settling = null;
        _emptyWhenSettled = null;
        if (done is null)
            return;

        // The layer is under the settled one, or fully transparent: dropping
        // its url frees the bitmap without a visible change.
        SetInstant(done, 0);
        done.Url = null;
    }

    /// <summary>Skips the transition for a change nobody can see, so no
    /// animation (and no compositor frames) runs for it.</summary>
    private void SetInstant(RemoteImage layer, double opacity)
    {
        var transitions = layer.Transitions;
        layer.Transitions = null;
        layer.Opacity = opacity;
        layer.Transitions = transitions;
    }
}
