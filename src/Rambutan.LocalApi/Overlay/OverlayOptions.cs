namespace Rambutan.LocalApi.Overlay;

/// <summary>Copies the analyzer's latest raw bands (0..1 each) into
/// <paramref name="destination"/> and returns their frame number, which stops
/// changing when the feed stops; a negative number means there is no analyzer yet.</summary>
public delegate long BandReader(Span<float> destination);

public sealed record OverlayOptions
{
    /// <summary>Fixed, so the URL pasted into OBS keeps working across restarts.
    /// Not the remote's port: the overlay has its own loopback-only host.</summary>
    public const int DefaultPort = 41742;

    public const int BandCount = 32;

    /// <summary>The overlay's own token, read on every request. It lets a client
    /// see what plays, never control it. Null refuses everyone.</summary>
    public required Func<string?> Token { get; init; }

    /// <summary>The style the page draws with; a change reaches open pages at once.</summary>
    public required OverlayConfigSource Config { get; init; }

    /// <summary>The analyzer's raw bands, or null when there is no visualizer.</summary>
    public BandReader? Bands { get; init; }
}

/// <summary>The current <see cref="OverlayConfig"/>, and a signal when it changes.</summary>
public sealed class OverlayConfigSource(OverlayConfig initial)
{
    private OverlayConfig _current = initial;

    public OverlayConfig Current
    {
        get => Volatile.Read(ref _current);
        set
        {
            Volatile.Write(ref _current, value);
            Changed?.Invoke();
        }
    }

    /// <summary>Raised on the setter's thread.</summary>
    public event Action? Changed;
}
