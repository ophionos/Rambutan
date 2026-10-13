using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Queue;

namespace Rambutan.App.Services.Demo;

/// <summary>
/// Answers LRCLIB's <c>/api/get</c> and <c>/api/search</c> in demo mode, so the
/// real provider, chain, cache and pane run end to end with every pane state on
/// hand. The lyrics are invented for the demo (never the real songs'), timed
/// across the track's listed length:
/// <list type="bullet">
/// <item>"Give Life Back to Music", "Instant Crush", "The Game of Love", "Within":
/// synced, with Japanese, Korean, Chinese and emoji lines among them. The last
/// three are word-timed (enhanced LRC), so the karaoke sweep shows.</item>
/// <item>"Giorgio by Moroder": plain lyrics only (the unsynced state).</item>
/// <item>"Motherboard": marked instrumental.</item>
/// <item>"Touch": no record at all ("No lyrics found").</item>
/// <item>Anything else: a generic invented verse, synced.</item>
/// </list>
/// <c>RAMBUTAN_DEMO_LATENCY</c> delays each answer and <c>RAMBUTAN_DEMO_FAIL=lyrics</c>
/// (or <c>all</c>) fails it as if offline, which shows the "couldn't reach" state.
/// </summary>
public sealed class DemoLyricsHandler(DemoOptions options, QueueModel? queue = null) : HttpMessageHandler
{
    public const string Area = "lyrics";

    private static readonly TimeSpan FallbackDuration = TimeSpan.FromMinutes(3);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri ?? throw new ArgumentException("No request URI.", nameof(request));
        var endpoint = uri.AbsolutePath.TrimEnd('/');
        endpoint = endpoint[(endpoint.LastIndexOf('/') + 1)..];
        var query = ParseQuery(uri.Query);
        var title = query.GetValueOrDefault("track_name", "");
        var artist = query.GetValueOrDefault("artist_name", "");
        AppLog.Write("demo", $"lrclib/{endpoint} → {Area} ({title})");

        await Task.Delay(options.Latency, ct);

        if (options.Fails(Area))
            throw new HttpRequestException($"Demo: '{Area}' is set to fail (RAMBUTAN_DEMO_FAIL).");

