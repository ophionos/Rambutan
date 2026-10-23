using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;

namespace Rambutan.Core.Playlists;

/// <summary>Why the saved playlists couldn't be read as they are.</summary>
public enum LocalPlaylistsProblem
{
    None,

    /// <summary>The file couldn't be read, and the list is empty. When it was set
    /// aside (<see cref="LoadedLocalPlaylists.SetAside"/>), changes save as usual;
    /// when it couldn't be, nothing is saved over it.</summary>
    Unreadable,

    /// <summary>A newer build wrote it. It is left as it is, and nothing is saved
    /// over it, so going back to that build keeps the playlists.</summary>
    Newer,
}

/// <param name="SetAside">Where an unreadable file was moved; null otherwise,
/// also when moving it failed.</param>
public sealed record LoadedLocalPlaylists(
    IReadOnlyList<LocalPlaylist> Playlists,
    LocalPlaylistsProblem Problem = LocalPlaylistsProblem.None,
    string? SetAside = null);

/// <summary>Where the playlists are kept (<c>local-playlists.json</c>).</summary>
public interface ILocalPlaylistStore
{
    LoadedLocalPlaylists Load();

    /// <summary>Writes all of them. Throws when they couldn't be written.</summary>
    void Save(IReadOnlyList<LocalPlaylist> playlists);
}

/// <summary>A change was refused: the file is a newer build's, or unreadable and
/// still in its place, and is left as it is. The message isn't for display.</summary>
public sealed class LocalPlaylistsReadOnlyException()
    : InvalidOperationException("The saved playlists are left as they are and can't be changed here.");

/// <summary>
/// The user's local playlists, held in memory and saved whole on each change.
/// A change whose save fails is undone and the error thrown, so what shows is
/// always what is kept. Thread-safe; <see cref="Changed"/> is raised on the
/// thread that made the change, after the save.
/// </summary>
public sealed class LocalPlaylistLibrary
{
    private readonly ILocalPlaylistStore _store;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<string> _newId;
    private readonly Lock _lock = new();
    private List<LocalPlaylist>? _playlists;
    private LocalPlaylistsProblem _problem;
    private string? _setAside;

    public LocalPlaylistLibrary(
        ILocalPlaylistStore store, Func<DateTimeOffset>? now = null, Func<string>? newId = null)
    {
        _store = store;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        _newId = newId ?? (() => Guid.NewGuid().ToString("N"));
    }

    public event Action? Changed;

    public LocalPlaylistsProblem Problem
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _problem;
            }
        }
    }

    /// <summary>Where an unreadable file was set aside; null otherwise.</summary>
    public string? SetAside
    {
        get
        {
            lock (_lock)
            {
                EnsureLoaded();
                return _setAside;
            }
        }
    }

    /// <summary>The playlists, the one changed last first.</summary>
    public IReadOnlyList<LocalPlaylist> All
    {
        get
        {
            lock (_lock)
                return [.. EnsureLoaded().OrderByDescending(p => p.ChangedAt)];
        }
    }

    public LocalPlaylist? Find(string id)
    {
        lock (_lock)
            return EnsureLoaded().FirstOrDefault(p => p.Id == id);
    }

    /// <summary>A new playlist holding <paramref name="items"/> (none by default).
    /// Throws <see cref="ArgumentException"/> for a blank name.</summary>
    public LocalPlaylist Create(string name, IEnumerable<LocalPlaylistItem>? items = null)
    {
        var clean = LocalPlaylists.CleanName(name) ?? throw new ArgumentException("A playlist needs a name.", nameof(name));
        var now = _now();
        var created = LocalPlaylists.Add(new LocalPlaylist(_newId(), clean, [], now, now), items ?? [], now).Playlist;
        Change(list => list.Add(created));
        return created;
    }

    /// <summary>A copy of a YouTube Music playlist's tracks, duplicates included.</summary>
    public LocalPlaylist Copy(string name, IEnumerable<TrackInfo> tracks, string copiedFrom)
    {
        var clean = LocalPlaylists.CleanName(name) ?? throw new ArgumentException("A playlist needs a name.", nameof(name));
        var copy = LocalPlaylists.Copy(_newId(), clean, tracks, copiedFrom, _now());
        Change(list => list.Add(copy));
        return copy;
    }

    /// <summary>Appends what the playlist doesn't hold yet; how many were added,
    /// or null when there is no such playlist.</summary>
    public int? Add(string id, IEnumerable<LocalPlaylistItem> items)
    {
        var added = 0;
        var found = Update(id, p =>
        {
            (var next, added) = LocalPlaylists.Add(p, items, _now());
            return next;
        });
        return found ? added : null;
    }

    public bool RemoveAt(string id, int index) => Update(id, p => LocalPlaylists.RemoveAt(p, index, _now()));

    public bool Move(string id, int from, int to) => Update(id, p => LocalPlaylists.Move(p, from, to, _now()));

    /// <summary>Throws <see cref="ArgumentException"/> for a blank name.</summary>
    public bool Rename(string id, string name)
    {
        var clean = LocalPlaylists.CleanName(name) ?? throw new ArgumentException("A playlist needs a name.", nameof(name));
        return Update(id, p => p.Name == clean ? p : p with { Name = clean, ChangedAt = _now() });
    }

    public bool Delete(string id)
    {
        var found = false;
        Change(list => found = list.RemoveAll(p => p.Id == id) > 0);
        return found;
    }

    /// <summary>
    /// Keeps the rows <see cref="LocalPlaylists.ResolveAll"/> found again, unless
    /// the user changed the playlist since it was read: then the next read finds
    /// them again. Not a change of the user's, so nothing is raised.
    /// </summary>
    public void KeepResolved(LocalPlaylist resolved)
    {
        lock (_lock)
        {
            var list = EnsureLoaded();
            var index = list.FindIndex(p => p.Id == resolved.Id);
            if (index < 0 || list[index].ChangedAt != resolved.ChangedAt ||
                list[index].Items.Count != resolved.Items.Count || IsReadOnly)
                return;
            var before = list[index];
            list[index] = resolved;
            try
            {
                _store.Save(list);
            }
            catch (Exception ex)
            {
                // Found again on the next read instead.
                AppLog.Write("playlists", $"couldn't keep the found files of a playlist: {ex.Message}");
                list[index] = before;
            }
        }
    }

    private bool Update(string id, Func<LocalPlaylist, LocalPlaylist> change)
    {
        var found = false;
        Change(list =>
        {
            var index = list.FindIndex(p => p.Id == id);
            if (index < 0)
                return;
            found = true;
            list[index] = change(list[index]);
        });
        return found;
    }

    private void Change(Action<List<LocalPlaylist>> change)
    {
        lock (_lock)
        {
            var list = EnsureLoaded();
            if (IsReadOnly)
                throw new LocalPlaylistsReadOnlyException();
            var before = list.ToList();
            change(list);
            if (list.SequenceEqual(before))
                return;
            try
            {
                _store.Save(list);
            }
            catch
            {
                list.Clear();
                list.AddRange(before);
                throw;
            }
        }
        Changed?.Invoke();
    }

    // Saving would replace the only copy of what the user had.
    private bool IsReadOnly => _problem == LocalPlaylistsProblem.Newer ||
        _problem == LocalPlaylistsProblem.Unreadable && _setAside is null;

    private List<LocalPlaylist> EnsureLoaded()
    {
        if (_playlists is null)
        {
            var loaded = _store.Load();
            _playlists = [.. loaded.Playlists];
            _problem = loaded.Problem;
            _setAside = loaded.SetAside;
        }
        return _playlists;
    }
}
