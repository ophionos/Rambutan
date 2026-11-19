namespace Rambutan.Integrations.ListenBrainz;

/// <summary>A ListenBrainz account on one server: the token only ever goes to the server it was entered for.</summary>
public sealed record ListenBrainzConnection(Uri Server, string Username, string Token);

/// <summary>
/// Where the ListenBrainz token lives between runs. A port rather than a file
/// here because it is a credential: App implements it beside the cookie vault,
/// with the same at-rest protection (DPAPI on Windows, a 0600 file on Linux).
/// </summary>
public interface IListenBrainzTokenStore
{
    /// <summary>The saved connection, or null when there is none or it cannot be read.</summary>
    ListenBrainzConnection? Load();

    void Save(ListenBrainzConnection connection);

    void Clear();
}
