using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Stations;

namespace Rambutan.App.Services;

/// <summary>
/// The playlists ListenBrainz made for the user, as last listed, and the
/// tracks of those loaded, <c>listenbrainz-stations.json</c> in the profile's
/// data folder. Versioned like every format the app writes; the rows are
/// <see cref="SavedStationRow"/>, as in <c>stations.json</c>.
/// </summary>
public sealed class SavedCreatedFor
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The ListenBrainz user the playlists were listed for; another
    /// user's are never shown.</summary>
    public string? User { get; set; }

    public DateTimeOffset ListedAt { get; set; }

    public List<SavedCreatedForPlaylist> Playlists { get; set; } = [];

    /// <summary>The loaded tracks, one entry per listed playlist at most.</summary>
    public List<SavedCreatedForList> Loaded { get; set; } = [];
}

public sealed class SavedCreatedForPlaylist
{
    /// <summary>"dailyJams", "weeklyJams" or "weeklyExploration".</summary>
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class SavedCreatedForList
{
    public string? PlaylistId { get; set; }
    public DateTimeOffset LoadedAt { get; set; }
    public List<SavedStationRow> Rows { get; set; } = [];
}

/// <summary>One of the user's playlists as last loaded.</summary>
public sealed record CreatedForSnapshot(string PlaylistId, DateTimeOffset LoadedAt, IReadOnlyList<StationRow> Rows);

/// <summary>What is saved for one user: the list, when it was fetched, and the
/// playlists loaded, by id.</summary>
public sealed record CreatedForSaved(
    DateTimeOffset ListedAt, IReadOnlyList<CreatedForPlaylist> Playlists,
    IReadOnlyDictionary<string, CreatedForSnapshot> Loaded);

/// <summary>
/// Reads and writes <see cref="SavedCreatedFor"/>. Never throws: a missing,
/// corrupt or newer file, or another user's, reads as nothing saved, and a
/// failed write is logged. Writes take turns.
/// </summary>
public sealed class CreatedForStore(string dataDirectory)
{
    private readonly object _write = new();

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "listenbrainz-stations.json");

    /// <summary>What is saved for <paramref name="user"/>; null when nothing is.</summary>
    public CreatedForSaved? Load(string user)
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), StationJsonContext.Default.SavedCreatedFor);
            if (saved is null || saved.Version is < 1 or > SavedCreatedFor.CurrentVersion)
                return null;
            if (!string.Equals(saved.User, user, StringComparison.OrdinalIgnoreCase))
                return null;
            var playlists = saved.Playlists
                .Select(p => KindOf(p.Kind) is { } kind && !string.IsNullOrEmpty(p.Id)
                    ? new CreatedForPlaylist(kind, p.Id, p.CreatedAt, p.ExpiresAt)
                    : null)
                .OfType<CreatedForPlaylist>()
                .ToList();
            var listed = playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
            var loaded = new Dictionary<string, CreatedForSnapshot>(StringComparer.Ordinal);
            foreach (var list in saved.Loaded)
                if (list.PlaylistId is { Length: > 0 } id && listed.Contains(id))
                    loaded[id] = new CreatedForSnapshot(id, list.LoadedAt,
                        list.Rows.Select(StationSnapshotStore.ToRow).OfType<StationRow>().ToList());
            return new CreatedForSaved(saved.ListedAt, playlists, loaded);
        }
        catch (Exception ex)
        {
            AppLog.Write("stations", $"Couldn't read the saved ListenBrainz playlists: {ex.Message}");
            return null;
        }
    }

    /// <summary>Saves <paramref name="saved"/> for <paramref name="user"/>, without
    /// the loaded tracks of playlists no longer listed.</summary>
    public void Save(string user, CreatedForSaved saved)
    {
        var listed = saved.Playlists.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var file = new SavedCreatedFor
        {
            User = user,
            ListedAt = saved.ListedAt,
            Playlists = saved.Playlists.Select(p => new SavedCreatedForPlaylist
            {
                Kind = NameOf(p.Kind),
                Id = p.Id,
                CreatedAt = p.CreatedAt,
                ExpiresAt = p.ExpiresAt,
            }).ToList(),
            Loaded = saved.Loaded.Values
                .Where(l => listed.Contains(l.PlaylistId))
                .Select(l => new SavedCreatedForList
                {
                    PlaylistId = l.PlaylistId,
                    LoadedAt = l.LoadedAt,
                    Rows = l.Rows.Select(StationSnapshotStore.FromRow).ToList(),
                }).ToList(),
        };
        try
        {
            lock (_write)
                StationSnapshotStore.WriteAtomically(Path, JsonSerializer.Serialize(file, StationJsonContext.Default.SavedCreatedFor));
        }
        catch (Exception ex)
        {
            AppLog.Write("stations", $"Couldn't save the ListenBrainz playlists: {ex.Message}");
        }
    }

    internal static string NameOf(CreatedForKind kind) => kind switch
    {
        CreatedForKind.DailyJams => "dailyJams",
        CreatedForKind.WeeklyJams => "weeklyJams",
        _ => "weeklyExploration",
    };

    internal static CreatedForKind? KindOf(string? name) => name switch
    {
        "dailyJams" => CreatedForKind.DailyJams,
        "weeklyJams" => CreatedForKind.WeeklyJams,
        "weeklyExploration" => CreatedForKind.WeeklyExploration,
        _ => null,
    };
}
