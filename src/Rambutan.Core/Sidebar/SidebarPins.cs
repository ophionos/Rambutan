namespace Rambutan.Core.Sidebar;

public enum PinKind
{
    Album,
    Playlist,
    /// <summary>A playlist on this computer.</summary>
    LocalPlaylist,
}

/// <summary>
/// An album or playlist the user pinned to the sidebar. <paramref name="Id"/> is
/// what opens it: an album's browse id, a playlist's id, a local playlist's id.
/// The title and cover are the ones it had when pinned (or last seen), so the
/// row can draw signed out and offline. A local playlist has no cover link: its
/// cover comes from its first track.
/// </summary>
public sealed record SidebarPin(PinKind Kind, string Id, string Title, string? CoverUrl = null);

/// <param name="ReadOnly">The file is a newer build's, or unreadable and still
/// in place: it must not be written over, so the pins can't be changed.</param>
public sealed record LoadedSidebarPins(IReadOnlyList<SidebarPin> Pins, bool ReadOnly = false);

public interface ISidebarPinStore
{
    /// <summary>Never throws: a file that can't be read gives no pins.</summary>
    LoadedSidebarPins Load();

    void Save(IReadOnlyList<SidebarPin> pins);
}

/// <summary>
/// The sidebar's pins, in the user's order. Changes are saved as they are made;
/// a failed save keeps the change for this run and is logged. Not thread-safe:
/// the UI thread only.
/// </summary>
public sealed class SidebarPinLibrary
{
    private readonly ISidebarPinStore _store;
    private readonly Action<string>? _log;
    private List<SidebarPin> _pins;

    public SidebarPinLibrary(ISidebarPinStore store, Action<string>? log = null)
    {
        _store = store;
        _log = log;
        var loaded = store.Load();
        _pins = Clean(loaded.Pins);
        IsReadOnly = loaded.ReadOnly;
    }

    public event Action? Changed;

    public IReadOnlyList<SidebarPin> Pins => _pins;

    /// <summary>The pins can't be changed (see <see cref="LoadedSidebarPins"/>);
    /// every change below then does nothing and returns false.</summary>
    public bool IsReadOnly { get; }

    public bool IsPinned(PinKind kind, string? id) => IndexOf(kind, id) >= 0;

    /// <summary>Adds the pin at the end. False when it is pinned already, has no
    /// id or title, or the pins are read-only.</summary>
    public bool Pin(SidebarPin pin)
    {
        if (IsReadOnly || !IsValid(pin) || IsPinned(pin.Kind, pin.Id))
            return false;
        return Apply([.. _pins, pin]);
    }

    public bool Unpin(PinKind kind, string? id)
    {
        var index = IndexOf(kind, id);
        if (IsReadOnly || index < 0)
            return false;
        var next = new List<SidebarPin>(_pins);
        next.RemoveAt(index);
        return Apply(next);
    }

    /// <summary>Moves a pin up (<paramref name="by"/> below zero) or down, as
    /// far as the list goes. False when it doesn't move.</summary>
    public bool Move(PinKind kind, string? id, int by)
    {
        var from = IndexOf(kind, id);
        if (IsReadOnly || from < 0)
            return false;
        var to = Math.Clamp((long)from + by, 0, _pins.Count - 1);
        if (to == from)
            return false;
        var next = new List<SidebarPin>(_pins);
        var pin = next[from];
        next.RemoveAt(from);
        next.Insert((int)to, pin);
        return Apply(next);
    }

    public bool CanMove(PinKind kind, string? id, int by)
    {
        var from = IndexOf(kind, id);
        return !IsReadOnly && from >= 0 && Math.Clamp((long)from + by, 0, _pins.Count - 1) != from;
    }

    /// <summary>Takes a pinned album's or playlist's title and cover as its page
    /// shows them now (a rename, a new cover). A null or empty title keeps the
    /// old one; a null cover keeps the old one too, since a page that failed
    /// has none. False when nothing changed.</summary>
    public bool Refresh(PinKind kind, string? id, string? title, string? coverUrl)
    {
        var index = IndexOf(kind, id);
        if (IsReadOnly || index < 0)
            return false;
        var old = _pins[index];
        var updated = old with
        {
            Title = string.IsNullOrWhiteSpace(title) ? old.Title : title.Trim(),
            CoverUrl = string.IsNullOrEmpty(coverUrl) ? old.CoverUrl : coverUrl,
        };
        if (updated == old)
            return false;
        var next = new List<SidebarPin>(_pins) { [index] = updated };
        return Apply(next);
    }

    private int IndexOf(PinKind kind, string? id) =>
        string.IsNullOrEmpty(id) ? -1 : _pins.FindIndex(p => p.Kind == kind && p.Id == id);

    private bool Apply(List<SidebarPin> next)
    {
        _pins = next;
        try
        {
            _store.Save(next);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"couldn't save the sidebar's pins: {ex.Message}");
        }
        Changed?.Invoke();
        return true;
    }

    private static bool IsValid(SidebarPin pin) =>
        Enum.IsDefined(pin.Kind) && !string.IsNullOrEmpty(pin.Id) && !string.IsNullOrWhiteSpace(pin.Title);

    // A hand-edited file: an entry that can't open is dropped, and so is a repeat.
    private static List<SidebarPin> Clean(IReadOnlyList<SidebarPin> pins)
    {
        var seen = new HashSet<(PinKind, string)>();
        return [.. pins.Where(p => p is not null && IsValid(p) && seen.Add((p.Kind, p.Id)))];
    }
}
