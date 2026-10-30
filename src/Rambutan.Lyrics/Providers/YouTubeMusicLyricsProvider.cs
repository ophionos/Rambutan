using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Providers;

/// <summary>
/// YouTube Music's own lyrics: plain text, with its line naming where the text
/// came from. Asks only by a YouTube id: the track's own, or the song a track
/// from elsewhere stands for (a local file's match). A track with neither has
/// none here, and nothing is sent for it.
/// </summary>
/// <param name="lookup">Asks YouTube Music for a track's lyrics by its video id:
/// null when it has none, an exception when it couldn't be asked.</param>
/// <param name="songOf">The video id of the song a track that isn't on YouTube
/// stands for: null when it has none, an exception when that couldn't be told.</param>
public sealed class YouTubeMusicLyricsProvider(
    Func<string, CancellationToken, Task<(string Text, string? Credit)?>> lookup,
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

        return LyricsResult.Found(LyricsDocument.FromPlainText(found.Text, SourceName) with
        {
            Credit = string.IsNullOrWhiteSpace(found.Credit) ? null : found.Credit.Trim(),
        });
    }
}
