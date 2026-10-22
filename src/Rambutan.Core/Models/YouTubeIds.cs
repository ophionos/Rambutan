namespace Rambutan.Core.Models;

/// <summary>
/// Which track ids may go to YouTube Music. Its video ids are letters, digits,
/// '-' and '_'; anything else, such as a local file's <c>local:</c> id, never
/// reaches a request or a YouTube link, because malformed requests could get the
/// user's account flagged as misuse. InnerTube and the stream resolver refuse
/// such an id before sending anything.
/// </summary>
public static class YouTubeIds
{
    /// <summary>True when <paramref name="id"/> has the form of a YouTube video id.
    /// The length isn't checked: only the characters decide.</summary>
    public static bool IsVideoId(string? id)
    {
        if (string.IsNullOrEmpty(id))
            return false;
        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')
                return false;
        }
        return true;
    }

    /// <exception cref="NotAYouTubeTrackException">The id isn't a YouTube video id.</exception>
    public static void Require(string? id)
    {
        if (!IsVideoId(id))
            throw new NotAYouTubeTrackException(id);
    }
}

/// <summary>A request to YouTube Music was refused before it was sent, because
/// the track's id isn't one of YouTube's.</summary>
public sealed class NotAYouTubeTrackException(string? id)
    : InvalidOperationException($"'{id}' is not a YouTube Music track id, so it is not sent there.")
{
    public string? TrackId { get; } = id;
}
