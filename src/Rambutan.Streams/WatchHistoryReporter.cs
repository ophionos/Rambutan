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
/// The cookies go to yt-dlp as a Netscape cookie file in a folder of the run's
/// own under <paramref name="workDirectory"/> (the profile's, so instances side
/// by side never touch each other's), owner-only on Linux, deleted when the run
/// ends. yt-dlp's page request can rotate some of them (the SIDCC family), and
/// it writes what it was sent back into the file; those values come back to the
/// caller, since a session that drops a rotated cookie can be refused later.
/// </remarks>
/// <param name="workDirectory">Holds the runs' cookie folders; nothing else.</param>
public sealed class WatchHistoryReporter(YtDlpManager ytDlp, string workDirectory)
{
    /// <summary>
    /// Reports the play, and returns the cookies yt-dlp ended with whose values
    /// differ from those sent. Throws <see cref="WatchHistoryException"/> when
    /// yt-dlp fails or says it couldn't mark the play,
    /// <see cref="NotAYouTubeTrackException"/> for an id that isn't YouTube's;
    /// cancellation stops the run.
    /// </summary>
    /// <param name="cookieHeader">The account's cookies, as a Cookie header.</param>
    public async Task<IReadOnlyList<(string Name, string Value)>> ReportAsync(
        string videoId, string cookieHeader, CancellationToken ct = default)
    {
        YouTubeIds.Require(videoId);
        var binary = await ytDlp.EnsureAsync(ct);

        var folder = Path.Combine(workDirectory, Guid.NewGuid().ToString("N"));
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

            return Changed(ParsePairs(cookieHeader), ReadCookieFile(await File.ReadAllTextAsync(cookies, ct)));
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // RemoveLeftovers tries again at the next start.
            }
        }
    }

    /// <summary>Deletes cookie folders an earlier run couldn't (a crash, a locked
    /// file). Call before any run starts. Never throws.</summary>
    public void RemoveLeftovers()
    {
        try
        {
            if (Directory.Exists(workDirectory))
                Directory.Delete(workDirectory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // --simulate: nothing is downloaded. The reports go during extraction, before
    // formats are chosen, so a signed-in client offering no format we can take
    // mustn't fail a run whose reports went. Warnings stay on, since a report
    // that couldn't be sent is only a warning.
    internal static IReadOnlyList<string> Arguments(string videoId, string cookieFile) =>
    [
        "--mark-watched", "--simulate", "--ignore-no-formats-error", "--no-playlist", "--quiet",
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
        foreach (var (name, value) in ParsePairs(cookieHeader))
        {
            text.Append(".youtube.com\tTRUE\t/\tTRUE\t").Append(expiry).Append('\t')
                .Append(name).Append('\t').Append(value).Append('\n');
        }
        return text.ToString();
    }

    /// <summary>The youtube.com cookies in a Netscape cookie file as yt-dlp
    /// writes it (HttpOnly ones under a <c>#HttpOnly_</c> domain prefix).</summary>
    internal static List<(string Name, string Value)> ReadCookieFile(string text)
    {
        const string httpOnly = "#HttpOnly_";
        var cookies = new List<(string, string)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(httpOnly, StringComparison.Ordinal))
                line = line[httpOnly.Length..];
            else if (line.StartsWith('#'))
                continue;
            var fields = line.Split('\t');
            if (fields.Length < 7 || fields[5].Length == 0)
                continue;
            var domain = fields[0].TrimStart('.');
            if (domain == "youtube.com" || domain.EndsWith(".youtube.com", StringComparison.Ordinal))
                cookies.Add((fields[5], fields[6]));
        }
        return cookies;
    }

    /// <summary>Those of <paramref name="after"/> whose value is new or changed;
    /// a cookie gone from it isn't reported (yt-dlp drops expired ones).</summary>
    internal static IReadOnlyList<(string Name, string Value)> Changed(
        IReadOnlyList<(string Name, string Value)> before, IReadOnlyList<(string Name, string Value)> after)
    {
        var sent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, value) in before)
            sent[name] = value;
        return after.Where(c => !sent.TryGetValue(c.Name, out var old) || old != c.Value).ToArray();
    }

    private static List<(string Name, string Value)> ParsePairs(string cookieHeader)
    {
        var pairs = new List<(string, string)>();
        foreach (var part in cookieHeader.Split(';'))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
                continue;
            var name = part[..eq].Trim();
            var value = part[(eq + 1)..].Trim();
            if (name.Length == 0 || (name + value).AsSpan().IndexOfAny("\t\r\n") >= 0)
                continue;
            pairs.Add((name, value));
        }
        return pairs;
    }
}

public sealed class WatchHistoryException(string videoId, string detail)
    : Exception($"Failed to add '{videoId}' to the history: {detail}");
