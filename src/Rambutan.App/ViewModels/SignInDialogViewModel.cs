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
/// <para>
/// A second, optional copy from accounts.google.com gives the Google sign-in,
/// from which Rambutan renews the session when it expires
/// (<see cref="InnerTubeClient.ReissueSessionAsync"/>).
/// </para>
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
    public const string GoogleInFirstBoxKey = "dialog.signIn.error.googleInFirstBox";
    public const string NoGoogleSignInKey = "dialog.signIn.error.noGoogleSignIn";
    public const string GoogleClipboardFoundKey = "dialog.signIn.renew.clipboardFound";

    private readonly Func<InnerTubeSession, CancellationToken, Task<SessionInfo>>? _verify;
    private CancellationTokenSource? _checking;

    /// <summary>The last clipboard text put into the box, so the same copy isn't
    /// offered twice and anything the listener typed is never overwritten.</summary>
    private string? _offered;

    /// <summary>The same, for the Google copy's box.</summary>
    private string? _offeredGoogle;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private string _cookieText = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand))]
    private bool _isChecking;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _clipboardNote = "";

    /// <summary>The optional accounts.google.com copy. Empty signs in without it.</summary>
    [ObservableProperty] private string _googleText = "";
    [ObservableProperty] private string _googleError = "";
    [ObservableProperty] private string _googleClipboardNote = "";

    /// <summary>Whether the optional Google step is shown. It opens by itself
    /// when it has something to show: a copy picked up, or a problem.</summary>
    [ObservableProperty] private bool _isGoogleStepOpen;

    partial void OnGoogleClipboardNoteChanged(string value) => IsGoogleStepOpen |= value != "";

    partial void OnGoogleErrorChanged(string value) => IsGoogleStepOpen |= value != "";

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

    /// <summary>The cookies checked out. The owner makes them the session, with
    /// the Google sign-in when one was given; the view closes the dialog.</summary>
    public event Action<InnerTubeSession, GoogleSignIn?, SessionInfo>? Succeeded;

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

    partial void OnGoogleTextChanged(string value)
    {
        GoogleError = "";
        if (value != _offeredGoogle)
            GoogleClipboardNote = "";
    }

    /// <summary>
    /// Offers the clipboard's text, read when the dialog opens or the listener
    /// switches back to it from the browser. It goes into the box only when it
    /// holds a YouTube sign-in, is a copy not offered before, and the box is empty
    /// or still holds the previous offer. A copy from accounts.google.com goes
    /// into the Google box by the same rules.
    /// </summary>
    /// <returns>Whether the text was taken.</returns>
    public bool OfferClipboard(string? text)
    {
        if (IsChecking || string.IsNullOrWhiteSpace(text) || text == _offered || text == _offeredGoogle)
            return false;
        // First: a Google copy holds SAPISID too, so it would pass for a YouTube one.
        if (GoogleSignIn.FromCookieString(text) is not null)
        {
            if (!string.IsNullOrWhiteSpace(GoogleText) && GoogleText != _offeredGoogle)
                return false;
            _offeredGoogle = text;
            GoogleText = text;
            GoogleClipboardNote = Strings.Get(GoogleClipboardFoundKey);
            return true;
        }
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
        GoogleError = "";
        if (GoogleSignIn.FromCookieString(CookieText) is not null)
        {
            Error = Strings.Get(GoogleInFirstBoxKey);
            return;
        }
        if (InnerTubeSession.FromCookieString(CookieText) is not { } session)
        {
            Error = Strings.Get(NoSignInKey);
            return;
        }
        GoogleSignIn? google = null;
        if (!string.IsNullOrWhiteSpace(GoogleText) && (google = GoogleSignIn.FromCookieString(GoogleText)) is null)
        {
            GoogleError = Strings.Get(NoGoogleSignInKey);
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
                    Succeeded?.Invoke(session, google, info);
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
