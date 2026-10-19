using Avalonia.Media;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Settings;
using SkiaSharp;

namespace Rambutan.App.Tests;

/// <summary>The visualizer's style and colour: how they are saved, where the
/// Now Playing view draws them, and the colour picked from a cover.</summary>
public class VisualizerStyleSettingTests
{
    private static readonly TrackInfo Track = new() { VideoId = "a", Title = "A", Artists = [] };

    [Fact]
    public void TheDefaults_AreBarsInTheAccent()
    {
        var saved = new AppSettings();
        Assert.Equal("bars", saved.VisualizerStyle);
        Assert.Equal("accent", saved.VisualizerColor);
        var appearance = new AppearanceSettings();
        Assert.Equal(VisualizerStyle.Bars, appearance.VisualizerStyle);
        Assert.Equal(VisualizerColorSource.Accent, appearance.VisualizerColorSource);
    }

    [Fact]
    public void EveryStyle_SurvivesASaveAndLoad()
    {
        foreach (var style in Enum.GetValues<VisualizerStyle>())
            Assert.Equal(style, AppearanceSettings.ParseStyle(AppearanceSettings.FormatStyle(style)));
        foreach (var source in Enum.GetValues<VisualizerColorSource>())
            Assert.Equal(source, AppearanceSettings.ParseColorSource(AppearanceSettings.FormatColorSource(source)));
        Assert.Equal(Enum.GetValues<VisualizerStyle>().Length, AppearanceSettings.AllStyleChoices.Distinct().Count());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("starfield")]
    [InlineData("Bars")]
    public void AnUnknownStyle_ReadsAsBars(string? saved) =>
        Assert.Equal(VisualizerStyle.Bars, AppearanceSettings.ParseStyle(saved));

    [Fact]
    public void AnUnknownColorSource_ReadsAsTheAccent() =>
        Assert.Equal(VisualizerColorSource.Accent, AppearanceSettings.ParseColorSource("rainbow"));

    [Theory]
    [InlineData("#4fc3f7", "#4FC3F7")]
    [InlineData("4FC3F7", "#4FC3F7")]
    [InlineData("#F00", "#FF0000")]
    [InlineData("#FF000080", "#FF0000")]
    [InlineData("red", AppearanceSettings.DefaultCustomColor)]
    [InlineData(null, AppearanceSettings.DefaultCustomColor)]
    public void ACustomColor_IsSavedAsSolidHex(string? saved, string read) =>
        Assert.Equal(read, AppearanceSettings.ParseCustomColor(saved));

    private static (NowPlayingViewModel Vm, AppearanceSettings Appearance) NowPlaying(
        VisualizerStyle style, bool artwork = true, bool lyrics = false)
    {
        var appearance = new AppearanceSettings { VisualizerStyle = style, Motion = MotionPreference.Full };
        var player = new PlayerBarViewModel
        {
            NowPlaying = Track, ShownTrack = Track, ArtworkUrl = artwork ? "https://example.com/a.jpg" : null,
        };
        var vm = new NowPlayingViewModel(player, settings: new AppSettings(), appearance: appearance) { IsOpen = true };
        if (lyrics)
            vm.ShowLyrics = true;
        return (vm, appearance);
    }

    [Fact]
    public void Radial_DrawsAroundTheArtwork_InPlaceOfTheStage()
    {
        var (vm, _) = NowPlaying(VisualizerStyle.Radial);

        Assert.True(vm.ShowsRing);
        Assert.True(vm.IsRingActive);
        Assert.False(vm.IsStageVisualizerShown);
        Assert.False(vm.IsStageActive);
    }

    [Fact]
    public void Radial_StaysAroundTheArtwork_WithTheLyricsBesideIt()
    {
        var (vm, _) = NowPlaying(VisualizerStyle.Radial, lyrics: true);
        vm.Lyrics.OnTrackChanged(Track);

        Assert.True(vm.IsLyricsPaneVisible);
        Assert.True(vm.ShowsRing);
        Assert.False(vm.IsStageActive);
    }

