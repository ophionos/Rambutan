using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;

namespace Rambutan.App.Services;

/// <summary>
/// The saved queue, <c>queue.json</c> beside <c>settings.json</c>.
/// Its own file rather than a settings field: it
/// changes on every track, and a settings write shouldn't carry a thousand-track
/// playlist along with it. Versioned like every format a user builds up. The DTOs are this file's own, not Core's <see cref="TrackInfo"/>,
/// so a model change can't silently change what's on disk.
/// </summary>
public sealed class SavedQueue
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int CurrentIndex { get; set; }
    public string Repeat { get; set; } = "off";
    public List<SavedTrack> Tracks { get; set; } = [];

    /// <summary>
    /// Present only while shuffle is on: for each entry of <see cref="Tracks"/>
    /// (which are in play order), its position in the original order. Added within
    /// version 1 because it's optional and changes no existing field's meaning: a
    /// reader that ignores it restores the same tracks in the same order, with
    /// shuffle off.
    /// </summary>
    public List<int>? ShuffleRanks { get; set; }

    /// <summary>
    /// Positions in <see cref="Tracks"/> of the tracks autoplay added, so the
    /// restored queue still captions them. Optional within version 1 like
    /// <see cref="ShuffleRanks"/>: a reader that ignores it shows them as queued.
    /// </summary>
    public List<int>? Autoplay { get; set; }
}

public sealed class SavedTrack
{
    public string? VideoId { get; set; }
    public string? Title { get; set; }
    public List<SavedArtist>? Artists { get; set; }
    public string? Album { get; set; }
    public string? AlbumBrowseId { get; set; }
    public double? DurationSeconds { get; set; }
    public string? ThumbnailUrl { get; set; }

    /// <summary>
    /// <see cref="TrackInfo.SongId"/>, so a restored queue still marks the song on
    /// the pages that list its counterpart. Optional within version 1: it
    /// changes no existing field's meaning, and a reader that ignores it marks by
    /// videoId only.
    /// </summary>
    public string? SongId { get; set; }

    /// <summary>
    /// <see cref="TrackInfo.ListedVideoId"/>, so an album row restored from the
    /// queue still falls back to the video its page listed. Optional within
    /// version 1, like <see cref="SongId"/>: a reader that ignores it plays the
    /// audio track with no fallback.
    /// </summary>
    public string? ListedVideoId { get; set; }

    public static SavedTrack From(TrackInfo t) => new()
    {
        VideoId = t.VideoId,
        Title = t.Title,
        Artists = t.Artists.Select(a => new SavedArtist { Name = a.Name, BrowseId = a.BrowseId }).ToList(),
        Album = t.Album,
        AlbumBrowseId = t.AlbumBrowseId,
        DurationSeconds = t.Duration?.TotalSeconds,
        ThumbnailUrl = t.ThumbnailUrl,
        SongId = t.SongId,
        ListedVideoId = t.ListedVideoId,
    };

    /// <summary>The track, or null when it has no id or title to play by.</summary>
    public TrackInfo? ToTrack() => string.IsNullOrEmpty(VideoId) || string.IsNullOrEmpty(Title)
        ? null
        : new TrackInfo
        {
            VideoId = VideoId,
            Title = Title,
            Artists = (Artists ?? [])
                .Where(a => !string.IsNullOrEmpty(a?.Name))
                .Select(a => new ArtistRef(a.Name!, a.BrowseId))
                .ToArray(),
            Album = Album,
            AlbumBrowseId = string.IsNullOrEmpty(AlbumBrowseId) ? null : AlbumBrowseId,
            Duration = DurationSeconds is double s && s > 0 && double.IsFinite(s)
                ? TimeSpan.FromSeconds(s) : null,
            ThumbnailUrl = ThumbnailUrl,
            SongId = string.IsNullOrEmpty(SongId) ? null : SongId,
            ListedVideoId = string.IsNullOrEmpty(ListedVideoId) ? null : ListedVideoId,
        };
}

