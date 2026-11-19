using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.App.Services;

/// <summary>Stored shape of the ListenBrainz connection, in the keyring item or the protected file.</summary>
public sealed class SavedListenBrainzConnection
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Server { get; set; }
    public string? Username { get; set; }
    public string? Token { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedListenBrainzConnection))]
internal sealed partial class ListenBrainzConnectionJsonContext : JsonSerializerContext;

/// <summary>
/// The ListenBrainz token, kept as the Last.fm session is
/// (<see cref="StoredCredential"/>): DPAPI on Windows, the system keyring on
/// Linux, an owner-only file on Linux without one. The server is stored with the
/// token, not as a setting, so a token only ever goes to the server it was
/// entered for. Called on the scrobbler's own worker, so the keyring's blocking
/// calls never reach the UI thread.
/// </summary>
public sealed class ListenBrainzTokenVault(string dataDirectory, SystemKeyring? keyring = null) : IListenBrainzTokenStore
{
    private readonly StoredCredential _credential = new(
        dataDirectory, "listenbrainz-token.bin", "listenbrainz-token",
        static () => Strings.Get("keyring.label.listenBrainz"), keyring ?? SystemKeyring.None);

    public ListenBrainzConnection? Load()
    {
        var (text, problem) = _credential.Read();
        if (problem is not CredentialProblem.None)
            AppLog.Write("listenbrainz", $"The saved token couldn't be read ({problem}); connect again, or restart with the keyring available.");
        if (text is null)
            return null;
        try
        {
            var saved = JsonSerializer.Deserialize(text, ListenBrainzConnectionJsonContext.Default.SavedListenBrainzConnection);
            if (saved is { Version: SavedListenBrainzConnection.CurrentVersion, Username: { Length: > 0 } name, Token: { Length: > 0 } token }
                && ListenBrainzClient.TryParseServer(saved.Server, out var server) && server is not null)
                return new ListenBrainzConnection(server, name, token);
            AppLog.Write("listenbrainz", $"Ignoring a saved connection this build can't read (version {saved?.Version}).");
        }
        catch (JsonException)
        {
            AppLog.Write("listenbrainz", "The saved ListenBrainz connection is unreadable; connect again.");
        }
        return null;
    }

    public void Save(ListenBrainzConnection connection)
    {
        var problem = _credential.Write(JsonSerializer.Serialize(
            new SavedListenBrainzConnection
            {
                Server = connection.Server.AbsoluteUri,
                Username = connection.Username,
                Token = connection.Token,
            },
            ListenBrainzConnectionJsonContext.Default.SavedListenBrainzConnection));
        if (problem is not CredentialProblem.None)
            AppLog.Write("listenbrainz", $"The token wasn't saved ({problem}); the next start will ask to connect again.");
    }

    public void Clear()
    {
        if (!_credential.Delete())
            AppLog.Write("listenbrainz", "Couldn't delete the saved ListenBrainz token.");
    }
}
