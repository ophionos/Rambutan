using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.Linux;

namespace Rambutan.App.Services;

/// <summary>Where this run keeps credentials (the YouTube cookies, the Last.fm
/// session, the phone remote's pairing).</summary>
public enum CredentialStorage
{
    /// <summary>Windows: a file encrypted with DPAPI for the current user.</summary>
    WindowsDataProtection,

    /// <summary>Linux: the system keyring, through the Secret Service.</summary>
    Keyring,

    /// <summary>Linux with no keyring running: an unencrypted, owner-only (0600) file.</summary>
    PlainFile,
}

/// <summary>
/// The system keyring this run uses, looked for once, in the background, from
/// startup on. Only Linux looks; Windows keeps credentials with DPAPI.
/// </summary>
public sealed class SystemKeyring(Task<ISecretStore?> store)
{
    /// <summary>No keyring: DPAPI on Windows, the plaintext file on Linux.</summary>
    public static SystemKeyring None { get; } = new(Task.FromResult<ISecretStore?>(null));

    public static SystemKeyring Detect() => OperatingSystem.IsLinux()
        ? new(Task.Run(async () => (ISecretStore?)await SecretServiceStore.ConnectAsync(
            Strings.Get("keyring.defaultCollection")).ConfigureAwait(false)))
        : None;

    /// <summary>The keyring, or null when there is none. Waits for the search
    /// when it is still running; that is bounded by its own time limits.</summary>
    public ISecretStore? Store => store.GetAwaiter().GetResult();

    public CredentialStorage Storage => StorageFor(Store);

    /// <summary><see cref="Storage"/> without blocking: for the UI thread, which
    /// must not wait on a search that can take tens of seconds on a hung bus.</summary>
    public async Task<CredentialStorage> GetStorageAsync()
    {
        ISecretStore? found;
        try
        {
            found = await store.ConfigureAwait(false);
        }
        catch (Exception)
        {
            found = null;
        }
        return StorageFor(found);
    }

    private static CredentialStorage StorageFor(ISecretStore? found) =>
        found is not null ? CredentialStorage.Keyring
        : OperatingSystem.IsWindows() ? CredentialStorage.WindowsDataProtection
        : CredentialStorage.PlainFile;
}

/// <summary>Why a credential couldn't be read or kept.</summary>
public enum CredentialProblem
{
    None,

    /// <summary>It was stored in the keyring, and this run has no keyring.</summary>
    KeyringMissing,

    /// <summary>The keyring is there and refused: locked, prompt dismissed, no answer.</summary>
    KeyringFailed,

    /// <summary>The file couldn't be written.</summary>
    FileFailed,
}

/// <summary>On-disk record that a credential lives in the keyring, beside where
/// its file would be. It holds no secret; it lets a run without a keyring say
/// where the sign-in went instead of silently starting signed out.</summary>
public sealed class KeyringMarker
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public string Store { get; set; } = "secret-service";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(KeyringMarker))]
internal sealed partial class KeyringMarkerJsonContext : JsonSerializerContext;

/// <summary>
/// One credential, kept the best way this run can (<see cref="CredentialStorage"/>).
/// Shared by <see cref="CookieVault"/>, <see cref="LastFmSessionVault"/> and
/// <see cref="RemoteTokenVault"/> so they can't drift apart in how they're kept.
/// <para>
/// With a keyring, the secret is a keyring item found by its attributes, which
/// include the data folder: a demo run or a second profile never reads or
/// replaces another folder's sign-in. A plaintext file left by a run without a
/// keyring is the newest copy (every keyring write removes it), so it moves into
/// the keyring when one is there: written, read back, then the file deleted.
/// Without a keyring it stays the file it always was, and callers say so.
/// </para>
/// Calls block on the keyring; run them off the UI thread.
/// </summary>
internal sealed class StoredCredential
{
    private readonly string _path;
    private readonly string _markerPath;
    private readonly string _kind;
    private readonly Func<string> _label;
    private readonly SystemKeyring _keyring;
    private readonly Dictionary<string, string> _attributes;

    /// <param name="fileName">The file in the data folder: the DPAPI file on
    /// Windows, the plaintext fallback on Linux.</param>
    /// <param name="kind">Names the credential among Rambutan's keyring items.</param>
    /// <param name="label">What a keyring manager shows for the item.</param>
    public StoredCredential(string dataDirectory, string fileName, string kind, Func<string> label, SystemKeyring keyring)
    {
        _path = Path.Combine(dataDirectory, fileName);
        _markerPath = Path.Combine(dataDirectory, Path.GetFileNameWithoutExtension(fileName) + ".keyring.json");
        _kind = kind;
        _label = label;
        _keyring = keyring;
        _attributes = new Dictionary<string, string>(FolderAttributes(dataDirectory), StringComparer.Ordinal)
        {
            ["kind"] = kind,
            // The item's format version: a later format looks under its own.
            ["version"] = "1",
        };
    }

