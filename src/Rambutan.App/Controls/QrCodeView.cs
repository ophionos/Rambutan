using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Rambutan.App.Controls;

/// <summary>
/// Draws a <see cref="QrCode"/>: dark modules on white, inside the four-module
/// quiet zone scanners need, whatever the theme. Modules are whole device pixels
/// where the space allows, so the edges stay sharp. A QR code says nothing to a
/// screen reader; the page shows the same link as text beside it.
/// </summary>
public sealed class QrCodeView : Control
{
    private const int QuietZone = 4;

    public static readonly StyledProperty<QrCode?> CodeProperty =
        AvaloniaProperty.Register<QrCodeView, QrCode?>(nameof(Code));

    static QrCodeView() => AffectsRender<QrCodeView>(CodeProperty);

    public QrCode? Code
    {
        get => GetValue(CodeProperty);
        set => SetValue(CodeProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        if (Code is not { } code)
            return;

        var modules = code.Size + QuietZone * 2;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        var scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        // Whole device pixels per module when there's room; fractional otherwise.
        var unit = Math.Floor(side * scaling / modules) / scaling;
        if (unit <= 0)
            unit = side / modules;
        var size = unit * modules;
        var origin = new Point((Bounds.Width - size) / 2, (Bounds.Height - size) / 2);

        context.FillRectangle(Brushes.White, new Rect(origin, new Size(size, size)));
        for (var y = 0; y < code.Size; y++)
        {
            for (var x = 0; x < code.Size; x++)
            {
                if (code[x, y])
                {
                    context.FillRectangle(Brushes.Black, new Rect(
                        origin.X + (x + QuietZone) * unit, origin.Y + (y + QuietZone) * unit, unit, unit));
                }
            }
        }
    }
}
