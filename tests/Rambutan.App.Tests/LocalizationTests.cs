using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using Rambutan.App.Localization;
using Rambutan.App.ViewModels;
using Rambutan.InnerTube;

namespace Rambutan.App.Tests;

/// <summary>The message syntax the string tables use.</summary>
public class MessageFormatTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en");

    private static string Format(string pattern, params (string, object?)[] args) =>
        MessageFormat.Parse(pattern).Format(En, [.. args.Select(a => new KeyValuePair<string, object?>(a.Item1, a.Item2))]);

    [Fact]
    public void Arguments_FillByName_AndNumbersUseTheCulture()
    {
        Assert.Equal("Added “Get Lucky” to Mix", Format("Added “{title}” to {playlist}", ("title", "Get Lucky"), ("playlist", "Mix")));
        Assert.Equal("1,234 songs", Format("{n} songs", ("n", 1234)));
    }

    [Theory]
    [InlineData(0, "No scrobbles")]
    [InlineData(1, "1 scrobble is waiting")]
    [InlineData(3, "3 scrobbles are waiting")]
    public void Plural_PrefersAnExactMatch_ThenTheCategory(int count, string expected) =>
        Assert.Equal(expected, Format(
            "{count, plural, =0 {No scrobbles} one {# scrobble is waiting} other {# scrobbles are waiting}}",
            ("count", count)));

    [Fact]
    public void Select_PicksTheBranch_OrOther()
    {
        const string pattern = "Couldn't load {what, select, home {Home} album {this album} other {it}}";
        Assert.Equal("Couldn't load this album", Format(pattern, ("what", "album")));
        Assert.Equal("Couldn't load it", Format(pattern, ("what", "anything")));
    }

    [Fact]
    public void Apostrophes_AreLiteral_UnlessTheyQuoteSyntax()
    {
        Assert.Equal("You're up to date.", Format("You're up to date."));
        Assert.Equal("a {brace} and it's", Format("a '{brace}' and it''s"));
    }

    [Fact]
    public void AMissingArgument_ShowsItsPlaceholder_RatherThanThrowing() =>
        Assert.Equal("Signed in as {name}", Format("Signed in as {name}"));

    [Theory]
    [InlineData("{count, plural, one {x}}")]            // no other
    [InlineData("Unbalanced }")]
    [InlineData("{when, date}")]                         // unsupported type
    [InlineData("{}")]
    public void MalformedPatterns_Throw(string pattern) =>
        Assert.Throws<FormatException>(() => MessageFormat.Parse(pattern));

    [Theory]
    [InlineData("en", 1, "one")]
    [InlineData("en", 0, "other")]
    [InlineData("fr", 0, "one")]
    [InlineData("ja", 1, "other")]
    [InlineData("ru", 21, "one")]
    [InlineData("ru", 3, "few")]
    [InlineData("ru", 11, "many")]
    [InlineData("pl", 22, "few")]
    [InlineData("pl", 25, "many")]
    public void PluralRules_FollowCldr(string language, int n, string category) =>
        Assert.Equal(category, PluralRules.Category(CultureInfo.GetCultureInfo(language), n));
}

/// <summary>The table format (versioned JSON, flat dotted keys).</summary>
public class StringTableTests
{
    private static StringTable Parse(string json) => StringTable.Parse(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void ReadsAVersionedTable()
    {
        var table = Parse("""{ "version": 1, "locale": "de", "name": "Deutsch", "strings": { "nav.home": "Start" } }""");
        Assert.Equal("de", table.Locale);
        Assert.Equal("Deutsch", table.NativeName);
        Assert.Equal("Start", table.Strings["nav.home"]);
    }

    [Theory]
    [InlineData("""{ "locale": "de", "strings": {} }""")]                                         // no version
    [InlineData("""{ "version": 2, "locale": "de", "strings": {} }""")]                           // newer than this build
    [InlineData("""{ "version": 1, "strings": {} }""")]                                           // no locale
    [InlineData("""{ "version": 1, "locale": "de", "strings": { "nav": { "home": "x" } } }""")]  // nested
    [InlineData("""{ "version": 1, "locale": "de", "strings": { "a.b": 1 } }""")]                // not a string
    [InlineData("""{ "version": 1, "locale": "de", "strings": { "a.b": "x", "a.b": "y" } }""")]  // duplicate
    public void RefusesWhatTheFormatDoesNotAllow(string json) =>
        Assert.Throws<InvalidDataException>(() => Parse(json));
}

/// <summary>
/// The shipped tables against the source: every key the app uses exists in
/// English, every English key is used, every string parses, and no view carries
/// a hard-coded user-visible literal.
/// </summary>
public partial class StringCoverageTests
{
    private static readonly string AppDir = Path.Combine(RepoRoot(), "src", "Rambutan.App");

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Rambutan.slnx")))
                return dir.FullName;
        }

