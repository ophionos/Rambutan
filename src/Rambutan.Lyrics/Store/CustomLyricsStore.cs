using System.Text.Json;
using Rambutan.Core.Diagnostics;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Store;

/// <summary>The user's own lyrics for a track, as they entered them.</summary>
/// <param name="Text">LRC, enhanced LRC, or plain text.</param>
/// <param name="OffsetMs">The user's timing correction; positive makes lyrics appear sooner.</param>
public sealed record CustomLyrics(string Text, int OffsetMs = 0);

/// <summary>What custom lyrics text will play as, for the editor to say before saving.</summary>
public enum CustomLyricsFormat
{
    /// <summary>Nothing but whitespace: nothing to save.</summary>
    Empty,
    /// <summary>No usable stamps: shown as text that doesn't follow playback.</summary>
    Plain,
    /// <summary>Line stamps: the playing line is highlighted.</summary>
    LineTimed,
    /// <summary>Word stamps on at least one line: the karaoke sweep.</summary>
    WordTimed,
}

/// <summary>
/// Storage half of custom lyrics: one versioned JSON file per video id under
/// <c>{dataDir}/lyrics/custom/</c>, holding the raw text and an offset rather
/// than a parsed document, so the editor gets back exactly what was written.
/// Custom lyrics win over every provider. Reads never throw (an unreadable file
/// is logged and treated as absent); writes do, since they're a user action the
/// UI must report.
/// </summary>
public sealed class CustomLyricsStore(string directory, TimeProvider? clock = null)
{
    public const int CurrentVersion = 1;
    public const string SourceName = "custom";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    public string DirectoryPath { get; } = directory;

    public CustomLyrics? TryGetRaw(string videoId)
    {
        if (PathFor(videoId) is not { } path)
            return null;

        try
        {
            if (!File.Exists(path))
                return null;

            var file = JsonSerializer.Deserialize(File.ReadAllText(path), LyricsJsonContext.Default.CustomLyricsFile);
            if (file is null || file.Text is null)
                return null;

            // Unlike the cache, this is the user's work: a newer version is
            // skipped but never overwritten here, and it's logged so it's findable.
            if (file.Version != CurrentVersion)
            {
                AppLog.Write("lyrics", $"Skipping custom lyrics for {videoId}: version {file.Version}, this build reads {CurrentVersion}.");
                return null;
            }

            return new CustomLyrics(file.Text, file.OffsetMs);
        }
        catch (Exception ex)
        {
            AppLog.Write("lyrics", $"Couldn't read custom lyrics for {videoId}: {ex.Message}");
            return null;
        }
    }

    public LyricsDocument? TryGet(string videoId) =>
        TryGetRaw(videoId) is { } raw ? ToDocument(raw) : null;

    /// <summary>
    /// Stamped text becomes a synced document with the offset applied; anything
    /// else is plain unsynced text (where an offset means nothing).
    /// </summary>
    public static LyricsDocument ToDocument(CustomLyrics raw)
    {
        var synced = LrcParser.Parse(raw.Text, SourceName, raw.OffsetMs);
        return synced.IsSynced ? synced : LyricsDocument.FromPlainText(raw.Text, SourceName);
    }

    /// <summary>How <paramref name="text"/> will play, by the same rule as <see cref="ToDocument"/>.</summary>
    public static CustomLyricsFormat FormatOf(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return CustomLyricsFormat.Empty;

        var synced = LrcParser.Parse(text, SourceName);
        if (!synced.IsSynced)
            return CustomLyricsFormat.Plain;

        return synced.Lines.Any(l => l.Words is { Count: > 0 })
            ? CustomLyricsFormat.WordTimed
            : CustomLyricsFormat.LineTimed;
    }

    /// <summary>Saves (replacing) the track's custom lyrics. Throws on I/O failure.</summary>
    public void Save(string videoId, CustomLyrics lyrics)
    {
        var path = PathFor(videoId) ?? throw new ArgumentException($"Not a usable video id: '{videoId}'", nameof(videoId));
        var file = new CustomLyricsFile
        {
            Version = CurrentVersion,
            Text = lyrics.Text,
            OffsetMs = lyrics.OffsetMs,
            UpdatedAt = _clock.GetUtcNow(),
        };
        LyricsFiles.WriteAtomic(path, JsonSerializer.Serialize(file, LyricsJsonContext.Default.CustomLyricsFile));
    }

    /// <summary>Removes the track's custom lyrics, if any. Throws on I/O failure.</summary>
    public void Delete(string videoId)
    {
        if (PathFor(videoId) is { } path)
            File.Delete(path);
    }

    private string? PathFor(string videoId) =>
        LyricsFiles.FileName(videoId) is { } name ? Path.Combine(DirectoryPath, name) : null;
}
