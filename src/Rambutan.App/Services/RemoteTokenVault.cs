using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Remote;

namespace Rambutan.App.Services;

/// <summary>On-disk shape of the phone remote's pairing token, in the keyring item or the protected file.</summary>
public sealed class SavedRemotePairing
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string? Token { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SavedRemotePairing))]
internal sealed partial class RemotePairingJsonContext : JsonSerializerContext;

/// <summary>
/// The phone remote's pairing token: a credential, so it's kept like the cookies
/// and the Last.fm session (<see cref="StoredCredential"/>: DPAPI on Windows, the
/// system keyring on Linux, an owner-only file on Linux without one), not in
/// settings.json. It lasts until the user resets pairing, so a paired phone keeps
/// working across restarts. Calls block on the keyring; run them off the UI thread.
/// The OBS overlay keeps its own token the same way, under its own name.
/// </summary>
public sealed class RemoteTokenVault(
    string dataDirectory, SystemKeyring? keyring = null,
    string name = "remote-pairing", string labelKey = "keyring.label.remote")
{
    private readonly StoredCredential _credential = new(
        dataDirectory, name + ".bin", name,
        () => Strings.Get(labelKey), keyring ?? SystemKeyring.None);

    /// <summary>The kept token, or a new one (kept) when there is none or it's unreadable.</summary>
    public string LoadOrCreate()
    {
        var (text, problem) = _credential.Read();
        if (problem is not CredentialProblem.None)
            AppLog.Write("remote", $"The saved pairing couldn't be read ({problem}); phones must pair again.");
        if (text is not null)
        {
            try
            {
                var saved = JsonSerializer.Deserialize(text, RemotePairingJsonContext.Default.SavedRemotePairing);
                if (saved is { Version: SavedRemotePairing.CurrentVersion } && PairingToken.IsWellFormed(saved.Token))
                    return saved.Token!;
            }
            catch (JsonException)
            {
            }
            AppLog.Write("remote", "The saved pairing is unreadable; phones must pair again.");
        }
        return Rotate();
    }

    /// <summary>Replaces the token; phones paired with the old one are refused.</summary>
    public string Rotate()
    {
        var token = PairingToken.Create();
        var problem = _credential.Write(JsonSerializer.Serialize(
            new SavedRemotePairing { Token = token }, RemotePairingJsonContext.Default.SavedRemotePairing));
        // The token still works for this session; phones pair again next time.
        if (problem is not CredentialProblem.None)
            AppLog.Write("remote", $"Couldn't save the pairing ({problem}).");
        return token;
    }
}
