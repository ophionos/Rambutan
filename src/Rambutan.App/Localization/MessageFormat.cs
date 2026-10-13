using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Rambutan.App.Localization;

/// <summary>
/// The subset of ICU MessageFormat the string tables use, parsed without
/// reflection or codegen so it stays NativeAOT-clean:
/// <list type="bullet">
/// <item><c>{name}</c>: an argument, numbers formatted for the locale.</item>
/// <item><c>{count, plural, =0 {…} one {# song} other {# songs}}</c>: exact
///   matches first, then the locale's plural category (<see cref="PluralRules"/>);
///   <c>#</c> inside a branch is the number.</item>
/// <item><c>{kind, select, like {…} other {…}}</c>: picks a branch by string value.</item>
/// <item>Apostrophes are literal (<c>Couldn't</c>) except that <c>'{</c>, <c>'}</c>
///   and <c>'#</c> quote up to the next apostrophe, and <c>''</c> is one apostrophe.</item>
/// </list>
/// Anything else inside braces is a format error: <see cref="Parse"/> throws, and a
/// test parses every table so a broken string fails CI instead of a screen.
/// </summary>
public sealed class MessageFormat
{
    private readonly Node[] _nodes;

    private MessageFormat(Node[] nodes) => _nodes = nodes;

    /// <summary>Argument names the message refers to, for tests and diagnostics.</summary>
    public IReadOnlyCollection<string> ArgumentNames
    {
        get
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            Collect(_nodes, names);
            return names;

            static void Collect(Node[] nodes, SortedSet<string> into)
            {
                foreach (var node in nodes)
                {
                    switch (node)
                    {
                        case ArgNode arg:
                            into.Add(arg.Name);
                            break;
                        case BranchNode branch:
                            into.Add(branch.Name);
                            foreach (var (_, body) in branch.Branches)
                                Collect(body, into);
                            break;
                    }
                }
            }
        }
    }

    public static MessageFormat Parse(string pattern)
    {
        var parser = new Parser(pattern);
        var nodes = parser.ParseNodes(inBranch: false);
        if (!parser.AtEnd)
            throw parser.Error("unbalanced '}'");
        return new MessageFormat(nodes);
    }

    /// <summary>Formats with the given arguments. A missing argument renders as its
    /// <c>{name}</c> placeholder rather than throwing: a wrong screen beats a crash.</summary>
    public string Format(CultureInfo culture, IReadOnlyList<KeyValuePair<string, object?>> args,
        Func<string, string>? literal = null)
    {
        var sb = new StringBuilder();
        Append(sb, _nodes, culture, args, literal, pound: null);
        return sb.ToString();
    }

    private static void Append(StringBuilder sb, Node[] nodes, CultureInfo culture,
        IReadOnlyList<KeyValuePair<string, object?>> args, Func<string, string>? literal, string? pound)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case TextNode text:
                    sb.Append(literal is null ? text.Text : literal(text.Text));
                    break;
                case PoundNode:
                    sb.Append(pound ?? "#");
                    break;
                case ArgNode arg:
                    sb.Append(TryGet(args, arg.Name, out var value)
                        ? Render(value, culture)
                        : "{" + arg.Name + "}");
                    break;
                case BranchNode branch:
                    if (!TryGet(args, branch.Name, out var selector))
                    {
                        sb.Append('{').Append(branch.Name).Append('}');
                        break;
                    }

                    var body = branch.Plural
                        ? PickPlural(branch, selector, culture)
                        : PickSelect(branch, selector);
                    if (body is not null)
                    {
                        Append(sb, body, culture, args, literal,
                            branch.Plural ? Render(selector, culture) : pound);
                    }
                    break;
            }
        }
    }

    private static Node[]? PickPlural(BranchNode branch, object? selector, CultureInfo culture)
    {
        var number = ToDecimal(selector);
        if (number is { } n)
        {
            foreach (var (key, body) in branch.Branches)
            {
                if (key.StartsWith('=')
                    && decimal.TryParse(key.AsSpan(1), NumberStyles.Number, CultureInfo.InvariantCulture, out var exact)
                    && exact == n)
                {
                    return body;
                }
            }

            var category = PluralRules.Category(culture, n);
            foreach (var (key, body) in branch.Branches)
            {
                if (key == category)
                    return body;
            }
        }

        return Other(branch);
    }

    private static Node[]? PickSelect(BranchNode branch, object? selector)
    {
        var value = Convert.ToString(selector, CultureInfo.InvariantCulture) ?? "";
        foreach (var (key, body) in branch.Branches)
        {
            if (key == value)
                return body;
        }

        return Other(branch);
    }

    private static Node[]? Other(BranchNode branch)
    {
        foreach (var (key, body) in branch.Branches)
        {
            if (key == "other")
                return body;
        }

        return null;
    }

    private static bool TryGet(IReadOnlyList<KeyValuePair<string, object?>> args, string name, out object? value)
    {
        foreach (var pair in args)
        {
            if (pair.Key == name)
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static decimal? ToDecimal(object? value) => value switch
    {
        int i => i,
        long l => l,
        double d when !double.IsNaN(d) && !double.IsInfinity(d) => (decimal)d,
        float f when !float.IsNaN(f) && !float.IsInfinity(f) => (decimal)f,
        decimal m => m,
        short s => s,
        byte b => b,
        uint u => u,
        ulong ul => ul,
        _ => null,
    };

    private static string Render(object? value, CultureInfo culture) => value switch
    {
        null => "",
        string s => s,
        int or long or short or byte or uint or ulong => ((IFormattable)value).ToString("N0", culture),
        IFormattable f => f.ToString(null, culture),
        _ => value.ToString() ?? "",
    };

    private abstract record Node;
    private sealed record TextNode(string Text) : Node;
    private sealed record PoundNode : Node;
    private sealed record ArgNode(string Name) : Node;
    private sealed record BranchNode(string Name, bool Plural, List<(string Key, Node[] Body)> Branches) : Node;

    private sealed class Parser(string text)
    {
        private int _pos;

        public bool AtEnd => _pos >= text.Length;

        public FormatException Error(string what) =>
            new($"Message format error at {_pos}: {what} in \"{text}\"");

        public Node[] ParseNodes(bool inBranch)
        {
            var nodes = new List<Node>();
            var sb = new StringBuilder();

            while (_pos < text.Length)
            {
                var c = text[_pos];
                if (c == '\'')
                {
                    var next = _pos + 1 < text.Length ? text[_pos + 1] : '\0';
                    if (next == '\'')
                    {
                        sb.Append('\'');
                        _pos += 2;
                    }
                    else if (next is '{' or '}' || (inBranch && next == '#'))
                    {
                        var close = text.IndexOf('\'', _pos + 1);
                        if (close < 0)
                            throw Error("unterminated quote");
                        sb.Append(text, _pos + 1, close - _pos - 1);
                        _pos = close + 1;
                    }
                    else
                    {
                        sb.Append('\'');
                        _pos++;
                    }
                }
                else if (c == '{')
                {
                    Flush();
                    _pos++;
                    nodes.Add(ParsePlaceholder());
                }
                else if (c == '}')
                {
                    if (!inBranch)
                        throw Error("unbalanced '}'");
                    break;
                }
                else if (c == '#' && inBranch)
                {
                    Flush();
                    nodes.Add(new PoundNode());
                    _pos++;
                }
                else
                {
                    sb.Append(c);
                    _pos++;
                }
            }

            Flush();
            return [.. nodes];

            void Flush()
            {
                if (sb.Length > 0)
                {
                    nodes.Add(new TextNode(sb.ToString()));
                    sb.Clear();
                }
            }
        }

        private Node ParsePlaceholder()
        {
            var name = ReadWord();
            if (name.Length == 0)
                throw Error("empty placeholder");
            SkipSpace();
            if (Peek() == '}')
            {
                _pos++;
                return new ArgNode(name);
            }

            Expect(',');
            var kind = ReadWord();
            if (kind is not ("plural" or "select"))
                throw Error($"unsupported placeholder type '{kind}'");
            SkipSpace();
            Expect(',');

            var branches = new List<(string, Node[])>();
            while (true)
            {
                SkipSpace();
                if (Peek() == '}')
                {
                    _pos++;
                    break;
                }

                var key = ReadWord();
                if (key.Length == 0)
                    throw Error("expected a branch key");
                SkipSpace();
                Expect('{');
                var body = ParseNodes(inBranch: true);
                Expect('}');
                branches.Add((key, body));
            }

            if (!branches.Exists(b => b.Item1 == "other"))
                throw Error($"'{name}' has no 'other' branch");
            return new BranchNode(name, kind == "plural", branches);
        }

        private string ReadWord()
        {
            SkipSpace();
            var start = _pos;
            while (_pos < text.Length && (char.IsLetterOrDigit(text[_pos]) || text[_pos] is '_' or '=' or '.' or '-'))
                _pos++;
            return text[start.._pos];
        }

        private void SkipSpace()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos]))
                _pos++;
        }

        private char Peek() => _pos < text.Length ? text[_pos] : '\0';

        private void Expect(char c)
        {
            SkipSpace();
            if (Peek() != c)
                throw Error($"expected '{c}'");
            _pos++;
        }
    }
}

