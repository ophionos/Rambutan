using Rambutan.App.Controls;
using Rambutan.App.Services;
using SkiaSharp;

namespace Rambutan.App.Tests;

/// <summary>The Now Playing visualizer's styles: their geometry, that each
/// draws, and that a frame allocates nothing.</summary>
public class VisualizerRendererTests
{
    private static readonly VisualizerPalette Palette = new(new SKColor(0xFF, 0x4E, 0x45), new SKColor(0xFF, 0xFF, 0xFF, 0x8C));

    private sealed class Buffers
    {
        public readonly float[] Bands = new float[32];
        public readonly float[] Peaks = new float[32];
        public readonly float[] Left = new float[512];
        public readonly float[] Right = new float[512];
        public readonly float[] Mono = new float[512];

        public Buffers(float level = 0.5f)
        {
            for (var i = 0; i < Bands.Length; i++)
            {
                Bands[i] = level * (0.5f + 0.5f * MathF.Sin(i * 0.4f));
                Peaks[i] = Math.Min(1, Bands[i] + 0.2f);
            }
            for (var i = 0; i < Left.Length; i++)
            {
                Left[i] = 0.6f * MathF.Sin(i * 0.11f);
                Right[i] = 0.4f * MathF.Sin(i * 0.11f + 0.6f);
                Mono[i] = (Left[i] + Right[i]) / 2;
            }
        }

        public VisualizerFrame Frame() => new()
        {
            Bands = Bands, Peaks = Peaks, Waveform = Mono, Left = Left, Right = Right,
            Level = 0.25f, Activity = 1, Time = 1, Palette = Palette,
        };
    }

    public static TheoryData<VisualizerStyle> Styles() => new(AppearanceSettings.StyleChoices);

