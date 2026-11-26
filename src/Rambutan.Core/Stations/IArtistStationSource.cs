namespace Rambutan.Core.Stations;

/// <summary>A listening service that builds a station from one artist.</summary>
public interface IArtistStationSource
{
    /// <summary>
    /// A station of the artist and artists like them, in the service's order.
    /// Throws <see cref="StationUnavailableException"/> when the service answers
    /// without a station, <see cref="StationTokenRejectedException"/> when it
    /// refuses the user's token, and <see cref="HttpRequestException"/> when it
    /// can't be reached.
    /// </summary>
    Task<IReadOnlyList<StationTrack>> GetArtistStationAsync(ArtistSeed artist, CancellationToken ct = default);
}