    [Fact]
    public void Radial_WithoutArtwork_FallsBackToBarsOnTheStage()
    {
        var (vm, _) = NowPlaying(VisualizerStyle.Radial, artwork: false);

        Assert.False(vm.ShowsRing);
        Assert.True(vm.IsStageActive);
        Assert.Equal(VisualizerStyle.Bars, vm.StageStyle);
    }

    [Fact]
    public void Radial_WithTheArtworkHidden_FallsBackToBarsOnTheStage()
    {
        var (vm, _) = NowPlaying(VisualizerStyle.Radial);
        vm.ToggleArtworkCommand.Execute(null);

        Assert.False(vm.ShowsRing);
        Assert.True(vm.IsStageActive);
        Assert.Equal(VisualizerStyle.Bars, vm.StageStyle);
    }

    [Fact]
    public void Radial_WhenTheArtworkGoes_SaysSo()
    {
        var (vm, _) = NowPlaying(VisualizerStyle.Radial);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Player.ArtworkUrl = null;

        Assert.False(vm.ShowsRing);
        Assert.Contains(nameof(NowPlayingViewModel.ShowsRing), changed);
        Assert.Contains(nameof(NowPlayingViewModel.IsArtworkShown), changed);
        Assert.Contains(nameof(NowPlayingViewModel.IsStageActive), changed);
    }

    [Fact]
    public void Radial_UnderReducedMotion_LeavesTheNoteInView()
    {
        var (vm, appearance) = NowPlaying(VisualizerStyle.Radial);
        appearance.Motion = MotionPreference.Reduced;

        Assert.False(vm.ShowsRing);
        Assert.False(vm.IsRingActive);
        Assert.False(vm.IsStageActive);
        Assert.True(vm.IsPausedNoteShown);
    }

    [Fact]
    public void AClosedNowPlaying_PollsNeither()
    {
        var (ring, _) = NowPlaying(VisualizerStyle.Radial);
        var (stage, _) = NowPlaying(VisualizerStyle.Led);
        ring.IsOpen = false;
        stage.IsOpen = false;

        Assert.False(ring.IsRingActive);
        Assert.False(stage.IsStageActive);
    }

    [Fact]
    public void TheStage_DrawsTheChosenStyle()
    {
        var (vm, appearance) = NowPlaying(VisualizerStyle.Oscilloscope);
        Assert.Equal(VisualizerStyle.Oscilloscope, vm.StageStyle);
        Assert.False(vm.ShowsRing);

        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        appearance.VisualizerStyle = VisualizerStyle.Area;

        Assert.Equal(VisualizerStyle.Area, vm.StageStyle);
        Assert.Contains(nameof(NowPlayingViewModel.StageStyle), changed);
    }

    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void ACover_GivesItsColor_InTheRightChannels()
    {
        // Skia's native pixel order is BGRA on Windows: a swapped read turns
        // a red cover blue.
        var red = ThemeColors.Pick(Png(new SKColor(0xE0, 0x20, 0x20)))!.Value;
        Assert.True(red.R > 0xC0 && red.B < 0x60, red.ToString());

        var blue = ThemeColors.Pick(Png(new SKColor(0x20, 0x40, 0xE0)))!.Value;
        Assert.True(blue.B > 0xC0 && blue.R < 0x60, blue.ToString());
    }

    [Fact]
    public void BytesThatArentAnImage_GiveNoColor() =>
        Assert.Null(ThemeColors.Pick([1, 2, 3, 4]));

    private static ThemeColors Colors(
        AppearanceSettings appearance, PlayerBarViewModel player, Func<string, CancellationToken, Task<byte[]>>? load) =>
        new(appearance, player, load);

    [Fact]
    public void TheAccent_IsNoColor()
    {
        var colors = Colors(new AppearanceSettings(), new PlayerBarViewModel(), null);
        Assert.Null(colors.Color);
    }

