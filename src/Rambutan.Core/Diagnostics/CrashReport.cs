using System.Text;
using System.Text.RegularExpressions;

namespace Rambutan.Core.Diagnostics;

/// <summary>
/// What the app keeps about a crash: the exception and where it ran, and
/// nothing about the person. No crash data leaves the machine on its own;
/// on the next start the user can open a GitHub issue pre-filled with this, read
/// it, and send it or not.
/// </summary>
public sealed partial record CrashReport(
    DateTimeOffset At,
    string AppVersion,
    string Os,
    string Runtime,
    string Exception)
{
    /// <summary>GitHub answers 414 to a long URL; this leaves room under its
    /// limit of about 8 KB.</summary>
    public const int MaxIssueUrlLength = 7000;

    private const string Truncated = "\n… (cut to fit the link; the crash file has the rest)";

    public static CrashReport Create(
        DateTimeOffset at, string appVersion, string os, string runtime, string exception,
        IEnumerable<KeyValuePair<string, string>> personal) =>
        new(at, appVersion, os, runtime, Scrub(exception, personal));

    /// <summary>
    /// Takes out what could name the person: each <paramref name="personal"/> value
    /// (folders, the user and machine names) becomes its placeholder, and every
    /// URL loses its query, where stream URLs carry the IP address and signatures.
    /// Values are replaced longest first, so a data folder inside the home folder
    /// keeps its own placeholder. Names are replaced as whole words only.
    /// </summary>
    public static string Scrub(string text, IEnumerable<KeyValuePair<string, string>> personal)
    {
        foreach (var (value, placeholder) in personal
                     .Where(p => p.Key.Trim().Length >= 2)
                     .OrderByDescending(p => p.Key.Length))
        {
            // A folder matches with either separator, as .NET may print either.
            var pattern = string.Join(@"[\\/]", value.TrimEnd('/', '\\').Split('/', '\\').Select(Regex.Escape));
            text = Regex.Replace(text, $@"(?<![\w]){pattern}(?![\w])", placeholder.Replace("$", "$$"),
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        return QueryPattern().Replace(text, "$1?…");
    }

    [GeneratedRegex(@"(\b[a-z][a-z0-9+.-]*://[^\s?#""'<>]+)\?[^\s""'<>]*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QueryPattern();

    /// <summary>The issue's title: the exception type, which is what tells one
    /// crash from another in the issue list.</summary>
    public string IssueTitle
    {
        get
        {
            var firstLine = Exception.Split('\n', 2)[0].Trim();
            var type = firstLine.Split(':', 2)[0].Trim();
            return $"Crash: {(type.Length is > 0 and <= 120 ? type : "unhandled exception")}";
        }
    }

    /// <summary>The issue's body. In English, like the log: it is read by the
    /// maintainer, whatever language the app runs in.</summary>
    public string IssueBody(int? exceptionLength = null)
    {
        var exception = Exception;
        if (exceptionLength is { } length && length < Exception.Length)
        {
            length = Math.Max(0, length);
            // Never between the halves of a surrogate pair (an emoji in a title).
            if (length > 0 && char.IsHighSurrogate(Exception[length - 1]))
                length--;
            exception = Exception[..length] + Truncated;
        }
        var body = new StringBuilder();
        body.Append("**What were you doing when Rambutan closed?**\n\n");
        body.Append("<!-- Describe it here if you can. -->\n\n");
        body.Append("**Crash details** (from Rambutan's crash file; read them before you send this)\n\n");
        body.Append($"- Version: {AppVersion}\n");
        body.Append($"- OS: {Os}\n");
        body.Append($"- Runtime: {Runtime}\n");
        body.Append($"- When: {At.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC\n\n");
        body.Append("```\n").Append(exception.Replace("```", "'''")).Append("\n```\n");
        return body.ToString();
    }

    public Uri IssueUrl(string repository)
    {
        var start = $"{repository.TrimEnd('/')}/issues/new?title={Uri.EscapeDataString(IssueTitle)}&body=";
        var length = Exception.Length;
        while (true)
        {
            var url = start + Uri.EscapeDataString(IssueBody(length));
            if (url.Length <= MaxIssueUrlLength || length == 0)
                return new Uri(url);
            // Escaping can triple a character's length, so shrink by what is over.
            length = Math.Max(0, length - Math.Max(64, (url.Length - MaxIssueUrlLength) / 3));
        }
    }
}
