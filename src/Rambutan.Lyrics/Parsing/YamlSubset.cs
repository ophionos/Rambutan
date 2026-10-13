using System.Globalization;
using System.Text;

namespace Rambutan.Lyrics.Parsing;

/// <summary>
/// Reads the block-style YAML that Lyricsfile documents use: mappings and
/// sequences by indentation (including sequences at their key's indentation),
/// plain, single-quoted and double-quoted scalars on one line, and literal or
/// folded block scalars. A mapping comes back as a
/// <see cref="Dictionary{TKey,TValue}"/>, a sequence as a <see cref="List{T}"/>,
/// and a scalar as its string; null is YAML's null.
/// </summary>
/// <remarks>
/// Anything outside that subset (flow collections, anchors and aliases, tags,
/// complex keys, directives, multi-line flow scalars, tabs in indentation) throws
/// <see cref="FormatException"/>, as do duplicate keys, which the Lyricsfile spec
/// forbids. Callers treat that as "no document" and fall back, so the subset fails
/// closed instead of guessing. No package: a YAML library would be the Lyrics
/// project's first dependency and need AOT vetting for one draft format.
/// </remarks>
internal sealed class YamlSubset
{
    /// <summary>Larger than any real lyrics document; a guard, not a format limit.</summary>
    public const int MaxLength = 4 * 1024 * 1024;

    private const int MaxDepth = 32;

    private readonly List<Line> _lines;
    private int _next;

    private YamlSubset(List<Line> lines) => _lines = lines;

    private sealed class Line(int indent, string content, string raw)
    {
        public int Indent = indent;
        public string Content = content;
        public readonly string Raw = raw;
    }

    /// <summary>The document's root value. Throws <see cref="FormatException"/>
    /// on anything outside the subset.</summary>
    public static object? Parse(string text)
    {
        if (text.Length > MaxLength)
            throw new FormatException("Document too large.");

        var lines = new List<Line>();
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var indent = 0;
            while (indent < line.Length && line[indent] == ' ')
                indent++;
            if (indent < line.Length && line[indent] == '\t')
                throw new FormatException("Tab in indentation.");
            lines.Add(new Line(indent, line[indent..].TrimEnd(), line));
        }

        var reader = new YamlSubset(lines);
        reader.SkipDocumentStart();
        if (reader.Peek() is null)
            return null;

