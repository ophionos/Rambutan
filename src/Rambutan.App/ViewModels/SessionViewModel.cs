using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.InnerTube;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The YouTube Music sign-in and its upkeep. One instance, shared by
/// the sidebar, the status strip, the Library's signed-out state and Settings, so
/// they can't disagree about whether you're signed in.
///
/// Upkeep: a session isn't only checked at startup. It is re-checked when
/// YouTube Music refuses a request made with it (401/403), and when the window is
/// activated after a while away. So an expired sign-in shows up as "Sign in
/// again" soon after it happens, not as a string of page errors.
///
/// Refresh: Google rotates some of the session's cookies and soon rejects the
/// old values, so a pasted copy that is never refreshed expires within days.
/// The session asks for fresh values at startup and then at most once per
/// <see cref="RotateEvery"/>, and stores whatever cookies YouTube refreshes.
///
/// Renewal: rotation needs the app running. A session left alone for a day or
/// two is refused, and rotating it no longer helps; when the user also gave the
/// Google sign-in, Google issues the session new cookies from it before the
/// session is called expired.
/// </summary>
public partial class SessionViewModel : ViewModelBase
{
    /// <summary>How stale a good session may get before activation re-checks it.
    /// One small request an hour at most.</summary>
    public static readonly TimeSpan RecheckValidAfter = TimeSpan.FromHours(1);

    /// <summary>An unverified session (offline at the last check) is retried
    /// sooner, so "unverified" clears soon after the network returns.</summary>
    public static readonly TimeSpan RecheckUnverifiedAfter = TimeSpan.FromMinutes(1);

    /// <summary>How often a good session asks Google to rotate its cookies. A
    /// browser tab does it more often; an hour keeps the stored copy fresh with
    /// far fewer calls, which matters because they act on the user's Google
    /// sign-in.</summary>
    public static readonly TimeSpan RotateEvery = TimeSpan.FromHours(1);

    /// <summary>How often the timer looks whether a rotation is due. Checking
    /// against the clock, not waiting a full hour, copes with sleep and resume.</summary>
    private static readonly TimeSpan RotateCheckEvery = TimeSpan.FromMinutes(5);

    private readonly InnerTubeClient? _innerTube;
    private readonly CookieVault? _vault;
    private readonly ToastHostViewModel? _toasts;
    private readonly Action? _sessionChanged;
    private readonly Action? _signInChanged;
    // The sign-in that pages were last loaded with: what _signInChanged compares
    // the client's session against.
    private InnerTubeSession? _pagesSignIn;
    private string? _accountPhoto;
    private readonly TimeProvider _time;
    private readonly SynchronizationContext? _ui;
    private DateTimeOffset? _lastChecked;
    private DateTimeOffset? _lastRotated;
    private int _rotating;
    private ITimer? _rotateTimer;
    private string? _accountName;
    private int _checking;

    [ObservableProperty] private string _accountLabel = Strings.Get("account.notSignedIn");
    [ObservableProperty] private bool _isSignedIn;
    [ObservableProperty] private SessionState _state = SessionState.Anonymous;

    /// <summary>A session problem that stays true until acted on: expired, or not
    /// verifiable because YouTube Music is unreachable. Empty when all is well.</summary>
    [ObservableProperty] private string _notice = "";

    /// <summary>The notice is one Sign in fixes.</summary>
    [ObservableProperty] private bool _needsSignIn;

    /// <summary>The sidebar's account chip: the account's name when YouTube Music
    /// gave one, else the same state text as <see cref="AccountLabel"/>.</summary>
    [ObservableProperty] private string _chipTitle = Strings.Get("account.notSignedIn");

    /// <summary>The chip's second line: the session state under a name, and empty
    /// when the title already is the state.</summary>
    [ObservableProperty] private string _chipStatus = "";

    /// <summary>Up to two letters for the chip's avatar; empty without a name,
    /// and the view shows a person glyph instead.</summary>
    [ObservableProperty] private string _accountInitials = "";

    /// <summary>The account's avatar, drawn over the initials; null without one.
    /// It goes with the name: kept while the session is expired or unverified,
    /// gone when signed out.</summary>
    [ObservableProperty] private string? _accountPhotoUrl;

    /// <summary>The Settings page's explanation of the current state.</summary>
    [ObservableProperty] private string _statusDetail = Strings.Get("session.detail.anonymous");

    /// <summary>The view opens the sign-in dialog for <see cref="CreateSignInDialog"/>.</summary>
    public event Action? SignInRequested;

