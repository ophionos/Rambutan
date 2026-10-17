using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Settings;

namespace Rambutan.App.Tests;

/// <summary>The stage, the artwork toggle over it, the advanced setting that
/// offers the animated presets, and cycling: where Now Playing draws what, and
/// when the style changes.</summary>
public class VisualizerStageTests
{
    private static readonly TrackInfo First = new() { VideoId = "a", Title = "A", Artists = [] };
    private static readonly TrackInfo Second = new() { VideoId = "b", Title = "B", Artists = [] };

    /// <summary>The cycle timer as the tests drive it.</summary>
    private sealed class FakeTimer
    {
        public Action? Tick;
        public int Started;
        public int Stopped;

        public IDisposable Every(TimeSpan interval, Action action)
        {
            Assert.Equal(NowPlayingViewModel.CycleTick, interval);
            Tick = action;
            Started++;
            return new Stop(this);
        }

        public bool Running => Started > Stopped;

        private sealed class Stop(FakeTimer timer) : IDisposable
        {
            public void Dispose()
            {
                timer.Stopped++;
                timer.Tick = null;
            }
        }
    }

    private static (NowPlayingViewModel Vm, AppearanceSettings Appearance, AppSettings Saved, FakeTimer Timer) NowPlaying(
        VisualizerStyle style = VisualizerStyle.Bars, bool advanced = false, bool artwork = true,
        VisualizerCycle cycle = VisualizerCycle.Off, Func<int, int>? random = null)
    {
        var appearance = new AppearanceSettings
        {
            VisualizerStyle = style, VisualizerAdvanced = advanced, VisualizerCycle = cycle,
            Motion = MotionPreference.Full,
        };
        var player = new PlayerBarViewModel
        {
            NowPlaying = First, ShownTrack = First, ArtworkUrl = "https://example.com/a.jpg", IsPlaying = true,
        };
        var saved = new AppSettings { ShowArtwork = artwork };
        var timer = new FakeTimer();
        var vm = new NowPlayingViewModel(player, settings: saved, appearance: appearance,
            every: timer.Every, random: random ?? (_ => 0)) { IsOpen = true };
        return (vm, appearance, saved, timer);
    }

    [Fact]
    public void TheDefaults_AreNoPresetsNoCycling_AndTheArtworkShown()
    {
        var saved = new AppSettings();
        Assert.False(saved.VisualizerAdvanced);
        Assert.Equal("off", saved.VisualizerCycle);
        Assert.True(saved.ShowArtwork);
        Assert.False(new AppearanceSettings().VisualizerAdvanced);
        Assert.Equal(VisualizerCycle.Off, new AppearanceSettings().VisualizerCycle);
    }

    [Fact]
    public void EveryCycleMode_SurvivesASaveAndLoad()
    {
        foreach (var cycle in Enum.GetValues<VisualizerCycle>())
            Assert.Equal(cycle, AppearanceSettings.ParseCycle(AppearanceSettings.FormatCycle(cycle)));
        Assert.Equal(VisualizerCycle.Off, AppearanceSettings.ParseCycle("sometimes"));
        Assert.Equal(VisualizerCycle.Off, AppearanceSettings.ParseCycle(null));
    }

    [Fact]
    public void APreset_WithoutAdvancedMode_DrawsBars_AndIsKept()
    {
        var (vm, appearance, _, _) = NowPlaying(VisualizerStyle.Plasma);

        Assert.Equal(VisualizerStyle.Bars, vm.StageStyle);
        Assert.Equal(VisualizerStyle.Plasma, appearance.VisualizerStyle);

        appearance.VisualizerAdvanced = true;
        Assert.Equal(VisualizerStyle.Plasma, vm.StageStyle);
        Assert.True(vm.IsPresetDrawn);
    }

    [Fact]
    public void TheStage_FillsThePane_DimmedUnderTheArtwork()
    {
        var (vm, _, _, _) = NowPlaying();

        Assert.True(vm.IsStageShown);
        Assert.True(vm.IsStageActive);
        Assert.True(vm.IsArtworkShown);
        Assert.True(vm.IsIdentityShown);
        Assert.False(vm.IsPausedNoteShown);
        Assert.InRange(vm.StageOpacity, 0.5, 0.9);
    }

    [Fact]
    public void TheArtworkToggle_IsRemembered_AndLeavesTheStageAlone()
    {
        var (vm, _, saved, _) = NowPlaying(artwork: false);
        Assert.False(vm.ShowArtwork);
        Assert.False(vm.IsArtworkShown);
        Assert.False(vm.IsIdentityShown);
        Assert.True(vm.IsStageActive);
        Assert.Equal(1, vm.StageOpacity);
        Assert.Equal("Show artwork", vm.ArtworkToggleTip);

        vm.ToggleArtworkCommand.Execute(null);

        Assert.True(vm.ShowArtwork);
        Assert.True(saved.ShowArtwork);
        Assert.True(vm.IsArtworkShown);
        Assert.True(vm.IsIdentityShown);
        Assert.True(vm.IsStageActive);
        Assert.Equal("Hide artwork", vm.ArtworkToggleTip);
    }

