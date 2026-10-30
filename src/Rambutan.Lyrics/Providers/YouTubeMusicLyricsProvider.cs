using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Providers;

/// <summary>A track's lyrics as YouTube Music gave them.</summary>
/// <param name="Text">The lyrics as plain text, a line per line.</param>
/// <param name="Credit">Its line naming where the text came from, or null.</param>
/// <param name="Lines">The lines with their timings, when it timed them; null
/// for plain lyrics. Line-level only: YouTube Music times no words.</param>
public sealed record YouTubeMusicLyrics(
    string Text,
    string? Credit,
    IReadOnlyList<(TimeSpan Start, TimeSpan End, string Text)>? Lines = null);

/// <summary>
/// YouTube Music's own lyrics: line-synced when it times them, else plain
/// text, with its line naming where the text came from. Asks only by a YouTube
/// id: the track's own, or the song a track from elsewhere stands for (a local
/// file's match). A track with neither has none here, and nothing is sent for it.
/// </summary>
/// <param name="lookup">Asks YouTube Music for a track's lyrics by its video id:
/// null when it has none, an exception when it couldn't be asked.</param>
/// <param name="songOf">The video id of the song a track that isn't on YouTube
/// stands for: null when it has none, an exception when that couldn't be told.</param>
public sealed class YouTubeMusicLyricsProvider(
    Func<string, CancellationToken, Task<YouTubeMusicLyrics?>> lookup,
    Func<string, CancellationToken, Task<string?>>? songOf = null) : ILyricsProvider
{
    public const string SourceName = "youtubeMusic";

    public string Name => SourceName;

    public async Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct)
    {
        var videoId = query.VideoId;
        if (!YouTubeIds.IsVideoId(videoId))
        {
            videoId = songOf is null || string.IsNullOrEmpty(videoId) ? null : await songOf(videoId, ct);
            if (!YouTubeIds.IsVideoId(videoId))
                return LyricsResult.NotAsked(SourceName);
        }

        if (await lookup(videoId!, ct) is not { } found || string.IsNullOrWhiteSpace(found.Text))
            return null;

        var document = found.Lines is { Count: > 0 } timed
            ? Synced(timed)
            : LyricsDocument.FromPlainText(found.Text, SourceName);
        return LyricsResult.Found(document with
        {
            Credit = string.IsNullOrWhiteSpace(found.Credit) ? null : found.Credit.Trim(),
        });
    }

    // A line lasts until the next one starts, as an LRC line does; YouTube
    // Music's own end only closes the last. Out-of-order starts are put in order
    // rather than trusted to the renderer.
    private static LyricsDocument Synced(IReadOnlyList<(TimeSpan Start, TimeSpan End, string Text)> timed)
    {
        var ordered = timed.OrderBy(l => l.Start).ToList();
        var lines = new List<LyricLine>(ordered.Count);
        for (var i = 0; i < ordered.Count; i++)
        {
            var (start, end, text) = ordered[i];
            var lineEnd = i + 1 < ordered.Count ? ordered[i + 1].Start : (end > start ? end : (TimeSpan?)null);
            lines.Add(new LyricLine(text.TrimEnd(), start, lineEnd));
        }
        return new LyricsDocument { Source = SourceName, IsSynced = true, Lines = lines };
    }
}
