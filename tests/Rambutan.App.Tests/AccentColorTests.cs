using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Appearance;
using SkiaSharp;

namespace Rambutan.App.Tests;

/// <summary>The accent: where its colour comes from, and how a new one
/// reaches the app's resources and the controls drawn with them.</summary>
public class AccentColorTests
{
    private static byte[] Png(SKColor color)
    {
        using var bitmap = new SKBitmap(16, 16);
        bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [Fact]
    public void TheDefault_IsRambutanRed()
    {
        var colors = new ThemeColors(new AppearanceSettings(), new PlayerBarViewModel(), null);
        Assert.Equal(AccentPalette.Default.Base, colors.Accent);
    }

    [Fact]
    public void ACustomAccent_FollowsTheSetting()
    {
        var appearance = new AppearanceSettings { AccentSource = AccentSource.Custom, AccentCustomColor = "#5B8DEF" };
        var colors = new ThemeColors(appearance, new PlayerBarViewModel(), null);
        Assert.Equal(new Rgb(0x5B, 0x8D, 0xEF), colors.Accent);

        appearance.AccentCustomColor = "#6BCB77";
        Assert.Equal(new Rgb(0x6B, 0xCB, 0x77), colors.Accent);

        appearance.AccentSource = AccentSource.Default;
        Assert.Equal(AccentPalette.Default.Base, colors.Accent);
    }

    [Fact]
    public async Task TheArtworkAccent_FollowsEachCover_AndTheVisualizerKeepsItsOwnSource()
    {
        var appearance = new AppearanceSettings { AccentSource = AccentSource.Artwork };
        var player = new PlayerBarViewModel { ArtworkUrl = "blue" };
        var reads = 0;
        // Held until the default is checked: the read ends on the thread pool
        // here (no UI thread), so a finished read could change the accent first.
        var blue = new TaskCompletionSource<byte[]>();
        var colors = new ThemeColors(appearance, player, (url, _) =>
        {
            reads++;
            return url == "blue" ? blue.Task : Task.FromResult(Png(SKColors.Green));
        });
        // Until the cover is read, the default.
        Assert.Equal(AccentPalette.Default.Base, colors.Accent);

        blue.SetResult(Png(SKColors.Blue));
        await colors.Pending;
        Assert.True(colors.Accent.B > 0xC0 && colors.Accent.R < 0x60, colors.Accent.ToString());
        Assert.Null(colors.Color);

        player.ArtworkUrl = "green";
        await colors.Pending;
        Assert.True(colors.Accent.G > 0xC0 && colors.Accent.B < 0x60, colors.Accent.ToString());

        // The visualizer from the artwork too: the cover read is shared.
        appearance.VisualizerColorSource = VisualizerColorSource.Artwork;
        await colors.Pending;
        Assert.Equal(colors.Accent, new Rgb(colors.Color!.Value.R, colors.Color.Value.G, colors.Color.Value.B));
        Assert.Equal(2, reads);
    }

    [Fact]
    public async Task WhileTheNextCoverIsRead_TheLastCoversAccentStays()
    {
        var appearance = new AppearanceSettings { AccentSource = AccentSource.Artwork };
        var player = new PlayerBarViewModel { ArtworkUrl = "blue" };
        var green = new TaskCompletionSource<byte[]>();
        var failing = new TaskCompletionSource<byte[]>();
        var colors = new ThemeColors(appearance, player, (url, _) => url switch
        {
            "blue" => Task.FromResult(Png(SKColors.Blue)),
            "green" => green.Task,
            _ => failing.Task,
        });
        await colors.Pending;
        var blue = colors.Accent;
        Assert.NotEqual(AccentPalette.Default.Base, blue);

        // A track change starts a read: the app keeps the blue meanwhile,
        // rather than flashing to the default red.
        player.ArtworkUrl = "green";
        Assert.Equal(blue, colors.Accent);
        green.SetResult(Png(SKColors.Green));
        await colors.Pending;
        Assert.True(colors.Accent.G > 0xC0 && colors.Accent.B < 0x60, colors.Accent.ToString());

        // A cover that can't be read gives the default, once the read ends.
        var greenAccent = colors.Accent;
        player.ArtworkUrl = "broken";
        Assert.Equal(greenAccent, colors.Accent);
        failing.SetException(new HttpRequestException("gone"));
        await colors.Pending;
        Assert.Equal(AccentPalette.Default.Base, colors.Accent);

        // No cover at all: the default straight away.
        player.ArtworkUrl = "blue";
        await colors.Pending;
        player.ArtworkUrl = null;
        Assert.Equal(AccentPalette.Default.Base, colors.Accent);
    }

    [Fact]
    public async Task AGreyCover_LeavesTheDefaultAccent_ButGivesTheVisualizerItsNeutral()
    {
        var appearance = new AppearanceSettings
        {
            AccentSource = AccentSource.Artwork, VisualizerColorSource = VisualizerColorSource.Artwork,
        };
        var player = new PlayerBarViewModel { ArtworkUrl = "grey" };
        var colors = new ThemeColors(appearance, player, (_, _) => Task.FromResult(Png(new SKColor(0x80, 0x80, 0x80))));

        await colors.Pending;

        Assert.Equal(AccentPalette.Default.Base, colors.Accent);
        Assert.NotNull(colors.Color);
    }

    [Fact]
    public void ANewAccent_ReachesTheBrushesAndFluentsControls() => Headless.Run(() =>
    {
        ShellResources.Ensure();
        var resources = Application.Current!.Resources;
        object Find(string key) => resources.TryGetResource(key, null, out var v) ? v! : throw new KeyNotFoundException(key);
        var accent = (SolidColorBrush)Find("Token.Brush.Accent");
        var window = new Window
        {
            Width = 300, Height = 200,
            Content = new ToggleSwitch { IsChecked = true },
        };
        window.Show();
        try
        {
            var yellow = AccentPalette.From(new(0xFF, 0xD9, 0x3D));
            AccentResources.Apply(resources, yellow);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(Color.FromRgb(0xFF, 0xD9, 0x3D), accent.Color);
            Assert.Equal(Color.FromRgb(0xFF, 0xD9, 0x3D), (Color)Find("SystemAccentColor"));
            Assert.Equal(Color.FromRgb(0x0A, 0x0A, 0x0C), ((SolidColorBrush)Find("Token.Brush.OnAccent")).Color);
            Assert.Equal(Color.FromRgb(0x0A, 0x0A, 0x0C), ((SolidColorBrush)Find("AccentButtonForeground")).Color);
            // The disabled fill is the accent faded over a dark panel, so its
            // text stays white even when the enabled one turns dark.
            Assert.Equal(Color.FromArgb(0x8C, 0xFF, 0xFF, 0xFF), ((SolidColorBrush)Find("AccentButtonForegroundDisabled")).Color);

            // A Fluent control reads the ramp through DynamicResource: its
            // switched-on track takes a colour from the new ramp.
            var fills = window.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Shape>()
                .Select(s => s.Fill).Concat(window.GetVisualDescendants().OfType<Border>().Select(b => b.Background))
                .OfType<ISolidColorBrush>().Select(b => b.Color).ToList();
            var ramp = new[] { yellow.Base, yellow.Light1, yellow.Light2, yellow.Light3, yellow.Dark1, yellow.Dark2, yellow.Dark3 }
                .Select(c => Color.FromRgb(c.R, c.G, c.B));
            Assert.Contains(fills, f => ramp.Contains(f));
        }
        finally
        {
            AccentResources.Apply(resources, AccentPalette.Default);
            window.Close();
        }
        Assert.Equal(Color.FromRgb(0xFF, 0x4E, 0x45), accent.Color);
    });

    [Fact]
    public void TheDefaultPalette_MatchesTheTokens() => Headless.Run(() =>
    {
        // Tokens.axaml holds the default's values for the first frame, and
        // AccentPalette.Default is what a reset applies: the two must agree.
        ShellResources.Ensure();
        var tokens = new ResourceDictionary();
        tokens.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Rambutan.App/"))
        {
            Source = new Uri("avares://Rambutan.App/Styles/Tokens.axaml"),
        });
        Color Of(string key) => tokens.TryGetResource(key, null, out var v)
            ? v switch { Color c => c, SolidColorBrush b => b.Color, _ => throw new InvalidOperationException(key) }
            : throw new KeyNotFoundException(key);
        Color C(Rgb c, byte a = 0xFF) => Color.FromArgb(a, c.R, c.G, c.B);
        var d = AccentPalette.Default;

        Assert.Equal(C(d.Base), Of("Token.Color.Accent"));
        Assert.Equal(C(d.Base), Of("SystemAccentColor"));
        Assert.Equal(C(d.Light1), Of("SystemAccentColorLight1"));
        Assert.Equal(C(d.Light2), Of("SystemAccentColorLight2"));
        Assert.Equal(C(d.Light3), Of("SystemAccentColorLight3"));
        Assert.Equal(C(d.Dark1), Of("SystemAccentColorDark1"));
        Assert.Equal(C(d.Dark2), Of("SystemAccentColorDark2"));
        Assert.Equal(C(d.Dark3), Of("SystemAccentColorDark3"));
        Assert.Equal(C(d.Hover), Of("AccentButtonBackgroundPointerOver"));
        Assert.Equal(C(d.Pressed), Of("AccentButtonBackgroundPressed"));
        Assert.Equal(C(d.Base, 0x59), Of("AccentButtonBackgroundDisabled"));
        Assert.Equal(C(d.OnAccent), Of("Token.Brush.OnAccent"));
        Assert.Equal(C(d.OnAccent), Of("AccentButtonForeground"));
        Assert.Equal(C(AccentPalette.White, 0x8C), Of("AccentButtonForegroundDisabled"));
    });
}
