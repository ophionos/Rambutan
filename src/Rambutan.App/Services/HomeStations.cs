using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Stations;

namespace Rambutan.App.Services;

/// <summary>
/// The Last.fm stations Home shows, and artists' stations from Last.fm and
/// ListenBrainz: which ones, for whom, and loading one into rows that play.
/// Last.fm's are active only while the setting is on and a Last.fm account is
/// connected, since the account's name is what Last.fm is asked about.
/// ListenBrainz's artist stations are active while the stations' own
/// ListenBrainz account is connected (<paramref name="listenBrainzConnected"/>),
/// whatever the Last.fm setting: connecting that token is their opt-in. Both are
/// off by default, like every outbound integration.
///
/// Each Last.fm list is saved as it loads (<see cref="StationSnapshotStore"/>), so Home
/// shows the last one at startup without asking Last.fm, and asks again only
/// when it is older than <see cref="Freshness"/> or Home is refreshed. The
/// search outcomes of every station are saved too (<see cref="StationMatchStore"/>),
/// so a track that comes round again, from either service, isn't searched again.
///
/// The playlists ListenBrainz makes for the user ("Created for you") show on
/// Home while their own setting is on (<see cref="CreatedForEnabled"/>) and the
/// stations' ListenBrainz account is connected; the account's user name is
/// what ListenBrainz is asked about, without the token. Their list is saved
/// (<see cref="CreatedForStore"/>) and fetched again when older than
/// <see cref="Freshness"/>; a playlist's tracks never change, so a playlist
/// loaded once is shown from the file until Home is refreshed.
/// </summary>
/// <param name="listenBrainzTokenRefused">Called, on the loading thread, when
/// ListenBrainz refuses the stations' token.</param>
/// <param name="listenBrainzUser">The stations' ListenBrainz account's user name.</param>
public sealed class HomeStations(
    IStationSource source, StationResolver resolver, Func<string?> username,
    StationSnapshotStore? snapshots = null, StationMatchStore? matches = null, TimeProvider? clock = null,
    IArtistStationSource? listenBrainz = null, Func<bool>? listenBrainzConnected = null,
    Action? listenBrainzTokenRefused = null,
    ICreatedForSource? createdFor = null, Func<string?>? listenBrainzUser = null, CreatedForStore? createdForStore = null)
{
    /// <summary>A saved list older than this is replaced when Home shows it.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(12);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _enabled;
    private bool _createdForEnabled;
    private string? _savedFor;
    private Dictionary<StationKind, StationSnapshot> _saved = [];
    private string? _createdForSavedFor;
    private CreatedForSaved? _createdForSaved;
    // The refresh running for the user in _refreshingFor, so the ones that ask
    // meanwhile share it rather than ask ListenBrainz again.
    private Task? _refreshing;
    private string? _refreshingFor;
    private DateTimeOffset? _createdForFailedAt;

    /// <summary>After the list failed to load, only Home's own refresh asks
    /// again before this has passed.</summary>
    public static readonly TimeSpan CreatedForRetryAfter = TimeSpan.FromMinutes(5);
    private Task? _matchesLoaded;
    // Saves run one after another in the order they were made, so an older
    // copy of the lists never lands after a newer one.
    private Task _lastSave = Task.CompletedTask;

    public static IReadOnlyList<StationKind> Shown { get; } = [StationKind.Mix, StationKind.Recommended];

    /// <summary>The setting or the connection changed, so Home adds or removes
    /// its station shelves. Raised on the caller's thread.</summary>
    public event Action? Changed;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
                return;
            _enabled = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Whose stations to load; null while Home shows none.</summary>
    public string? User => Enabled && username() is { Length: > 0 } name ? name : null;

    /// <summary>The setting that shows the user's ListenBrainz playlists on Home.</summary>
    public bool CreatedForEnabled
    {
        get => _createdForEnabled;
        set
        {
            if (_createdForEnabled == value)
                return;
            _createdForEnabled = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Whose ListenBrainz playlists to show; null while Home shows none.</summary>
    public string? CreatedForUser =>
        CreatedForEnabled && ListenBrainzAvailable && createdFor is not null
        && listenBrainzUser?.Invoke() is { Length: > 0 } name ? name : null;

    /// <summary>The playlists Home shows, from the list last fetched: the
    /// current one of each kind, in Home's order (<see cref="CreatedFor.Current"/>).</summary>
    public IReadOnlyList<CreatedForPlaylist> CreatedForShown =>
        CreatedForUser is { } user && CreatedForSavedFor(user) is { } saved
            ? CreatedFor.Current(saved.Playlists, _clock.GetUtcNow())
            : [];

    /// <summary>The playlist's tracks as last loaded for the active user, or null.</summary>
    public CreatedForSnapshot? SavedCreatedFor(string playlistId) =>
        CreatedForUser is { } user ? CreatedForSavedFor(user)?.Loaded.GetValueOrDefault(playlistId) : null;

    /// <summary>
    /// Fetches the user's list of playlists when none is saved, the saved one
    /// is older than <see cref="Freshness"/>, or <paramref name="fresh"/>; saves
    /// it, and raises <see cref="Changed"/> on the caller's context when that
    /// changes <see cref="CreatedForShown"/>. A user ListenBrainz doesn't know has
    /// an empty list. Never throws but for cancellation: a failure keeps the
    /// saved list and is logged. Does nothing while <see cref="CreatedForUser"/> is null.
    /// </summary>
    public Task RefreshCreatedForAsync(bool fresh, CancellationToken ct = default)
    {
        if (CreatedForUser is not { } user)
            return Task.CompletedTask;
        if (_refreshing is { IsCompleted: false } running
            && string.Equals(_refreshingFor, user, StringComparison.OrdinalIgnoreCase)
            && !fresh)
            return running;
        if (!fresh && CreatedForSavedFor(user) is { } saved && !IsStale(saved.ListedAt))
            return Task.CompletedTask;
        // Home asks on every connection change, which the scrobbler reports often.
        if (!fresh && _createdForFailedAt is { } failed && _clock.GetUtcNow() - failed < CreatedForRetryAfter)
            return Task.CompletedTask;
        _refreshingFor = user;
        return _refreshing = RefreshCreatedForAsync(user, ct);
    }

    private async Task RefreshCreatedForAsync(string user, CancellationToken ct)
    {
        IReadOnlyList<CreatedForPlaylist> listed;
        try
        {
            listed = await createdFor!.GetCreatedForAsync(user, ct);
        }
        catch (StationUnavailableException ex)
        {
            AppLog.Write("stations", $"ListenBrainz has no playlists for the user: {ex.Message}");
            listed = [];
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            AppLog.Write("stations", $"Couldn't list the ListenBrainz playlists: {ex.Message}");
            _createdForFailedAt = _clock.GetUtcNow();
            return;
        }
        _createdForFailedAt = null;
        ct.ThrowIfCancellationRequested();
        // The account or the setting changed while ListenBrainz answered.
        if (!string.Equals(CreatedForUser, user, StringComparison.OrdinalIgnoreCase))
            return;

        var before = ShownIds();
        var previous = CreatedForSavedFor(user);
        var ids = listed.Select(p => p.Id).ToHashSet(StringComparer.Ordinal);
        var kept = (previous?.Loaded ?? new Dictionary<string, CreatedForSnapshot>())
            .Where(l => ids.Contains(l.Key))
            .ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
        var saved = _createdForSaved = new CreatedForSaved(_clock.GetUtcNow(), listed, kept);
        // Before the save, which continues off the caller's context.
        if (!before.SequenceEqual(ShownIds()))
            Changed?.Invoke();
        await Save(() => createdForStore?.Save(user, saved));
    }

    /// <summary>
    /// The playlist's tracks, resolved to what plays, and saved under its id.
    /// Throws <see cref="StationUnavailableException"/> when ListenBrainz no
    /// longer has it, <see cref="HttpRequestException"/> when it or YouTube
    /// Music can't be reached, and <see cref="InvalidOperationException"/> when
    /// <see cref="CreatedForUser"/> is null.
    /// </summary>
    public async Task<CreatedForSnapshot> LoadCreatedForAsync(CreatedForPlaylist playlist, CancellationToken ct = default)
    {
        var user = CreatedForUser ?? throw new InvalidOperationException("No ListenBrainz playlist is active.");
        var rows = await ResolveAsync(() => createdFor!.GetPlaylistAsync(playlist.Id, ct), ct);
        var snapshot = new CreatedForSnapshot(playlist.Id, _clock.GetUtcNow(), rows);

        // Kept only while the list names it; the list may have moved on meanwhile.
        if (CreatedForSavedFor(user) is { } current && current.Playlists.Any(p => p.Id == playlist.Id))
        {
            var loaded = current.Loaded.ToDictionary(l => l.Key, l => l.Value, StringComparer.Ordinal);
            loaded[playlist.Id] = snapshot;
            var saved = _createdForSaved = current with { Loaded = loaded };
            await Save(() => createdForStore?.Save(user, saved));
        }
        else
        {
            await Save(null);
        }
        return snapshot;
    }

    private List<string> ShownIds() => CreatedForShown.Select(p => p.Id).ToList();

    /// <summary>An artist's ListenBrainz station can load: the stations'
    /// ListenBrainz account is connected.</summary>
    public bool ListenBrainzAvailable => listenBrainz is not null && listenBrainzConnected?.Invoke() == true;

    /// <summary>Call when the Last.fm or the stations' ListenBrainz connection may have changed.</summary>
    public void ConnectionChanged() => Changed?.Invoke();

    /// <summary>The list last loaded for the active user, or null.</summary>
    public StationSnapshot? Saved(StationKind kind) =>
        User is { } user ? SavedFor(user).GetValueOrDefault(kind) : null;

    /// <summary>The list is old enough to load again (or dated in the future).</summary>
    public bool IsStale(StationSnapshot snapshot) => IsStale(snapshot.LoadedAt);

    private bool IsStale(DateTimeOffset loadedAt)
    {
        var age = _clock.GetUtcNow() - loadedAt;
        return age < TimeSpan.Zero || age > Freshness;
    }

    /// <summary>
    /// A fresh sample of the station, resolved to what plays, and saved. Throws
    /// <see cref="StationUnavailableException"/> when Last.fm has no station to
    /// give, <see cref="HttpRequestException"/> when it or YouTube Music can't be
    /// reached, and <see cref="InvalidOperationException"/> when none is active.
    /// </summary>
    public async Task<StationSnapshot> LoadAsync(StationKind kind, CancellationToken ct = default)
    {
        var user = User ?? throw new InvalidOperationException("No Last.fm station is active.");
        var rows = await ResolveAsync(() => source.GetStationAsync(user, kind, ct), ct);
        var snapshot = new StationSnapshot(kind, _clock.GetUtcNow(), rows);

        var saved = SavedFor(user);
        saved[kind] = snapshot;
        var all = saved.Values.ToList();
        await Save(() => snapshots?.Save(user, all));
        return snapshot;
    }

    /// <summary>
    /// A fresh sample of an artist's station, opened from the artist's
    /// page, resolved to what plays. Gated like Home's stations, since Last.fm is
    /// told the artist's name. The list itself isn't saved, only the search
    /// outcomes; the exceptions are <see cref="LoadAsync"/>'s.
    /// </summary>
    public async Task<(IReadOnlyList<StationRow> Rows, DateTimeOffset LoadedAt)> LoadArtistAsync(
        string artist, CancellationToken ct = default)
    {
        if (User is null)
            throw new InvalidOperationException("No Last.fm station is active.");
        var rows = await ResolveAsync(() => source.GetArtistStationAsync(artist, ct), ct);
        await Save(null);
        return (rows, _clock.GetUtcNow());
    }

    /// <summary>
    /// A fresh sample of an artist's ListenBrainz station (LB Radio), resolved
    /// as <see cref="LoadArtistAsync"/> resolves Last.fm's, with its exceptions;
    /// <see cref="InvalidOperationException"/> when
    /// <see cref="ListenBrainzAvailable"/> is false. A refused token
    /// (<see cref="StationTokenRejectedException"/>) is reported to the account
    /// first, then thrown.
    /// </summary>
    public async Task<(IReadOnlyList<StationRow> Rows, DateTimeOffset LoadedAt)> LoadListenBrainzArtistAsync(
        ArtistSeed artist, CancellationToken ct = default)
    {
        if (!ListenBrainzAvailable)
            throw new InvalidOperationException("No ListenBrainz station is active.");
        IReadOnlyList<StationRow> rows;
        try
        {
            rows = await ResolveAsync(() => listenBrainz!.GetArtistStationAsync(artist, ct), ct);
        }
        catch (StationTokenRejectedException)
        {
            listenBrainzTokenRefused?.Invoke();
            throw;
        }
        await Save(null);
        return (rows, _clock.GetUtcNow());
    }

    private async Task<IReadOnlyList<StationRow>> ResolveAsync(
        Func<Task<IReadOnlyList<StationTrack>>> fetch, CancellationToken ct)
    {
        // The saved outcomes load before the first resolve, or its save would
        // replace them with only the new ones.
        if (matches is not null)
            await (_matchesLoaded ??= Task.Run(matches.Load));
        return await resolver.ResolveAsync(await fetch(), ct);
    }

    private Task Save(Action? lists) =>
        _lastSave = _lastSave.ContinueWith(_ =>
        {
            lists?.Invoke();
            matches?.Save();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

    private Dictionary<StationKind, StationSnapshot> SavedFor(string user)
    {
        if (!string.Equals(_savedFor, user, StringComparison.OrdinalIgnoreCase))
        {
            _saved = (snapshots?.Load(user) ?? []).ToDictionary(s => s.Kind);
            _savedFor = user;
        }
        return _saved;
    }

    private CreatedForSaved? CreatedForSavedFor(string user)
    {
        if (!string.Equals(_createdForSavedFor, user, StringComparison.OrdinalIgnoreCase))
        {
            _createdForSaved = createdForStore?.Load(user);
            _createdForSavedFor = user;
        }
        return _createdForSaved;
    }
}