    [Fact]
    public void ATrackWithoutACover_ShowsNoArtwork_WhateverTheToggle()
    {
        var (vm, _, _, _) = NowPlaying();
        vm.Player.ArtworkUrl = null;

        Assert.True(vm.ShowArtwork);
        Assert.False(vm.IsArtworkShown);
        Assert.False(vm.IsIdentityShown);
        Assert.True(vm.IsStageActive);
    }

    [Fact]
    public void WithNothingLoaded_TheStageIsntShown_AndTheCoverStays()
    {
        var (vm, _, _, _) = NowPlaying(artwork: false);
        vm.Player.NowPlaying = null;

        Assert.False(vm.IsStageShown);
        Assert.False(vm.IsStageActive);
        Assert.True(vm.IsArtworkShown);
        Assert.True(vm.IsIdentityShown);
    }

    [Fact]
    public void TheRing_WithTheArtworkHidden_IsBars()
    {
        var (vm, _, _, _) = NowPlaying(VisualizerStyle.Radial, artwork: false);

        Assert.False(vm.ShowsRing);
        Assert.False(vm.IsRingActive);
        Assert.True(vm.IsStageActive);
        Assert.Equal(VisualizerStyle.Bars, vm.StageStyle);
    }

    [Fact]
    public void TheStage_UnderReducedMotion_ShowsTheNote_AndTheTitle()
    {
        var (vm, appearance, _, _) = NowPlaying(artwork: false);
        appearance.Motion = MotionPreference.Reduced;

        Assert.False(vm.IsStageVisualizerShown);
        Assert.False(vm.IsStageActive);
        // The "paused" note and the title, in place of a still pane.
        Assert.True(vm.IsPausedNoteShown);
        Assert.True(vm.IsIdentityShown);
        Assert.False(vm.IsArtworkShown);
    }

    [Fact]
    public void LyricsOverTheStage_DimIt()
    {
        var (vm, _, _, _) = NowPlaying(artwork: false);
        vm.ShowLyrics = true;
        vm.Lyrics.OnTrackChanged(First);

        Assert.True(vm.IsLyricsPaneVisible);
        Assert.True(vm.IsStageActive);
        Assert.True(vm.StageOpacity < 0.5);
    }

    [Fact]
    public void TheSoftwareNote_ShowsOnlyWhileAPresetDraws()
    {
        var (vm, appearance, _, _) = NowPlaying(VisualizerStyle.Tunnel, advanced: true);
        vm.IsDrawnInSoftware = true;
        Assert.True(vm.ShowsSoftwareNote);

        appearance.VisualizerStyle = VisualizerStyle.Line;
        Assert.False(vm.ShowsSoftwareNote);

        appearance.VisualizerStyle = VisualizerStyle.Tunnel;
        vm.IsOpen = false;
        Assert.False(vm.ShowsSoftwareNote);
    }

    [Fact]
    public void RandomCycling_ChangesAfterThirtySecondsOfPlaying()
    {
        var (vm, appearance, _, timer) = NowPlaying(cycle: VisualizerCycle.Random);
        Assert.True(timer.Running);

        for (var i = 0; i < 29; i++)
            timer.Tick!();
        Assert.Equal(VisualizerStyle.Bars, vm.DrawnStyle);

        timer.Tick!();
        // The first of the others: never the style already shown.
        Assert.Equal(VisualizerStyle.MirroredBars, vm.DrawnStyle);
        Assert.Equal(VisualizerStyle.Bars, appearance.VisualizerStyle);
    }

    [Fact]
    public void RandomCycling_CountsOnlyPlayingTime()
    {
        var (vm, _, _, timer) = NowPlaying(cycle: VisualizerCycle.Random);
        vm.Player.IsPlaying = false;

        for (var i = 0; i < 60; i++)
            timer.Tick!();

        Assert.Equal(VisualizerStyle.Bars, vm.DrawnStyle);
    }

    [Fact]
    public void RandomCycling_StopsItsTimer_WhileNoVisualizerIsSeen()
    {
        var (vm, appearance, _, timer) = NowPlaying(cycle: VisualizerCycle.Random);
        vm.IsOpen = false;
        Assert.False(timer.Running);

        vm.IsOpen = true;
        Assert.True(timer.Running);

        appearance.Motion = MotionPreference.Reduced;
        Assert.False(timer.Running);
    }

