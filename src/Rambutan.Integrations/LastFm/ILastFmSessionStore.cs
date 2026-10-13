namespace Rambutan.Integrations.LastFm;

/// <summary>
/// Where the Last.fm session key lives between runs. A port rather than a file
/// here because it's a credential: App implements it beside the cookie vault,
/// with the same at-rest protection (DPAPI on Windows, a 0600 file on Linux).
/// </summary>
public interface ILastFmSessionStore
{
    /// <summary>The saved session, or null when there is none or it can't be read.</summary>
    LastFmSession? Load();

    void Save(LastFmSession session);

    void Clear();
}
