using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Import;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;
using Rambutan.Core.Text;

namespace Rambutan.App.Services;

/// <summary>
/// The Spotify import's files and memory: reading a picked file, the matches
/// an import made (<c>import-matches.json</c>, the stations' format), and
/// what each import created (<c>spotify-imports.json</c>), so importing the
/// same list again adds only what is new.
/// </summary>
public sealed class SpotifyImports
{
    /// <summary>The newest matches kept: enough for a large library and the
    /// imports after it.</summary>
    public const int MaxMatches = 50_000;

    private readonly StationMatchStore _matchStore;
    private readonly SpotifyImportRecordStore _records;

    /// <param name="searchSongs">A signed-out catalog's song search (the
    /// stations' client), never the account's.</param>
    /// <param name="readLink">Reads a share link's list from Spotify; null
    /// when links can't be read.</param>
    /// <param name="spacing">The pause between searches; null for the matcher's own.</param>
    public SpotifyImports(
        string dataDirectory, Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>> searchSongs,
        Func<SpotifyListLink, CancellationToken, Task<ImportedList>>? readLink = null, TimeSpan? spacing = null)
    {
        ReadLink = readLink;
        var cache = new StationMatchCache(maxEntries: MaxMatches);
        _matchStore = new StationMatchStore(dataDirectory, cache, "import-matches.json");
        _records = new SpotifyImportRecordStore(dataDirectory);
        SearchSongs = searchSongs;
        Matcher = new ImportMatcher(searchSongs, cache, spacing);
        _matchStore.Load();
    }

    public ImportMatcher Matcher { get; }

    public Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>> SearchSongs { get; }

    /// <summary>Reads a share link's list: a request to Spotify, sent only when
    /// the user asks. Null when links can't be read.</summary>
    public Func<SpotifyListLink, CancellationToken, Task<ImportedList>>? ReadLink { get; }

    /// <summary>Saves the matches made so far. Never throws; call off the UI thread.</summary>
    public void SaveMatches() => _matchStore.Save();

    public IReadOnlyList<ImportRecord> LoadRecords() => _records.Load();

    /// <summary>Writes every record, the ones <see cref="LoadRecords"/> gave
    /// with an import's changes. Never throws; call off the UI thread.</summary>
    public void SaveRecords(IReadOnlyList<ImportRecord> records) => _records.Save(records);

    /// <summary>
    /// The lists in a picked file: Spotify's export (its zip, or one of its
    /// JSON files) or a CSV. Reads the disk; call off the UI thread.
    /// </summary>
    /// <exception cref="ImportFormatException">Not a file the import reads.</exception>
    /// <exception cref="IOException">The file couldn't be read.</exception>
    public static IReadOnlyList<ImportedList> Read(string path)
    {
        if (IsZip(path))
        {
            using var zip = ZipFile.OpenRead(path);
            var files = zip.Entries
                .Where(e => SpotifyExport.IsListFile(e.FullName))
                .Select(e => (e.FullName, Text(e)))
                .ToList();
            if (files.Count == 0)
                throw new ImportFormatException(ImportProblem.Unreadable, "no playlist file in the zip");
            return SpotifyExport.ReadAll(files);
        }

        var text = FileText.Decode(File.ReadAllBytes(path));
        var extension = System.IO.Path.GetExtension(path);
        return extension.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? SpotifyExport.Read(text)
            : TrackCsv.Read(text, System.IO.Path.GetFileNameWithoutExtension(path));
    }

    private static bool IsZip(string path)
    {
        Span<byte> head = stackalloc byte[4];
        using var file = File.OpenRead(path);
        return file.ReadAtLeast(head, 4, throwOnEndOfStream: false) == 4 &&
               head[0] == (byte)'P' && head[1] == (byte)'K' && head[2] == 3 && head[3] == 4;
    }

    private static string Text(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        return FileText.Decode(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }
}

/// <summary><c>spotify-imports.json</c>: what each import created. Versioned
/// like every format the app writes.</summary>
public sealed class SavedSpotifyImports
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public List<SavedSpotifyImport> Imports { get; set; } = [];
}

public sealed class SavedSpotifyImport
{
    public string? ListName { get; set; }

    /// <summary>"playlist" or "likedSongs".</summary>
    public string? ListKind { get; set; }