    [Fact]
    public void Cycling_PicksFromThePresetsOnlyInAdvancedMode()
    {
        var picks = new List<int>();
        var (vm, _, _, advancedTimer) = NowPlaying(
            advanced: true, cycle: VisualizerCycle.Random, random: n => { picks.Add(n); return n - 1; });
        var (basic, _, _, basicTimer) = NowPlaying(
            cycle: VisualizerCycle.Random, random: n => { picks.Add(n); return n - 1; });

        for (var i = 0; i < 30; i++)
        {
            advancedTimer.Tick!();
            basicTimer.Tick!();
        }

        Assert.Equal(VisualizerStyle.Kaleidoscope, vm.DrawnStyle);
        Assert.Equal(VisualizerStyle.Vectorscope, basic.DrawnStyle);
        Assert.Equal([AppearanceSettings.AllStyleChoices.Length - 1, AppearanceSettings.StyleChoices.Length - 1], picks);
    }

    [Fact]
    public void Cycling_WithTheArtworkHidden_LeavesOutTheRing()
    {
        var picks = new List<int>();
        var (vm, _, _, timer) = NowPlaying(artwork: false, cycle: VisualizerCycle.Random, random: n => { picks.Add(n); return 0; });
        for (var i = 0; i < 30; i++)
            timer.Tick!();

        // Bars shown; the others less the ring.
        Assert.Equal([AppearanceSettings.StyleChoices.Length - 2], picks);
    }

    [Fact]
    public void TrackChangeCycling_PicksOnANewTrack_NotTheFirst()
    {
        var (vm, appearance, _, timer) = NowPlaying(cycle: VisualizerCycle.TrackChange);
        Assert.False(timer.Running);
        Assert.Equal(VisualizerStyle.Bars, vm.DrawnStyle);

        vm.Player.NowPlaying = First;
        Assert.Equal(VisualizerStyle.Bars, vm.DrawnStyle);

        vm.Player.NowPlaying = Second;
        Assert.Equal(VisualizerStyle.MirroredBars, vm.DrawnStyle);
        Assert.Equal(VisualizerStyle.Bars, appearance.VisualizerStyle);
    }

    [Fact]
    public void ChangingTheChoiceOrTheMode_EndsTheCycledStyle()
    {
        var (vm, appearance, _, _) = NowPlaying(cycle: VisualizerCycle.TrackChange);
        vm.Player.NowPlaying = Second;
        Assert.Equal(VisualizerStyle.MirroredBars, vm.DrawnStyle);

        appearance.VisualizerCycle = VisualizerCycle.Off;
        Assert.Equal(VisualizerStyle.Bars, vm.DrawnStyle);

        appearance.VisualizerCycle = VisualizerCycle.TrackChange;
        vm.Player.NowPlaying = First;
        Assert.Equal(VisualizerStyle.MirroredBars, vm.DrawnStyle);
        appearance.VisualizerStyle = VisualizerStyle.Led;
        Assert.Equal(VisualizerStyle.Led, vm.DrawnStyle);
    }

    [Fact]
    public void TheCycler_WithNothingElseOffered_StaysPut()
    {
        var cycler = new VisualizerCycler(_ => 0);
        Assert.False(cycler.Next([VisualizerStyle.Bars], VisualizerStyle.Bars));
        Assert.Null(cycler.Current);
    }

    private static (SettingsPageViewModel Page, AppearanceSettings Appearance) Settings(AppearanceSettings appearance)
    {
        var page = new SettingsPageViewModel(
            new PlaybackSettings(), new AppSettings(), appearance,
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { });
        return (page, appearance);
    }

    [Fact]
    public void ThePicker_ShowsBarsForAKeptPreset_AndBringsItBack()
    {
        var (page, appearance) = Settings(new AppearanceSettings { VisualizerStyle = VisualizerStyle.Plasma });
        Assert.False(page.VisualizerAdvanced);
        Assert.Equal(Array.IndexOf(AppearanceSettings.AllStyleChoices, VisualizerStyle.Bars), page.VisualizerStyleIndex);

        page.VisualizerAdvanced = true;
        Assert.True(appearance.VisualizerAdvanced);
        Assert.Equal(Array.IndexOf(AppearanceSettings.AllStyleChoices, VisualizerStyle.Plasma), page.VisualizerStyleIndex);

        page.VisualizerAdvanced = false;
        Assert.Equal(VisualizerStyle.Plasma, appearance.VisualizerStyle);
    }

    [Fact]
    public void ThePicker_WritesAPresetAndTheCycleMode()
    {
        var (page, appearance) = Settings(new AppearanceSettings { VisualizerAdvanced = true });
        page.VisualizerStyleIndex = Array.IndexOf(AppearanceSettings.AllStyleChoices, VisualizerStyle.Feedback);
        page.VisualizerCycleIndex = (int)VisualizerCycle.Random;

        Assert.Equal(VisualizerStyle.Feedback, appearance.VisualizerStyle);
        Assert.Equal(VisualizerCycle.Random, appearance.VisualizerCycle);
    }
}
