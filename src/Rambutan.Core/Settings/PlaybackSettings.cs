namespace Rambutan.Core.Settings;

/// <summary>
/// Playback preferences read live by the coordinator. Mutable so the
/// settings UI can flip values mid-playback; persistence happens at the App layer.
/// </summary>
public sealed class PlaybackSettings
{
    public bool CrossfadeEnabled { get; set; }
    public double CrossfadeSeconds { get; set; } = 6;

    /// <summary>Manual skips fade into the armed successor instead of cutting.
    /// Only meaningful while <see cref="CrossfadeEnabled"/>.</summary>
    public bool CrossfadeOnSkip { get; set; } = true;

    /// <summary>Volume normalization: attenuate tracks louder than the
    /// platform reference using YTM loudness metadata. Applies from the next
    /// track onward when toggled mid-playback.</summary>
    public bool NormalizeVolume { get; set; } = true;

    public double Volume { get; set; } = 1.0;

    /// <summary>Autoplay: a queue that runs out continues with radio seeded
    /// from its last track, unless repeat is on. Default on, as in YouTube Music.</summary>
    public bool Autoplay { get; set; } = true;

    /// <summary>Stream format preference, read by the resolver on each
    /// resolution, so a change applies from the next track resolved.</summary>
    public AudioQuality AudioQuality { get; set; } = AudioQuality.High;
}

/// <summary>
/// The stream quality tiers the resolver can actually honour. YouTube serves
/// every track in a high and a low Opus rendition (AAC as the fallback for each);
/// the higher-bitrate AAC format needs Premium, which the resolver doesn't have, so
/// there is no tier above <see cref="High"/>.
/// </summary>
public enum AudioQuality
{
    /// <summary>The best audio format offered: Opus at about 130–160 kbps, else
    /// AAC at 128 kbps.</summary>
    High,

    /// <summary>The smallest audio format offered: Opus at about 50 kbps, else AAC
    /// at 48 kbps.</summary>
    DataSaver,
}

public static class AudioQualityNames
{
    /// <summary>Settings-file spelling: readable, and an unknown value reads as
    /// <see cref="AudioQuality.High"/>.</summary>
    public static AudioQuality Parse(string? value) => value switch
    {
        "data-saver" => AudioQuality.DataSaver,
        _ => AudioQuality.High,
    };

    public static string Format(AudioQuality quality) => quality switch
    {
        AudioQuality.DataSaver => "data-saver",
        _ => "high",
    };
}