        var duration = query.TryGetValue("duration", out var seconds) &&
                       double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s)
            ? TimeSpan.FromSeconds(s)
            : DurationOf(title);

        var record = Record(title, artist, query.GetValueOrDefault("album_name"), duration);
        return endpoint switch
        {
            "get" when record is null => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("""{"code":404,"name":"TrackNotFound","message":"Failed to find specified track"}"""),
            },
            "get" => Json(record!),
            "search" => Json(record is null ? "[]" : "[" + record + "]"),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    /// <summary>The track's length as the queue lists it, since a search request
    /// doesn't carry one and the provider rejects a record of the wrong length.</summary>
    private TimeSpan DurationOf(string title) =>
        queue?.Items.FirstOrDefault(t => string.Equals(t.Title, title, StringComparison.OrdinalIgnoreCase))?.Duration
        ?? FallbackDuration;

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    /// <summary>One LRCLIB record as JSON, or null for the track that has none.</summary>
    private static string? Record(string title, string artist, string? album, TimeSpan duration)
    {
        var key = title.Trim().ToLowerInvariant();
        if (key.StartsWith("touch", StringComparison.Ordinal))
            return null;

        string? synced = null, plain = null;
        var instrumental = false;
        if (key.StartsWith("motherboard", StringComparison.Ordinal))
        {
            instrumental = true;
        }
        else if (key.StartsWith("giorgio", StringComparison.Ordinal))
        {
            plain = string.Join('\n', Plain);
        }
        else
        {
            var verse = key switch
            {
                _ when key.StartsWith("give life", StringComparison.Ordinal) => GiveLife,
                _ when key.StartsWith("instant crush", StringComparison.Ordinal) => Crush,
                _ when key.StartsWith("the game of love", StringComparison.Ordinal) => GameOfLove,
                _ when key.StartsWith("within", StringComparison.Ordinal) => Within,
                _ => Generic,
            };
            var wordTimed = verse != GiveLife && verse != Generic;
            synced = Timed(verse, duration, wordTimed);
            plain = string.Join('\n', verse);
        }

        // Written by hand rather than through a serializer context: a record is
        // eight fields, in the shape LRCLIB documents and the provider parses.
        using var buffer = new System.IO.MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("id", (long)(uint)key.GetHashCode(StringComparison.Ordinal) % 1_000_000);
            json.WriteString("trackName", title);
            json.WriteString("artistName", artist);
            json.WriteString("albumName", album);
            json.WriteNumber("duration", Math.Round(duration.TotalSeconds));
            json.WriteBoolean("instrumental", instrumental);
            json.WriteString("plainLyrics", plain);
            json.WriteString("syncedLyrics", synced);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Stamps the verse from four seconds in, a line every four and a half seconds
    /// so a demo shows the active line moving without waiting, repeating it to
    /// fill the track. A blank line between passes is a stamped instrumental gap,
    /// as real files have.
    /// </summary>
    private static string Timed(string[] verse, TimeSpan duration, bool wordTimed)
    {
        var lrc = new StringBuilder();
        var at = TimeSpan.FromSeconds(4);
        var step = TimeSpan.FromSeconds(4.5);
        var end = duration - TimeSpan.FromSeconds(4);
        for (var pass = 0; at < end && pass < 8; pass++)
        {
            foreach (var line in verse)
            {
                if (at >= end)
                    break;
                lrc.Append(CultureInfo.InvariantCulture, $"[{Stamp(at)}]{(wordTimed ? Words(line, at) : line)}\n");
                at += step;
            }

            lrc.Append(CultureInfo.InvariantCulture, $"[{Stamp(at)}]\n");
            at += step * 2;
        }

        return lrc.ToString();
    }

    private static string Stamp(TimeSpan at) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)at.TotalMinutes:00}:{at.Seconds:00}.{at.Milliseconds / 10:00}");

    /// <summary>
    /// Enhanced-LRC word stamps across the first four seconds of the line: one
    /// per space-separated word, and one per character in CJK text, which has no
    /// spaces between words. Each word's share of the time follows its length.
    /// </summary>
    private static string Words(string line, TimeSpan start)
    {
        var tokens = new List<string>();
        foreach (var word in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (word.Any(IsCjk))
                tokens.AddRange(word.Select(c => c.ToString()));
            else
                tokens.Add(word);
            tokens[^1] += " ";
        }

        tokens[^1] = tokens[^1].TrimEnd();
        var weights = tokens.Select(t => Math.Max(1, t.Trim().Length)).ToArray();
        var perUnit = TimeSpan.FromSeconds(4) / weights.Sum();
        var text = new StringBuilder();
        var at = start;
        for (var i = 0; i < tokens.Count; i++)
        {
            text.Append('<').Append(Stamp(at)).Append('>').Append(tokens[i]);
            at += perUnit * weights[i];
        }

        return text.ToString();
    }

    private static bool IsCjk(char c) =>
        c is (>= '぀' and <= 'ヿ') or (>= '一' and <= '鿿') or (>= '가' and <= '힯');

    private static Dictionary<string, string> ParseQuery(string query) =>
        query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .ToDictionary(
                p => Uri.UnescapeDataString(p[0]),
                p => p.Length > 1 ? Uri.UnescapeDataString(p[1].Replace('+', ' ')) : "",
                StringComparer.Ordinal);

    // Invented for the demo. Mixed scripts and emoji on purpose: they are what the
    // font fallbacks have to get right.
    private static readonly string[] GiveLife =
    [
        "Turn the lights down low, let the speakers glow",
        "Every wire is humming something that we know",
        "Hands up to the ceiling 🙌",
        "Floor is made of rhythm, walls are made of sound",
        "Play it back again ✨",
        "We were never lost, just waiting to be found",
    ];

    private static readonly string[] Crush =
    [
        "Streetlights blinking out of time",
        "夜の街に 光が揺れる",
        "I kept your number, never dialled a line",
        "기억 속의 너를 찾아",
        "Static on the radio 📻",
        "心跳还在 等你回答",
        "Say it once and let it go 💫",
    ];

    private static readonly string[] GameOfLove =
    [
        "Slow turn, the record's spinning low",
        "あなたの声が まだ聞こえる",
        "Every move I make, you already know",
        "雨の音と ふたりのリズム ☔",
        "Hold the note a little longer",
        "終わらない歌を 歌おう",
    ];

    private static readonly string[] Within =
    [
        "Quiet rooms and open doors",
        "창문 너머 별이 빛나",
        "Pages turning on the floor",
        "我在这里 慢慢地走",
        "Who I am is who I was before 🌙",
    ];

    private static readonly string[] Generic =
    [
        "Bassline rolling through the night",
        "Keep it steady, keep it bright",
        "Another verse, another light",
        "The chorus comes around again",
        "Sing it louder than we did back then 🎶",
    ];

    private static readonly string[] Plain =
    [
        "A voice remembers where it started",
        "A small room, a big machine",
        "Everyone said it couldn't sound like that",
        "",
        "So we let the circuits sing",
        "And the click became the beat",
        "And the future found its feet",
        "",
        "(no timing in this one: the demo's unsynced state)",
    ];
}
