using System;

namespace Rambutan.App.Controls;

/// <summary>
/// Decides when the visualizer redraws. A redraw makes the compositor present a
/// new frame, and that frame, not the few rectangles in it, is what costs GPU:
/// the animation-frame callback alone costs nothing measurable. So the bars
/// redraw only when one of them moved since the last draw, and no more often than
/// <see cref="MinInterval"/>.
/// The comparison is against what was last drawn, not the previous frame, so a
/// slow decay that moves less than a step per frame still adds up to a redraw.
/// An animated style moves on its own, so <see cref="ShouldDrawAnimated"/> draws
/// it at the cap until it comes to rest instead.
/// Render thread only.
/// </summary>
internal sealed class VisualizerRedrawGate
{
    /// <summary>Half a DIP: below a device pixel at the usual scalings.</summary>
    internal const double Step = 0.5;

    private int[] _drawn = [];
    private bool _force = true;
    private bool _restDrawn;
    private TimeSpan _dueAt;

    /// <summary>Zero redraws at the display rate.</summary>
    public TimeSpan MinInterval { get; set; }

    /// <summary>The next <see cref="ShouldDraw"/> redraws whatever the levels
    /// are (start, a new palette, layout or size), without waiting for the cap.</summary>
    public void Reset() => _force = true;

    /// <summary>
    /// True when the bars should be redrawn now; their quantized heights are then
    /// remembered as drawn. <paramref name="peaks"/> may be empty.
    /// </summary>
    public bool ShouldDraw(TimeSpan now, ReadOnlySpan<float> levels, ReadOnlySpan<float> peaks, double height)
    {
        if (!_force && now < _dueAt)
            return false;

        var count = levels.Length + peaks.Length;
        if (_drawn.Length != count)
        {
            _drawn = new int[count];
            _force = true;
        }

        var changed = _force;
        for (var i = 0; i < count; i++)
        {
            var level = i < levels.Length ? levels[i] : peaks[i - levels.Length];
            var quantized = (int)Math.Round(level * height / Step);
            if (quantized != _drawn[i])
            {
                _drawn[i] = quantized;
                changed = true;
            }
        }

        if (!changed)
            return false;
        Drew(now);
        return true;
    }

    /// <summary>
    /// For a style that moves on its own: true at every <see cref="MinInterval"/>
    /// while it isn't <paramref name="resting"/>; once it is, true once more for
    /// the resting picture, then false until it moves again or
    /// <see cref="Reset"/>.
    /// </summary>
    public bool ShouldDrawAnimated(TimeSpan now, bool resting)
    {
        if (resting && _restDrawn && !_force)
            return false;
        if (!_force && now < _dueAt)
            return false;
        _restDrawn = resting;
        Drew(now);
        return true;
    }

    private void Drew(TimeSpan now)
    {
        // Due a whole interval after the last due time, not after this frame:
        // frames rarely land on the interval, and counting from the frame would
        // round every gap up (30 a second at 100 Hz would draw 25). After a pause
        // in drawing, start over from now rather than catch up.
        _dueAt += MinInterval;
        if (_dueAt <= now)
            _dueAt = now + MinInterval;
        _force = false;
    }
}
