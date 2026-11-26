namespace Rambutan.Integrations.LastFm;

/// <summary>
/// Where the user's own Last.fm API key and shared secret live between runs.
/// The secret is a credential, so App keeps it as it keeps the session
/// (<see cref="ILastFmSessionStore"/>), never in the settings file.
/// </summary>
public interface ILastFmKeyStore
{
    /// <summary>The saved key, or null when there is none or it can't be read.</summary>
    LastFmApiKey? Load();

    void Save(LastFmApiKey key);

    void Clear();
}