    /// <summary>"youTubeMusic" or "thisComputer".</summary>
    public string? Target { get; set; }

    public string? PlaylistName { get; set; }

    public List<SavedImportedPart> Parts { get; set; } = [];

    public List<string> Keys { get; set; } = [];

    public DateTimeOffset ImportedAt { get; set; }

    /// <summary>The list's Spotify URI when it was read from a link, to read
    /// it again; absent for a file's list.</summary>
    public string? Source { get; set; }

    /// <summary>For an import to YouTube Music, the id of the account whose
    /// playlists these are; absent for this computer.</summary>
    public string? Account { get; set; }
}

public sealed class SavedImportedPart
{
    public string? PlaylistId { get; set; }

    public int Count { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedSpotifyImports))]
internal sealed partial class SpotifyImportJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="SavedSpotifyImports"/>. Never throws: a
/// missing, corrupt or newer file reads as no imports (and a newer one is not
/// written over), and a failed write is logged.
/// </summary>
public sealed class SpotifyImportRecordStore(string dataDirectory)
{
    private readonly Lock _write = new();

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "spotify-imports.json");

    private bool _newer;

    public IReadOnlyList<ImportRecord> Load()
    {
        try
        {
            if (!File.Exists(Path))
                return [];
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), SpotifyImportJsonContext.Default.SavedSpotifyImports);
            if (saved is null || saved.Version < 1)
                return [];
            if (saved.Version > SavedSpotifyImports.CurrentVersion)
            {
                _newer = true;
                return [];
            }
            return saved.Imports.Select(ToRecord).OfType<ImportRecord>().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Write("import", $"Couldn't read the earlier imports: {ex.Message}");
            return [];
        }
    }

    public void Save(IReadOnlyList<ImportRecord> records)
    {
        if (_newer)
        {
            AppLog.Write("import", "The earlier imports were written by a newer build; leaving them as they are.");
            return;
        }
        var saved = new SavedSpotifyImports { Imports = records.Select(FromRecord).ToList() };
        try
        {
            lock (_write)
                StationSnapshotStore.WriteAtomically(Path, JsonSerializer.Serialize(saved, SpotifyImportJsonContext.Default.SavedSpotifyImports));
        }
        catch (Exception ex)
        {
            AppLog.Write("import", $"Couldn't save the imports: {ex.Message}");
        }
    }

    private static SavedSpotifyImport FromRecord(ImportRecord r) => new()
    {
        ListName = r.ListName,
        ListKind = r.ListKind == ImportedListKind.LikedSongs ? "likedSongs" : "playlist",
        Target = r.Target == ImportTarget.YouTubeMusic ? "youTubeMusic" : "thisComputer",
        PlaylistName = r.PlaylistName,
        Parts = r.Parts.Select(p => new SavedImportedPart { PlaylistId = p.PlaylistId, Count = p.Count }).ToList(),
        Keys = r.Keys.ToList(),
        ImportedAt = r.ImportedAt,
        Source = r.Source,
        Account = r.Account,
    };

    /// <summary>A record as saved; null when a field it can't do without is
    /// missing or unknown.</summary>
    private static ImportRecord? ToRecord(SavedSpotifyImport s)
    {
        ImportedListKind? kind = s.ListKind switch
        {
            "playlist" => ImportedListKind.Playlist,
            "likedSongs" => ImportedListKind.LikedSongs,
            _ => null,
        };
        ImportTarget? target = s.Target switch
        {
            "youTubeMusic" => ImportTarget.YouTubeMusic,
            "thisComputer" => ImportTarget.ThisComputer,
            _ => null,
        };
        if (kind is null || target is null || string.IsNullOrEmpty(s.PlaylistName))
            return null;
        var parts = s.Parts
            .Where(p => !string.IsNullOrEmpty(p.PlaylistId) && p.Count >= 0)
            .Select(p => new ImportedPart(p.PlaylistId!, p.Count))
            .ToList();
        return new ImportRecord(s.ListName ?? "", kind.Value, target.Value, s.PlaylistName, parts,
            s.Keys.Where(k => k is not null).ToList(), s.ImportedAt,
            SpotifyLinks.Parse(s.Source) is { } link ? link.Uri : null,
            target == ImportTarget.YouTubeMusic && !string.IsNullOrEmpty(s.Account) ? s.Account : null);
    }
}
