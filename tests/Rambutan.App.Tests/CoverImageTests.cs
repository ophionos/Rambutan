using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Rambutan.App.Controls;

namespace Rambutan.App.Tests;

public class CoverImageTests
{
    private static Bitmap Bitmap() =>
        new WriteableBitmap(new PixelSize(2, 2), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);

    // The layer a url is loading into; there is no cache in these tests, so the
    // bitmap arrives when the test says so.
    private static RemoteImage LayerOf(CoverImage cover, string url) =>
        cover.Layers.Single(l => l.Url == url);

    private static void Arrive(CoverImage cover, string url) => LayerOf(cover, url).Source = Bitmap();

    private static void Pump(Window window, Func<bool> until)
    {
        for (var i = 0; i < 200 && !until(); i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            window.InvalidateVisual();
            Dispatcher.UIThread.RunJobs(DispatcherPriority.Render);
            Thread.Sleep(10);
        }
    }

    private static (Window Window, CoverImage Cover) Build(bool reduced = false)
    {
        var cover = new CoverImage { Width = 56, Height = 56, ReduceMotion = reduced };
        var window = new Window { Width = 200, Height = 200, Content = cover };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return (window, cover);
    }

    [Fact]
    public void Fade_IsTwoHundredMs_OrNoneUnderReducedMotion() => Headless.Run(() =>
    {
        var cover = new CoverImage();
        Assert.Equal(TimeSpan.FromMilliseconds(200), CoverImage.FadeDuration);
        Assert.Equal(CoverImage.FadeDuration, cover.CurrentFade);
        Assert.All(cover.Layers, l => Assert.NotNull(l.Transitions));

        cover.ReduceMotion = true;
        Assert.Equal(TimeSpan.Zero, cover.CurrentFade);
        Assert.All(cover.Layers, l => Assert.Null(l.Transitions));

        cover.ReduceMotion = false;
        Assert.Equal(CoverImage.FadeDuration, cover.CurrentFade);
    });

    [Fact]
    public void ANewCover_ShowsOverTheOldOnlyOnceItsBitmapIsThere() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.Url = "a";
        Arrive(cover, "a");
        Pump(window, () => LayerOf(cover, "a").Opacity == 1);
        var a = LayerOf(cover, "a");

        cover.Url = "b";
        var b = LayerOf(cover, "b");
        Assert.Equal(1, a.Opacity);
        Assert.Equal(0, b.Opacity);
        Assert.NotNull(a.Source);

        Arrive(cover, "b");
        Assert.True(b.ZIndex > a.ZIndex);
        Pump(window, () => b.Opacity == 1 && a.Url is null);
        Assert.Equal(1, b.Opacity);
        window.Close();
    });

    [Fact]
    public void AfterTheSwap_OnlyOneLayerHoldsACover() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.Url = "a";
        Arrive(cover, "a");
        Pump(window, () => LayerOf(cover, "a").Opacity == 1);
        cover.Url = "b";
        Arrive(cover, "b");
        Pump(window, () => cover.Layers.Count(l => l.Url is not null) == 1);

        var kept = Assert.Single(cover.Layers, l => l.Url is not null);
        Assert.Equal("b", kept.Url);
        var other = cover.Layers.Single(l => !ReferenceEquals(l, kept));
        Assert.Null(other.Source);
        Assert.Equal(0, other.Opacity);
        window.Close();
    });

    [Fact]
    public void UnderReducedMotion_ASwapIsDoneInTheSameCall() => Headless.Run(() =>
    {
        var (window, cover) = Build(reduced: true);
        cover.Url = "a";
        Arrive(cover, "a");
        cover.Url = "b";
        Arrive(cover, "b");

        var kept = Assert.Single(cover.Layers, l => l.Url is not null);
        Assert.Equal("b", kept.Url);
        Assert.Equal(1, kept.Opacity);
        window.Close();
    });

    [Fact]
    public void RapidChanges_EndShowingTheLastCoverOnly() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.Url = "a";
        Arrive(cover, "a");
        Pump(window, () => LayerOf(cover, "a").Opacity == 1);

        cover.Url = "b";
        cover.Url = "c";
        Assert.DoesNotContain(cover.Layers, l => l.Url == "b");
        Arrive(cover, "c");
        Pump(window, () => cover.Layers.Count(l => l.Url is not null) == 1);

        var kept = Assert.Single(cover.Layers, l => l.Url is not null);
        Assert.Equal("c", kept.Url);
        Assert.Equal(1, kept.Opacity);
        window.Close();
    });

    [Fact]
    public void ChangingAgainWhileTheFadeRuns_EndsOnTheLastCover() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.Url = "a";
        Arrive(cover, "a");
        Pump(window, () => LayerOf(cover, "a").Opacity == 1);

        cover.Url = "b";
        Arrive(cover, "b");
        cover.Url = "c";
        Arrive(cover, "c");
        Pump(window, () => cover.Layers.Count(l => l.Url is not null) == 1);

        var kept = Assert.Single(cover.Layers, l => l.Url is not null);
        Assert.Equal("c", kept.Url);
        Assert.Equal(1, kept.Opacity);
        window.Close();
    });

    [Fact]
    public void ANullUrl_RemovesTheCover() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.Url = "a";
        Arrive(cover, "a");
        Pump(window, () => LayerOf(cover, "a").Opacity == 1);

        cover.Url = null;
        Pump(window, () => cover.Layers.All(l => l.Url is null));

        Assert.All(cover.Layers, l =>
        {
            Assert.Null(l.Source);
            Assert.Equal(0, l.Opacity);
        });
        window.Close();
    });

    [Fact]
    public void ACoverThatFailsToLoad_RemovesTheOldOne() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.Url = "a";
        Arrive(cover, "a");
        Pump(window, () => LayerOf(cover, "a").Opacity == 1);

        cover.Url = "b";
        cover.OnLayerLoadFailed(LayerOf(cover, "b"), EventArgs.Empty);
        Pump(window, () => cover.Layers.All(l => l.Source is null && l.Url is null));

        Assert.All(cover.Layers, l => Assert.Equal(0, l.Opacity));
        window.Close();
    });

    [Fact]
    public void UrlStretchAndDecodeWidth_ReachTheLayer() => Headless.Run(() =>
    {
        var (window, cover) = Build();
        cover.DecodeWidth = 640;
        cover.Stretch = Avalonia.Media.Stretch.UniformToFill;
        cover.Url = "a";

        var layer = LayerOf(cover, "a");
        Assert.Equal(640, layer.DecodeWidth);
        Assert.Equal(Avalonia.Media.Stretch.UniformToFill, layer.Stretch);
        window.Close();
    });
}
