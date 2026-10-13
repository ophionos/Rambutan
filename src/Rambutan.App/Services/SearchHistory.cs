using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Services;

/// <summary>
/// <c>search-history.json</c>: the queries searched on this machine, newest first.
/// Its own file, versioned like every format a user builds up.
/// It never leaves the machine.
/// </summary>
public sealed class SavedSearchHistory
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<string> Queries { get; set; } = [];
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedSearchHistory))]
internal sealed partial class SearchHistoryJsonContext : JsonSerializerContext;

/// <summary>Reads and writes <see cref="SavedSearchHistory"/> with atomic writes,
/// like <see cref="QueueStore"/>. Never throws on load: a missing, corrupt or
/// newer file means an empty history.</summary>
public sealed class SearchHistoryStore(string dataDirectory)
{
    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "search-history.json");

    public IReadOnlyList<string> Load()
    {
        try
        {
            if (!File.Exists(Path))
                return [];

            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), SearchHistoryJsonContext.Default.SavedSearchHistory);
            if (saved is null)
                return [];

            if (saved.Version > SavedSearchHistory.CurrentVersion || saved.Version < 1)
            {
                AppLog.Write("search", $"Ignoring search-history.json version {saved.Version}; this build reads {SavedSearchHistory.CurrentVersion}.");
                return [];
            }

            return saved.Queries;
        }
        catch (Exception ex)
        {
            AppLog.Write("search", $"Couldn't read the search history, starting empty: {ex.Message}");
            return [];
        }
    }

    public void Save(IReadOnlyList<string> queries)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new SavedSearchHistory { Queries = [.. queries] }, SearchHistoryJsonContext.Default.SavedSearchHistory));
        File.Move(temp, Path, overwrite: true);
    }
}

/// <summary>
/// The recent-queries list under the search box: newest first, one entry per
/// query whatever its case (the latest spelling wins), at most
/// <see cref="MaxKept"/>. Every change is saved straight away; a failed save is
/// logged and the list in memory still holds.
/// </summary>
public sealed class SearchHistory
{
    public const int MaxKept = 20;

    private readonly SearchHistoryStore? _store;
    private readonly List<string> _queries;

    public SearchHistory(SearchHistoryStore? store)
    {
        _store = store;
        _queries = Normalize(store?.Load() ?? []);
    }

    public IReadOnlyList<string> Queries => _queries;

    public event Action? Changed;

    public void Add(string query)
    {
        query = query.Trim();
        if (query.Length == 0)
            return;
        if (_queries.Count > 0 && _queries[0] == query)
            return;

        _queries.RemoveAll(q => string.Equals(q, query, StringComparison.OrdinalIgnoreCase));
        _queries.Insert(0, query);
        if (_queries.Count > MaxKept)
            _queries.RemoveRange(MaxKept, _queries.Count - MaxKept);
        Commit();
    }

    public void Remove(string query)
    {
        if (_queries.Remove(query))
            Commit();
    }

    public void Clear()
    {
        if (_queries.Count == 0)
            return;
        _queries.Clear();
        Commit();
    }

    private void Commit()
    {
        try
        {
            _store?.Save(_queries);
        }
        catch (Exception ex)
        {
            AppLog.Write("search", $"Couldn't save the search history: {ex.Message}");
        }

        Changed?.Invoke();
    }

    /// <summary>A hand-edited file can hold blanks, duplicates or too many.</summary>
    private static List<string> Normalize(IEnumerable<string?> queries) =>
        queries.Where(q => !string.IsNullOrWhiteSpace(q))
            .Select(q => q!.Trim())
            .DistinctBy(q => q, StringComparer.OrdinalIgnoreCase)
            .Take(MaxKept)
            .ToList();
}
