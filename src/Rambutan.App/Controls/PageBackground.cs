using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Rambutan.Core.Appearance;
using Rambutan.Core.Diagnostics;

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
///
/// A cover with little colour (a pastel) would blur to an olive or khaki that
/// the scrim turns brown. Each cover is measured once when it arrives, and
/// <see cref="CoverBackdrop"/> says how grey and how dim to draw it; a weak
/// one is drawn from a greyed copy of its small decode, made then, so the blur
/// stays the only pass per frame. A vivid cover is drawn as it always was.
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

    // Two layers, so a track change dissolves instead of hard-cutting the whole
    // window's colour. The outgoing cover fades as soon as the track changes;
    // the incoming one fades in once its bitmap has actually arrived, so a slow
    // or failed fetch shows the scrim rather than the previous song's art.
    private readonly RemoteImage[] _layers = [CreateLayer(), CreateLayer()];
    private int _current;

    // Each layer's opacity for its cover, and the greyed copy it draws, if any.
    // The cache's bitmap is shared with other images, so it is never changed.
    private readonly double[] _opacity = [CoverBackdrop.ArtOpacity, CoverBackdrop.ArtOpacity];
    private readonly WriteableBitmap?[] _treated = new WriteableBitmap?[2];

    // Above both layers whichever one is raised to the front.
    private readonly Border _scrim = new() { ZIndex = 2 };

    public PageBackground()
    {
        for (var i = 0; i < _layers.Length; i++)
        {
            var index = i;
            var layer = _layers[i];
            Children.Add(layer);
            layer.PropertyChanged += (_, e) =>
            {
                if (e.Property == Image.SourceProperty)
                    OnSourceChanged(index, e.OldValue, e.NewValue);
            };
        }
        Children.Add(_scrim);
        _scrim.Bind(Border.BackgroundProperty, this.GetObservable(ScrimProperty));
    }

    private void OnSourceChanged(int index, object? oldValue, object? newValue)
    {
        var layer = _layers[index];
        if (oldValue is not null && ReferenceEquals(oldValue, _treated[index]) && !ReferenceEquals(newValue, oldValue))
        {
            _treated[index] = null;
            ((WriteableBitmap)oldValue).Dispose();
        }
        if (newValue is Bitmap cover && !ReferenceEquals(cover, _treated[index]))
        {
            var (treatment, treated) = Treat(cover);
            _opacity[index] = treatment.ArtOpacity;
            if (treated is not null)
            {
                // Comes back here with the copy, which is drawn as it is.
                _treated[index] = treated;
                layer.Source = treated;
                return;
            }
        }
        if (newValue is not null && index == _current)
            layer.Opacity = _opacity[index];
    }

    /// <summary>The treatment for <paramref name="cover"/>, and its greyed copy
    /// when it is to be drawn greyer; no copy for a vivid cover. A cover whose
    /// pixels can't be read is drawn as it is.</summary>
    private static (BackdropTreatment Treatment, WriteableBitmap? Treated) Treat(Bitmap cover)
    {
        WriteableBitmap? copy = null;
        try
        {
            var size = cover.PixelSize;
            copy = new WriteableBitmap(size, cover.Dpi, PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            var rowBytes = size.Width * 4;
            var rgba = new byte[rowBytes * size.Height];
            BackdropTreatment treatment;
            using (var buffer = copy.Lock())
            {
                cover.CopyPixels(buffer);
                for (var y = 0; y < size.Height; y++)
                    Marshal.Copy(buffer.Address + y * buffer.RowBytes, rgba, y * rowBytes, rowBytes);
                treatment = CoverBackdrop.For(CoverBackdrop.Chroma(rgba));
                if (treatment.Saturation < 1)
                {
                    CoverBackdrop.Desaturate(rgba, treatment.Saturation);
                    for (var y = 0; y < size.Height; y++)
                        Marshal.Copy(rgba, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
                }
            }
            if (treatment.Saturation < 1)
                return (treatment, copy);
            copy.Dispose();
            return (treatment, null);
        }
        catch (Exception ex)
        {
            copy?.Dispose();
            AppLog.Write("render", $"Couldn't read the background cover's pixels: {ex.Message}");
            return (new BackdropTreatment(1, CoverBackdrop.ArtOpacity), null);
        }
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
            incoming.Opacity = _opacity[_current];
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
