using System;
using System.IO;
using SkiaSharp;

namespace Rambutan.App.Controls;

/// <summary>
/// An animated preset that is one SkSL shader: its source is an embedded
/// <c>Visualizer/*.sksl</c> file, and the rest is how fast it moves.
/// </summary>
/// <param name="Name">The source's file name, without the extension.</param>
/// <param name="Speed">How much phase a second of full activity adds, before
/// the level speeds it up.</param>
internal sealed record ShaderPreset(string Name, float Speed)
{
    private SKRuntimeEffect? _effect;

    /// <summary>
    /// The compiled shader, shared by every renderer of this preset and never
    /// disposed: it is immutable, so the UI thread that compiles it and the
    /// render thread that draws with it can share it. Every source takes
    /// <c>uniform float4 u[3]</c>: (width, height, phase, activity),
    /// (bass, mid, high, beat), (red, green, blue, 0), sizes in pixels and the
    /// rest 0..1. Throws when the source doesn't compile, which the tests catch.
    /// </summary>
    public SKRuntimeEffect Effect => _effect ??= Compile(Name);

    internal static string Source(string name)
    {
        using var stream = typeof(ShaderPreset).Assembly.GetManifestResourceStream($"Rambutan.App.Visualizer.{name}.sksl")
            ?? throw new InvalidOperationException($"No shader source named {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static SKRuntimeEffect Compile(string name) =>
        SKRuntimeEffect.CreateShader(Source(name), out var errors)
            ?? throw new InvalidOperationException($"Shader {name} doesn't compile: {errors}");
}

internal static class ShaderPresets
{
    public static readonly ShaderPreset Plasma = new("plasma", 0.6f);
    public static readonly ShaderPreset Tunnel = new("tunnel", 0.9f);
    public static readonly ShaderPreset Kaleidoscope = new("kaleidoscope", 0.5f);
}

/// <summary>
/// Draws a <see cref="ShaderPreset"/> over the whole area, at
/// <see cref="VisualizerRenderScale"/> into an offscreen surface, scaled up.
/// A shader's uniforms are baked in when SkiaSharp builds it, so each frame
/// builds one: about 300 bytes of wrappers a frame, disposed in the frame.
/// SkiaSharp has no way to update a built shader.
/// </summary>
internal sealed class ShaderRenderer : IVisualizerRenderer
{
    private readonly ShaderPreset _preset;
    private readonly SKRuntimeEffectUniforms _uniforms;
    private readonly float[] _values = new float[12];
    private readonly VisualizerSurface _surface = new();
    private readonly EasedAudio _audio = new();
    // Replaces the last frame rather than blending over it: blended, the
    // translucent edges would build up to opaque in a few frames.
    private readonly SKPaint _fill = new() { BlendMode = SKBlendMode.Src };
    private readonly SKPaint _present = new();
    private double _phase;

    public ShaderRenderer(ShaderPreset preset)
    {
        _preset = preset;
        _uniforms = new SKRuntimeEffectUniforms(preset.Effect);
    }

    public VisualizerInputs Inputs => VisualizerInputs.Beat;

    public bool IsAnimated => true;

    /// <summary>The phase after <paramref name="seconds"/> more: it moves only
    /// while the feed runs, and faster with the level, so the picture comes to
    /// rest with the music rather than freezing mid-move when the redraws stop.</summary>
    internal static double Advance(double phase, float seconds, float activity, float level, float speed) =>
        // Wrapped so the float the shader gets keeps its precision; the jump
        // comes after about a day of playing at full speed.
        (phase + seconds * activity * speed * (0.6 + level)) % 100_000;

    public void Render(SKCanvas canvas, SKSize size, float scale, in VisualizerFrame frame)
    {
        _audio.Update(in frame);
        _phase = Advance(_phase, _audio.Delta, frame.Activity, frame.Level, _preset.Speed);

        var pixels = VisualizerRenderScale.Pixels(size, scale, frame.Gpu is not null);
        if (_surface.Ensure(frame.Gpu, pixels, out _) is not { } surface)
            return;

        var colour = frame.Palette.Bar;
        _values[0] = pixels.Width;
        _values[1] = pixels.Height;
        _values[2] = (float)_phase;
        _values[3] = frame.Activity;
        _values[4] = _audio.Bass;
        _values[5] = _audio.Mid;
        _values[6] = _audio.High;
        _values[7] = _audio.Beat;
        _values[8] = colour.Red / 255f;
        _values[9] = colour.Green / 255f;
        _values[10] = colour.Blue / 255f;
        _uniforms["u"] = _values;

        using (var shader = _preset.Effect.ToShader(_uniforms))
        {
            _fill.Shader = shader;
            surface.Canvas.DrawRect(0, 0, pixels.Width, pixels.Height, _fill);
            _fill.Shader = null;
        }
        VisualizerSurface.Present(surface, canvas, new SKRect(0, 0, size.Width, size.Height), _present);
    }

    public void Dispose()
    {
        _surface.Dispose();
        _uniforms.Dispose();
        _fill.Dispose();
        _present.Dispose();
    }
}
