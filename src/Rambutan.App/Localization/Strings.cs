using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Localization;

public sealed record LanguageOption(string Code, string NativeName);

/// <summary>
/// The app's localizer Every user-visible string comes from here: XAML
/// through <see cref="TExtension"/> (<c>{l:T settings.title}</c>), code through
/// <see cref="Get"/> and <see cref="Format"/>.
/// <para>
/// Tables are versioned JSON embedded in the assembly (<c>Locales/*.json</c>,
/// see <see cref="StringTable"/>). English is the source and the fallback: a key
/// missing from the active table falls back to English, and one missing from
/// English too renders as the key itself. Each miss is logged once.
/// </para>
/// <para>
/// Static because strings are needed where no container reaches (controls,
/// converters, value formatters), like <c>AppLog</c>. It starts in English, which
/// is what tests see; the app calls <see cref="SetLanguage"/> at startup.
/// </para>
/// </summary>
public static class Strings
{
    public const string SourceLocale = "en";

    /// <summary>The generated pseudo-locale: accented, 35% longer and bracketed,
    /// to show truncation and strings that skipped the localizer. A dev tool:
    /// offered in Settings only in debug and demo builds, or via
    /// <c>RAMBUTAN_LOCALE=qps</c>.</summary>
    public const string PseudoLocale = "qps";

    private const string ResourcePrefix = "Rambutan.App.Locales.";

    private static readonly Lazy<Dictionary<string, StringTable>> Tables = new(LoadTables);
    private static readonly ConcurrentDictionary<string, MessageFormat?> Parsed = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, byte> Reported = new(StringComparer.Ordinal);

    private static StringTable? _active;
    private static bool _pseudo;

    /// <summary>Raised on the thread that changed the language, after the change.
    /// Holds its handlers strongly: for long-lived code. View models use
    /// <see cref="WhenChanged{T}"/>.</summary>
    public static event Action? Changed;

    private static readonly List<(WeakReference<object> Owner, Action<object> Refresh)> Owners = [];

    /// <summary>
    /// Calls <paramref name="refresh"/> with <paramref name="owner"/> after each
    /// language change, for as long as the owner is alive: text a view model
    /// computed in code is re-read, the way <see cref="TExtension"/> re-reads XAML.
    /// Pass a <c>static</c> lambda (<c>static vm => vm.Refresh()</c>); one that
    /// captured the owner would keep it alive forever.
    /// </summary>
    public static void WhenChanged<T>(T owner, Action<T> refresh) where T : class
    {
        lock (Owners)
        {
            Owners.RemoveAll(o => !o.Owner.TryGetTarget(out _));
            Owners.Add((new WeakReference<object>(owner), o => refresh((T)o)));
        }
    }

    /// <summary>Whether Settings offers the pseudo-locale: debug builds, demo
    /// mode, or a session started with <c>RAMBUTAN_LOCALE=qps</c>.</summary>
    public static bool OfferPseudo { get; set; }

    /// <summary>The active language's code ("en", "qps", …).</summary>
    public static string Language { get; private set; } = SourceLocale;

    /// <summary>Culture for numbers in messages and for plural rules.</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("en");

    /// <summary>Incremented on every change; lets bindings re-read.</summary>
    public static int Version { get; private set; }

    /// <summary>Languages with a table, English first. The pseudo-locale is
    /// included only when <paramref name="includePseudo"/>.</summary>
    public static IReadOnlyList<LanguageOption> Available(bool includePseudo)
    {
        var list = Tables.Value.Values
            .OrderBy(t => t.Locale == SourceLocale ? 0 : 1)
            .ThenBy(t => t.NativeName, StringComparer.CurrentCulture)
            .Select(t => new LanguageOption(t.Locale, t.NativeName))
            .ToList();
        if (includePseudo)
            list.Add(new LanguageOption(PseudoLocale, "Ƥšéûðö (qps)"));
        return list;
    }

    /// <summary>
    /// Picks the language: <paramref name="code"/> if a table exists for it (or a
    /// parent: "pt-BR" → "pt"), else the OS UI language the same way, else English.
    /// Returns the code chosen. Call on the UI thread: live bindings re-render
    /// synchronously.
    /// </summary>
    public static string SetLanguage(string? code)
    {
        var chosen = Resolve(code) ?? Resolve(CultureInfo.CurrentUICulture.Name) ?? SourceLocale;
        if (chosen == Language && Version > 0)
            return chosen;

        _pseudo = chosen == PseudoLocale;
        _active = _pseudo ? null : Tables.Value.GetValueOrDefault(chosen);
        Language = chosen;
        Culture = _pseudo ? CultureInfo.GetCultureInfo("en") : SafeCulture(chosen);
        Parsed.Clear();
        Version++;
        AppLog.Write("i18n", $"Language: {chosen} (asked for {code ?? "system"}, OS {CultureInfo.CurrentUICulture.Name})");
        Changed?.Invoke();

        List<(object Owner, Action<object> Refresh)> live;
        lock (Owners)
        {
            Owners.RemoveAll(o => !o.Owner.TryGetTarget(out _));
            live = [];
            foreach (var (owner, refresh) in Owners)
            {
                if (owner.TryGetTarget(out var target))
                    live.Add((target, refresh));
            }
        }

        foreach (var (owner, refresh) in live)
            refresh(owner);
        return chosen;
    }

