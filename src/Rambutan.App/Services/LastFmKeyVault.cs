using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.LastFm;

namespace Rambutan.App.Services;

/// <summary>Stored shape of the user's own Last.fm API key, in the keyring item or the protected file.</summary>
public sealed class SavedLastFmKey
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Key { get; set; }
    public string? Secret { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedLastFmKey))]
internal sealed partial class LastFmKeyJsonContext : JsonSerializerContext;

/// <summary>
/// The user's own Last.fm API key and shared secret, kept per profile as the
/// session is (<see cref="StoredCredential"/>), never in the settings file
/// (maintainer, 2026-11-26). Called on the scrobbler's own worker, so the
/// keyring's blocking calls never reach the UI thread.
/// </summary>
public sealed class LastFmKeyVault(string dataDirectory, SystemKeyring? keyring = null) : ILastFmKeyStore
{
    private readonly StoredCredential _credential = new(
        dataDirectory, "lastfm-key.bin", "lastfm-key",
        static () => Strings.Get("keyring.label.lastFmKey"), keyring ?? SystemKeyring.None);

    public LastFmApiKey? Load()
    {
        var (text, problem) = _credential.Read();
        if (problem is not CredentialProblem.None)
            AppLog.Write("lastfm", $"The saved API key couldn't be read ({problem}); enter it again, or restart with the keyring available.");
        if (text is null)
            return null;
        try
        {
            var saved = JsonSerializer.Deserialize(text, LastFmKeyJsonContext.Default.SavedLastFmKey);
            if (saved is { Version: SavedLastFmKey.CurrentVersion } && LastFmApiKey.From(saved.Key, saved.Secret) is { } key)
                return key;
            AppLog.Write("lastfm", $"Ignoring a saved API key this build can't read (version {saved?.Version}).");
        }
        catch (JsonException)
        {
            AppLog.Write("lastfm", "The saved Last.fm API key is unreadable; enter it again.");
        }
        return null;
    }

    public void Save(LastFmApiKey key)
    {
        var problem = _credential.Write(JsonSerializer.Serialize(
            new SavedLastFmKey { Key = key.Key, Secret = key.Secret },
            LastFmKeyJsonContext.Default.SavedLastFmKey));
        if (problem is not CredentialProblem.None)
            AppLog.Write("lastfm", $"The API key wasn't saved ({problem}); the next start will use the build's key.");
    }

    public void Clear()
    {
        if (!_credential.Delete())
            AppLog.Write("lastfm", "Couldn't delete the saved Last.fm API key.");
    }
}