    // Design-time
    public SessionViewModel() => _time = TimeProvider.System;

    /// <param name="sessionChanged">Runs whenever the session is set, replaced or
    /// dropped: what was loaded for the old one (the Library) is stale.</param>
    /// <param name="signInChanged">Runs after requests start going out as someone
    /// else: signed in, signed out, or a session dropped as expired. Not for a
    /// state change on the same sign-in (a re-check, rotated cookies), and not for
    /// the session restored at startup, which pages load with from the start.</param>
    public SessionViewModel(
        InnerTubeClient innerTube,
        CookieVault? vault,
        ToastHostViewModel toasts,
        Action sessionChanged,
        TimeProvider? time = null,
        Action? signInChanged = null)
    {
        _innerTube = innerTube;
        _vault = vault;
        _toasts = toasts;
        _sessionChanged = sessionChanged;
        _signInChanged = signInChanged;
        _time = time ?? TimeProvider.System;
        _ui = SynchronizationContext.Current;
        innerTube.SessionRejected += OnSessionRejected;
        innerTube.SessionUpdated += OnSessionUpdated;
        innerTube.GoogleSignInUpdated += OnGoogleSignInUpdated;
        Strings.WhenChanged(this, static vm => vm.ApplyText());
    }

    private readonly TaskCompletionSource _cookiesLoaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once <see cref="RestoreAsync"/> has read the stored
    /// cookies (or found none), before it checks them over the network: what a
    /// request at startup waits for so it goes out signed in.</summary>
    public Task CookiesLoaded => _cookiesLoaded.Task;

    /// <summary>Restores the stored session at startup and checks it once.</summary>
    public async Task RestoreAsync()
    {
        try
        {
            if (_innerTube is null ||
                _vault is null ||
                await _vault.LoadAsync() is not { } stored ||
                InnerTubeSession.FromCookieString(stored) is not { } session)
            {
                return;
            }

            _innerTube.SetSession(session);
            _pagesSignIn = session;
        }
        finally
        {
            _cookiesLoaded.TrySetResult();
        }
        // After CookiesLoaded: pages need only the session, and the check is the
        // first thing that can use the Google sign-in.
        if (await _vault.LoadGoogleAsync() is { } google && GoogleSignIn.FromCookieString(google) is { } signIn)
            _innerTube.SetGoogleSignIn(signIn);
        await CheckAsync(announce: false);
        await RotateIfDueAsync();
        _rotateTimer ??= _time.CreateTimer(
            _ => OnUi(() => _ = RotateIfDueAsync()), null, RotateCheckEvery, RotateCheckEvery);
    }

    /// <summary>
    /// Rotates the session's cookies when it is signed in, checked good, and
    /// wasn't rotated in the last <see cref="RotateEvery"/>. A failed call is
    /// only logged (by the client): the session is unchanged, and the regular
    /// checks still decide whether it needs a new sign-in.
    /// </summary>
    public async Task RotateIfDueAsync()
    {
        if (_innerTube?.Session is null || State is not SessionState.Valid ||
            (_lastRotated is { } last && _time.GetUtcNow() - last < RotateEvery) ||
            Interlocked.Exchange(ref _rotating, 1) == 1)
        {
            return;
        }

        try
        {
            _lastRotated = _time.GetUtcNow();
            await _innerTube.RotateCookiesAsync();
        }
        finally
        {
            Volatile.Write(ref _rotating, 0);
        }
    }

    /// <summary>YouTube refreshed some of the session's cookies. Stored, unless
    /// the session was signed out or replaced meanwhile, or a later refresh
    /// already superseded this one; writes happen one at a time on the UI thread.</summary>
    private void OnSessionUpdated(InnerTubeSession updated) => OnUi(() =>
    {
        if (ReferenceEquals(_innerTube?.Session, updated))
            _vault?.Update(updated.CookieHeader);
    });

    /// <summary>Google refreshed its own sign-in's cookies while renewing the
    /// session. Stored, as <see cref="OnSessionUpdated"/> stores the session's.</summary>
    private void OnGoogleSignInUpdated(GoogleSignIn updated) => OnUi(() =>
    {
        if (ReferenceEquals(_innerTube?.GoogleSignIn, updated))
            _vault?.UpdateGoogle(updated.CookieHeader);
    });

