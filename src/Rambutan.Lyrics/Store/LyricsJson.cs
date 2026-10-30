using System.Text.Json.Serialization;
using Rambutan.Core.Local;
using Rambutan.Lyrics.Models;

namespace Rambutan.Lyrics.Store;

// On-disk shapes for the lyrics cache and the custom-lyrics store. They are this
// library's own DTOs, not the model records, so a model change can't silently
// change what's on disk (the same rule QueueStore follows). Times are integer
// milliseconds.

internal sealed class CachedLyricsFile
{
    public int Version { get; set; }
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>"found", "instrumental" or "notFound".</summary>
    public string? Status { get; set; }

    public CachedDocument? Document { get; set; }
}

internal sealed class CachedDocument
{
    public string? Source { get; set; }
    public bool IsSynced { get; set; }
    public int OffsetMs { get; set; }
    public string? Credit { get; set; }
    public List<CachedLine>? Lines { get; set; }

    public static CachedDocument From(LyricsDocument doc) => new()
    {
        Source = doc.Source,
        IsSynced = doc.IsSynced,
        OffsetMs = doc.OffsetMs,
        Credit = doc.Credit,
        Lines = doc.Lines.Select(l => new CachedLine
        {
            Text = l.Text,
            StartMs = Ms(l.Start),
            EndMs = l.End is { } e ? Ms(e) : null,
            Words = l.Words?.Select(w => new CachedWord
            {
                Text = w.Text,
                StartMs = Ms(w.Start),
                EndMs = w.End is { } we ? Ms(we) : null,
            }).ToList(),
        }).ToList(),
    };

    /// <summary>The model document, or null when a required field is missing.</summary>
    public LyricsDocument? ToModel()
    {
        if (string.IsNullOrEmpty(Source) || Lines is null)
            return null;

        return new LyricsDocument
        {
            Source = Source,
            IsSynced = IsSynced,
            OffsetMs = OffsetMs,
            Credit = Credit,
            Lines = Lines.OfType<CachedLine>().Select(l => new LyricLine(
                l.Text ?? "",
                Time(l.StartMs),
                l.EndMs is { } e ? Time(e) : null,
                l.Words?.OfType<CachedWord>()
                    .Select(w => new LyricWord(w.Text ?? "", Time(w.StartMs), w.EndMs is { } we ? Time(we) : null))
                    .ToList())).ToList(),
        };
    }

    private static long Ms(TimeSpan t) => (long)Math.Round(t.TotalMilliseconds);

    private static TimeSpan Time(long ms) => TimeSpan.FromMilliseconds(Math.Max(0, ms));
}

internal sealed class CachedLine
{
    public string? Text { get; set; }
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
    public List<CachedWord>? Words { get; set; }
}

internal sealed class CachedWord
{
    public string? Text { get; set; }
    public long StartMs { get; set; }
    public long? EndMs { get; set; }
}

internal sealed class CustomLyricsFile
{
    public int Version { get; set; }

    public string? Text { get; set; }

    /// <summary>The user's timing adjustment, on top of any <c>[offset:]</c> tag in <see cref="Text"/>.</summary>
    public int OffsetMs { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(CachedLyricsFile))]
[JsonSerializable(typeof(CustomLyricsFile))]
internal sealed partial class LyricsJsonContext : JsonSerializerContext;

internal static class LyricsFiles
{
    /// <summary>
    /// The file name for a track id, or null when the id isn't safe to use as
    /// one. YouTube ids are [A-Za-z0-9_-]; anything else is refused rather than
    /// escaped, so no id can name a path outside the store. A local file's
    /// <c>local:n</c> id is <c>local.n.json</c>: a dot is never in a YouTube id,
    /// so the two can't name the same file.
    /// </summary>
    public static string? FileName(string videoId)
    {
        if (LocalIds.IsLocal(videoId))
        {
            var number = videoId[LocalIds.Prefix.Length..];
            return number.Length is > 0 and <= 20 && number.All(char.IsAsciiDigit) ? $"local.{number}.json" : null;
        }
        if (string.IsNullOrEmpty(videoId) || videoId.Length > 64)
            return null;
        foreach (var c in videoId)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                return null;
        }

        return videoId + ".json";
    }

    /// <summary>Writes through a temp file and a rename, so a crash never leaves half a file.</summary>
    public static void WriteAtomic(string path, string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, contents);
        File.Move(temp, path, overwrite: true);
    }
}
