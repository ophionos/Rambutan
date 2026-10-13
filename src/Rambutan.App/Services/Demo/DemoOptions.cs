using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Demo mode: the whole app running on recorded InnerTube responses, generated
/// audio and placeholder artwork, with no network. It exists so the UI can be
/// driven and screenshotted where YouTube Music is unreachable (cloud sessions,
/// CI, a contributor without an account), and so loading and failure states can be
/// produced on demand instead of waited for.
///
/// Everything above the network is real: InnerTube parsers, playback coordinator,
/// audio engine, lyrics provider chain, view models. Only the edges are stand-ins:
/// the InnerTube and LRCLIB HTTP handlers, the stream source and the artwork download. Demo runs keep their
/// own data directory, so they never touch the real settings, cookies or caches.
///
/// Environment:
/// <list type="bullet">
/// <item><c>RAMBUTAN_DEMO</c>: <c>1</c> (signed in as a demo account) or
/// <c>anonymous</c>. Unset = normal app.</item>
/// <item><c>RAMBUTAN_DEMO_FIXTURES</c>: fixture directory. Default: found by walking
/// up from the app and working directories to
/// <c>tests/Rambutan.InnerTube.Tests/Fixtures</c>.</item>
/// <item><c>RAMBUTAN_DEMO_LATENCY</c>: milliseconds added to every catalog and lyrics
/// response (default 300, about a real round trip; long values show skeletons).</item>
/// <item><c>RAMBUTAN_DEMO_FAIL</c>: comma-separated areas that fail as if offline:
/// <c>home, moods, search, album, artist, playlist, library, radio, account, mutations, lyrics, lastfm, downloads,
/// artwork</c> (no cover loads except a download's kept one), <c>streams</c> (a track that isn't downloaded fails to play as offline),
/// or <c>all</c>. <c>home-more</c>, <c>playlist-more</c>, <c>library-more</c> and <c>search-more</c>
/// fail only the next page, after the first loaded. <c>session</c> (named only, not part of <c>all</c>)
/// refuses every signed-in request with 401, as an expired session does; cookies
/// whose SAPISID is <c>expired</c> are always refused. <c>network-drop</c> (named only)
/// drops the network 20 s into each track's first play, for 30 s
/// (<see cref="DemoNetworkDrop"/>). <c>lastfm-unavailable</c> (named only) answers
/// Last.fm's station requests as it does for a profile it has no station for
/// (<see cref="DemoLastFmStationHandler"/>).</item>
/// </list>
/// </summary>
public sealed record DemoOptions(
    bool SignedIn,
    string FixturesDirectory,
    TimeSpan Latency,
    IReadOnlySet<string> FailingAreas)
{
    public const string AccountName = "Demo Listener";

    /// <summary>The Last.fm user a demo's station shelves ask for; a demo has no
    /// Last.fm connection, so the stations setting alone turns them on.</summary>
    public const string LastFmUser = "demo-listener";

    /// <summary>The demo options, or null when demo mode is off. Throws when demo
    /// mode is on but the fixtures can't be found: a demo that silently fell back
    /// to the network would defeat its purpose.</summary>
    public static DemoOptions? FromEnvironment()
    {
        var mode = Environment.GetEnvironmentVariable("RAMBUTAN_DEMO");
        if (string.IsNullOrWhiteSpace(mode) || mode is "0")
            return null;

        var fixtures = Environment.GetEnvironmentVariable("RAMBUTAN_DEMO_FIXTURES")
            is { Length: > 0 } explicitDir ? explicitDir : FindFixtures();
        if (fixtures is null || !Directory.Exists(fixtures))
        {
            throw new DirectoryNotFoundException(
                "RAMBUTAN_DEMO is set but the recorded fixtures weren't found. Run from the " +
                "repository, or point RAMBUTAN_DEMO_FIXTURES at tests/Rambutan.InnerTube.Tests/Fixtures.");
        }

        var latency = int.TryParse(
            Environment.GetEnvironmentVariable("RAMBUTAN_DEMO_LATENCY"), out var ms) ? ms : 300;

        var failing = (Environment.GetEnvironmentVariable("RAMBUTAN_DEMO_FAIL") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(a => a.ToLowerInvariant())
            .ToHashSet();

        return new DemoOptions(
            SignedIn: !mode.Equals("anonymous", StringComparison.OrdinalIgnoreCase),
            fixtures,
            TimeSpan.FromMilliseconds(Math.Max(0, latency)),
            failing);
    }

    /// <summary>"playlist" also fails "playlist-more" (its continuations); naming
    /// "playlist-more" alone fails only the continuation, which is how a failure
    /// beside content that already loaded is produced.</summary>
    public bool Fails(string area) =>
        FailingAreas.Contains("all") || FailingAreas.Contains(area) ||
        (area.IndexOf('-') is > 0 and var dash && FailingAreas.Contains(area[..dash]));

    private static string? FindFixtures()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName, "tests", "Rambutan.InnerTube.Tests", "Fixtures");
                if (Directory.Exists(candidate))
                    return candidate;
            }
        }
        return null;
    }
}
