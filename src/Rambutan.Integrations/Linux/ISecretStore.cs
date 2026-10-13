namespace Rambutan.Integrations.Linux;

/// <summary>
/// A system keyring: secrets kept by another process and found again by their
/// attributes. On Linux that is the freedesktop Secret Service
/// (<see cref="SecretServiceStore"/>: GNOME Keyring, KWallet, KeePassXC).
/// Calls block, with their own time limits; run them off the UI thread.
/// </summary>
public interface ISecretStore
{
    /// <summary>The secret stored under these attributes, or null when there is none.</summary>
    /// <exception cref="SecretStoreException">The keyring couldn't answer: locked,
    /// unlock dismissed, timed out, or gone.</exception>
    string? Read(IReadOnlyDictionary<string, string> attributes);

    /// <summary>Stores the secret under these attributes, replacing any there.</summary>
    /// <param name="label">The name a keyring manager (Seahorse, KWalletManager) shows.</param>
    /// <exception cref="SecretStoreException">Not stored.</exception>
    void Write(IReadOnlyDictionary<string, string> attributes, string label, string secret);

    /// <summary>Removes every secret stored under these attributes.</summary>
    /// <exception cref="SecretStoreException">Not removed.</exception>
    void Delete(IReadOnlyDictionary<string, string> attributes);
}

/// <summary>The keyring was reached but didn't do what was asked; the message says why.</summary>
public sealed class SecretStoreException(string message, Exception? inner = null) : Exception(message, inner);
