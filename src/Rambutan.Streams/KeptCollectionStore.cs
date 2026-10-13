using System.Text.Json;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Downloads;

namespace Rambutan.Streams;

/// <summary>
/// The kept copies of downloaded albums and playlists, in
/// <c>downloads-collections.json</c> beside <c>downloads.json</c>. An unreadable
/// file is set aside and read as empty: the downloads themselves don't depend on it.
/// </summary>
public sealed class KeptCollectionStore(string dataDirectory) : IKeptCollectionStore
{
    private readonly string _path = Path.Combine(dataDirectory, "downloads-collections.json");

    public IReadOnlyList<KeptCollection> Load()
    {
        try
        {
            if (!File.Exists(_path))
                return [];
            var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), DownloadsJsonContext.Default.SavedKeptCollections);
            if (saved is null || saved.Version is < 1 or > SavedKeptCollections.CurrentVersion)
                throw new InvalidDataException($"version {saved?.Version} isn't one this build reads");
            return saved.Collections.Select(ToCollection).OfType<KeptCollection>().ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Write("downloads", $"couldn't read downloads-collections.json, set aside: {ex.Message}");
            try
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
            }
            catch
            {
            }
            return [];
        }
    }

    public void Save(IReadOnlyList<KeptCollection> collections)
    {
        var saved = new SavedKeptCollections { Collections = collections.Select(FromCollection).ToList() };
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, DownloadsJsonContext.Default.SavedKeptCollections));
            File.Move(temp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            // The copies are a convenience: the downloads still play from the Downloads page.
            AppLog.Write("downloads", $"couldn't save downloads-collections.json: {ex.Message}");
        }
    }

    private static KeptCollection? ToCollection(SavedKeptCollection c)
    {
        if (string.IsNullOrEmpty(c.Id) || string.IsNullOrEmpty(c.Title)
            || !Enum.TryParse<KeptCollectionKind>(c.Kind, ignoreCase: true, out var kind))
            return null;
        var tracks = (c.Tracks ?? []).Where(DownloadStore.IsValid).Select(DownloadStore.ToTrack).ToArray();
        return tracks.Length == 0
            ? null
            : new KeptCollection(kind, c.Id, c.Title, c.Artist, c.ArtistBrowseId, c.SubtitleInfo, c.ThumbnailUrl, tracks, c.KeptAt);
    }

    private static SavedKeptCollection FromCollection(KeptCollection c) => new()
    {
        Kind = c.Kind == KeptCollectionKind.Album ? "album" : "playlist",
        Id = c.Id,
        Title = c.Title,
        Artist = c.Artist,
        ArtistBrowseId = c.ArtistBrowseId,
        SubtitleInfo = c.SubtitleInfo,
        ThumbnailUrl = c.ThumbnailUrl,
        KeptAt = c.KeptAt,
        Tracks = c.Tracks.Select(DownloadStore.TrackRecord).ToList(),
    };
}

/// <summary><c>downloads-collections.json</c>, with its own DTOs like <c>downloads.json</c>.</summary>
public sealed class SavedKeptCollections
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedKeptCollection> Collections { get; set; } = [];
}

public sealed class SavedKeptCollection
{
    /// <summary>"album" or "playlist".</summary>
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public string? Title { get; set; }
    public string? Artist { get; set; }
    public string? ArtistBrowseId { get; set; }
    public string? SubtitleInfo { get; set; }
    public string? ThumbnailUrl { get; set; }
    public DateTimeOffset KeptAt { get; set; }
    public List<SavedDownloadTrack>? Tracks { get; set; }
}
