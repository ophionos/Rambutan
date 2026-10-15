using Rambutan.App.Controls;
using Rambutan.App.Services;
using SkiaSharp;

namespace Rambutan.App.Tests;

/// <summary>The animated presets: that their shaders compile, that each draws,
/// comes to rest and stays within its allocation, and that none flashes.</summary>
public class VisualizerPresetTests
{
    private static readonly SKColor Red = new(0xFF, 0x4E, 0x45);

    public static TheoryData<VisualizerStyle> Presets() => new(AppearanceSettings.PresetChoices);

    private sealed class Feed
    {
        public readonly float[] Bands = new float[32];
        public double Time = 1;
        public float Beat;
        public float Activity = 1;
        public SKColor Colour = Red;

        public void Level(float level)
        {
            for (var i = 0; i < Bands.Length; i++)
                Bands[i] = level * (0.5f + 0.5f * MathF.Sin(i * 0.4f));
        }

        public VisualizerFrame Frame()
        {
            float level = 0;
            foreach (var band in Bands)
                level += band;
            return new VisualizerFrame
            {
                Bands = Bands, Beat = Beat, Level = level / Bands.Length, Activity = Activity, Time = Time,
                Palette = new VisualizerPalette(Colour, SKColors.White),
            };
        }
    }

    private static SKSurface Surface(int width, int height) =>
        SKSurface.Create(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));

    private static byte[] Pixels(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        return pixmap.GetPixelSpan().ToArray();
    }

    [Fact]
    public void EveryShaderSource_Compiles_AndHasAPreset()
    {
        const string prefix = "Rambutan.App.Visualizer.";
        var names = typeof(ShaderPreset).Assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".sksl", StringComparison.Ordinal))
            .Select(name => name[prefix.Length..^".sksl".Length])
            .Order()
            .ToArray();

        Assert.Equal(["kaleidoscope", "plasma", "tunnel"], names);
        foreach (var name in names)
            Assert.NotNull(new ShaderPreset(name, 1).Effect);
        Assert.Equal(names, new[] { ShaderPresets.Plasma, ShaderPresets.Tunnel, ShaderPresets.Kaleidoscope }
            .Select(preset => preset.Name).Order());
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void EveryPreset_DrawsSomething_Animated_OnTheBeat(VisualizerStyle style)
    {
        using var surface = Surface(600, 110);
        using var renderer = VisualizerRenderers.Create(style);
        var feed = new Feed();
        feed.Level(0.5f);

        renderer.Render(surface.Canvas, new SKSize(600, 110), 1, feed.Frame());

        var bytes = Pixels(surface);
        var painted = 0;
        for (var i = 3; i < bytes.Length; i += 4)
        {
            if (bytes[i] > 0)
                painted++;
        }
        Assert.True(painted > 200, $"{style} painted {painted} pixels");
        Assert.True(renderer.IsAnimated);
        Assert.Equal(VisualizerInputs.Beat, renderer.Inputs);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void APresetsEdges_StayFaded_FrameAfterFrame(VisualizerStyle style)
    {
        using var surface = Surface(600, 110);
        using var renderer = VisualizerRenderers.Create(style);
        var feed = new Feed();
        feed.Level(0.5f);

        for (var frame = 0; frame < 30; frame++)
        {
            feed.Time += 1 / 30.0;
            surface.Canvas.Clear(SKColors.Transparent);
            renderer.Render(surface.Canvas, new SKSize(600, 110), 1, feed.Frame());
        }

        // Every preset fades out towards the area's edge; one that drew over
        // its last frame would build the edge up to opaque.
        using var image = surface.Snapshot();
        using var pixmap = image.PeekPixels();
        Assert.True(pixmap.GetPixelColor(300, 0).Alpha < 40, $"{style}: top edge alpha {pixmap.GetPixelColor(300, 0).Alpha}");
        Assert.True(pixmap.GetPixelColor(0, 55).Alpha < 40, $"{style}: left edge alpha {pixmap.GetPixelColor(0, 55).Alpha}");
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void APreset_WithoutActivity_ComesToRest(VisualizerStyle style)
    {
        using var surface = Surface(320, 180);
        using var renderer = VisualizerRenderers.Create(style);
        var feed = new Feed { Activity = 0 };
        byte[] previous = [];
        byte[] last = [];

        // The feedback loop's trails need a few seconds to fade out.
        for (var frame = 0; frame < 150; frame++)
        {
            feed.Time += 1 / 30.0;
            surface.Canvas.Clear(SKColors.Transparent);
            renderer.Render(surface.Canvas, new SKSize(320, 180), 1, feed.Frame());
            previous = last;
            last = Pixels(surface);
        }

        Assert.Equal(previous, last);
    }

    [Theory]
    [MemberData(nameof(Presets))]
    public void APresetsFrame_AllocatesOnlyItsShaderAndSnapshots(VisualizerStyle style)
    {
        using var surface = Surface(400, 200);
        using var renderer = VisualizerRenderers.Create(style);
        var feed = new Feed();
        feed.Level(0.5f);
        for (var i = 0; i < 20; i++)
        {
            feed.Time += 1 / 30.0;
            renderer.Render(surface.Canvas, new SKSize(400, 200), 1.5f, feed.Frame());
        }

        // About 300 bytes of SkiaSharp wrappers a frame (a built shader, its
        // uniform data, the snapshots that scale up); anything per band or
        // per pixel would be far more.
        var allocated = new long[3];
        for (var pass = 0; pass < allocated.Length; pass++)
        {
            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < 20; i++)
            {
                feed.Time += 1 / 30.0;
                feed.Bands[i % 32] = i / 20f;
                renderer.Render(surface.Canvas, new SKSize(400, 200), 1.5f, feed.Frame());
            }
            allocated[pass] = (GC.GetAllocatedBytesForCurrentThread() - before) / 20;
        }
        Assert.InRange(allocated.Min(), 0, 512);
    }

    /// <summary>
    /// WCAG 2.3.1: no more than three flashes in any second, a flash being a
    /// pair of opposing changes in relative luminance of 0.1 or more where the
    /// darker side is below 0.8. The feed is the worst a broken analyzer could
    /// send: every band and the beat jump between nothing and full, every
    /// frame (which the easing averages out) and five times a second (which it
    /// passes). Measured over the whole area and each quarter, in the accent's
    /// red and in white. Bands moving opposite ways cancel out in an average,
    /// so small tiles are counted too: WCAG allows flashing over a combined
    /// area of about a quarter of the screen, so no more than a quarter of the
    /// tiles may flash.
    /// </summary>
    [Theory]
    [MemberData(nameof(Presets))]
    public void NoPreset_Flashes(VisualizerStyle style)
    {
        const int Tile = 20;
        foreach (var (colour, half) in new[] { (Red, 1), (Red, 3), (SKColors.White, 1), (SKColors.White, 3) })
        {
            using var surface = Surface(320, 180);
            using var renderer = VisualizerRenderers.Create(style);
            var feed = new Feed { Colour = colour };
            var regions = new[]
            {
                new SKRectI(0, 0, 320, 180),
                new SKRectI(0, 0, 160, 90), new SKRectI(160, 0, 320, 90),
                new SKRectI(0, 90, 160, 180), new SKRectI(160, 90, 320, 180),
            };
            var tiles = (from y in Enumerable.Range(0, 180 / Tile)
                         from x in Enumerable.Range(0, 320 / Tile)
                         select new SKRectI(x * Tile, y * Tile, (x + 1) * Tile, (y + 1) * Tile)).ToArray();
            var luminance = regions.Select(_ => new List<double>()).ToArray();
            var tileLuminance = tiles.Select(_ => new List<double>()).ToArray();

            for (var frame = 0; frame < 90; frame++)
            {
                feed.Time += 1 / 30.0;
                feed.Level(frame / half % 2);
                feed.Beat = frame / half % 2;
                surface.Canvas.Clear(SKColors.Transparent);
                renderer.Render(surface.Canvas, new SKSize(320, 180), 1, feed.Frame());
                var bytes = Pixels(surface);
                for (var r = 0; r < regions.Length; r++)
                    luminance[r].Add(MeanLuminance(bytes, 320, regions[r]));
                for (var t = 0; t < tiles.Length; t++)
                    tileLuminance[t].Add(MeanLuminance(bytes, 320, tiles[t]));
            }

            foreach (var series in luminance)
            {
                var worst = MostTransitionsInASecond(Transitions(series), framesPerSecond: 30);
                Assert.True(worst <= 6, $"{style} in {colour}, {half}-frame steps: {worst} luminance transitions in a second");
            }
            var flashing = tileLuminance.Count(series => MostTransitionsInASecond(Transitions(series), framesPerSecond: 30) > 6);
            Assert.True(flashing * 4 <= tiles.Length,
                $"{style} in {colour}, {half}-frame steps: {flashing} of {tiles.Length} tiles flash");
        }
    }

    [Fact]
    public void TheFlashCounter_CountsASquareWave()
    {
        // 0.1 ↔ 0.5 five times a second: ten transitions a second.
        var series = Enumerable.Range(0, 60).Select(i => i / 3 % 2 == 0 ? 0.1 : 0.5).ToList();
        Assert.Equal(10, MostTransitionsInASecond(Transitions(series), 30));

        // A slow drift isn't a flash, nor a change that is too small.
        Assert.Empty(Transitions(Enumerable.Range(0, 60).Select(i => 0.3 + 0.05 * (i % 2)).ToList()));
        // Above 0.8 on both sides isn't one either.
        Assert.Empty(Transitions(Enumerable.Range(0, 60).Select(i => i % 2 == 0 ? 0.85 : 0.97).ToList()));
    }

    /// <summary>The frames where luminance turned by 0.1 or more from its last
    /// extreme, with the darker side below 0.8.</summary>
    internal static List<int> Transitions(IReadOnlyList<double> series)
    {
        var frames = new List<int>();
        var direction = 0;
        double low = series[0], high = series[0];
        for (var i = 1; i < series.Count; i++)
        {
            var v = series[i];
            if (direction >= 0)
            {
                high = Math.Max(high, v);
                if (high - v >= 0.1 && v < 0.8)
                {
                    frames.Add(i);
                    direction = -1;
                    low = v;
                    continue;
                }
            }
            if (direction <= 0)
            {
                low = Math.Min(low, v);
                if (v - low >= 0.1 && low < 0.8)
                {
                    frames.Add(i);
                    direction = 1;
                    high = v;
                }
            }
        }
        return frames;
    }

    private static int MostTransitionsInASecond(List<int> frames, int framesPerSecond)
    {
        var most = 0;
        for (var start = 0; start < frames.Count; start++)
        {
            var count = frames.Count(f => f >= frames[start] && f < frames[start] + framesPerSecond);
            most = Math.Max(most, count);
        }
        return most;
    }

    /// <summary>The mean relative luminance of a region, over black (the
    /// pixels are premultiplied, so they already are).</summary>
    private static double MeanLuminance(byte[] rgba, int stride, SKRectI region)
    {
        double sum = 0;
        for (var y = region.Top; y < region.Bottom; y++)
        {
            for (var x = region.Left; x < region.Right; x++)
            {
                var i = (y * stride + x) * 4;
                sum += 0.2126 * Linear(rgba[i]) + 0.7152 * Linear(rgba[i + 1]) + 0.0722 * Linear(rgba[i + 2]);
            }
        }
        return sum / (region.Width * region.Height);
    }

    private static double Linear(byte value)
    {
        var c = value / 255.0;
        return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    }

    [Fact]
    public void TheRenderScale_IsHalfTheScreen_WithinABudget()
    {
        // The 892 × 110 band at 100 %: half its pixels each way.
        Assert.Equal(new SKSizeI(446, 55), VisualizerRenderScale.Pixels(new SKSize(892, 110), 1, gpu: true));
        // A 4K stage: held to the GPU budget.
        var stage = VisualizerRenderScale.Pixels(new SKSize(1920, 1080), 2, gpu: true);
        Assert.InRange(stage.Width * stage.Height, 0, VisualizerRenderScale.GpuBudget * 1.01);
        // In software, a far smaller budget.
        var software = VisualizerRenderScale.Pixels(new SKSize(1280, 720), 1, gpu: false);
        Assert.InRange(software.Width * software.Height, 0, VisualizerRenderScale.SoftwareBudget * 1.01);
        Assert.Equal(new SKSizeI(1, 1), VisualizerRenderScale.Pixels(new SKSize(0, 0), 1, gpu: true));
    }

    [Fact]
    public void TheShadersPhase_MovesOnlyWithActivity()
    {
        Assert.Equal(3.0, ShaderRenderer.Advance(3, 1, activity: 0, level: 1, speed: 1));
        Assert.True(ShaderRenderer.Advance(3, 1, activity: 1, level: 1, speed: 1) > 3);
        Assert.True(ShaderRenderer.Advance(0, 1, 1, level: 1, speed: 1) > ShaderRenderer.Advance(0, 1, 1, level: 0, speed: 1));
    }

    [Fact]
    public void TheFeedbackLoop_StandsStillWithoutActivity_AndFadesWithTheClock()
    {
        Assert.Equal(1f, FeedbackRenderer.Zoom(0.1f, activity: 0, bass: 1, beat: 1));
        Assert.True(FeedbackRenderer.Zoom(0.1f, activity: 1, bass: 0, beat: 0) > 1);
        Assert.Equal(1f, FeedbackRenderer.Keep(0));
        Assert.InRange(FeedbackRenderer.Keep((float)FeedbackRenderer.FadeTau), 0.36f, 0.37f);
    }

    [Fact]
    public void TheFeedbackRing_StaysWithinItsReach()
    {
        using var path = new SKPath();
        var bands = Enumerable.Range(0, 32).Select(i => i % 2 == 0 ? 1f : 0f).ToArray();
        FeedbackRenderer.TraceRing(path, bands, cx: 100, cy: 100, radius: 20, reach: 30, turn: 0.7f);

        Assert.Equal(64, path.PointCount);
        foreach (var point in path.Points)
        {
            var distance = SKPoint.Distance(point, new SKPoint(100, 100));
            Assert.InRange(distance, 20 - 0.01f, 50 + 0.01f);
        }
    }
}
