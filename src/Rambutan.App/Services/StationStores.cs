using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Stations;

namespace Rambutan.App.Services;

/// <summary>
/// The Last.fm stations last loaded, <c>stations.json</c> in the
/// profile's data folder: Home shows them at startup without asking Last.fm,
/// and offline, with their date. Each list keeps every row, the ones with
/// nothing to play included, since the station's own page lists those too.
/// Versioned like every format the app writes; the DTOs are this file's own.
/// </summary>
public sealed class SavedStations
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The Last.fm user the lists were loaded for; another user's are
    /// never shown.</summary>
    public string? User { get; set; }

    public List<SavedStation> Stations { get; set; } = [];
}

public sealed class SavedStation
{
    /// <summary>"mix", "recommended" or "library".</summary>
    public string? Kind { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public List<SavedStationRow> Rows { get; set; } = [];
}

public sealed class SavedStationRow
{
    public string? Artist { get; set; }
    public string? Title { get; set; }
    public double? DurationSeconds { get; set; }
    /// <summary>The station's own YouTube link.</summary>
    public string? VideoId { get; set; }
    /// <summary>"song", "serviceVideo" or "notFound".</summary>
    public string? Match { get; set; }
    public SavedTrack? Track { get; set; }
}

/// <summary>
/// The search outcomes kept between runs (<see cref="StationMatchCache"/>),
/// <c>station-matches.json</c> in the profile's data folder. Outcomes made under
/// other matcher rules (<see cref="TrackMatcher.RulesVersion"/>) are dropped.
/// </summary>
public sealed class SavedStationMatches
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public int Rules { get; set; }
    public List<SavedStationMatch> Entries { get; set; } = [];
}

public sealed class SavedStationMatch
{
    /// <summary><see cref="StationMatchCache.KeyOf"/>: normalized artist and title.</summary>
    public string? Key { get; set; }
    public DateTimeOffset SearchedAt { get; set; }
    public SavedTrack? Song { get; set; }
    public SavedTrack? OtherVersion { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedStations))]
[JsonSerializable(typeof(SavedStationMatches))]
internal sealed partial class StationJsonContext : JsonSerializerContext;

/// <summary>One station as last loaded.</summary>
public sealed record StationSnapshot(StationKind Kind, DateTimeOffset LoadedAt, IReadOnlyList<StationRow> Rows);

