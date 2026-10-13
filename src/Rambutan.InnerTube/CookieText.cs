using System.Text;

namespace Rambutan.InnerTube;

/// <summary>
/// Finds the Cookie header in whatever the user pasted during sign-in. Dev tools
/// offer several ways to copy a request, and hunting for the one header value is
/// the hardest part of signing in, so the dialog accepts all of these:
/// <list type="bullet">
/// <item>the bare header value: <c>SID=…; SAPISID=…</c></item>
/// <item>a header line or a whole copied header block, including <c>Cookie: …</c></item>
/// <item>"Copy as cURL" from Chrome, Edge, Brave (bash or cmd flavour) or Firefox,
/// where the cookies are in <c>-b '…'</c>, <c>--cookie</c>, or <c>-H 'cookie: …'</c></item>
/// </list>
/// Only the cookie string is kept; the rest of a copied request is dropped.
/// </summary>
public static class CookieText
{
    /// <summary>The Cookie header value in <paramref name="raw"/>, or the trimmed
    /// text itself when it has no recognisable wrapper.</summary>
    public static string ExtractCookieHeader(string raw)
    {
        var text = raw.Trim();
        if (text.StartsWith("curl", StringComparison.OrdinalIgnoreCase))
            return FromCurl(text) ?? text;

        foreach (var line in text.Split('\n'))
        {
            if (HeaderValue(line.Trim()) is { } value)
                return value;
        }

        return text;
    }

    /// <summary>"cookie: a=b" → "a=b"; null for any other header.</summary>
    private static string? HeaderValue(string header)
    {
        const string Name = "cookie:";
        return header.StartsWith(Name, StringComparison.OrdinalIgnoreCase)
            ? header[Name.Length..].Trim()
            : null;
    }

    private static string? FromCurl(string command)
    {
        // Chrome's "Copy as cURL (cmd)" escapes with carets: ^" for a quote, ^% for
        // a percent, ^ before a line break to continue it. Dropping each caret
        // leaves ordinary double-quoted shell text.
        if (command.Contains("^\"", StringComparison.Ordinal))
            command = RemoveCarets(command);

        var tokens = ShellTokens(command);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            switch (tokens[i])
            {
                case "-b" or "--cookie":
                    return tokens[i + 1].Trim();
                case "-H" or "--header" when HeaderValue(tokens[i + 1].Trim()) is { } value:
                    return value;
            }
        }

        return null;
    }

    private static string RemoveCarets(string text)
    {
        var result = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '^' && i + 1 < text.Length)
                i++;
            result.Append(text[i]);
        }
        return result.ToString();
    }

    /// <summary>
    /// Splits a POSIX-shell command line into words: 'single', $'ansi-c' and
    /// "double" quoting, backslash escapes, and backslash-newline continuations.
    /// Enough for what browsers generate, not a shell.
    /// </summary>
    internal static List<string> ShellTokens(string command)
    {
        var tokens = new List<string>();
        var word = new StringBuilder();
        var inWord = false;

        for (var i = 0; i < command.Length; i++)
        {
            var c = command[i];
            switch (c)
            {
                case ' ' or '\t' or '\r' or '\n':
                    if (inWord)
                    {
                        tokens.Add(word.ToString());
                        word.Clear();
                        inWord = false;
                    }
                    break;

                case '\\':
                    inWord |= i + 1 < command.Length && command[i + 1] is not ('\n' or '\r');
                    if (i + 1 < command.Length)
                    {
                        i++;
                        if (command[i] == '\r' && i + 1 < command.Length && command[i + 1] == '\n')
                            i++;
                        else if (command[i] is not ('\n' or '\r'))
                            word.Append(command[i]);
                    }
                    break;

                case '\'':
                    inWord = true;
                    for (i++; i < command.Length && command[i] != '\''; i++)
                        word.Append(command[i]);
                    break;

                case '$' when i + 1 < command.Length && command[i + 1] == '\'':
                    inWord = true;
                    for (i += 2; i < command.Length && command[i] != '\''; i++)
                    {
                        if (command[i] == '\\' && i + 1 < command.Length)
                        {
                            i++;
                            word.Append(command[i] switch { 'n' => '\n', 't' => '\t', 'r' => '\r', var e => e });
                        }
                        else
                        {
                            word.Append(command[i]);
                        }
                    }
                    break;

                case '"':
                    inWord = true;
                    for (i++; i < command.Length && command[i] != '"'; i++)
                    {
                        if (command[i] == '\\' && i + 1 < command.Length &&
                            command[i + 1] is '"' or '\\' or '$' or '`')
                        {
                            i++;
                        }
                        word.Append(command[i]);
                    }
                    break;

                default:
                    inWord = true;
                    word.Append(c);
                    break;
            }
        }

        if (inWord)
            tokens.Add(word.ToString());
        return tokens;
    }
}