    /// <summary>
    /// The window came back to the foreground. Re-checks a signed-in session
    /// that hasn't been checked for a while; does nothing otherwise.
    /// </summary>
    public Task OnActivatedAsync()
    {
        if (_lastChecked is not { } last || _innerTube?.Session is null)
            return Task.CompletedTask;

        var due = State is SessionState.Unreachable ? RecheckUnverifiedAfter : RecheckValidAfter;
        return _time.GetUtcNow() - last >= due ? CheckAsync(announce: false) : Task.CompletedTask;
    }

    /// <summary>Probes the current session and applies the answer. A refused
    /// session gets one rotation and one more probe first, and then, with a
    /// Google sign-in, one renewal and one more probe. Concurrent calls
    /// coalesce: a check already running answers for all of them.</summary>
    private async Task CheckAsync(bool announce)
    {
        if (_innerTube is null || Interlocked.Exchange(ref _checking, 1) == 1)
            return;

        try
        {
            var session = _innerTube.Session;
            var info = await _innerTube.GetSessionInfoAsync();
            var refused = info.State is SessionState.Expired;
            // Refused: perhaps only the rotating cookies went stale while the app
            // was closed. One rotation, then one more look, before giving up.
            if (refused && session is not null &&
                InnerTubeSession.IsSameSignIn(session, _innerTube.Session))
            {
                _lastRotated = _time.GetUtcNow();
                if (await _innerTube.RotateCookiesAsync() && !ReferenceEquals(session, _innerTube.Session))
                    info = await _innerTube.GetSessionInfoAsync();
                // Rotation can't bring back a sign-in YouTube has stopped accepting;
                // Google can issue it again from its own.
                if (info.State is SessionState.Expired &&
                    InnerTubeSession.IsSameSignIn(session, _innerTube.Session) &&
                    await _innerTube.ReissueSessionAsync())
                {
                    info = await _innerTube.GetSessionInfoAsync();
                }
            }
            // Signed out, or signed in again, while the probe was out: its answer
            // is about a session that's gone. Rotated cookies are still the same
            // sign-in (the probe's own response may have rotated them).
            if (!InnerTubeSession.IsSameSignIn(session, _innerTube.Session))
                return;
            // Rescued: the pages loaded meanwhile went out with the refused cookies,
            // and YouTube Music answers those as if signed out, without an error.
            // They reload, as for a new sign-in, though it is the same one.
            var rescued = refused && info.State is not SessionState.Expired;
            OnUi(() =>
            {
                if (rescued)
                    _pagesSignIn = null;
                Apply(info, announce, forceChanged: rescued);
            });
        }
        finally
        {
            Volatile.Write(ref _checking, 0);
        }
    }

