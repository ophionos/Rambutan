using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Playlists;

namespace Rambutan.LocalLibrary;

/// <summary>
/// The user's local playlists, in <c>local-playlists.json</c> in the profile's
/// folder. An unreadable file is set aside (<c>.unreadable</c>) and read as
/// empty; a newer build's file, or an unreadable one that couldn't be moved, is
/// read as empty and left as it is.
/// </summary>
public sealed class LocalPlaylistStore(string dataDirectory) : ILocalPlaylistStore
{
    private readonly string _path = Path.Combine(dataDirectory, "local-playlists.json");

    public LoadedLocalPlaylists Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new([]);
            var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), LocalPlaylistsJsonContext.Default.SavedLocalPlaylists);
            if (saved is { Version: > SavedLocalPlaylists.CurrentVersion })
            {
                AppLog.Write("playlists", $"local-playlists.json is version {saved.Version}, newer than this build's; left as it is");
                return new([], LocalPlaylistsProblem.Newer);
            }
            if (saved is null || saved.Version < 1)
                throw new InvalidDataException($"version {saved?.Version} isn't one this build reads");
            return new(saved.Playlists.Select(ToPlaylist).OfType<LocalPlaylist>().ToArray());
        }
        catch (Exception ex)
        {
            AppLog.Write("playlists", $"couldn't read local-playlists.json, set aside: {ex.Message}");
            string? aside = _path + ".unreadable";
            try
            {
                File.Move(_path, aside, overwrite: true);
            }
            catch (Exception moveEx)
            {
                AppLog.Write("playlists", $"couldn't set local-playlists.json aside, left as it is: {moveEx.Message}");
                aside = null;
            }
            return new([], LocalPlaylistsProblem.Unreadable, aside);
        }
    }

    public void Save(IReadOnlyList<LocalPlaylist> playlists)
    {
        var saved = new SavedLocalPlaylists { Playlists = [.. playlists.Select(FromPlaylist)] };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, LocalPlaylistsJsonContext.Default.SavedLocalPlaylists));
        File.Move(temp, _path, overwrite: true);
    }

    private static LocalPlaylist? ToPlaylist(SavedLocalPlaylist? p)
    {
        if (p is null || string.IsNullOrEmpty(p.Id) || LocalPlaylists.CleanName(p.Name) is not { } name)
            return null;
        // A row that can't be read is dropped, not the playlist.
        var items = (p.Items ?? [])
            .Where(DownloadStore.IsValid)
            .Select(i => new LocalPlaylistItem(DownloadStore.ToTrack(i!), string.IsNullOrEmpty(i!.Path) ? null : i.Path))
            .ToArray();
        return new LocalPlaylist(p.Id, name, items, p.CreatedAt, p.ChangedAt,
            string.IsNullOrEmpty(p.CopiedFrom) ? null : p.CopiedFrom);
    }

    private static SavedLocalPlaylist FromPlaylist(LocalPlaylist p) => new()
    {
        Id = p.Id,
        Name = p.Name,
        CreatedAt = p.CreatedAt,
        ChangedAt = p.ChangedAt,
        CopiedFrom = p.CopiedFrom,
        Items = [.. p.Items.Select(i => DownloadStore.CopyTrack(i.Track, new SavedLocalPlaylistItem { Path = i.Path }))],
    };
}

/// <summary><c>local-playlists.json</c>, with its own DTOs like the local index.</summary>
public sealed class SavedLocalPlaylists
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedLocalPlaylist?> Playlists { get; set; } = [];
}

public sealed class SavedLocalPlaylist
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ChangedAt { get; set; }

    /// <summary>The YouTube Music playlist it was copied from; absent for one the user started.</summary>
    public string? CopiedFrom { get; set; }
    public List<SavedLocalPlaylistItem?>? Items { get; set; }
}

/// <summary>A row: the track as it was added (a local file's tags then), and a
/// local file's path. The download order fields are never written.</summary>
public sealed class SavedLocalPlaylistItem : SavedDownloadTrack
{
    public string? Path { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedLocalPlaylists))]
internal sealed partial class LocalPlaylistsJsonContext : JsonSerializerContext;