    [Theory]
    [MemberData(nameof(Styles))]
    public void EveryStyle_DrawsSomething(VisualizerStyle style)
    {
        var size = style == VisualizerStyle.Radial ? new SKSize(400, 400) : new SKSize(600, 110);
        using var surface = SKSurface.Create(new SKImageInfo((int)size.Width, (int)size.Height, SKColorType.Rgba8888, SKAlphaType.Premul));
        using var renderer = VisualizerRenderers.Create(style);
        var buffers = new Buffers();

        renderer.Render(surface.Canvas, size, 1, buffers.Frame());

        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        var painted = 0;
        var bytes = pixmap.GetPixelSpan();
        for (var i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] > 0)
                painted++;
        }
        Assert.True(painted > 200, $"{style} painted {painted} pixels");
    }

    [Theory]
    [MemberData(nameof(Styles))]
    public void AFrame_AllocatesNothing(VisualizerStyle style)
    {
        var size = new SKSize(400, 400);
        using var surface = SKSurface.Create(new SKImageInfo(400, 400));
        using var renderer = VisualizerRenderers.Create(style);
        var buffers = new Buffers();
        // The first frame may build what the size or colour needs (a gradient).
        renderer.Render(surface.Canvas, size, 1, buffers.Frame());

        // A one-off allocation on this thread (the JIT recompiling a hot method)
        // can land in any one pass; a per-frame allocation lands in every pass.
        var allocated = new long[3];
        for (var pass = 0; pass < allocated.Length; pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
            {
                buffers.Bands[i % 32] = i / 20f;
                renderer.Render(surface.Canvas, size, 1.5f, buffers.Frame());
            }
            allocated[pass] = GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Assert.Equal(0, allocated.Min());
    }

    [Fact]
    public void TheFactory_GivesBarsForBars()
    {
        using var renderer = VisualizerRenderers.Create(VisualizerStyle.Bars);
        Assert.IsType<BarsRenderer>(renderer);
    }

    [Theory]
    [InlineData(VisualizerStyle.Oscilloscope)]
    [InlineData(VisualizerStyle.Vectorscope)]
    public void TheScopes_ReadTheWaveform_AndAnimate(VisualizerStyle style)
    {
        using var renderer = VisualizerRenderers.Create(style);
        Assert.Equal(VisualizerInputs.Waveform, renderer.Inputs);
        Assert.True(renderer.IsAnimated);
    }

    [Theory]
    [InlineData(VisualizerStyle.Bars)]
    [InlineData(VisualizerStyle.MirroredBars)]
    [InlineData(VisualizerStyle.Led)]
    [InlineData(VisualizerStyle.Line)]
    [InlineData(VisualizerStyle.Area)]
    [InlineData(VisualizerStyle.Radial)]
    public void TheSpectrumStyles_ReadOnlyTheBands_AndRestWithThem(VisualizerStyle style)
    {
        using var renderer = VisualizerRenderers.Create(style);
        Assert.Equal(VisualizerInputs.None, renderer.Inputs);
        Assert.False(renderer.IsAnimated);
    }

    [Theory]
    [InlineData(110f, 18)]
    [InlineData(64f, 11)]
    [InlineData(4f, 1)]
    [InlineData(0f, 1)]
    public void Led_FitsWholeSegments(float height, int segments) =>
        Assert.Equal(segments, LedRenderer.Segments(height));

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(0.02f, 0)]
    [InlineData(0.03f, 1)]
    [InlineData(0.5f, 9)]
    [InlineData(1f, 18)]
    [InlineData(1.5f, 18)]
    public void Led_LightsSegmentsToTheLevel(float level, int lit) =>
        Assert.Equal(lit, LedRenderer.Lit(level, 18));

    [Fact]
    public void Oscilloscope_TriggersOnTheFirstRisingZeroCrossing()
    {
        float[] samples = [0.5f, 0.2f, -0.1f, -0.4f, -0.2f, 0.1f, 0.3f, -0.2f, 0.4f];
        Assert.Equal(5, OscilloscopeRenderer.Trigger(samples, 8));
    }

    [Fact]
    public void Oscilloscope_WithNoCrossing_StartsAtTheBeginning()
    {
        Assert.Equal(0, OscilloscopeRenderer.Trigger([0.1f, 0.2f, 0.3f], 2));
        // A crossing after the limit doesn't count: the window must still fit.
        Assert.Equal(0, OscilloscopeRenderer.Trigger([-0.1f, -0.2f, -0.3f, 0.3f], 2));
    }

    [Fact]
    public void Vectorscope_MonoIsVertical_AndOppositePhaseHorizontal()
    {
        var (monoX, monoY) = VectorscopeRenderer.Plot(0.5f, 0.5f);
        Assert.Equal(0, monoX, 6);
        Assert.True(monoY > 0);

        var (sideX, sideY) = VectorscopeRenderer.Plot(0.5f, -0.5f);
        Assert.Equal(0, sideY, 6);
        Assert.True(sideX > 0);

        Assert.Equal((1f, 0f), VectorscopeRenderer.Plot(2f, -2f));
    }

    [Fact]
    public void Radial_LowsAtTheBottom_HighsAtTheTop_MirroredLeftAndRight()
    {
        var (lowX, lowY) = RadialRenderer.Direction(0, 32, mirrored: false);
        var (highX, highY) = RadialRenderer.Direction(31, 32, mirrored: false);
        var (mirrorX, mirrorY) = RadialRenderer.Direction(0, 32, mirrored: true);

        Assert.True(lowY > 0.99f && lowX > 0);   // just right of straight down
        Assert.True(highY < -0.99f && highX > 0); // just right of straight up
        Assert.Equal(-lowX, mirrorX);
        Assert.Equal(lowY, mirrorY);
    }

    [Theory]
    [InlineData(1f, 0f, 150f)]
    [InlineData(0f, -1f, 150f)]
    [InlineData(0.70710678f, 0.70710678f, 212.13f)]
    public void Radial_SpokesStartAtTheArtworksEdge(float x, float y, float distance) =>
        Assert.Equal(distance, RadialRenderer.ToEdge(150, x, y), 1);

    [Fact]
    public void TheCurve_StaysInsideItsBand()
    {
        float[] levels = [0, 1, 0, 1, 1, 0, 0.5f];
        using var path = new SKPath();
        SpectrumCurveRenderer.Trace(path, levels, 300, top: 1, bottom: 109, moveFirst: true);

        var bounds = path.Bounds;
        Assert.InRange(bounds.Top, 1 - 0.01f, 109);
        Assert.InRange(bounds.Bottom, 1, 109 + 0.01f);
        Assert.Equal(0, bounds.Left, 3);
        Assert.Equal(300, bounds.Right, 3);
    }

    [Fact]
    public void AStoppedFeed_IsIdle() =>
        Assert.True(VisualizerControl.IsIdle(stale: true, [0.5f, 0.8f]));

    [Fact]
    public void ASilentFeed_IsIdle()
    {
        // Paused, the output runs on and the analyzer reads silence: fresh
        // frames, every band at the floor.
        Assert.True(VisualizerControl.IsIdle(stale: false, new float[32]));
    }

    [Fact]
    public void AFeedWithAnyLevel_IsLive()
    {
        var bands = new float[32];
        bands[31] = 0.01f;
        Assert.False(VisualizerControl.IsIdle(stale: false, bands));
    }

    [Fact]
    public void TheRing_IsGatedByItsSpokes_NotItsArea()
    {
        using var ring = VisualizerRenderers.Create(VisualizerStyle.Radial);
        using var bars = VisualizerRenderers.Create(VisualizerStyle.Bars);
        Assert.Equal(RadialRenderer.Extent, ring.LevelReach(new SKSize(400, 400)));
        Assert.Equal(110, bars.LevelReach(new SKSize(600, 110)));
    }
}
