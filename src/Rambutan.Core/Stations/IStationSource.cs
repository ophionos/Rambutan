namespace Rambutan.Core.Stations;

/// <summary>
/// Where stations come from (Last.fm's, today). Each call may return a different
/// sample of the same station, so a caller keeps what it loaded rather than
/// asking again for the same list.
/// </summary>
public interface IStationSource
{
    /// <summary>
    /// The station's tracks, in the service's order. Throws
    /// <see cref="StationUnavailableException"/> when the service answers without
    /// a station, and <see cref="HttpRequestException"/> when it can't be reached.
    /// </summary>
    Task<IReadOnlyList<StationTrack>> GetStationAsync(
        string user, StationKind kind, CancellationToken ct = default);

    /// <summary>
    /// A station of the artist and artists like them, with the same
    /// exceptions as <see cref="GetStationAsync"/>; an artist the service doesn't
    /// know is <see cref="StationUnavailableException"/>.
    /// </summary>
    Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(string artist, CancellationToken ct = default);
}
