using System;
using System.Threading.Tasks;
using Rambutan.App.Localization;

namespace Rambutan.App.Services;

/// <summary>
/// The YouTube Music cookies, and the Google sign-in that can renew them when
/// the user gave one, kept the best way this run can
/// (<see cref="StoredCredential"/>): DPAPI on Windows, the system keyring on
/// Linux, and an owner-only plaintext file on Linux when no keyring is running,
/// which the sign-in dialog and a toast say. Every problem becomes a
/// <see cref="Warning"/>, never a silent sign-out.
/// <para>
/// The keyring can take a while (it may prompt for its password), so work runs
/// one call at a time off the caller's thread: <see cref="Save"/>,
/// <see cref="Update"/> and <see cref="Clear"/> return at once, and
/// <see cref="LoadAsync"/> sees every write made before it.
/// </para>
/// </summary>
public sealed class CookieVault(string dataDirectory, SystemKeyring? keyring = null)
{
    private readonly StoredCredential _credential = new(
        dataDirectory, "session.bin", "youtube-cookies",
        static () => Strings.Get("keyring.label.cookies"), keyring ?? SystemKeyring.None);

    private readonly StoredCredential _google = new(
        dataDirectory, "google-session.bin", "google-cookies",
        static () => Strings.Get("keyring.label.googleCookies"), keyring ?? SystemKeyring.None);

    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public event Action<string>? Warning;

    /// <summary>Blocks on the keyring search; off the UI thread, use <see cref="GetStorageAsync"/>.</summary>
    public CredentialStorage Storage => _credential.Storage;

    public Task<CredentialStorage> GetStorageAsync() => _credential.GetStorageAsync();

    /// <summary>
    /// How cookies are kept on this computer, in words the sign-in dialog shows.
    /// It lives here so the promise can't drift from the implementation.
    /// </summary>
    public string StorageDescription => Describe(Storage);

    public static string Describe(CredentialStorage storage) => storage switch
    {
        CredentialStorage.WindowsDataProtection => Strings.Get("signIn.storage.encrypted"),
        CredentialStorage.Keyring => Strings.Get("signIn.storage.keyring"),
        _ => Strings.Get("signIn.storage.plain"),
    };

    public Task<string?> LoadAsync() => Enqueue(() =>
    {
        var (value, problem) = _credential.Read();
        switch (problem)
        {
            case CredentialProblem.KeyringMissing:
                Warning?.Invoke(Strings.Get("toast.signIn.keyringMissing"));
                break;
            case CredentialProblem.KeyringFailed:
                Warning?.Invoke(Strings.Get("toast.signIn.keyringUnreadable"));
                break;
        }
        return value;
    });

    /// <summary>Blocks until the stored cookies are read; for callers off the UI thread.</summary>
    public string? Load() => LoadAsync().GetAwaiter().GetResult();

    /// <summary>The stored Google sign-in, or null when there is none or it can't
    /// be read. Quiet: <see cref="LoadAsync"/> already warns about the keyring,
    /// and without it the session still works until it expires.</summary>
    public Task<string?> LoadGoogleAsync() => Enqueue(() => _google.Read().Value);

    /// <summary>Stores a new sign-in. A null <paramref name="googleCookies"/>
    /// removes the stored Google sign-in, which belongs to the sign-in replaced.</summary>
    public Task Save(string cookieString, string? googleCookies = null) => Enqueue(() =>
    {
        if (Storage is CredentialStorage.PlainFile)
            Warning?.Invoke(Strings.Get("toast.signIn.unencrypted"));
        WarnIfNotKept(_credential.Write(cookieString));
        if (googleCookies is null)
            _google.Delete();
        else
            WarnIfNotKept(_google.Write(googleCookies));
        return true;
    });

    /// <summary>Stores refreshed cookies for a session that is already saved.
    /// Quiet: the unencrypted warning was given when it was first saved, and a
    /// rotation isn't something the user did. A failure is only logged; the
    /// copy saved at sign-in is still there.</summary>
    public Task Update(string cookieString) => Enqueue(() => _credential.Write(cookieString));

    /// <summary>Stores the Google sign-in's refreshed cookies, as <see cref="Update"/> does.</summary>
    public Task UpdateGoogle(string googleCookies) => Enqueue(() => _google.Write(googleCookies));

    public Task Clear() => Enqueue(() =>
    {
        // Sign-out must not crash on a locked/inaccessible vault; stale cookies
        // only mean the next launch restores the session again.
        var deleted = _credential.Delete();
        if (!_google.Delete() || !deleted)
            Warning?.Invoke(Strings.Get("toast.signOut.cookiesStuck"));
        return true;
    });

    private void WarnIfNotKept(CredentialProblem problem)
    {
        if (problem is CredentialProblem.KeyringFailed)
            Warning?.Invoke(Strings.Get("toast.signIn.keyringNotSaved"));
        else if (problem is CredentialProblem.FileFailed)
            Warning?.Invoke(Strings.Get("toast.signIn.notSaved"));
    }

    private Task<T> Enqueue<T>(Func<T> work)
    {
        lock (_gate)
        {
            var next = _tail.ContinueWith(_ => work(), TaskScheduler.Default);
            _tail = next;
            return next;
        }
    }
}
