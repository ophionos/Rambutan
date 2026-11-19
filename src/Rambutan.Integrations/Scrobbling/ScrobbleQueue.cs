using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;

namespace Rambutan.Integrations.Scrobbling;

/// <summary>On-disk shape of a scrobble queue file (<c>scrobble-queue.json</c> for Last.fm).</summary>
public sealed class SavedScrobbleQueue
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<ScrobbleEntry> Scrobbles { get; set; } = [];

    /// <summary>The service ended the connection, and plays are still recorded until
    /// the user connects again or chooses Disconnect. Added within version 1:
    /// files without it read as false.</summary>
    public bool SessionEnded { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedScrobbleQueue))]
internal sealed partial class ScrobbleQueueJsonContext : JsonSerializerContext;

/// <summary>
/// Scrobbles waiting to reach a scrobbling service (the offline queue), oldest first,
/// persisted as versioned JSON at <c>{dataDir}/{fileName}</c> with atomic
/// writes. Every scrobble goes through here, online or not, so a crash between
/// earning a scrobble and sending it loses nothing. Capped at
/// <see cref="Capacity"/>: past that the oldest are dropped, with a log line.
/// </summary>
/// <remarks>
/// A corrupt file, or one a newer build wrote, is moved aside to
/// <c>{fileName}.unreadable</c> rather than overwritten, and the queue
/// starts empty. Mutations come from one worker; <see cref="Count"/> may be read
/// from anywhere.
/// </remarks>
public sealed class ScrobbleQueue
{
    public const int Capacity = 500;

    private readonly List<ScrobbleEntry> _entries = [];
    private readonly object _sync = new();

    private readonly string _logCategory;

    /// <param name="fileName">One file per service, so two queues never share entries.</param>
    /// <param name="logCategory">The <see cref="AppLog"/> category of the service that owns the queue.</param>
    public ScrobbleQueue(string dataDirectory, string fileName = "scrobble-queue.json", string logCategory = "lastfm")
    {
        Path = System.IO.Path.Combine(dataDirectory, fileName);
        _logCategory = logCategory;
    }

    public string Path { get; }

    public int Count
    {
        get
        {
            lock (_sync)
                return _entries.Count;
        }
    }

    private bool _sessionEnded;

    /// <summary>The service ended the connection (not the user): plays are still
    /// queued. Saved with the entries, so it outlasts a restart.</summary>
    public bool SessionEnded
    {
        get
        {
            lock (_sync)
                return _sessionEnded;
        }
        set
        {
            lock (_sync)
            {
                if (_sessionEnded == value)
                    return;
                _sessionEnded = value;
                Save();
            }
        }
    }

    public void Load()
    {
        lock (_sync)
        {
            _entries.Clear();
            _sessionEnded = false;
            if (!File.Exists(Path))
                return;

            try
            {
                var saved = JsonSerializer.Deserialize(
                    File.ReadAllText(Path), ScrobbleQueueJsonContext.Default.SavedScrobbleQueue);
                if (saved is null || saved.Version is < 1 or > SavedScrobbleQueue.CurrentVersion)
                {
                    SetAside($"version {saved?.Version.ToString() ?? "missing"}; this build reads {SavedScrobbleQueue.CurrentVersion}");
                    return;
                }

                _entries.AddRange(saved.Scrobbles.Where(e =>
                    !string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Track)));
                TrimToCapacity();
                _sessionEnded = saved.SessionEnded;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                SetAside(ex.Message);
            }
        }
    }

    public void Enqueue(ScrobbleEntry entry)
    {
        lock (_sync)
        {
            _entries.Add(entry);
            TrimToCapacity();
            Save();
        }
    }

    public IReadOnlyList<ScrobbleEntry> Peek(int max)
    {
        lock (_sync)
            return _entries.Take(max).ToArray();
    }

    /// <summary>Removes the oldest <paramref name="count"/> scrobbles (the ones a <see cref="Peek"/> returned).</summary>
    public void RemoveOldest(int count)
    {
        lock (_sync)
        {
            _entries.RemoveRange(0, Math.Min(count, _entries.Count));
            Save();
        }
    }

    /// <summary>Empties the queue and ends <see cref="SessionEnded"/>: the user's own Disconnect.</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _sessionEnded = false;
            Save();
        }
    }

    private void TrimToCapacity()
    {
        var excess = _entries.Count - Capacity;
        if (excess <= 0)
            return;
        AppLog.Write(_logCategory, $"Scrobble queue full; dropped the {excess} oldest.");
        _entries.RemoveRange(0, excess);
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var temp = Path + ".tmp";
            var saved = new SavedScrobbleQueue { Scrobbles = [.. _entries], SessionEnded = _sessionEnded };
            File.WriteAllText(temp, JsonSerializer.Serialize(saved, ScrobbleQueueJsonContext.Default.SavedScrobbleQueue));
            File.Move(temp, Path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still queued in memory; the next write tries again.
            AppLog.Write(_logCategory, $"Couldn't save the scrobble queue: {ex.Message}");
        }
    }

    private void SetAside(string reason)
    {
        AppLog.Write(_logCategory, $"Setting the scrobble queue file aside ({reason}); starting empty.");
        try
        {
            File.Move(Path, Path + ".unreadable", overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write(_logCategory, $"Couldn't move it aside: {ex.Message}");
        }
    }
}