        var root = reader.ParseNode(0, 0);
        if (reader.Peek() is { } extra && extra.Content != "...")
            throw new FormatException($"Unexpected content: {extra.Content}");
        return root;
    }

    private void SkipDocumentStart()
    {
        if (Peek() is { } first)
        {
            if (first.Content.StartsWith('%'))
                throw new FormatException("Directives are not supported.");
            if (first.Indent == 0 && (first.Content == "---" || first.Content.StartsWith("--- ", StringComparison.Ordinal)))
            {
                if (first.Content.Length > 3)
                    throw new FormatException("Content after the document marker.");
                _next++;
            }
        }
    }

    /// <summary>The next line with content, skipping blank and comment lines.</summary>
    private Line? Peek()
    {
        while (_next < _lines.Count)
        {
            var line = _lines[_next];
            if (line.Content.Length > 0 && !line.Content.StartsWith('#'))
                return line;
            _next++;
        }
        return null;
    }

    private object? ParseNode(int minIndent, int depth)
    {
        if (depth > MaxDepth)
            throw new FormatException("Nesting too deep.");
        if (Peek() is not { } line || line.Indent < minIndent)
            return null;
        if (IsSequenceItem(line.Content))
            return ParseSequence(line.Indent, depth);
        if (SplitKey(line.Content) is not null)
            return ParseMapping(line.Indent, depth);
        throw new FormatException($"Expected a mapping or a sequence: {line.Content}");
    }

    private Dictionary<string, object?> ParseMapping(int indent, int depth)
    {
        var map = new Dictionary<string, object?>(StringComparer.Ordinal);
        while (Peek() is { } line && line.Indent >= indent)
        {
            if (line.Indent > indent)
                throw new FormatException($"Unexpected indentation: {line.Content}");
            if (IsSequenceItem(line.Content))
                break;
            if (SplitKey(line.Content) is not var (key, rest))
                throw new FormatException($"Expected a key: {line.Content}");
            _next++;

            object? value;
            if (rest.Length == 0)
            {
                // The value is the block below, or a sequence at the key's own
                // indentation, which is how many writers emit lists.
                value = Peek() is { } below && below.Indent == indent && IsSequenceItem(below.Content)
                    ? ParseSequence(indent, depth + 1)
                    : ParseNode(indent + 1, depth + 1);
            }
            else if (rest[0] is '|' or '>')
            {
                value = ReadBlockScalar(rest, indent);
            }
            else
            {
                value = ParseScalar(rest);
                if (Peek() is { } after && after.Indent > indent)
                    throw new FormatException("Multi-line scalars are not supported.");
            }

            if (!map.TryAdd(key, value))
                throw new FormatException($"Duplicate key: {key}");
        }
        return map;
    }

    private List<object?> ParseSequence(int indent, int depth)
    {
        var list = new List<object?>();
        while (Peek() is { } line && line.Indent == indent && IsSequenceItem(line.Content))
        {
            var content = line.Content[1..];
            var gap = content.Length - content.TrimStart(' ').Length;
            content = content.TrimStart(' ');

            if (content.Length == 0)
            {
                _next++;
                list.Add(ParseNode(indent + 1, depth + 1));
            }
            else if (IsSequenceItem(content) || SplitKey(content) is not null)
            {
                // "- key: value" opens a mapping (or "- - x" a sequence) whose
                // first entry is on this line: read the line again as if the
                // item's content started it, at the column where it stands.
                line.Indent = indent + 1 + gap;
                line.Content = content;
                list.Add(ParseNode(line.Indent, depth + 1));
            }
            else
            {
                _next++;
                list.Add(content[0] is '|' or '>' ? ReadBlockScalar(content, indent) : ParseScalar(content));
                if (content[0] is not ('|' or '>') && Peek() is { } after && after.Indent > indent)
                    throw new FormatException("Multi-line scalars are not supported.");
            }
        }
        return list;
    }

    private static bool IsSequenceItem(string content) =>
        content == "-" || content.StartsWith("- ", StringComparison.Ordinal);

    /// <summary>Splits "key: rest" (the key plain or quoted); null when the line
    /// is not a mapping entry.</summary>
    private static (string Key, string Value)? SplitKey(string content)
    {
        string key;
        int colon;
        if (content[0] is '\'' or '"')
        {
            var (quoted, length) = ReadQuoted(content);
            if (length >= content.Length || content[length] != ':')
                return null;
            key = quoted;
            colon = length;
        }
        else
        {
            if (content[0] is '?' or '[' or '{' or '&' or '*' or '!' or '|' or '>' or '%' or '@' or '`' or ',')
                return null;
            colon = content.IndexOf(": ", StringComparison.Ordinal);
            if (colon < 0)
            {
                if (!content.EndsWith(':'))
                    return null;
                colon = content.Length - 1;
            }
            key = content[..colon].TrimEnd();
            if (key.Length == 0 || key.Contains(" #", StringComparison.Ordinal))
                return null;
        }

        if (colon + 1 < content.Length && content[colon + 1] != ' ')
            return null;
        var rest = StripComment(content[(colon + 1)..].Trim());
        return (key, rest);
    }

    private static string StripComment(string rest)
    {
        if (rest.StartsWith('#'))
            return "";
        if (rest.Length > 0 && rest[0] is '\'' or '"')
            return rest; // A quoted scalar checks what follows it itself.
        var comment = rest.IndexOf(" #", StringComparison.Ordinal);
        return comment < 0 ? rest : rest[..comment].TrimEnd();
    }

    private static string? ParseScalar(string text)
    {
        if (text[0] is '\'' or '"')
        {
            var (value, length) = ReadQuoted(text);
            var after = text[length..].Trim();
            if (after.Length > 0 && !after.StartsWith('#'))
                throw new FormatException($"Content after a quoted scalar: {after}");
            return value;
        }

        if (text[0] is '[' or '{' or '&' or '*' or '!' or '%' or '@' or '`' or '?' or ',')
            throw new FormatException($"Unsupported value: {text}");
        if (text.Contains(": ", StringComparison.Ordinal))
            throw new FormatException($"Unsupported plain scalar: {text}");

        return text is "~" or "null" or "Null" or "NULL" ? null : text;
    }

    /// <summary>A quoted scalar at the start of <paramref name="text"/>, and how
    /// many characters it took. Throws when it doesn't close on this line.</summary>
    private static (string Value, int Length) ReadQuoted(string text)
    {
        var quote = text[0];
        var value = new StringBuilder();
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (quote == '\'')
            {
                if (c != '\'')
                {
                    value.Append(c);
                }
                else if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i++;
                }
                else
                {
                    return (value.ToString(), i + 1);
                }
            }
            else if (c == '"')
            {
                return (value.ToString(), i + 1);
            }
            else if (c == '\\')
            {
                if (++i >= text.Length)
                    break; // A line continuation: multi-line, not supported.
                i = AppendEscape(text, i, value);
            }
            else
            {
                value.Append(c);
            }
        }
        throw new FormatException("Unterminated or multi-line quoted scalar.");
    }

    /// <summary>Appends the escape whose letter is at <paramref name="i"/>; returns
    /// the index of its last character.</summary>
    private static int AppendEscape(string text, int i, StringBuilder value)
    {
        switch (text[i])
        {
            case '0': value.Append('\0'); return i;
            case 'a': value.Append('\a'); return i;
            case 'b': value.Append('\b'); return i;
            case 't' or '\t': value.Append('\t'); return i;
            case 'n': value.Append('\n'); return i;
            case 'v': value.Append('\v'); return i;
            case 'f': value.Append('\f'); return i;
            case 'r': value.Append('\r'); return i;
            case 'e': value.Append('\u001B'); return i;
            case ' ': value.Append(' '); return i;
            case '"': value.Append('"'); return i;
            case '/': value.Append('/'); return i;
            case '\\': value.Append('\\'); return i;
            case 'N': value.Append('\u0085'); return i;
            case '_': value.Append('\u00A0'); return i;
            case 'L': value.Append('\u2028'); return i;
            case 'P': value.Append('\u2029'); return i;
            case 'x': return AppendCodePoint(text, i, 2, value);
            case 'u': return AppendCodePoint(text, i, 4, value);
            case 'U': return AppendCodePoint(text, i, 8, value);
            default: throw new FormatException($"Unknown escape: \\{text[i]}");
        }
    }

    private static int AppendCodePoint(string text, int i, int digits, StringBuilder value)
    {
        if (i + digits >= text.Length ||
            !int.TryParse(text.AsSpan(i + 1, digits), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code) ||
            code > 0x10FFFF || code is >= 0xD800 and <= 0xDFFF)
        {
            throw new FormatException("Bad escape.");
        }
        value.Append(char.ConvertFromUtf32(code));
        return i + digits;
    }

    /// <summary>
    /// A literal (<c>|</c>) or folded (<c>&gt;</c>) block below a key at
    /// <paramref name="parentIndent"/>: every following line that is blank or
    /// indented deeper. Folding joins lines with spaces; chomping indicators are
    /// honoured, explicit indentation indicators are not supported.
    /// </summary>
    private string ReadBlockScalar(string header, int parentIndent)
    {
        var folded = header[0] == '>';
        var indicators = StripComment(header[1..].Trim());
        if (indicators is not ("" or "-" or "+"))
            throw new FormatException($"Unsupported block scalar header: {header}");

        var body = new List<string>();
        var contentIndent = -1;
        while (_next < _lines.Count)
        {
            var line = _lines[_next];
            if (line.Content.Length == 0)
            {
                body.Add("");
                _next++;
                continue;
            }
            if (line.Indent <= parentIndent)
                break;
            if (contentIndent < 0)
                contentIndent = line.Indent;
            if (line.Indent < contentIndent)
                throw new FormatException("Block scalar indentation decreases.");
            body.Add(line.Raw.TrimEnd('\r')[contentIndent..]);
            _next++;
        }

        var trailingBlanks = 0;
        while (body.Count > 0 && body[^1].Length == 0)
        {
            body.RemoveAt(body.Count - 1);
            trailingBlanks++;
        }

        var text = folded ? Fold(body) : string.Join('\n', body);
        return indicators switch
        {
            "-" => text,
            "+" => text + new string('\n', trailingBlanks + (body.Count > 0 ? 1 : 0)),
            _ => body.Count > 0 ? text + "\n" : "",
        };
    }

    private static string Fold(List<string> lines)
    {
        var text = new StringBuilder();
        for (var i = 0; i < lines.Count; i++)
        {
            if (i > 0)
                text.Append(lines[i].Length == 0 || lines[i - 1].Length == 0 ? "\n" : " ");
            text.Append(lines[i]);
        }
        return text.ToString();
    }
}
