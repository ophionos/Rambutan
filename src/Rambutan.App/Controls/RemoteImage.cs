using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Rambutan.App.Services;

namespace Rambutan.App.Controls;

public sealed class RemoteImage : Image
{
    public static readonly StyledProperty<string?> UrlProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Url));

    /// <summary>
    /// Width in pixels to decode to — set it to roughly 2× the layout size so art
    /// stays sharp on fractional-DPI displays without holding YTM's 544px source
    /// bitmaps in memory. The default suits the 140–150px cards.
    /// </summary>
    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<RemoteImage, int>(nameof(DecodeWidth), defaultValue: 320);

    /// <summary>Set once at composition time (App startup).</summary>
    public static ArtworkCache? Cache { get; set; }

    private int _generation;

    public string? Url
    {
        get => GetValue(UrlProperty);
        set => SetValue(UrlProperty, value);
    }

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == UrlProperty)
            Load(change.GetNewValue<string?>());
    }

    private async void Load(string? url)
    {
        var generation = Interlocked.Increment(ref _generation);
        Source = null;

        if (url is null || Cache is null)
            return;

        var bitmap = await Cache.GetAsync(url, DecodeWidth);
        if (bitmap is not null && generation == _generation)
            await Dispatcher.UIThread.InvokeAsync(() => Source = bitmap);
    }
}