public sealed class SavedArtist
{
    public string? Name { get; set; }
    public string? BrowseId { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedQueue))]
internal sealed partial class QueueJsonContext : JsonSerializerContext;

/// <summary>Reads and writes <see cref="SavedQueue"/> with atomic writes, like
/// <see cref="AppSettingsStore"/>. Never throws on load: a missing, corrupt or
/// newer file means "no saved queue", and the app starts empty.</summary>
public sealed class QueueStore(string dataDirectory)
{
    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "queue.json");

    public QueueSnapshot? Load()
    {
        try
        {
            if (!File.Exists(Path))
                return null;

            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), QueueJsonContext.Default.SavedQueue);
            if (saved is null)
                return null;

            // A newer app wrote this; its meaning may have changed. Starting empty
            // is honest, and the file is left alone until this run saves over it.
            if (saved.Version > SavedQueue.CurrentVersion || saved.Version < 1)
            {
                AppLog.Write("queue", $"Ignoring queue.json version {saved.Version}; this build reads {SavedQueue.CurrentVersion}.");
                return null;
            }

            return ToSnapshot(saved);
        }
        catch (Exception ex)
        {
            AppLog.Write("queue", $"Couldn't read the saved queue, starting empty: {ex.Message}");
            return null;
        }
    }

    public void Save(QueueSnapshot snapshot)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(FromSnapshot(snapshot), QueueJsonContext.Default.SavedQueue));
        File.Move(temp, Path, overwrite: true);
    }

    public static SavedQueue FromSnapshot(QueueSnapshot snapshot) => new()
    {
        CurrentIndex = snapshot.CurrentIndex,
        Repeat = snapshot.Repeat switch
        {
            RepeatMode.All => "all",
            RepeatMode.One => "one",
            _ => "off",
        },
        Tracks = snapshot.Tracks.Select(SavedTrack.From).ToList(),
        ShuffleRanks = snapshot.ShuffleRanks?.ToList(),
        Autoplay = snapshot.AutoplayIndices is { Count: > 0 } autoplay ? autoplay.ToList() : null,
    };

    /// <summary>Tracks missing an id or a title are dropped (the index and the
    /// shuffle ranks follow the tracks they pointed at); an unknown repeat word
    /// reads as off, and shuffle ranks that don't fit the tracks as shuffle off.</summary>
    public static QueueSnapshot ToSnapshot(SavedQueue saved)
    {
        var tracks = new List<TrackInfo>();
        var kept = new List<int>();
        var current = -1;
        for (var i = 0; i < saved.Tracks.Count; i++)
        {
            if (saved.Tracks[i]?.ToTrack() is not { } track)
                continue;

            if (i <= saved.CurrentIndex)
                current = tracks.Count;
            kept.Add(i);
            tracks.Add(track);
        }

        var repeat = saved.Repeat switch
        {
            "all" => RepeatMode.All,
            "one" => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        // Autoplay marks follow the tracks they pointed at, like the index.
        var autoplay = saved.Autoplay is { } marks
            ? kept.Select((original, i) => (original, i)).Where(p => marks.Contains(p.original)).Select(p => p.i).ToArray()
            : null;
        return new QueueSnapshot(
            tracks, Math.Max(0, current), repeat, ShuffleRanksFor(saved, kept),
            autoplay is { Length: > 0 } ? autoplay : null);
    }

    /// <summary>The saved ranks of the tracks that survived loading, renumbered
    /// 0..n-1 in the same relative order; null when shuffle was off or the ranks
    /// don't describe the saved tracks.</summary>
    private static int[]? ShuffleRanksFor(SavedQueue saved, List<int> kept)
    {
        if (saved.ShuffleRanks is not { } ranks ||
            !QueueSnapshot.IsPermutation(ranks, saved.Tracks.Count))
        {
            return null;
        }

        var survivors = kept.Select(i => ranks[i]).ToArray();
        var order = survivors.Order().ToArray();
        return survivors.Select(r => Array.BinarySearch(order, r)).ToArray();
    }
}