/// <summary>
/// Reads and writes <see cref="SavedStations"/>. Never throws: a missing,
/// corrupt or newer file reads as nothing saved, and a failed write is logged.
/// Writes may come from two stations finishing together, so they take turns.
/// </summary>
public sealed class StationSnapshotStore(string dataDirectory)
{
    private readonly object _write = new();

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "stations.json");

    /// <summary>The lists saved for <paramref name="user"/>; empty for anyone else.</summary>
    public IReadOnlyList<StationSnapshot> Load(string user)
    {
        try
        {
            if (!File.Exists(Path))
                return [];
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), StationJsonContext.Default.SavedStations);
            if (saved is null || saved.Version is < 1 or > SavedStations.CurrentVersion)
                return [];
            if (!string.Equals(saved.User, user, StringComparison.OrdinalIgnoreCase))
                return [];
            return saved.Stations
                .Select(s => KindOf(s.Kind) is { } kind
                    ? new StationSnapshot(kind, s.LoadedAt, s.Rows.Select(ToRow).OfType<StationRow>().ToList())
                    : null)
                .OfType<StationSnapshot>()
                .ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write("stations", $"Couldn't read the saved stations: {ex.Message}");
            return [];
        }
    }

    public void Save(string user, IEnumerable<StationSnapshot> stations)
    {
        var saved = new SavedStations
        {
            User = user,
            Stations = stations.Select(s => new SavedStation
            {
                Kind = NameOf(s.Kind),
                LoadedAt = s.LoadedAt,
                Rows = s.Rows.Select(FromRow).ToList(),
            }).ToList(),
        };
        try
        {
            lock (_write)
                WriteAtomically(Path, JsonSerializer.Serialize(saved, StationJsonContext.Default.SavedStations));
        }
        catch (Exception ex)
        {
            AppLog.Write("stations", $"Couldn't save the stations: {ex.Message}");
        }
    }

    internal static string NameOf(StationKind kind) => kind switch
    {
        StationKind.Mix => "mix",
        StationKind.Recommended => "recommended",
        _ => "library",
    };

    internal static StationKind? KindOf(string? name) => name switch
    {
        "mix" => StationKind.Mix,
        "recommended" => StationKind.Recommended,
        "library" => StationKind.Library,
        _ => null,
    };

    private static SavedStationRow FromRow(StationRow row) => new()
    {
        Artist = row.Source.Artist,
        Title = row.Source.Title,
        DurationSeconds = row.Source.Duration?.TotalSeconds,
        VideoId = row.Source.VideoId,
        Match = row.Match switch
        {
            StationMatch.Song => "song",
            StationMatch.ServiceVideo => "serviceVideo",
            _ => "notFound",
        },
        Track = row.Track is { } track ? SavedTrack.From(track) : null,
    };

    /// <summary>A row as saved; null when it has no artist or title. A row
    /// that says it plays but has no track to play reads as not found.</summary>
    private static StationRow? ToRow(SavedStationRow r)
    {
        if (string.IsNullOrEmpty(r.Artist) || string.IsNullOrEmpty(r.Title))
            return null;
        var source = new StationTrack(r.Artist, r.Title,
            r.DurationSeconds is double s && s > 0 && double.IsFinite(s) ? TimeSpan.FromSeconds(s) : null,
            string.IsNullOrEmpty(r.VideoId) ? null : r.VideoId);
        var track = r.Track?.ToTrack();
        var match = r.Match switch
        {
            "song" => StationMatch.Song,
            "serviceVideo" => StationMatch.ServiceVideo,
            _ => StationMatch.NotFound,
        };
        return track is null || match == StationMatch.NotFound
            ? new StationRow(source, StationMatch.NotFound, null)
            : new StationRow(source, match, track);
    }

    internal static void WriteAtomically(string path, string json)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json);
        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>
/// Loads <see cref="SavedStationMatches"/> into a <see cref="StationMatchCache"/>
/// and saves it back. Never throws, like <see cref="StationSnapshotStore"/>.
/// </summary>
public sealed class StationMatchStore(string dataDirectory, StationMatchCache cache)
{
    private readonly object _write = new();

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "station-matches.json");

    public void Load()
    {
        try
        {
            if (!File.Exists(Path))
                return;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), StationJsonContext.Default.SavedStationMatches);
            if (saved is null || saved.Version is < 1 or > SavedStationMatches.CurrentVersion)
                return;
            if (saved.Rules != TrackMatcher.RulesVersion)
            {
                AppLog.Write("stations", $"Dropping saved matches from matcher rules {saved.Rules}; this build uses {TrackMatcher.RulesVersion}.");
                return;
            }
            cache.Import(saved.Entries
                .Where(e => !string.IsNullOrEmpty(e.Key))
                .Select(e => new KeyValuePair<string, StationSearchOutcome>(
                    e.Key!, new StationSearchOutcome(e.Song?.ToTrack(), e.OtherVersion?.ToTrack(), e.SearchedAt))));
        }
        catch (Exception ex)
        {
            AppLog.Write("stations", $"Couldn't read the saved matches: {ex.Message}");
        }
    }

    public void Save()
    {
        var saved = new SavedStationMatches
        {
            Rules = TrackMatcher.RulesVersion,
            Entries = cache.Export().Select(e => new SavedStationMatch
            {
                Key = e.Key,
                SearchedAt = e.Value.SearchedAt,
                Song = e.Value.Song is { } song ? SavedTrack.From(song) : null,
                OtherVersion = e.Value.OtherVersion is { } other ? SavedTrack.From(other) : null,
            }).ToList(),
        };
        try
        {
            lock (_write)
                StationSnapshotStore.WriteAtomically(Path, JsonSerializer.Serialize(saved, StationJsonContext.Default.SavedStationMatches));
        }
        catch (Exception ex)
        {
            AppLog.Write("stations", $"Couldn't save the matches: {ex.Message}");
        }
    }
}