    private void OnSessionRejected()
    {
        // Raised from inside the refused request; the probe is one more request
        // and runs after it, not inside it. A probe already running (its own 401
        // raises this too) answers for this one.
        if (Volatile.Read(ref _checking) == 0)
            OnUi(() => _ = CheckAsync(announce: false));
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    /// <summary>A dialog whose Sign in checks the pasted cookies against YouTube
    /// Music and, when they're good, makes them the session.</summary>
    public SignInDialogViewModel CreateSignInDialog()
    {
        var dialog = new SignInDialogViewModel(
            (session, ct) => _innerTube!.GetSessionInfoAsync(session, ct),
            // Not Storage: that waits for the keyring search, here on the UI thread.
            _vault?.GetStorageAsync() ?? Task.FromResult(CredentialStorage.WindowsDataProtection));
        dialog.Succeeded += Accept;
        return dialog;
    }

    private void Accept(InnerTubeSession session, GoogleSignIn? google, SessionInfo info)
    {
        _innerTube?.SetSession(session);
        // Without one, the previous sign-in's must not renew this one.
        _innerTube?.SetGoogleSignIn(google);
        _vault?.Save(session.CookieHeader, google?.CookieHeader);
        // New cookies can be a different account even when the name reads the same.
        Apply(info, announce: true, forceChanged: true);
        // Freshly pasted cookies are as fresh as they get; the timer takes it from here.
        _lastRotated = _time.GetUtcNow();
        _rotateTimer ??= _time.CreateTimer(
            _ => OnUi(() => _ = RotateIfDueAsync()), null, RotateCheckEvery, RotateCheckEvery);
    }

    [RelayCommand]
    private void SignIn() => SignInRequested?.Invoke();

    [RelayCommand]
    private void SignOut()
    {
        _innerTube?.SetSession(null);
        _innerTube?.SetGoogleSignIn(null);
        _vault?.Clear();
        _lastChecked = null;
        Apply(new SessionInfo(SessionState.Anonymous, null), announce: false);
        _toasts?.Show(Strings.Get("toast.signedOut"));
    }

    private void Apply(SessionInfo info, bool announce, bool forceChanged = false)
    {
        _lastChecked = info.State is SessionState.Anonymous ? null : _time.GetUtcNow();
        // A routine re-check that finds things as they were mustn't reload the
        // Library under the listener.
        if (forceChanged || info.State != State || info.AccountName != _accountName)
            _sessionChanged?.Invoke();
        _accountName = info.AccountName;
        _accountPhoto = info.AccountPhotoUrl;
        State = info.State;
        NeedsSignIn = false;
        switch (info.State)
        {
            case SessionState.Valid:
                IsSignedIn = true;
                break;
            case SessionState.Expired:
                // Expired must be actionable, never a silent empty feed. It
                // stays on the strip, with Sign in, until it's dealt with.
                IsSignedIn = false;
                NeedsSignIn = true;
                // The stored cookies stay: if this was a misreading, the next
                // launch's check restores them; a real expiry fails it again.
                _innerTube?.SetSession(null);
                break;
            case SessionState.Unreachable:
                // Offline says nothing about the cookies: keep the session so
                // authed surfaces work once the network returns; until then their
                // own errors are the honest signal.
                IsSignedIn = true;
                break;
            default:
                IsSignedIn = false;
                break;
        }
        ApplyText();
        // An outcome of signing in, not a state: the sidebar already shows
        // who's signed in, so a restored session at startup says nothing.
        if (announce && info.State is SessionState.Valid)
            _toasts?.Show(Strings.Format("toast.signedInAs", ("name", AccountLabel)));

        // Last, once the client's session is what it will be: a page reloaded
        // from here goes out with it, not with the cookies just dropped.
        if (!InnerTubeSession.IsSameSignIn(_pagesSignIn, _innerTube?.Session))
        {
            _pagesSignIn = _innerTube?.Session;
            _signInChanged?.Invoke();
        }
    }

    /// <summary>The account line, the strip's notice and the Settings detail for
    /// the current state, in the current language.</summary>
    private void ApplyText()
    {
        var time = ("time", (object?)CheckedAt());
        (AccountLabel, Notice, StatusDetail) = State switch
        {
            SessionState.Valid => (
                _accountName ?? Strings.Get("account.signedIn"), "",
                Strings.Format(_innerTube?.GoogleSignIn is null ? "session.detail.valid" : "session.detail.validRenews", time)),
            SessionState.Expired => (
                Strings.Get("account.expired"), Strings.Get("session.expiredNotice"),
                Strings.Format("session.detail.expired", time)),
            SessionState.Unreachable => (
                Strings.Get("account.unverified"), Strings.Get("session.unreachableNotice"),
                Strings.Format("session.detail.unreachable", time)),
            _ => (Strings.Get("account.notSignedIn"), "", Strings.Get("session.detail.anonymous")),
        };

        // A name survives an expired or unverified session in the chip, with the
        // state beneath it; signed out, the name is gone with the cookies. Without
        // a name, an unverified session still takes two lines: "Signed in
        // (unverified)" on one is too long for the sidebar.
        var name = State == SessionState.Anonymous ? null : _accountName;
        var unverified = State == SessionState.Unreachable;
        ChipTitle = name ?? (unverified ? Strings.Get("account.signedIn") : AccountLabel);
        ChipStatus = name is null && !unverified ? "" : State switch
        {
            SessionState.Expired => Strings.Get("account.expired"),
            SessionState.Unreachable => Strings.Get("account.chip.unverified"),
            _ => Strings.Get("account.signedIn"),
        };
        AccountInitials = Initials(name);
        AccountPhotoUrl = State == SessionState.Anonymous ? null : _accountPhoto;
    }

    /// <summary>The first letter of the first two words, upper-cased: "Demo
    /// Listener" is "DL". Letters are text elements, so an accented or
    /// non-Latin first letter stays whole.</summary>
    public static string Initials(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";
        var words = name.Split((char[]?)null, 3, StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(words.Take(2).Select(w => StringInfo.GetNextTextElement(w)));
        return initials.ToUpper(CultureInfo.CurrentCulture);
    }

    private string CheckedAt() =>
        _lastChecked is { } checkedAt
            ? TimeZoneInfo.ConvertTime(checkedAt, _time.LocalTimeZone).ToString("t", CultureInfo.CurrentCulture)
            : "";
}