    /// <summary>The string for <paramref name="key"/>, arguments unfilled.</summary>
    public static string Get(string key) => FormatCore(key, []);

    /// <summary>The string for <paramref name="key"/> with named arguments filled:
    /// <c>Strings.Format("settings.lastfm.pending", ("count", 3))</c>.</summary>
    public static string Format(string key, params (string Name, object? Value)[] args)
    {
        var list = new KeyValuePair<string, object?>[args.Length];
        for (var i = 0; i < args.Length; i++)
            list[i] = new(args[i].Name, args[i].Value);
        return FormatCore(key, list);
    }

    internal static string FormatCore(string key, IReadOnlyList<KeyValuePair<string, object?>> args)
    {
        var message = Parsed.GetOrAdd(key, Compile);
        if (message is null)
            return key;

        var text = message.Format(Culture, args, _pseudo ? Pseudo.Accent : null);
        return _pseudo ? Pseudo.Pad(text) : text;
    }

    /// <summary>The English text of every key, for tests.</summary>
    internal static IReadOnlyDictionary<string, string> Source => Tables.Value[SourceLocale].Strings;

    internal static IReadOnlyCollection<StringTable> AllTables => Tables.Value.Values;

    /// <summary>Adds a table until disposed, for tests of fallback between tables.</summary>
    internal static IDisposable UseTableForTests(StringTable table)
    {
        Tables.Value[table.Locale] = table;
        return new Removal(table.Locale);
    }

    private sealed class Removal(string locale) : IDisposable
    {
        public void Dispose()
        {
            Tables.Value.Remove(locale);
            SetLanguage(SourceLocale);
        }
    }

    private static MessageFormat? Compile(string key)
    {
        string? pattern = null;
        if (_active is not null && _active.Strings.TryGetValue(key, out var translated))
        {
            pattern = translated;
        }
        else if (Tables.Value.TryGetValue(SourceLocale, out var english)
                 && english.Strings.TryGetValue(key, out var source))
        {
            pattern = source;
            if (_active is not null && _active.Locale != SourceLocale)
                ReportOnce($"{_active.Locale}:{key}", $"'{key}' is missing from {_active.Locale}; using English");
        }
        else
        {
            ReportOnce(key, $"'{key}' is missing from every table; showing the key");
            return null;
        }

        try
        {
            return MessageFormat.Parse(pattern);
        }
        catch (FormatException ex)
        {
            ReportOnce("format:" + key, ex.Message);
            return MessageFormat.Parse("'{'" + key + "'}'");
        }
    }

    private static void ReportOnce(string id, string message)
    {
        if (Reported.TryAdd(id, 0))
            AppLog.Write("i18n", message);
    }

    private static string? Resolve(string? code)
    {
        if (string.IsNullOrWhiteSpace(code) || code == "system")
            return null;
        if (code == PseudoLocale)
            return PseudoLocale;

        var tables = Tables.Value;
        for (var name = code; !string.IsNullOrEmpty(name);)
        {
            var match = tables.Keys.FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
            var dash = name.LastIndexOf('-');
            name = dash > 0 ? name[..dash] : null;
        }

        return null;
    }

    private static CultureInfo SafeCulture(string code)
    {
        try
        {
            return CultureInfo.GetCultureInfo(code);
        }
        catch (CultureNotFoundException)
        {
            // Invariant-globalization builds know no cultures; English rules then.
            return CultureInfo.InvariantCulture;
        }
    }

    private static Dictionary<string, StringTable> LoadTables()
    {
        var tables = new Dictionary<string, StringTable>(StringComparer.Ordinal);
        var assembly = typeof(Strings).Assembly;
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            if (!resource.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                || !resource.EndsWith(".json", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource)!;
                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                var table = StringTable.Parse(buffer.ToArray());
                tables[table.Locale] = table;
            }
            catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException)
            {
                // A broken translation must not take English down with it.
                AppLog.Write("i18n", $"Skipped {resource}: {ex.Message}");
            }
        }

        if (!tables.ContainsKey(SourceLocale))
            throw new InvalidOperationException("The English string table is missing from the build.");
        return tables;
    }

    /// <summary>The pseudo-locale's transforms.</summary>
    internal static class Pseudo
    {
        private const string From = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
        private const string To = "àƀçðéƒĝĥîĵķļɱñöþǫŕšţûṽŵẋýžÅƁÇÐÉƑĜĤÎĴĶĻṀÑÖÞǪŔŠŢÛṼŴẊÝŽ";

        public static string Accent(string text)
        {
            var sb = new StringBuilder(text.Length);
            foreach (var c in text)
            {
                var i = From.IndexOf(c);
                sb.Append(i >= 0 ? To[i] : c);
            }

            return sb.ToString();
        }

        /// <summary>About 35% longer (translations into German or French often
        /// run 30–40% over English), bracketed so a clipped end shows.</summary>
        public static string Pad(string text)
        {
            if (text.Length == 0)
                return text;
            var extra = Math.Max(1, (int)Math.Ceiling(text.Length * 0.35));
            var padding = new StringBuilder(extra);
            const string filler = "·ŵîðé ţéẋţ";
            for (var i = 0; i < extra; i++)
                padding.Append(filler[i % filler.Length]);
            return $"[{text} {padding}]";
        }
    }
}
