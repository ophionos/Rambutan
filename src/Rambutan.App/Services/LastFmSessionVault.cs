using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.LastFm;

namespace Rambutan.App.Services;

/// <summary>Stored shape of the Last.fm session, in the keyring item or the protected file.</summary>
public sealed class SavedLastFmSession
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Username { get; set; }
    public string? Key { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedLastFmSession))]
internal sealed partial class LastFmSessionJsonContext : JsonSerializerContext;

/// <summary>
/// The Last.fm session key, kept beside the cookie vault in the same way
/// (<see cref="StoredCredential"/>): DPAPI on Windows, the system keyring on
/// Linux, an owner-only file on Linux without one. Versioned JSON inside, like
/// every format Rambutan owns. Called on the scrobbler's own worker, so the
/// keyring's blocking calls never reach the UI thread.
/// </summary>
public sealed class LastFmSessionVault(string dataDirectory, SystemKeyring? keyring = null) : ILastFmSessionStore
{
    private readonly StoredCredential _credential = new(
        dataDirectory, "lastfm-session.bin", "lastfm-session",
        static () => Strings.Get("keyring.label.lastFm"), keyring ?? SystemKeyring.None);

    public LastFmSession? Load()
    {
        var (text, problem) = _credential.Read();
        if (problem is not CredentialProblem.None)
            AppLog.Write("lastfm", $"The saved session couldn't be read ({problem}); connect again, or restart with the keyring available.");
        if (text is null)
            return null;
        try
        {
            var saved = JsonSerializer.Deserialize(text, LastFmSessionJsonContext.Default.SavedLastFmSession);
            if (saved is { Version: SavedLastFmSession.CurrentVersion, Username: { Length: > 0 } name, Key: { Length: > 0 } key })
                return new LastFmSession(name, key);
            AppLog.Write("lastfm", $"Ignoring a saved session this build can't read (version {saved?.Version}).");
        }
        catch (JsonException)
        {
            AppLog.Write("lastfm", "The saved Last.fm session is unreadable; connect again.");
        }
        return null;
    }

    public void Save(LastFmSession session)
    {
        var problem = _credential.Write(JsonSerializer.Serialize(
            new SavedLastFmSession { Username = session.Username, Key = session.Key },
            LastFmSessionJsonContext.Default.SavedLastFmSession));
        if (problem is not CredentialProblem.None)
            AppLog.Write("lastfm", $"The session wasn't saved ({problem}); the next start will ask to connect again.");
    }

    public void Clear()
    {
        if (!_credential.Delete())
            AppLog.Write("lastfm", "Couldn't delete the saved Last.fm session.");
    }
}