    [Fact]
    public void ACustomColor_FollowsTheSetting()
    {
        var appearance = new AppearanceSettings { VisualizerColorSource = VisualizerColorSource.Custom, VisualizerCustomColor = "#112233" };
        var colors = Colors(appearance, new PlayerBarViewModel(), null);
        Assert.Equal(Color.FromRgb(0x11, 0x22, 0x33), colors.Color);

        appearance.VisualizerCustomColor = "#445566";
        Assert.Equal(Color.FromRgb(0x44, 0x55, 0x66), colors.Color);

        appearance.VisualizerColorSource = VisualizerColorSource.Accent;
        Assert.Null(colors.Color);
    }

    [Fact]
    public async Task TheArtworkSource_ReadsTheCover()
    {
        var appearance = new AppearanceSettings { VisualizerColorSource = VisualizerColorSource.Artwork };
        var player = new PlayerBarViewModel { ArtworkUrl = "red" };
        var reads = 0;
        var colors = Colors(appearance, player, (url, _) =>
        {
            reads++;
            return Task.FromResult(Png(url == "red" ? SKColors.Red : SKColors.Blue));
        });
        // Each check names what it read, so a failure says why.
        await colors.Pending;
        Assert.True(colors.Color is { R: > 0xC0 }, $"red cover read as {colors.Color?.ToString() ?? "null"}");

        player.ArtworkUrl = "blue";
        await colors.Pending;
        Assert.True(colors.Color is { B: > 0xC0 }, $"blue cover read as {colors.Color?.ToString() ?? "null"}, reads: {reads}");
        Assert.Equal(2, reads);
    }

    [Fact]
    public void TheArtworkSource_ReadsNothingWhileAnotherIsChosen()
    {
        var reads = 0;
        var player = new PlayerBarViewModel { ArtworkUrl = "red" };
        _ = Colors(new AppearanceSettings(), player, (_, _) =>
        {
            reads++;
            return Task.FromResult(Array.Empty<byte>());
        });
        player.ArtworkUrl = "blue";

        Assert.Equal(0, reads);
    }

    [Fact]
    public async Task ASlowCover_ThatFinishesLate_DoesntReplaceTheNewOne()
    {
        var appearance = new AppearanceSettings { VisualizerColorSource = VisualizerColorSource.Artwork };
        var player = new PlayerBarViewModel { ArtworkUrl = "slow" };
        var slow = new TaskCompletionSource<byte[]>();
        var colors = Colors(appearance, player, (url, _) =>
            url == "slow" ? slow.Task : Task.FromResult(Png(SKColors.Blue)));
        var first = colors.Pending;

        player.ArtworkUrl = "fast";
        await colors.Pending;
        slow.SetResult(Png(SKColors.Red));
        await first;

        Assert.True(colors.Color is { B: > 0xC0 }, $"read as {colors.Color?.ToString() ?? "null"}");
    }

    [Fact]
    public async Task ANewCover_ShowsTheAccent_UntilItIsRead()
    {
        var appearance = new AppearanceSettings { VisualizerColorSource = VisualizerColorSource.Artwork };
        var player = new PlayerBarViewModel { ArtworkUrl = "red" };
        var next = new TaskCompletionSource<byte[]>();
        var colors = Colors(appearance, player, (url, _) =>
            url == "red" ? Task.FromResult(Png(SKColors.Red)) : next.Task);
        await colors.Pending;
        Assert.NotNull(colors.Color);

        player.ArtworkUrl = "next";
        Assert.Null(colors.Color);
        next.SetResult(Png(SKColors.Blue));
        await colors.Pending;
        Assert.NotNull(colors.Color);
    }

    private static (SettingsPageViewModel Page, AppearanceSettings Appearance) Settings(AppearanceSettings? appearance = null)
    {
        appearance ??= new AppearanceSettings();
        var page = new SettingsPageViewModel(
            new PlaybackSettings(), new AppSettings(), appearance,
            new Rambutan.Integrations.UpdateService(null), new SessionViewModel(), () => { });
        return (page, appearance);
    }

