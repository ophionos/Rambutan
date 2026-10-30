using Rambutan.Core.Models;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Providers;

/// <summary>
/// YouTube Music's own lyrics: plain text, with its line naming where the text
/// came from. Asks only by the track's YouTube id, so a track that isn't on
/// YouTube Music has none here, and nothing is sent for it.
/// </summary>
/// <param name="lookup">Asks YouTube Music for a track's lyrics by its video id:
/// null when it has none, an exception when it couldn't be asked.</param>
public sealed class YouTubeMusicLyricsProvider(
    Func<string, CancellationToken, Task<(string Text, string? Credit)?>> lookup) : ILyricsProvider
{
    public const string SourceName = "youtubeMusic";

    public string Name => SourceName;

    public async Task<LyricsResult?> TryGetAsync(LyricsQuery query, CancellationToken ct)
    {
        if (!YouTubeIds.IsVideoId(query.VideoId))
            return null;

        if (await lookup(query.VideoId, ct) is not { } found || string.IsNullOrWhiteSpace(found.Text))
            return null;

        return LyricsResult.Found(LyricsDocument.FromPlainText(found.Text, SourceName) with
        {
            Credit = string.IsNullOrWhiteSpace(found.Credit) ? null : found.Credit.Trim(),
        });
    }
}
