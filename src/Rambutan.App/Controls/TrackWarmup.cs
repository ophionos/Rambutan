using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Rambutan.Core.Playback;

namespace Rambutan.App.Controls;

/// <summary>
/// Hover warm-up's trigger. Every track row already names its track through
/// <see cref="TrackMenu.TrackProperty"/>, so rows need no markup of their own: the
/// pointer entering or leaving one is reported to <see cref="Scheduler"/>, which
/// decides whether and when to resolve (see <see cref="HoverWarmup"/>).
/// </summary>
public static class TrackWarmup
{
    private static HoverWarmup? _scheduler;

    /// <summary>Composition-time hook, like <see cref="RemoteImage.Cache"/>. Without
    /// it, hovering does nothing.</summary>
    public static HoverWarmup? Scheduler
    {
        get => _scheduler;
        set
        {
            _scheduler = value;
            Install();
        }
    }

    private static bool _installed;
    private static readonly ConditionalWeakTable<Control, object?> Hooked = new();

    private static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        TrackMenu.TrackProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            // Once per control: the handlers read the track at the moment of entry,
            // so a recycled row reports whatever it shows now.
            if (e.NewValue is null || Hooked.TryGetValue(control, out _))
                return;
            Hooked.Add(control, null);

            string? entered = null;
            control.PointerEntered += (_, _) =>
            {
                if (TrackMenu.GetTrack(control) is { VideoId: { Length: > 0 } id })
                {
                    entered = id;
                    _scheduler?.Enter(id);
                }
            };
            control.PointerExited += (_, _) => Leave();
            control.DetachedFromVisualTree += (_, _) => Leave();

            void Leave()
            {
                if (entered is null)
                    return;
                _scheduler?.Leave(entered);
                entered = null;
            }
        });
    }
}
