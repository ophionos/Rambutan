using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Stations;

namespace Rambutan.App.Services;

/// <summary>
/// The Last.fm stations Home shows and artists' stations: which
/// ones, for whom, and loading one into rows that play. Active only while the
/// setting is on and a Last.fm account is connected, since the account's name
/// is what Last.fm is asked about. Off by default, like every outbound integration.
///
/// Each list is saved as it loads (<see cref="StationSnapshotStore"/>), so Home
/// shows the last one at startup without asking Last.fm, and asks again only
/// when it is older than <see cref="Freshness"/> or Home is refreshed. The
/// search outcomes are saved too (<see cref="StationMatchStore"/>), so a track
/// that comes round again isn't searched again.
/// </summary>
public sealed class HomeStations(
    IStationSource source, StationResolver resolver, Func<string?> username,
    StationSnapshotStore? snapshots = null, StationMatchStore? matches = null, TimeProvider? clock = null)
{
    /// <summary>A saved list older than this is replaced when Home shows it.</summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromHours(12);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private bool _enabled;
    private string? _savedFor;
    private Dictionary<StationKind, StationSnapshot> _saved = [];
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

    /// <summary>Call when the Last.fm connection may have changed.</summary>
    public void ConnectionChanged() => Changed?.Invoke();

    /// <summary>The list last loaded for the active user, or null.</summary>
    public StationSnapshot? Saved(StationKind kind) =>
        User is { } user ? SavedFor(user).GetValueOrDefault(kind) : null;

    /// <summary>The list is old enough to load again (or dated in the future).</summary>
    public bool IsStale(StationSnapshot snapshot)
    {
        var age = _clock.GetUtcNow() - snapshot.LoadedAt;
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
}
