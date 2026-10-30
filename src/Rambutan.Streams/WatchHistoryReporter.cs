using System.Globalization;
using System.Text;
using Rambutan.Core.Models;

namespace Rambutan.Streams;

/// <summary>
/// Adds a play to the account's YouTube Music history through yt-dlp's
/// <c>--mark-watched</c>, which sends the player response's playback report and
/// then its watch-time report for the whole track (yt-dlp sends both, and only
/// when it is given cookies). A run of its own, after the play counts, so stream
/// resolution stays signed out and a warm-up never reaches the history.
/// </summary>
/// <remarks>
/// The cookies go to yt-dlp as a Netscape cookie file in a folder of this run's
/// own, owner-only on Linux, deleted when the run ends. yt-dlp writes rotated
/// cookies back into that file; they are dropped with it.
/// </remarks>
public sealed class WatchHistoryReporter(YtDlpManager ytDlp)
{
    /// <summary>
    /// Reports the play. Throws <see cref="WatchHistoryException"/> when yt-dlp
    /// fails or says it couldn't mark the play, <see cref="NotAYouTubeTrackException"/>
    /// for an id that isn't YouTube's; cancellation stops the run.
    /// </summary>
    /// <param name="cookieHeader">The account's cookies, as a Cookie header.</param>
    public async Task ReportAsync(string videoId, string cookieHeader, CancellationToken ct = default)
    {
        YouTubeIds.Require(videoId);
        var binary = await ytDlp.EnsureAsync(ct);

        var folder = Path.Combine(Path.GetTempPath(), "rambutan-watched", Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(folder);
        else
            Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cookies = Path.Combine(folder, "cookies.txt");

        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows())
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using (var file = new StreamWriter(cookies, Encoding.UTF8, options))
                await file.WriteAsync(CookieFile(cookieHeader, DateTimeOffset.UtcNow.AddDays(1)));

            var result = await ProcessRunner.RunAsync(binary, Arguments(videoId, cookies), ct, workingDirectory: folder);
            if (result.ExitCode != 0)
                throw new WatchHistoryException(videoId, result.StdErr.Trim());
            // yt-dlp only warns when a report can't be sent, and exits 0.
            if (result.StdErr.Contains("Unable to mark", StringComparison.Ordinal))
                throw new WatchHistoryException(videoId, result.StdErr.Trim());
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Retried on the next start: the folder holds the account's cookies.
            }
        }
    }

    /// <summary>Deletes cookie folders an earlier run couldn't (a crash, a locked
    /// file). Never throws.</summary>
    public static void RemoveLeftovers()
    {
        try
        {
            var root = Path.Combine(Path.GetTempPath(), "rambutan-watched");
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // --simulate: nothing is downloaded. Warnings stay on, since a report that
    // couldn't be sent is only a warning.
    internal static IReadOnlyList<string> Arguments(string videoId, string cookieFile) =>
    [
        "--mark-watched", "--simulate", "--no-playlist", "--quiet",
        "-f", "bestaudio/best",
        "--cookies", cookieFile,
        $"https://music.youtube.com/watch?v={videoId}",
    ];

    /// <summary>
    /// A Cookie header as a Netscape cookie file for yt-dlp: each cookie on
    /// youtube.com and its subdomains, secure, expiring at <paramref name="expires"/>.
    /// A pair without a name or with a tab or line break in it is left out.
    /// </summary>
    internal static string CookieFile(string cookieHeader, DateTimeOffset expires)
    {
        var text = new StringBuilder("# Netscape HTTP Cookie File\n");
        var expiry = expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        foreach (var part in cookieHeader.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length == 0 || (name + value).AsSpan().IndexOfAny("\t\r\n") >= 0)
                continue;
            text.Append(".youtube.com\tTRUE\t/\tTRUE\t").Append(expiry).Append('\t')
                .Append(name).Append('\t').Append(value).Append('\n');
        }
        return text.ToString();
    }
}

public sealed class WatchHistoryException(string videoId, string detail)
    : Exception($"Failed to add '{videoId}' to the history: {detail}");
