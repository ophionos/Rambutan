using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Rambutan.App.Services;

/// <summary>
/// A secret kept in one file, protected at rest the way a file can be.
/// Windows: DPAPI, current user. Linux: plaintext with owner-only permissions
/// (0600), the fallback for a session with no system keyring
/// (<see cref="StoredCredential"/> decides which is used).
/// </summary>
internal static class ProtectedFile
{
    /// <summary>The stored text, or null when there is none or it can't be read.</summary>
    public static string? Read(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var stored = File.ReadAllBytes(path);
            var bytes = OperatingSystem.IsWindows()
                ? ProtectedData.Unprotect(stored, optionalEntropy: null, DataProtectionScope.CurrentUser)
                : stored;
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            // Unreadable or corrupt: the same as absent.
            return null;
        }
    }

    public static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bytes = Encoding.UTF8.GetBytes(text);
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllBytes(path,
                ProtectedData.Protect(bytes, optionalEntropy: null, DataProtectionScope.CurrentUser));
            return;
        }

        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        // Created owner-only, so the secret is never briefly world-readable; the
        // chmod covers a file that already existed with looser permissions.
        using (var stream = new FileStream(path, new FileStreamOptions
               {
                   Mode = FileMode.Create,
                   Access = FileAccess.Write,
                   UnixCreateMode = ownerOnly,
               }))
        {
            stream.Write(bytes);
        }
        File.SetUnixFileMode(path, ownerOnly);
    }

    /// <summary>Deletes the file; false when it couldn't be removed.</summary>
    public static bool Delete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
