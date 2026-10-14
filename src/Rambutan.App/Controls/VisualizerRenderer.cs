using System;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>What a renderer reads besides the bands. The handler fetches only
/// these, so an output nobody draws lets its analyzer lease lapse.</summary>
[Flags]
internal enum VisualizerInputs
{
    None = 0,
    /// <summary><see cref="VisualizerFrame.Waveform"/>, <see cref="VisualizerFrame.Left"/>
    /// and <see cref="VisualizerFrame.Right"/>.</summary>
    Waveform = 1,
    /// <summary><see cref="VisualizerFrame.Beat"/>.</summary>
    Beat = 2,
}

/// <summary>The colours a style draws with, converted once when they change.</summary>
internal readonly record struct VisualizerPalette(SKColor Bar, SKColor Peak);

/// <summary>
/// One frame's input to a <see cref="IVisualizerRenderer"/>: spans over buffers
/// the handler owns and reuses, valid only during the
/// <see cref="IVisualizerRenderer.Render"/> call.
/// </summary>
internal readonly ref struct VisualizerFrame
{
    /// <summary>Smoothed band levels, 0..1, low to high.</summary>
    public ReadOnlySpan<float> Bands { get; init; }

    /// <summary>Peak-hold levels per band, 0..1; empty when peaks are off.</summary>
    public ReadOnlySpan<float> Peaks { get; init; }

    /// <summary>The recent waveform, mono, -1..1, oldest first; empty unless the
    /// renderer asks for <see cref="VisualizerInputs.Waveform"/>. Scaled by
    /// <see cref="Activity"/>, so it flattens when the feed stops.</summary>
    public ReadOnlySpan<float> Waveform { get; init; }

    /// <summary>The left channel of <see cref="Waveform"/>.</summary>
    public ReadOnlySpan<float> Left { get; init; }

    /// <summary>The right channel of <see cref="Waveform"/>.</summary>
    public ReadOnlySpan<float> Right { get; init; }

    /// <summary>The beat envelope, 0..1: jumps on a beat, falls within a few
    /// hundred milliseconds. Beats come at most three a second.</summary>
    public float Beat { get; init; }

    /// <summary>The mean of <see cref="Bands"/>.</summary>
    public float Level { get; init; }

    /// <summary>1 while the feed runs, falling to 0 after it stops (pause, stale
    /// analyzer). An animated style scales its motion by it, so it comes to rest
    /// instead of stopping mid-move when the redraws stop.</summary>
    public float Activity { get; init; }

    /// <summary>Seconds since the visualizer started drawing.</summary>
    public double Time { get; init; }

    public VisualizerPalette Palette { get; init; }
}

/// <summary>
/// A visualizer style, drawn through Skia on the render thread. Built on the UI
/// thread and handed to the handler, which owns it from then on: every call,
/// and <see cref="IDisposable.Dispose"/>, comes on the render thread. Allocates
/// nothing per frame: paints, paths and surfaces are fields, reused.
/// </summary>
internal interface IVisualizerRenderer : IDisposable
{
    VisualizerInputs Inputs { get; }

    /// <summary>
    /// True when the style moves on its own (rotation, trails, a shader's time):
    /// it redraws at the frame cap while anything plays, until the feed stops
    /// and <see cref="VisualizerFrame.Activity"/> reaches 0. False when the
    /// picture follows the bands and peaks alone, so it redraws only when they
    /// move visibly. A style that draws the waveform is animated.
    /// </summary>
    bool IsAnimated { get; }

    /// <summary>
    /// Draws one frame in a <paramref name="size"/> area at the canvas origin, in
    /// device-independent pixels. <paramref name="scale"/> is device pixels per
    /// DIP: what an offscreen surface multiplies its size by, and what a
    /// hairline divides by. The canvas's state is restored afterwards.
    /// </summary>
    void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame);
}
