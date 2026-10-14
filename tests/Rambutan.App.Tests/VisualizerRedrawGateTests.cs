using System;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

/// <summary>When the visualizer redraws: only on visible change, within its cap.</summary>
public class VisualizerRedrawGateTests
{
    private const double Height = 28;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void TheFirstFrame_Draws()
    {
        var gate = new VisualizerRedrawGate();
        Assert.True(gate.ShouldDraw(Ms(0), [0f, 0f], [], Height));
    }

    [Fact]
    public void BarsAtRest_DoNotRedraw()
    {
        var gate = new VisualizerRedrawGate();
        gate.ShouldDraw(Ms(0), [0f, 0f], [], Height);

        for (var frame = 1; frame <= 100; frame++)
            Assert.False(gate.ShouldDraw(Ms(frame * 10), [0f, 0f], [], Height));
    }

    [Fact]
    public void AChangeBelowAStep_DoesNotRedraw()
    {
        var gate = new VisualizerRedrawGate();
        gate.ShouldDraw(Ms(0), [0.5f], [], Height);

        var tiny = (float)(VisualizerRedrawGate.Step * 0.4 / Height);
        Assert.False(gate.ShouldDraw(Ms(10), [0.5f + tiny], [], Height));
    }

    [Fact]
    public void ASlowDecay_AddsUpToARedraw()
    {
        // Each frame falls a fifth of a step: never a step from the frame before,
        // but a step from what was last drawn within a few frames.
        var gate = new VisualizerRedrawGate();
        var level = 0.5f;
        gate.ShouldDraw(Ms(0), [level], [], Height);

        var drew = false;
        for (var frame = 1; frame <= 10 && !drew; frame++)
        {
            level -= (float)(VisualizerRedrawGate.Step * 0.2 / Height);
            drew = gate.ShouldDraw(Ms(frame * 10), [level], [], Height);
        }
        Assert.True(drew);
    }

    [Fact]
    public void AMovingPeak_Redraws()
    {
        var gate = new VisualizerRedrawGate();
        gate.ShouldDraw(Ms(0), [0.2f], [0.8f], Height);

        Assert.True(gate.ShouldDraw(Ms(10), [0.2f], [0.6f], Height));
    }

    [Fact]
    public void TheCap_HoldsBackAChange_UntilItsIntervalPasses()
    {
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        gate.ShouldDraw(Ms(0), [0.1f], [], Height);

        Assert.False(gate.ShouldDraw(Ms(10), [0.9f], [], Height));
        Assert.False(gate.ShouldDraw(Ms(30), [0.9f], [], Height));
        Assert.True(gate.ShouldDraw(Ms(40), [0.9f], [], Height));
    }

    [Theory]
    [InlineData(100)]
    [InlineData(144)]
    [InlineData(60)]
    public void TheCap_HoldsItsRate_WhenFramesDontLandOnTheInterval(int displayHz)
    {
        var gate = new VisualizerRedrawGate { MinInterval = TimeSpan.FromSeconds(1.0 / 30) };
        var draws = 0;
        for (var frame = 0; frame < displayHz * 2; frame++)
        {
            var level = frame % 2 == 0 ? 0.2f : 0.8f;
            if (gate.ShouldDraw(TimeSpan.FromSeconds((double)frame / displayHz), [level], [], Height))
                draws++;
        }
        Assert.InRange(draws, 59, 61);
    }

    [Fact]
    public void AfterAPauseInDrawing_TheCapStartsOver_RatherThanCatchingUp()
    {
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        gate.ShouldDraw(Ms(0), [0.1f], [], Height);
        for (var t = 10; t <= 1000; t += 10)
            gate.ShouldDraw(Ms(t), [0.1f], [], Height);

        Assert.True(gate.ShouldDraw(Ms(1010), [0.9f], [], Height));
        Assert.False(gate.ShouldDraw(Ms(1020), [0.1f], [], Height));
        Assert.False(gate.ShouldDraw(Ms(1040), [0.1f], [], Height));
        Assert.True(gate.ShouldDraw(Ms(1050), [0.1f], [], Height));
    }

    [Fact]
    public void WithoutACap_EveryVisibleChangeRedraws()
    {
        var gate = new VisualizerRedrawGate();
        gate.ShouldDraw(Ms(0), [0.1f], [], Height);

        Assert.True(gate.ShouldDraw(Ms(1), [0.5f], [], Height));
        Assert.True(gate.ShouldDraw(Ms(2), [0.1f], [], Height));
    }

    [Fact]
    public void Reset_RedrawsUnchangedBars_WithoutWaitingForTheCap()
    {
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        gate.ShouldDraw(Ms(0), [0.3f], [], Height);

        gate.Reset();
        Assert.True(gate.ShouldDraw(Ms(5), [0.3f], [], Height));
        Assert.False(gate.ShouldDraw(Ms(100), [0.3f], [], Height));
    }

    [Fact]
    public void ADifferentBarCount_Redraws()
    {
        var gate = new VisualizerRedrawGate();
        gate.ShouldDraw(Ms(0), [0f, 0f], [], Height);

        Assert.True(gate.ShouldDraw(Ms(10), [0f, 0f, 0f], [], Height));
    }

    [Fact]
    public void AnAnimatedStyle_DrawsAtTheCap_WhileItMoves()
    {
        // 30 a second on a 100 Hz display, for one second.
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        var draws = 0;
        for (var t = 0; t < 1000; t += 10)
            if (gate.ShouldDrawAnimated(Ms(t), resting: false))
                draws++;

        Assert.InRange(draws, 29, 31);
    }

    [Fact]
    public void AnAnimatedStyle_DrawsItsRestOnce_ThenStops()
    {
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        gate.ShouldDrawAnimated(Ms(0), resting: false);

        Assert.True(gate.ShouldDrawAnimated(Ms(40), resting: true));
        for (var t = 50; t <= 2000; t += 10)
            Assert.False(gate.ShouldDrawAnimated(Ms(t), resting: true));
    }

    [Fact]
    public void AnAnimatedStyle_AtRest_StartsAgainWhenItMoves()
    {
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        gate.ShouldDrawAnimated(Ms(0), resting: true);
        Assert.False(gate.ShouldDrawAnimated(Ms(500), resting: true));

        Assert.True(gate.ShouldDrawAnimated(Ms(510), resting: false));
    }

    [Fact]
    public void AnAnimatedStyle_AtRest_RedrawsOnReset()
    {
        // A new palette or size must show even when nothing plays.
        var gate = new VisualizerRedrawGate { MinInterval = Ms(1000.0 / 30) };
        gate.ShouldDrawAnimated(Ms(0), resting: true);

        gate.Reset();
        Assert.True(gate.ShouldDrawAnimated(Ms(5), resting: true));
        Assert.False(gate.ShouldDrawAnimated(Ms(100), resting: true));
    }
}
