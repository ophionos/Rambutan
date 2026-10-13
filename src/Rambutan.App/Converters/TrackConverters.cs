using System.Globalization;
using Avalonia.Data.Converters;
using Rambutan.Core.Models;

namespace Rambutan.App.Converters;

/// <summary>
/// True when a list row is the track currently playing.
/// </summary>
/// <remarks>
/// Takes two <c>TrackInfo.Identity</c> values rather than two <c>TrackInfo</c>
/// records. <c>TrackInfo</c> is a record, so <c>==</c> compares every field, and
/// fields such as <c>SetVideoId</c> differ per surface, so comparing whole records
/// would silently never match. A named property path also binds the same way on
/// every surface, whether the row's DataContext is the track itself (album,
/// playlist, artist) or wraps it (search).
///
/// <para><b>Marks a song where YouTube Music says which one.</b> It hands out
/// different videoIds for one album track per surface: the album page lists the
/// music video, search lists the audio track. <c>TrackIdentity.SameSong</c>
/// matches them through the album row's credits link, and falls back to the
/// exact videoId where no surface names the counterpart. Title and artist
/// are never compared: that would mark covers, live cuts and remixes.</para>
///
/// The queue does not use this at all: <c>QueueItemViewModel.IsCurrent</c> is
/// index-based, which is the right key there because a queue may hold the same
/// track twice and only one of those rows is playing.
/// </remarks>
public sealed class IsPlayingTrackConverter : IMultiValueConverter
{
    public static readonly IsPlayingTrackConverter Instance = new(invert: false);

    /// <summary>True for every row except the playing one: album track numbers
    /// give way to the playing glyph in the same slot.</summary>
    public static readonly IsPlayingTrackConverter Inverted = new(invert: true);

    private readonly bool _invert;

    private IsPlayingTrackConverter(bool invert) => _invert = invert;

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Count < 2)
        {
            return _invert;
        }

        // Nothing playing binds through as null/UnsetValue, which is "no match"
        // rather than "matches every row with no id".
        var isPlaying = values[0] is TrackIdentity row
            && values[1] is TrackIdentity playing
            && row.SameSong(playing);
        return isPlaying != _invert;
    }
}

/// <summary>
/// True when a list row's track has a download: the row's <c>VideoId</c> against
/// the set in <c>TrackActionsViewModel.DownloadedIds</c>. Bound like
/// <see cref="IsPlayingTrackConverter"/>, so a bare <c>TrackInfo</c> row needs no
/// view model of its own. Unlike it, it marks a video, not a song: the download is
/// one video's audio, and its counterpart's edit can differ.
/// </summary>
public sealed class IsDownloadedConverter : IMultiValueConverter
{
    public static readonly IsDownloadedConverter Instance = new();

    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Count >= 2
        && values[0] is string { Length: > 0 } videoId
        && values[1] is IReadOnlySet<string> downloaded
        && downloaded.Contains(videoId);
}

/// <summary>
/// A track's cover: the download's own copy when it has one, as a <c>file:</c> URI
/// that <c>RemoteImage</c> reads in place, so it shows offline and outlives the
/// artwork cache; otherwise the remote thumbnail. Keyed by <c>VideoId</c> like
/// <see cref="IsDownloadedConverter"/>, which also means the same song reached as
/// another video shows its remote cover.
/// </summary>
public sealed class ArtworkConverter : IValueConverter
{
    public static readonly ArtworkConverter Instance = new();

    /// <summary>VideoId → the download's kept cover; set once at composition (App startup).</summary>
    public static Func<string, string?>? LocalArtwork { get; set; }

    public static string? For(Rambutan.Core.Models.TrackInfo? track) =>
        track is null ? null
        : LocalArtwork?.Invoke(track.VideoId) is { } path ? new Uri(path).AbsoluteUri
        : track.ThumbnailUrl;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        For(value as Rambutan.Core.Models.TrackInfo);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
