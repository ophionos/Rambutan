using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Rambutan.App.ViewModels;
using Rambutan.Core.Appearance;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Visualizer;
using SkiaSharp;

namespace Rambutan.App.Services;

/// <summary>
/// The app's accent and the colour the Now Playing visualizer draws in, from the
/// appearance settings and, for their artwork sources, the playing track's
/// cover. A cover is read only while one of them is chosen.
/// </summary>
public sealed partial class ThemeColors : ObservableObject
{
    /// <summary>The cover is scaled to this many pixels a side before picking:
    /// enough for its colours, little enough to read in a millisecond.</summary>
    private const int SampleSize = 48;

    private readonly AppearanceSettings _appearance;
    private readonly PlayerBarViewModel _player;
    private readonly Func<string, CancellationToken, Task<byte[]>>? _loadBytes;
    private CancellationTokenSource? _reading;
    private string? _readingUrl;
    private string? _pickedUrl;
    private Color? _picked;
    private Rgb? _coverAccent;

    /// <summary>The colour to draw in; null for the theme's accent. With the
    /// artwork source, the accent until the cover has been read, and when it
    /// can't be.</summary>
    [ObservableProperty] private Color? _color;

    /// <summary>The accent the user chose, before <see cref="AccentPalette"/>
    /// makes it readable. With the artwork source, the default red until the
    /// cover has been read, when it can't be, and when the cover has no vivid
    /// colour: a grey accent would make the playing track's marker look like
    /// plain text. While a new cover is read, the last cover's accent stays,
    /// so a track change doesn't flash the whole app red.</summary>
    [ObservableProperty] private Rgb _accent = AccentPalette.Default.Base;

    /// <summary>The cover read in progress, for tests to wait on.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <param name="loadBytes">The cover's bytes by URL; null reads no covers,
    /// so the artwork source draws in the accent.</param>
    public ThemeColors(
        AppearanceSettings appearance,
        PlayerBarViewModel player,
        Func<string, CancellationToken, Task<byte[]>>? loadBytes)
    {
        _appearance = appearance;
        _player = player;
        _loadBytes = loadBytes;
        appearance.PropertyChanged += OnAppearanceChanged;
        player.PropertyChanged += OnPlayerChanged;
        Update();
    }

    private void OnAppearanceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AppearanceSettings.VisualizerColorSource) or nameof(AppearanceSettings.VisualizerCustomColor)
            or nameof(AppearanceSettings.AccentSource) or nameof(AppearanceSettings.AccentCustomColor))
            Update();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerBarViewModel.ArtworkUrl))
            Update();
    }

    private void Update()
    {
        var wantsCover = _appearance.VisualizerColorSource == VisualizerColorSource.Artwork
            || _appearance.AccentSource == AccentSource.Artwork;
        var url = _player.ArtworkUrl;
        Color? cover = null;
        var keepAccent = false;
        if (!wantsCover)
        {
            Cancel();
            _coverAccent = null;
        }
        else if (url is not null && url == _pickedUrl)
        {
            Cancel();
            cover = _picked;
        }
        else if (_readingUrl != url || _reading is null)
        {
            // The last cover's colour would be wrong for this one: the
            // visualizer's default shows until this one is read. The accent
            // repaints the whole app, so it keeps the last cover's meanwhile.
            Cancel();
            if (url is not null && _loadBytes is not null)
            {
                Pending = ReadAsync(url);
                keepAccent = true;
            }
        }
        Apply(cover, keepAccent);
    }

    private void Apply(Color? cover, bool keepAccent = false)
    {
        Color = _appearance.VisualizerColorSource switch
        {
            VisualizerColorSource.Custom => CssHexColor.TryParse(_appearance.VisualizerCustomColor, out var custom)
                ? Avalonia.Media.Color.FromRgb(custom.R, custom.G, custom.B)
                : null,
            VisualizerColorSource.Artwork => cover,
            _ => null,
        };
        Accent = _appearance.AccentSource switch
        {
            AccentSource.Custom => CssHexColor.TryParse(_appearance.AccentCustomColor, out var custom)
                ? new Rgb(custom.R, custom.G, custom.B)
                : AccentPalette.Default.Base,
            AccentSource.Artwork when cover is { } c && !IsNeutral(c) => new Rgb(c.R, c.G, c.B),
            AccentSource.Artwork when keepAccent && _coverAccent is { } last => last,
            _ => AccentPalette.Default.Base,
        };
        _coverAccent = _appearance.AccentSource == AccentSource.Artwork && Accent != AccentPalette.Default.Base
            ? Accent
            : null;
    }

    private static bool IsNeutral(Color c) =>
        (c.R, c.G, c.B) == ArtworkColor.Neutral;

    private void Cancel()
    {
        _reading?.Cancel();
        _reading?.Dispose();
        _reading = null;
        _readingUrl = null;
    }

    private async Task ReadAsync(string url)
    {
        var reading = new CancellationTokenSource();
        _reading = reading;
        _readingUrl = url;
        var token = reading.Token;
        Color? picked = null;
        try
        {
            var bytes = await _loadBytes!(url, token);
            picked = await Task.Run(() => Pick(bytes), token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            AppLog.Write("viz", $"Couldn't read the cover's colour: {ex.Message}");
        }

        // Back on the UI thread. A newer track or a changed source cancelled
        // this read; a result that arrives anyway is for a cover no longer shown.
        if (token.IsCancellationRequested || url != _player.ArtworkUrl)
            return;
        _reading = null;
        _readingUrl = null;
        reading.Dispose();
        _pickedUrl = url;
        _picked = picked;
        Apply(picked);
    }

    /// <summary>The visualizer colour for an encoded image; null when it can't be
    /// decoded or has no opaque pixel.</summary>
    internal static Color? Pick(byte[] encoded)
    {
        using var data = SKData.CreateCopy(encoded);
        using var codec = SKCodec.Create(data);
        if (codec is null)
            return null;
        // A JPEG decodes at an eighth of its size for little more than the cost
        // of reading it; the sample needs no more.
        var info = codec.Info.WithSize(codec.GetScaledDimensions(SampleSize / (float)Math.Max(1, Math.Min(codec.Info.Width, codec.Info.Height))));
        using var source = SKBitmap.Decode(codec, info);
        if (source is null)
            return null;
        // ArtworkColor reads unpremultiplied RGBA; Skia's native order is BGRA
        // on Windows, so the sample's format is spelled out.
        using var sample = new SKBitmap(new SKImageInfo(SampleSize, SampleSize, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        if (!source.ScalePixels(sample, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None)))
            return null;
        return ArtworkColor.Pick(sample.GetPixelSpan()) is { } c ? Avalonia.Media.Color.FromRgb(c.R, c.G, c.B) : null;
    }
}
