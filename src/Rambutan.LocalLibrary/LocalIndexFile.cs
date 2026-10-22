using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;

namespace Rambutan.LocalLibrary;

/// <summary>
/// <c>local-index.json</c> in the profile's folder: every file a scan has seen,
/// and the next id to give. Missing and unreadable files stay in it, so their ids
/// are kept and they aren't read again until they change.
/// </summary>
internal sealed class LocalIndexFile(string dataDirectory, string coverDirectory)
{
    public const string FileName = "local-index.json";

    private readonly string _path = Path.Combine(dataDirectory, FileName);

    public (IReadOnlyList<LocalFile> Files, long NextId) Load()
    {
        try
        {
            if (!File.Exists(_path))
                return ([], 1);
            var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), LocalIndexJsonContext.Default.SavedLocalIndex);
            if (saved is null || saved.Version is < 1 or > SavedLocalIndex.CurrentVersion)
                throw new InvalidDataException($"version {saved?.Version} isn't one this build reads");
            var files = saved.Files
                .Where(f => LocalIds.IsLocal(f.Id) && !string.IsNullOrEmpty(f.Path))
                .Select(f => ToFile(f, coverDirectory))
                .ToList();
            return (files, Math.Max(saved.NextId, 1));
        }
        catch (Exception ex)
        {
            // Fails closed: start empty rather than guess at the file, and set it
            // aside so the next save can't destroy what it held. Its ids may still
            // be in queues and playlists, and the next id is lost with it, so the
            // new ids start at the current time in milliseconds, far above any
            // count of files a library reaches.
            AppLog.Write("local", $"couldn't read {FileName}, set aside: {ex.Message}");
            try
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }
            catch
            {
            }
            return ([], DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public void Save(IEnumerable<LocalFile> files, long nextId)
    {
        var saved = new SavedLocalIndex { NextId = nextId, Files = files.Select(ToSaved).ToList() };
        Directory.CreateDirectory(dataDirectory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, LocalIndexJsonContext.Default.SavedLocalIndex));
        File.Move(temp, _path, overwrite: true);
    }

    private static LocalFile ToFile(SavedLocalFile f, string covers) => new(
        f.Id!, f.Path!, f.Size, f.ModifiedAt,
        f.Unreadable == true ? null : new LocalFileTags(
            Codec: f.Codec ?? "",
            Duration: f.DurationSeconds is { } s ? TimeSpan.FromSeconds(s) : null,
            Title: f.Title, Artist: f.Artist, Album: f.Album, AlbumArtist: f.AlbumArtist, Genre: f.Genre,
            TrackNumber: f.TrackNumber, DiscNumber: f.DiscNumber, Year: f.Year,
            TrackGainDb: f.TrackGainDb, AlbumGainDb: f.AlbumGainDb),
        f.Cover is { Length: > 0 } cover ? Path.Combine(covers, Path.GetFileName(cover)) : null,
        f.Missing == true,
        f.FolderCover is { Length: > 0 } picture
            ? new FoundFile(picture, f.FolderCoverSize ?? 0, f.FolderCoverModifiedAt ?? default)
            : null);

    private static SavedLocalFile ToSaved(LocalFile f) => new()
    {
        Id = f.Id,
        Path = f.Path,
        Size = f.Size,
        ModifiedAt = f.ModifiedAt,
        Missing = f.IsMissing ? true : null,
        Unreadable = f.IsUnreadable ? true : null,
        Codec = f.Tags?.Codec,
        DurationSeconds = f.Tags?.Duration?.TotalSeconds,
        Title = f.Tags?.Title,
        Artist = f.Tags?.Artist,
        Album = f.Tags?.Album,
        AlbumArtist = f.Tags?.AlbumArtist,
        Genre = f.Tags?.Genre,
        TrackNumber = f.Tags?.TrackNumber,
        DiscNumber = f.Tags?.DiscNumber,
        Year = f.Tags?.Year,
        TrackGainDb = f.Tags?.TrackGainDb,
        AlbumGainDb = f.Tags?.AlbumGainDb,
        Cover = f.CoverPath is { } cover ? Path.GetFileName(cover) : null,
        FolderCover = f.FolderCover?.Path,
        FolderCoverSize = f.FolderCover?.Size,
        FolderCoverModifiedAt = f.FolderCover?.ModifiedAt,
    };
}

/// <summary>Its own DTOs, not Core's records, so a model change can't silently
/// change what's on disk.</summary>
public sealed class SavedLocalIndex
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The number the next new file gets. Only ever grows.</summary>
    public long NextId { get; set; } = 1;

    public List<SavedLocalFile> Files { get; set; } = [];
}

public sealed class SavedLocalFile
{
    public string? Id { get; set; }

    /// <summary>The full path, as the scan found it.</summary>
    public string? Path { get; set; }

    public long Size { get; set; }
    public DateTimeOffset ModifiedAt { get; set; }
    public bool? Missing { get; set; }

    /// <summary>The file isn't audio the reader could open; it has no tags.</summary>
    public bool? Unreadable { get; set; }

    public string? Codec { get; set; }
    public double? DurationSeconds { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? Album { get; set; }
    public string? AlbumArtist { get; set; }
    public string? Genre { get; set; }
    public int? TrackNumber { get; set; }
    public int? DiscNumber { get; set; }
    public int? Year { get; set; }

    /// <summary>ReplayGain 2.0 gains in dB (an Opus R128 gain is moved to its reference).</summary>
    public double? TrackGainDb { get; set; }
    public double? AlbumGainDb { get; set; }

    /// <summary>The cover's file name in the cover cache, which is beside the
    /// index, so the profile's folder can move.</summary>
    public string? Cover { get; set; }

    /// <summary>The full path of the folder's picture that <see cref="Cover"/>
    /// copies, with its size and time, so a changed picture is noticed; null
    /// when the cover is embedded or there is none.</summary>
    public string? FolderCover { get; set; }

    public long? FolderCoverSize { get; set; }
    public DateTimeOffset? FolderCoverModifiedAt { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedLocalIndex))]
internal sealed partial class LocalIndexJsonContext : JsonSerializerContext;
