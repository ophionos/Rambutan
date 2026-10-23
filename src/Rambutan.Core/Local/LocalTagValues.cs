using System.Globalization;

namespace Rambutan.Core.Local;

/// <summary>
/// Reads the numbers that tags hold as text. Tag formats spell them differently
/// ("3", "3/12", "2019-04-01", "-6.20 dB"), so each reader passes the text here.
/// </summary>
public static class LocalTagValues
{
    /// <summary>
    /// The tag an export writes with its download's YouTube id, so a scan that
    /// finds the file knows its song without a search (maintainer's decision):
    /// a Vorbis comment, an ID3 <c>TXXX</c> frame, an MP4 freeform atom under
    /// <c>com.apple.iTunes</c>. These names stay in the user's files: never change them.
    /// </summary>
    public const string YouTubeVideoIdKey = "YOUTUBE_VIDEO_ID";

    /// <summary>The same for <see cref="Models.TrackInfo.SongId"/>, when the track has one.</summary>
    public const string YouTubeSongIdKey = "YOUTUBE_SONG_ID";

    /// <summary>A YouTube id as a tag holds it, trimmed; null when it is empty or
    /// isn't YouTube-shaped (<see cref="Models.YouTubeIds"/>), so a tag can't
    /// make the app send YouTube something else.</summary>
    public static string? YouTubeId(string? text) =>
        text?.Trim() is { Length: > 0 } id && Models.YouTubeIds.IsVideoId(id) ? id : null;

    /// <summary>The number before any "/total": "3/12" is 3. Null for none, zero
    /// or anything that isn't a number.</summary>
    public static int? Position(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var slash = text.IndexOf('/');
        var number = (slash >= 0 ? text[..slash] : text).Trim();
        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 ? n : null;
    }

    /// <summary>The year a date tag starts with: "2019", "2019-04-01" and
    /// "2019-04-01T00:00:00" are 2019.</summary>
    public static int? Year(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var span = text.AsSpan().Trim();
        return span.Length >= 4
            && (span.Length == 4 || !char.IsAsciiDigit(span[4]))
            && int.TryParse(span[..4], NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            && year > 0
                ? year
                : null;
    }

    /// <summary>A ReplayGain value in dB: "-6.20 dB", "+1.5 dB" or "-6.2". Null
    /// for anything else, or for a value no real track has (beyond ±64 dB).</summary>
    public static double? GainDb(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var span = text.AsSpan().Trim();
        if (span.EndsWith("dB", StringComparison.OrdinalIgnoreCase))
            span = span[..^2].TrimEnd();
        return double.TryParse(span, NumberStyles.Float, CultureInfo.InvariantCulture, out var db)
            && double.IsFinite(db) && Math.Abs(db) <= 64
                ? db
                : null;
    }

    /// <summary>An Opus R128 gain tag as a ReplayGain value. The tag is a whole
    /// number of 1/256 dB that brings the track to −23 LUFS (RFC 7845);
    /// ReplayGain 2.0's reference is −18 LUFS, 5 dB louder. Null for anything else.</summary>
    public static double? R128AsReplayGainDb(string? text) =>
        int.TryParse(text?.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var q78)
            ? q78 / 256.0 + 5
            : null;

    /// <summary>
    /// Whether a tag holds the song's lyrics, as libav names it: <c>lyrics</c>
    /// (MP4 <c>©lyr</c>, the Vorbis comment), <c>lyrics-eng</c> and the like
    /// (ID3v2 <c>USLT</c>, with its language and any description after the
    /// dash), and the Vorbis comments <c>UNSYNCEDLYRICS</c> and
    /// <c>SYNCEDLYRICS</c>. Not <c>LYRICIST</c>, which names a person.
    /// </summary>
    public static bool IsLyricsKey(string key) =>
        key.Equals("lyrics", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("lyrics-", StringComparison.OrdinalIgnoreCase)
        || key.Equals("unsyncedlyrics", StringComparison.OrdinalIgnoreCase)
        || key.Equals("syncedlyrics", StringComparison.OrdinalIgnoreCase);

    /// <summary>A text tag as the library keeps it: trimmed, and null when empty.</summary>
    public static string? Text(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
