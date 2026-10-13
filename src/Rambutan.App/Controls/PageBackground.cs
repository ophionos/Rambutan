using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.Composition;

namespace Rambutan.App.Controls;

/// <summary>
/// The page-background layer: now-playing artwork blurred behind the
/// whole shell, under a scrim, with the shell's panels translucent above it.
///
/// Two details that are not obvious and were both found by looking at it:
/// the bitmap is scaled past the edges so the blur never samples outside it
/// (otherwise a bright fringe rings the window), and the scrim is what keeps
/// text legible on saturated covers — without it a loud sleeve washes out the
/// sidebar labels.
///
/// The blur runs per-frame on a 96px decode rather than being baked once into a
/// bitmap. Baking it once would be cheaper, but at this decode size the live effect
/// measured fine. Revisit if the visualizer and karaoke sweep end up sharing
/// the frame budget with it.
///
/// It is the only blurred backdrop: Now Playing is translucent over it.
/// </summary>
public sealed class PageBackground : Panel
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<PageBackground, string?>(nameof(Url));

    /// <summary>Scrim over the art. Tuned against a saturated cover, not a dark
    /// one — a near-black sleeve looks the same at any value.</summary>
    public static readonly StyledProperty<IBrush> ScrimProperty =
        AvaloniaProperty.Register<PageBackground, IBrush>(
            nameof(Scrim), new SolidColorBrush(Color.FromArgb(0xA6, 0x0A, 0x0A, 0x0C)));

    /// <summary>How long one cover takes to give way to the next.</summary>
    public static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(400);

    /// <summary>Under reduced motion a cover replaces the last at
    /// once: a whole-window colour wash is exactly the motion the setting asks
    /// to be spared.</summary>
    public static readonly StyledProperty<bool> ReduceMotionProperty =
        AvaloniaProperty.Register<PageBackground, bool>(nameof(ReduceMotion));

    private const double ArtOpacity = 0.85;

    // Two layers, so a track change dissolves instead of hard-cutting the whole
    // window's colour. The outgoing cover fades as soon as the track changes;
    // the incoming one fades in once its bitmap has actually arrived, so a slow
    // or failed fetch shows the scrim rather than the previous song's art.
    private readonly RemoteImage[] _layers = [CreateLayer(), CreateLayer()];
    private int _current;

    // Above both layers whichever one is raised to the front.
    private readonly Border _scrim = new() { ZIndex = 2 };

    public PageBackground()
    {
        foreach (var layer in _layers)
        {
            Children.Add(layer);
            layer.PropertyChanged += (sender, e) =>
            {
                if (e.Property == Image.SourceProperty && e.NewValue is not null &&
                    ReferenceEquals(sender, _layers[_current]))
                {
                    ((RemoteImage)sender!).Opacity = ArtOpacity;
                }
            };
        }
        Children.Add(_scrim);
        _scrim.Bind(Border.BackgroundProperty, this.GetObservable(ScrimProperty));
    }

    private static RemoteImage CreateLayer() => new()
    {
        // Small decode: it is blurred to illegibility anyway, and this is the
        // one image on screen that covers the entire window.
        DecodeWidth = 96,
        Stretch = Stretch.UniformToFill,
        Opacity = 0,
        Effect = new BlurEffect { Radius = 90 },
        RenderTransform = new ScaleTransform(1.2, 1.2),
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Transitions =
        [
            new DoubleTransition { Property = OpacityProperty, Duration = FadeDuration },
        ],
    };

    /// <summary>The fade the layers use now: <see cref="FadeDuration"/>, or zero
    /// under reduced motion.</summary>
    public TimeSpan CurrentFade =>
        _layers[0].Transitions is [DoubleTransition fade, ..] ? fade.Duration : TimeSpan.Zero;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty)
        {
            ShowArt(change.GetNewValue<string?>());
        }
        else if (change.Property == ReduceMotionProperty)
        {
            var duration = Services.AppearanceSettings.Duration(FadeDuration, change.GetNewValue<bool>());
            foreach (var layer in _layers)
            {
                if (layer.Transitions is [DoubleTransition fade, ..])
                    fade.Duration = duration;
            }
        }
    }

    private void ShowArt(string? url)
    {
        var outgoing = _layers[_current];
        if (outgoing.Url == url)
            return;

        outgoing.Opacity = 0;
        _current = 1 - _current;
        var incoming = _layers[_current];
        incoming.ZIndex = 1;
        outgoing.ZIndex = 0;
        // Already showing this bitmap (back to the previous cover): the
        // Source won't change, so fade it back in directly.
        if (incoming.Url == url && incoming.Source is not null)
            incoming.Opacity = ArtOpacity;
        else
            incoming.Url = url;
    }

    public bool ReduceMotion
    {
        get => GetValue(ReduceMotionProperty);
        set => SetValue(ReduceMotionProperty, value);
    }

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public IBrush Scrim
    {
        get => GetValue(ScrimProperty);
        set => SetValue(ScrimProperty, value);
    }
}