/// <summary>
/// CLDR plural categories for the languages Rambutan has, or is likely to get,
/// tables for. Integers only matter here (counts of songs, scrobbles, minutes).
/// A language not listed takes English's rule, which is right for most of
/// Western Europe; add a case when a table for one that differs lands.
/// </summary>
public static class PluralRules
{
    public static string Category(CultureInfo culture, decimal n)
    {
        var language = culture.TwoLetterISOLanguageName;
        var integer = decimal.Truncate(n) == n;
        var i = (long)decimal.Truncate(decimal.Abs(n));

        switch (language)
        {
            case "ja" or "ko" or "zh" or "vi" or "th" or "id" or "ms":
                return "other";
            case "fr" or "pt" when culture.Name != "pt-PT":
                return i is 0 or 1 ? "one" : "other";
            case "ru" or "uk" when integer:
                return (i % 10, i % 100) switch
                {
                    (1, not 11) => "one",
                    ( >= 2 and <= 4, not (>= 12 and <= 14)) => "few",
                    _ => "many",
                };
            case "pl" when integer:
                return i == 1 ? "one"
                    : (i % 10 is >= 2 and <= 4 && i % 100 is not (>= 12 and <= 14)) ? "few"
                    : "many";
            default:
                return integer && i == 1 ? "one" : "other";
        }
    }
}