    [Fact]
    public void ThePicker_SeedsFromAndWritesToTheLiveSettings()
    {
        var (page, appearance) = Settings(new AppearanceSettings
        {
            VisualizerStyle = VisualizerStyle.Vectorscope, VisualizerColorSource = VisualizerColorSource.Artwork,
        });
        Assert.Equal(Array.IndexOf(AppearanceSettings.StyleChoices, VisualizerStyle.Vectorscope), page.VisualizerStyleIndex);
        Assert.Equal((int)VisualizerColorSource.Artwork, page.VisualizerColorIndex);
        Assert.False(page.IsCustomVisualizerColor);

        page.VisualizerStyleIndex = Array.IndexOf(AppearanceSettings.StyleChoices, VisualizerStyle.Led);
        page.VisualizerColorIndex = (int)VisualizerColorSource.Custom;

        Assert.Equal(VisualizerStyle.Led, appearance.VisualizerStyle);
        Assert.Equal(VisualizerColorSource.Custom, appearance.VisualizerColorSource);
        Assert.True(page.IsCustomVisualizerColor);
    }

    [Fact]
    public void AHexThatIsntASolidColor_SaysSo_AndKeepsTheLastOne()
    {
        var (page, appearance) = Settings();
        page.VisualizerCustom.Hex = "#12ab34";
        Assert.Null(page.VisualizerCustom.Error);
        Assert.Equal("#12AB34", appearance.VisualizerCustomColor);

        page.VisualizerCustom.Hex = "#12ab";
        Assert.NotNull(page.VisualizerCustom.Error);
        Assert.Equal("#12AB34", appearance.VisualizerCustomColor);

        page.VisualizerCustom.Hex = "#FF000080";
        Assert.NotNull(page.VisualizerCustom.Error);
        Assert.Equal("#12AB34", appearance.VisualizerCustomColor);
    }

    [Fact]
    public void APresetOrThePicker_SetsTheCustomColor()
    {
        var (page, appearance) = Settings();
        var preset = page.VisualizerCustom.Presets[2];
        page.VisualizerCustom.UsePresetCommand.Execute(preset);
        Assert.Equal(preset.Hex, appearance.VisualizerCustomColor);
        Assert.Equal(preset.Hex, page.VisualizerCustom.Hex);

        page.VisualizerCustom.Color = Color.FromRgb(0x10, 0x20, 0x30);
        Assert.Equal("#102030", appearance.VisualizerCustomColor);
        Assert.Equal("#102030", page.VisualizerCustom.Hex);
    }

    [Fact]
    public void EveryPreset_HasAName() =>
        Assert.All(Settings().Page.VisualizerCustom.Presets, p => Assert.False(string.IsNullOrWhiteSpace(p.Name)));

    [Fact]
    public void TheAccent_SeedsFromAndWritesToTheLiveSettings()
    {
        var (page, appearance) = Settings(new AppearanceSettings { AccentSource = AccentSource.Artwork });
        Assert.Equal((int)AccentSource.Artwork, page.AccentIndex);
        Assert.False(page.IsCustomAccent);
        Assert.Equal(AppearanceSettings.DefaultAccentColor, page.AccentCustom.Hex);

        page.AccentIndex = (int)AccentSource.Custom;
        page.AccentCustom.UsePresetCommand.Execute(page.AccentCustom.Presets[5]);

        Assert.True(page.IsCustomAccent);
        Assert.Equal(AccentSource.Custom, appearance.AccentSource);
        Assert.Equal(page.AccentCustom.Presets[5].Hex, appearance.AccentCustomColor);
        // Its own colour: the visualizer's is untouched.
        Assert.Equal(AppearanceSettings.DefaultCustomColor, appearance.VisualizerCustomColor);

        page.AccentCustom.Hex = "#12";
        Assert.NotNull(page.AccentCustom.Error);
        Assert.Equal(page.AccentCustom.Presets[5].Hex, appearance.AccentCustomColor);
    }

    [Fact]
    public void BothColorFields_HaveTheirOwnNames()
    {
        var page = Settings().Page;
        Assert.NotEqual(page.VisualizerCustom.PickName, page.AccentCustom.PickName);
        Assert.NotEqual(page.VisualizerCustom.HexName, page.AccentCustom.HexName);
        Assert.DoesNotContain("settings.", page.AccentCustom.PickName + page.AccentCustom.HexName);
    }
}
