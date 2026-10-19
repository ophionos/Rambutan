using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Rambutan.Integrations.Taskbar;

namespace Rambutan.App.Services;

/// <summary>
/// Draws the taskbar toolbar's icons from the app's own glyphs
/// (<c>Styles/Icons.axaml</c>), so they match the player bar. Windows draws a
/// thumbnail button's icon as given, so the colour follows the taskbar's mode.
/// </summary>
internal static class TaskbarGlyphs
{
    public static string ResourceKey(TaskbarGlyph glyph) => glyph switch
    {
        TaskbarGlyph.Previous => "Icon.Previous",
        TaskbarGlyph.Play => "Icon.Play",
        TaskbarGlyph.Pause => "Icon.Pause",
        TaskbarGlyph.Next => "Icon.Next",
        TaskbarGlyph.Like => "Icon.ThumbUpOutline",
        TaskbarGlyph.Liked => "Icon.ThumbUp",
        _ => "Icon.Shuffle",
    };

    /// <summary>Every glyph <paramref name="find"/> knows, <paramref name="size"/>
    /// pixels square. Must run on the UI thread.</summary>
    public static Dictionary<TaskbarGlyph, IconPixels> RenderAll(Func<string, Geometry?> find, int size, Color color)
    {
        var icons = new Dictionary<TaskbarGlyph, IconPixels>();
        foreach (var glyph in Enum.GetValues<TaskbarGlyph>())
        {
            if (find(ResourceKey(glyph)) is { } geometry)
                icons[glyph] = Render(geometry, size, color, marked: glyph == TaskbarGlyph.ShuffleOn);
        }
        return icons;
    }

    /// <summary>A 24×24 glyph scaled to <paramref name="size"/>, as premultiplied
    /// BGRA. A thumbnail button can't show that it is on, so a
    /// <paramref name="marked"/> glyph sits smaller over a dot, as an active mode does.</summary>
    public static IconPixels Render(Geometry geometry, int size, Color color, bool marked)
    {
        var scale = size / 24.0;
        using var bitmap = new RenderTargetBitmap(new PixelSize(size, size), new Vector(96, 96));
        using (var context = bitmap.CreateDrawingContext())
        {
            var brush = new ImmutableSolidColorBrush(color);
            var placement = marked
                ? Matrix.CreateTranslation(-12, -12) * Matrix.CreateScale(0.78, 0.78) * Matrix.CreateTranslation(12, 9.5)
                : Matrix.Identity;
            using (context.PushTransform(placement * Matrix.CreateScale(scale, scale)))
                context.DrawGeometry(brush, null, geometry);
            if (marked)
                context.DrawEllipse(brush, null, new Point(12 * scale, 21.8 * scale), 1.7 * scale, 1.7 * scale);
        }

        var pixels = new byte[size * size * 4];
        var pin = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(0, 0, size, size), pin.AddrOfPinnedObject(), pixels.Length, size * 4);
        }
        finally
        {
            pin.Free();
        }
        if (bitmap.Format == PixelFormat.Rgba8888)
        {
            for (var i = 0; i < pixels.Length; i += 4)
                (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
        }
        return new IconPixels(size, pixels);
    }
}