        throw new InvalidOperationException("Can't find the repository root from " + AppContext.BaseDirectory);
    }

    /// <summary>The taskbar toolbar's rules choose its tips, as keys of the App's table.</summary>
    private static readonly string TaskbarDir = Path.Combine(RepoRoot(), "src", "Rambutan.Integrations", "Taskbar");

    private static IEnumerable<string> SourceFiles(string pattern) =>
        Directory.EnumerateFiles(AppDir, pattern, SearchOption.AllDirectories)
            .Concat(pattern == "*.cs" ? Directory.EnumerateFiles(TaskbarDir, pattern) : [])
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    [GeneratedRegex(@"\{l:T\s+(?:Key=)?([A-Za-z0-9.]+)")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"""([a-z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""")]
    private static partial Regex CodeKeyLiteral();

    // File names that happen to look like keys ("queue.json").
    private static readonly string[] FileExtensions = ["json", "log", "bin", "dll"];

    /// <summary>Keys the app refers to: every <c>{l:T key}</c> in XAML, and every
    /// dotted literal in C# whose first segment is one of the table's groups (so
    /// keys chosen by a ternary count too).</summary>
    private static SortedSet<string> UsedKeys()
    {
        var groups = Strings.Source.Keys.Select(k => k[..k.IndexOf('.')]).ToHashSet();
        var used = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles("*.axaml"))
        {
            foreach (Match m in XamlKey().Matches(File.ReadAllText(file)))
                used.Add(m.Groups[1].Value);
        }

        foreach (var file in SourceFiles("*.cs"))
        {
            // The localizer's own doc comments quote example keys.
            if (file.Contains($"{Path.DirectorySeparatorChar}Localization{Path.DirectorySeparatorChar}"))
                continue;
            foreach (Match m in CodeKeyLiteral().Matches(File.ReadAllText(file)))
            {
                var key = m.Groups[1].Value;
                if (groups.Contains(key[..key.IndexOf('.')]) && !FileExtensions.Contains(key[(key.LastIndexOf('.') + 1)..]))
                    used.Add(key);
            }
        }

        return used;
    }

    [Fact]
    public void EveryUsedKey_IsInTheEnglishTable()
    {
        var missing = UsedKeys().Where(k => !Strings.Source.ContainsKey(k)).ToList();
        Assert.True(missing.Count == 0, "Missing from Locales/en.json: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryEnglishKey_IsUsed()
    {
        var used = UsedKeys();
        var unused = Strings.Source.Keys.Where(k => !used.Contains(k)).Order(StringComparer.Ordinal).ToList();
        Assert.True(unused.Count == 0, "Unused keys in Locales/en.json: " + string.Join(", ", unused));
    }

    [Fact]
    public void EveryString_Parses_AndTranslationsKeepTheArguments()
    {
        var english = Strings.AllTables.Single(t => t.Locale == Strings.SourceLocale);
        var problems = new List<string>();
        foreach (var table in Strings.AllTables)
        {
            foreach (var (key, pattern) in table.Strings)
            {
                try
                {
                    var args = MessageFormat.Parse(pattern).ArgumentNames;
                    if (english.Strings.TryGetValue(key, out var source)
                        && !args.SequenceEqual(MessageFormat.Parse(source).ArgumentNames))
                    {
                        problems.Add($"{table.Locale}:{key} has arguments {string.Join(",", args)}");
                    }
                    else if (!english.Strings.ContainsKey(key))
                    {
                        problems.Add($"{table.Locale}:{key} is not in English");
                    }
                }
                catch (FormatException ex)
                {
                    problems.Add($"{table.Locale}:{key}: {ex.Message}");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }

    [GeneratedRegex(@"\b(Text|Content|ToolTip\.Tip|AutomationProperties\.Name|AutomationProperties\.HelpText|PlaceholderText|Watermark|Title|Header)=""([^""{][^""]*)""")]
    private static partial Regex XamlLiteral();

    [GeneratedRegex(@"StringFormat=('[^']*[A-Za-z][^']*'|[^,}]*[A-Za-z]{2,}[^,}]*)")]
    private static partial Regex XamlStringFormat();

    /// <summary>Literals that stay put, with the reason. Keep it short.</summary>
    private static readonly HashSet<string> Allowlist =
    [
        // The product name, which no translation changes.
        "MainWindow.axaml|Title|Rambutan",
        "MainWindow.axaml|Text|Rambutan",
        // What to type into the browser's Network filter, whatever the language.
        "SignInDialog.axaml|Text|browse",
    ];

    [Fact]
    public void Views_HaveNoHardCodedText()
    {
        var found = new List<string>();
        foreach (var file in SourceFiles("*.axaml"))
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                foreach (Match m in XamlLiteral().Matches(lines[i]))
                {
                    var value = m.Groups[2].Value;
                    if (!value.Any(char.IsLetter) || Allowlist.Contains($"{name}|{m.Groups[1].Value}|{value}"))
                        continue;
                    found.Add($"{name}:{i + 1} {m.Value}");
                }

                foreach (Match m in XamlStringFormat().Matches(lines[i]))
                    found.Add($"{name}:{i + 1} {m.Value}");
            }
        }

        Assert.True(found.Count == 0, "Use {l:T key} instead of literal text:\n" + string.Join("\n", found));
    }

    [Fact]
    public void AnUnknownKey_ShowsTheKey() =>
        Assert.Equal("no.such.key", Strings.Get("no.such.key"));

    [Fact]
    public void EnglishWording_IsWhatTheViewsUsedToSay()
    {
        Assert.Equal("13 songs", Strings.Format("duration.songs", ("count", 13)));
        Assert.Equal("1 song", Strings.Format("duration.songs", ("count", 1)));
        Assert.Equal("1 scrobble is waiting to be sent.", Strings.Format("settings.lastfm.pending", ("count", 1)));
        Assert.Equal("3 scrobbles are waiting to be sent. Disconnecting discards them.",
            Strings.Format("settings.lastfm.pendingConnected", ("count", 3)));
        Assert.Equal("Couldn't load this album", Strings.Format("error.load.other.title", ("what", "album")));
    }
}

/// <summary>Tests that switch the language: global state, so they run alone,
/// after the parallel tests that expect English, and on the UI thread, where the
/// app switches it (live bindings from other tests' controls re-render).</summary>
[CollectionDefinition(nameof(LanguageSwitch), DisableParallelization = true)]
public sealed class LanguageSwitch;

[Collection(nameof(LanguageSwitch))]
public class LanguageSwitchTests
{
    [Fact]
    public void AnUnknownLanguage_FallsBackToEnglish() => Headless.Run(() =>
    {
        try
        {
            Assert.Equal("en", Strings.SetLanguage("tlh-XX"));
            Assert.Equal("Play all", Strings.Get("common.playAll"));
        }
        finally
        {
            Strings.SetLanguage("en");
        }
    });

    [Fact]
    public void AKeyMissingFromATranslation_FallsBackToEnglish() => Headless.Run(() =>
    {
        var german = StringTable.Parse(Encoding.UTF8.GetBytes(
            """{ "version": 1, "locale": "de", "name": "Deutsch", "strings": { "common.playAll": "Alle abspielen" } }"""));
        using (Strings.UseTableForTests(german))
        {
            Assert.Equal("de", Strings.SetLanguage("de-AT"));
            Assert.Equal("Alle abspielen", Strings.Get("common.playAll"));
            Assert.Equal("Shuffle", Strings.Get("common.shuffle"));
            Assert.Contains(Strings.Available(includePseudo: false), l => l.Code == "de" && l.NativeName == "Deutsch");
        }

        Assert.Equal("en", Strings.Language);
    });

    [Fact]
    public void PseudoLocale_AccentsAndLengthens_ButNotArguments() => Headless.Run(() =>
    {
        try
        {
            Strings.SetLanguage(Strings.PseudoLocale);
            var play = Strings.Get("common.playAll");
            Assert.StartsWith("[Þļàý àļļ", play);
            Assert.EndsWith("]", play);
            Assert.True(play.Length >= "Play all".Length * 1.3, play);

            var added = Strings.Format("toast.track.addedToQueue", ("title", "Get Lucky"));
            Assert.Contains("“Get Lucky”", added);
            Assert.Contains(Strings.Available(includePseudo: true), l => l.Code == Strings.PseudoLocale);
            Assert.DoesNotContain(Strings.Available(includePseudo: false), l => l.Code == Strings.PseudoLocale);
        }
        finally
        {
            Strings.SetLanguage("en");
        }
    });

    [Fact]
    public void XamlStrings_FollowALanguageChange() => Headless.Run(() =>
    {
        var text = new TextBlock();
        text.Bind(TextBlock.TextProperty, (BindingBase)new TExtension("common.playAll").ProvideValue(null!));
        var window = new Window { Content = text };
        window.Show();
        try
        {
            Assert.Equal("Play all", text.Text);
            Strings.SetLanguage(Strings.PseudoLocale);
            Assert.StartsWith("[Þļàý", text.Text);
        }
        finally
        {
            Strings.SetLanguage("en");
            window.Close();
        }

        Assert.Equal("Play all", text.Text);
    });

    [Fact]
    public void SessionText_FollowsALanguageChange() => Headless.Run(() =>
    {
        using var client = new InnerTubeClient(new HttpClient());
        var session = new SessionViewModel(client, null, new ToastHostViewModel(new ManualClock().Schedule), () => { });
        try
        {
            Assert.Equal("Not signed in", session.AccountLabel);
            Strings.SetLanguage(Strings.PseudoLocale);
            Assert.StartsWith("[", session.AccountLabel);
            Assert.StartsWith("[", session.StatusDetail);
        }
        finally
        {
            Strings.SetLanguage("en");
        }

        Assert.Equal("Not signed in", session.AccountLabel);
    });

    private sealed class Owner
    {
        public int Refreshed;
    }

    [Fact]
    public void WhenChanged_CallsLiveOwners() => Headless.Run(() =>
    {
        var owner = new Owner();
        Strings.WhenChanged(owner, static o => o.Refreshed++);
        try
        {
            Strings.SetLanguage(Strings.PseudoLocale);
            Assert.Equal(1, owner.Refreshed);
        }
        finally
        {
            Strings.SetLanguage("en");
        }

        Assert.Equal(2, owner.Refreshed);
    });
}
