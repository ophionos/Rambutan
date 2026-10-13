using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.InnerTube;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The sign-in dialog. It walks through copying a request from the
/// browser's dev tools, picks the copy up from the clipboard when the listener
/// comes back, and checks the cookies with YouTube Music before closing. A
/// problem is shown in the dialog, next to the text that caused it, instead of on
/// the status strip after the dialog has gone.
/// </summary>
public partial class SignInDialogViewModel : ViewModelBase
{
    public const string YouTubeMusicUrl = "https://music.youtube.com";

    // Keys rather than text, so a test can name the message it expects whatever
    // the language.
    public const string NoSignInKey = "dialog.signIn.error.noSignIn";
    public const string RejectedKey = "dialog.signIn.error.rejected";
    public const string UnreachableKey = "dialog.signIn.error.unreachable";
    public const string ClipboardFoundKey = "dialog.signIn.clipboardFound";

    private readonly Func<InnerTubeSession, CancellationToken, Task<SessionInfo>>? _verify;
    private CancellationTokenSource? _checking;

    /// <summary>The last clipboard text put into the box, so the same copy isn't
    /// offered twice and anything the listener typed is never overwritten.</summary>
    private string? _offered;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _cookieText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isChecking;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _clipboardNote = "";

    /// <summary>Where the cookies will be kept (<see cref="CookieVault.Storage"/>),
    /// or null while the keyring search started at launch is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StorageNote), nameof(IsStorageEncrypted))]
    private CredentialStorage? _storage;

    public string StorageNote => Storage is { } storage
        ? CookieVault.Describe(storage)
        : Strings.Get("signIn.storage.looking");

    /// <summary>False where cookies are stored unencrypted (Linux with no system
    /// keyring running): the note then becomes a notice above the steps, read
    /// before the listener signs in, instead of a footnote.</summary>
    public bool IsStorageEncrypted => Storage is not CredentialStorage.PlainFile;

    /// <summary>The cookies checked out. The owner makes them the session; the
    /// view closes the dialog.</summary>
    public event Action<InnerTubeSession, SessionInfo>? Succeeded;

    // Design-time
    public SignInDialogViewModel() { }

    public SignInDialogViewModel(
        Func<InnerTubeSession, CancellationToken, Task<SessionInfo>> verify,
        CredentialStorage storage = CredentialStorage.WindowsDataProtection)
        : this(verify, Task.FromResult(storage))
    {
    }

    /// <param name="storage">Answers when the keyring search does; the dialog
    /// opens at once and shows the note when it knows.</param>
    public SignInDialogViewModel(
        Func<InnerTubeSession, CancellationToken, Task<SessionInfo>> verify,
        Task<CredentialStorage> storage)
    {
        _verify = verify;
        if (storage.IsCompletedSuccessfully)
            Storage = storage.Result;
        else
            _ = ShowStorageWhenKnownAsync(storage);
    }

    // Resumes on the caller's (the UI) thread.
    private async Task ShowStorageWhenKnownAsync(Task<CredentialStorage> storage) =>
        Storage = await storage;

    partial void OnCookieTextChanged(string value)
    {
        Error = "";
        if (value != _offered)
            ClipboardNote = "";
    }

    /// <summary>
    /// Offers the clipboard's text, read when the dialog opens or the listener
    /// switches back to it from the browser. It goes into the box only when it
    /// holds a YouTube sign-in, is a copy not offered before, and the box is empty
    /// or still holds the previous offer.
    /// </summary>
    /// <returns>Whether the text was taken.</returns>
    public bool OfferClipboard(string? text)
    {
        if (IsChecking || string.IsNullOrWhiteSpace(text) || text == _offered)
            return false;
        if (!string.IsNullOrWhiteSpace(CookieText) && CookieText != _offered)
            return false;
        if (InnerTubeSession.FromCookieString(text) is null)
            return false;

        _offered = text;
        CookieText = text;
        ClipboardNote = Strings.Get(ClipboardFoundKey);
        return true;
    }

    private bool CanSignIn() => !IsChecking && !string.IsNullOrWhiteSpace(CookieText);

    [RelayCommand(CanExecute = nameof(CanSignIn))]
    private async Task SignInAsync()
    {
        Error = "";
        if (InnerTubeSession.FromCookieString(CookieText) is not { } session)
        {
            Error = Strings.Get(NoSignInKey);
            return;
        }
        if (_verify is null)
            return;

        using var checking = _checking = new CancellationTokenSource();
        IsChecking = true;
        try
        {
            var info = await _verify(session, checking.Token);
            if (checking.IsCancellationRequested)
                return;

            switch (info.State)
            {
                case SessionState.Valid:
                    Succeeded?.Invoke(session, info);
                    break;
                case SessionState.Unreachable:
                    Error = Strings.Get(UnreachableKey);
                    break;
                default:
                    Error = Strings.Get(RejectedKey);
                    break;
            }
        }
        catch (OperationCanceledException) when (checking.IsCancellationRequested)
        {
            // Cancelled with the dialog; nobody is waiting for an answer.
        }
        finally
        {
            _checking = null;
            IsChecking = false;
        }
    }

    /// <summary>The dialog is closing without signing in: drop any check in flight.</summary>
    public void Cancel() => _checking?.Cancel();
}
