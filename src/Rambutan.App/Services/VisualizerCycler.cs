using System;
using System.Collections.Generic;

namespace Rambutan.App.Services;

/// <summary>
/// Changes the visualizer's style on its own, as Winamp's did: at random every
/// <see cref="AppearanceSettings.CycleInterval"/> of playing, or on each new
/// track. It never picks the style already shown. The user's choice stays in
/// <see cref="AppearanceSettings.VisualizerStyle"/>; this only says what shows
/// instead.
/// </summary>
public sealed class VisualizerCycler
{
    private readonly Func<int, int> _random;
    private TimeSpan _shownFor;

    /// <param name="random">A number from 0 up to, not including, its argument.</param>
    public VisualizerCycler(Func<int, int>? random = null) => _random = random ?? Random.Shared.Next;

    /// <summary>The style cycling picked; null shows the chosen one.</summary>
    public VisualizerStyle? Current { get; private set; }

    /// <summary>Back to the chosen style: the choice, the mode or what is
    /// offered changed.</summary>
    public void Reset()
    {
        Current = null;
        _shownFor = TimeSpan.Zero;
    }

    /// <summary>Counts <paramref name="elapsed"/> of playing towards the next
    /// change, and changes when the interval is up. True when it changed.</summary>
    public bool Elapse(TimeSpan elapsed, IReadOnlyList<VisualizerStyle> offered, VisualizerStyle shown)
    {
        _shownFor += elapsed;
        return _shownFor >= AppearanceSettings.CycleInterval && Next(offered, shown);
    }

    /// <summary>Picks another of <paramref name="offered"/> than
    /// <paramref name="shown"/>, and starts its interval. False when there is
    /// no other.</summary>
    public bool Next(IReadOnlyList<VisualizerStyle> offered, VisualizerStyle shown)
    {
        var others = 0;
        foreach (var style in offered)
        {
            if (style != shown)
                others++;
        }
        if (others == 0)
            return false;

        var pick = _random(others);
        foreach (var style in offered)
        {
            if (style == shown)
                continue;
            if (pick-- == 0)
            {
                Current = style;
                break;
            }
        }
        _shownFor = TimeSpan.Zero;
        return true;
    }
}