    /// <summary>The attributes every keyring item of one data folder has, whatever
    /// its kind or version: deleting by these removes that folder's items and no
    /// other folder's (a deleted profile).</summary>
    public static IReadOnlyDictionary<string, string> FolderAttributes(string dataDirectory)
    {
        var folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataDirectory));
        if (folder.Length == 0)
            throw new ArgumentException("A data folder is needed.", nameof(dataDirectory));
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["application"] = "rambutan",
            ["data-dir"] = folder,
        };
    }

    public CredentialStorage Storage => _keyring.Storage;

    public Task<CredentialStorage> GetStorageAsync() => _keyring.GetStorageAsync();

    /// <summary>The attributes the keyring item is stored under (for tests and logs).</summary>
    public IReadOnlyDictionary<string, string> Attributes => _attributes;

    public (string? Value, CredentialProblem Problem) Read()
    {
        var keyring = _keyring.Store;
        var file = ProtectedFile.Read(_path);
        if (keyring is null)
        {
            if (file is null && File.Exists(_markerPath))
            {
                AppLog.Write("keyring", $"The {_kind} is in the system keyring, and this run has none.");
                return (null, CredentialProblem.KeyringMissing);
            }
            return (file, CredentialProblem.None);
        }

        if (file is not null)
        {
            MoveIntoKeyring(keyring, file);
            return (file, CredentialProblem.None);
        }

        try
        {
            var value = keyring.Read(_attributes);
            if (value is null && File.Exists(_markerPath))
                AppLog.Write("keyring", $"The {_kind} is no longer in the system keyring.");
            return (value, CredentialProblem.None);
        }
        catch (SecretStoreException e)
        {
            AppLog.Write("keyring", $"Couldn't read the {_kind} from the system keyring: {e.Message}");
            return (null, CredentialProblem.KeyringFailed);
        }
    }

    /// <summary>Stores the credential. With a keyring that refuses, nothing is
    /// written in plaintext instead: a keyring is there, so the user expects it used.</summary>
    public CredentialProblem Write(string text)
    {
        if (_keyring.Store is not { } keyring)
        {
            try
            {
                ProtectedFile.Write(_path, text);
                return CredentialProblem.None;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            {
                AppLog.Write("keyring", $"Couldn't write the {_kind} file: {e.Message}");
                return CredentialProblem.FileFailed;
            }
        }

        try
        {
            keyring.Write(_attributes, _label(), text);
        }
        catch (SecretStoreException e)
        {
            AppLog.Write("keyring", $"Couldn't store the {_kind} in the system keyring: {e.Message}");
            return CredentialProblem.KeyringFailed;
        }
        WriteMarker();
        // An older plaintext copy would win over the keyring on the next read.
        if (File.Exists(_path) && !ProtectedFile.Delete(_path))
            AppLog.Write("keyring", $"Couldn't delete the old {_kind} file.");
        return CredentialProblem.None;
    }

    /// <summary>Removes every stored copy; false when one couldn't be removed.</summary>
    public bool Delete()
    {
        var removed = true;
        if (_keyring.Store is { } keyring)
        {
            try
            {
                keyring.Delete(_attributes);
                ProtectedFile.Delete(_markerPath);
            }
            catch (SecretStoreException e)
            {
                AppLog.Write("keyring", $"Couldn't delete the {_kind} from the system keyring: {e.Message}");
                removed = false;
            }
        }
        else if (File.Exists(_markerPath))
        {
            // The keyring copy is out of reach; the marker stays so it isn't forgotten.
            AppLog.Write("keyring", $"The {_kind} is in the system keyring, which this run can't reach to delete it.");
            removed = false;
        }
        return ProtectedFile.Delete(_path) && removed;
    }

    private void MoveIntoKeyring(ISecretStore keyring, string text)
    {
        try
        {
            keyring.Write(_attributes, _label(), text);
            if (keyring.Read(_attributes) != text)
                throw new SecretStoreException("the keyring returned something else when read back");
        }
        catch (SecretStoreException e)
        {
            AppLog.Write("keyring", $"Kept the {_kind} file; moving it into the system keyring failed: {e.Message}");
            return;
        }
        WriteMarker();
        AppLog.Write("keyring", ProtectedFile.Delete(_path)
            ? $"Moved the {_kind} from its unencrypted file into the system keyring."
            : $"Stored the {_kind} in the system keyring, but couldn't delete its unencrypted file.");
    }

    private void WriteMarker()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_markerPath)!);
            File.WriteAllText(_markerPath, JsonSerializer.Serialize(new KeyringMarker(),
                KeyringMarkerJsonContext.Default.KeyringMarker));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Only costs the "your sign-in is in the keyring" notice on a run without one.
            AppLog.Write("keyring", $"Couldn't write {Path.GetFileName(_markerPath)}: {e.Message}");
        }
    }
}
