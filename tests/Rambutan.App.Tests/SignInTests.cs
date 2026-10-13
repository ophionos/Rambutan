using System.Net;
using System.Net.Http;
using System.Text;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.InnerTube;

namespace Rambutan.App.Tests;

/// <summary>
/// The sign-in dialog: guided paste, clipboard pickup, and the check that keeps
/// it open with the problem shown inside it.
/// </summary>
public class SignInDialogTests
{
    private const string Copied =
        "curl 'https://music.youtube.com/youtubei/v1/browse' -b 'SID=a; __Secure-3PAPISID=b'";

    private readonly List<InnerTubeSession> _verified = [];
    private Func<Task<SessionInfo>> _answer = () => Task.FromResult(new SessionInfo(SessionState.Valid, "Listener"));

    private SignInDialogViewModel Dialog() => new((session, _) =>
    {
        _verified.Add(session);
        return _answer();
    });

    [Fact]
    public void Clipboard_WithACopiedRequest_FillsTheBox_AndSaysSo()
    {
        var dialog = Dialog();

        Assert.True(dialog.OfferClipboard(Copied));

        Assert.Equal(Copied, dialog.CookieText);
        Assert.Equal(Strings.Get(SignInDialogViewModel.ClipboardFoundKey), dialog.ClipboardNote);
        Assert.True(dialog.SignInCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://music.youtube.com/watch?v=abc")]
    [InlineData("VISITOR_INFO1_LIVE=abc; PREF=f1")] // cookies, but signed out
    public void Clipboard_WithoutASignIn_IsLeftAlone(string? text)
    {
        var dialog = Dialog();

        Assert.False(dialog.OfferClipboard(text));

        Assert.Equal("", dialog.CookieText);
        Assert.Equal("", dialog.ClipboardNote);
    }

    [Fact]
    public void Clipboard_NeverOverwritesWhatTheListenerTyped()
    {
        var dialog = Dialog();
        dialog.CookieText = "SAPISID=typed";

        Assert.False(dialog.OfferClipboard(Copied));
        Assert.Equal("SAPISID=typed", dialog.CookieText);
    }

    [Fact]
    public void Clipboard_ReplacesItsOwnEarlierOffer_ButDoesNotRepeatIt()
    {
        var dialog = Dialog();
        dialog.OfferClipboard("SAPISID=first");

        Assert.False(dialog.OfferClipboard("SAPISID=first"));
        Assert.True(dialog.OfferClipboard("SAPISID=second"));
        Assert.Equal("SAPISID=second", dialog.CookieText);
    }

    [Fact]
    public void EditingTheOffer_DropsTheClipboardNote()
    {
        var dialog = Dialog();
        dialog.OfferClipboard(Copied);

        dialog.CookieText += " ";

        Assert.Equal("", dialog.ClipboardNote);
    }

    [Fact]
    public async Task NoSapisid_IsExplainedInTheDialog_WithoutAskingYouTube()
    {
        var dialog = Dialog();
        dialog.CookieText = "VISITOR_INFO1_LIVE=abc";

        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Get(SignInDialogViewModel.NoSignInKey), dialog.Error);
        Assert.Empty(_verified);
    }

    [Theory]
    [InlineData(SessionState.Expired, SignInDialogViewModel.RejectedKey)]
    [InlineData(SessionState.Unreachable, SignInDialogViewModel.UnreachableKey)]
    public async Task AFailedCheck_StaysInTheDialog(SessionState state, string messageKey)
    {
        _answer = () => Task.FromResult(new SessionInfo(state, null));
        var dialog = Dialog();
        var succeeded = false;
        dialog.Succeeded += (_, _) => succeeded = true;
        dialog.CookieText = Copied;

        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.False(succeeded);
        Assert.Equal(Strings.Get(messageKey), dialog.Error);
        Assert.False(dialog.IsChecking);

        dialog.CookieText = "SAPISID=fresh";
        Assert.Equal("", dialog.Error);
    }

    [Fact]
    public async Task AGoodCheck_HandsOverTheSessionFromTheCopiedRequest()
    {
        var dialog = Dialog();
        (InnerTubeSession Session, SessionInfo Info)? result = null;
        dialog.Succeeded += (s, i) => result = (s, i);
        dialog.CookieText = Copied;

        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.NotNull(result);
        Assert.Equal("SID=a; __Secure-3PAPISID=b", result!.Value.Session.CookieHeader);
        Assert.Equal("Listener", result.Value.Info.AccountName);
        Assert.Equal("", dialog.Error);
    }

    [Fact]
    public async Task WhileChecking_SignInIsDisabled_AndCancelDropsTheAnswer()
    {
        var pending = new TaskCompletionSource<SessionInfo>();
        _answer = () => pending.Task;
        var dialog = Dialog();
        var succeeded = false;
        dialog.Succeeded += (_, _) => succeeded = true;
        dialog.CookieText = Copied;

        var run = dialog.SignInCommand.ExecuteAsync(null);
        Assert.True(dialog.IsChecking);
        Assert.False(dialog.SignInCommand.CanExecute(null));
        Assert.False(dialog.OfferClipboard("SAPISID=other"));

        dialog.Cancel();
        pending.SetResult(new SessionInfo(SessionState.Valid, "Listener"));
        await run;

        Assert.False(succeeded);
        Assert.False(dialog.IsChecking);
    }
}

/// <summary>The session's upkeep: checks on refusal and on return, and
/// sign-in / sign-out through the shared view model.</summary>
public sealed class SessionViewModelTests : IDisposable
{
    private const string AccountMenu =
        """{"actions":[{"openPopupAction":{"popup":{"multiPageMenuRenderer":{"header":{"activeAccountHeaderRenderer":{"accountName":{"runs":[{"text":"Listener"}]},"accountPhoto":{"thumbnails":[{"url":"https://yt3.ggpht.com/a=s48"},{"url":"https://yt3.ggpht.com/a=s88"}]}}}}}}}]}""";

    private sealed class Server : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public bool Offline { get; set; }
        public int AccountChecks { get; private set; }
        public int Rotations { get; private set; }
        /// <summary>What a RotateCookies call sets.</summary>
        public string[] RotatedCookies { get; set; } = [];
        /// <summary>What an account check sets.</summary>
        public string[] AccountCookies { get; set; } = [];
        /// <summary>Refuse account checks until a rotation has happened: only the
        /// rotating cookies went stale.</summary>
        public bool StaleUntilRotated { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (Offline)
                throw new HttpRequestException("offline");
            var isAccount = request.RequestUri!.AbsolutePath.EndsWith("account/account_menu", StringComparison.Ordinal);
            var isRotate = request.RequestUri.AbsolutePath == "/RotateCookies";
            if (isAccount)
                AccountChecks++;
            if (isRotate)
                Rotations++;
            var status = StaleUntilRotated && isAccount && Rotations == 0 ? HttpStatusCode.Unauthorized : Status;
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(
                    status == HttpStatusCode.OK && isAccount ? AccountMenu : "{}", Encoding.UTF8, "application/json"),
                RequestMessage = request,
            };
            foreach (var cookie in isRotate ? RotatedCookies : isAccount ? AccountCookies : [])
                response.Headers.TryAddWithoutValidation("Set-Cookie", cookie);
            return Task.FromResult(response);
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-session-" + Guid.NewGuid().ToString("N"));
    private readonly Server _server = new();
    private readonly ManualTime _time = new();
    private readonly InnerTubeClient _client;
    private readonly CookieVault _vault;
    private readonly ToastHostViewModel _toasts = new(new ManualClock().Schedule);
    private readonly SessionViewModel _session;
    private int _changes;
    private int _signIns;
    // The client's session when signInChanged ran: what a page reloaded then uses.
    private InnerTubeSession? _sessionAtSignIn;

    public SessionViewModelTests()
    {
        _client = new InnerTubeClient(new HttpClient(_server));
        _vault = new CookieVault(_dir);
        _session = new SessionViewModel(_client, _vault, _toasts, () => _changes++, _time, () =>
        {
            _signIns++;
            _sessionAtSignIn = _client.Session;
        });
    }

    public void Dispose()
    {
        _client.Dispose();
        // Sign-out clears the vault without waiting, as the app does; the vault
        // runs its work in order, so this read returns once that delete is done,
        // and the folder isn't removed while session.bin is still open.
        _vault.LoadAsync().GetAwaiter().GetResult();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private async Task SignedInAsync()
    {
        await _vault.Save("SAPISID=stored");
        await _session.RestoreAsync();
        Assert.Equal(SessionState.Valid, _session.State);
        _changes = 0;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++)
            await Task.Delay(10);
        Assert.True(condition());
    }

    [Fact]
    public async Task Restore_ChecksTheStoredCookies_Quietly()
    {
        await SignedInAsync();

        Assert.True(_session.IsSignedIn);
        Assert.Equal("Listener", _session.AccountLabel);
        Assert.Empty(_toasts.Toasts);
        Assert.StartsWith("Signed in with cookies from your browser. Last checked", _session.StatusDetail);
        // Pages load with the restored session from the start: reloading
        // them for it would load Home twice at every launch.
        Assert.Equal(0, _signIns);
    }

    [Fact]
    public async Task SigningIn_ThroughTheDialog_SetsSavesAndAnnounces()
    {
        var dialog = _session.CreateSignInDialog();
        dialog.CookieText = "curl 'https://music.youtube.com/' -b 'SID=x; SAPISID=pasted'";

        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.True(_session.IsSignedIn);
        Assert.Equal("pasted", _client.Session!.Sapisid);
        Assert.Equal("SID=x; SAPISID=pasted", _vault.Load());
        Assert.Equal("Signed in as Listener", Assert.Single(_toasts.Toasts).Message);
        Assert.Equal(1, _changes);
        Assert.Equal(1, _signIns);
        Assert.Same(_client.Session, _sessionAtSignIn);
    }

    [Fact]
    public async Task ARefusedPaste_LeavesTheCurrentSessionAlone()
    {
        await SignedInAsync();
        var live = _client.Session;
        var dialog = _session.CreateSignInDialog();
        dialog.CookieText = "SAPISID=stale";
        _server.Status = HttpStatusCode.Unauthorized;

        await dialog.SignInCommand.ExecuteAsync(null);

        Assert.Equal(Strings.Get(SignInDialogViewModel.RejectedKey), dialog.Error);
        Assert.Same(live, _client.Session);
        Assert.Equal("SAPISID=stored", _vault.Load());
        Assert.True(_session.IsSignedIn);
    }

    [Fact]
    public async Task ARefusedRequest_MidSession_ChecksAndAsksToSignInAgain()
    {
        await SignedInAsync();
        _server.Status = HttpStatusCode.Unauthorized;

        await Assert.ThrowsAsync<HttpRequestException>(() => _client.GetLibraryPlaylistsAsync());
        await Eventually(() => _session.State == SessionState.Expired);

        Assert.False(_session.IsSignedIn);
        Assert.True(_session.NeedsSignIn);
        Assert.Equal("Your session has expired.", _session.Notice);
        Assert.Null(_client.Session);
        Assert.Equal(1, _changes);
        // Pages reload signed out, not with the cookies just refused.
        Assert.Equal(1, _signIns);
        Assert.Null(_sessionAtSignIn);
        // Kept, in case the refusal was a misreading: the next launch checks again.
        Assert.Equal("SAPISID=stored", _vault.Load());
    }

    [Fact]
    public async Task Activation_RechecksOnlyAStaleSession()
    {
        await SignedInAsync();
        var checks = _server.AccountChecks;

        _time.Now += SessionViewModel.RecheckValidAfter - TimeSpan.FromMinutes(1);
        await _session.OnActivatedAsync();
        Assert.Equal(checks, _server.AccountChecks);

        _time.Now += TimeSpan.FromMinutes(1);
        await _session.OnActivatedAsync();
        Assert.Equal(checks + 1, _server.AccountChecks);
        // Nothing changed, so nothing that was loaded for it reloads.
        Assert.Equal(0, _changes);
    }

    [Fact]
    public async Task Activation_RetriesAnUnverifiedSessionSoon_AndClearsTheNotice()
    {
        _server.Offline = true;
        await _vault.Save("SAPISID=stored");
        await _session.RestoreAsync();
        Assert.Equal(SessionState.Unreachable, _session.State);
        Assert.Equal("Couldn't reach YouTube Music to verify your session.", _session.Notice);

        _server.Offline = false;
        _time.Now += SessionViewModel.RecheckUnverifiedAfter;
        await _session.OnActivatedAsync();

        Assert.Equal(SessionState.Valid, _session.State);
        Assert.Equal("", _session.Notice);
        Assert.Equal("Listener", _session.AccountLabel);
        // The same sign-in, only verified now: nothing to reload for.
        Assert.Equal(0, _signIns);
    }

    [Fact]
    public async Task Activation_SignedOut_AsksNothing()
    {
        _time.Now += TimeSpan.FromDays(1);
        await _session.OnActivatedAsync();

        Assert.Equal(0, _server.AccountChecks);
    }

    [Fact]
    public async Task SignOut_ForgetsTheCookies()
    {
        await SignedInAsync();

        _session.SignOutCommand.Execute(null);

        Assert.False(_session.IsSignedIn);
        Assert.Null(_client.Session);
        Assert.Null(_vault.Load());
        Assert.Equal("Not signed in", _session.AccountLabel);
        Assert.Equal("Signed out", Assert.Single(_toasts.Toasts).Message);
        Assert.Equal(1, _changes);
        Assert.Equal(1, _signIns);
    }

    [Fact]
    public async Task TheAccountChip_NamesTheAccount_AndForgetsItOnSignOut()
    {
        Assert.Equal("Not signed in", _session.ChipTitle);
        Assert.Equal("", _session.ChipStatus);
        Assert.Equal("", _session.AccountInitials);

        await SignedInAsync();
        Assert.Equal("Listener", _session.ChipTitle);
        Assert.Equal("Signed in", _session.ChipStatus);
        Assert.Equal("L", _session.AccountInitials);
        // The largest of the sizes YouTube Music sent.
        Assert.Equal("https://yt3.ggpht.com/a=s88", _session.AccountPhotoUrl);

        _session.SignOutCommand.Execute(null);
        Assert.Equal("Not signed in", _session.ChipTitle);
        Assert.Equal("", _session.ChipStatus);
        Assert.Equal("", _session.AccountInitials);
        Assert.Null(_session.AccountPhotoUrl);
    }

    [Fact]
    public async Task TheAccountChip_WithoutAName_ShowsTheState_OnTwoShortLines()
    {
        _server.Offline = true;
        await _vault.Save("SAPISID=stored");
        await _session.RestoreAsync();

        Assert.Equal("Signed in (unverified)", _session.AccountLabel);
        Assert.Equal("Signed in", _session.ChipTitle);
        Assert.Equal("Unverified", _session.ChipStatus);
        Assert.Equal("", _session.AccountInitials);
    }

    [Fact]
    public async Task Restore_RotatesTheCookies_AndStoresTheFreshOnes()
    {
        _server.RotatedCookies = ["__Secure-1PSIDTS=rotated; Domain=.youtube.com"];

        await SignedInAsync();

        Assert.Equal(1, _server.Rotations);
        Assert.Equal("SAPISID=stored; __Secure-1PSIDTS=rotated", _client.Session!.CookieHeader);
        Assert.Equal("SAPISID=stored; __Secure-1PSIDTS=rotated", _vault.Load());
        // A refresh isn't a change of account: nothing reloads for it.
        Assert.Equal(0, _changes);
        Assert.True(_session.IsSignedIn);
    }

    [Fact]
    public async Task AStaleStoredSession_IsRotatedBeforeItsCalledExpired()
    {
        _server.StaleUntilRotated = true;
        _server.RotatedCookies = ["__Secure-1PSIDTS=rotated; Domain=.youtube.com"];
        await _vault.Save("SAPISID=stored; __Secure-1PSIDTS=stale");

        await _session.RestoreAsync();

        Assert.Equal(SessionState.Valid, _session.State);
        Assert.Equal(1, _server.Rotations);
        Assert.Equal(2, _server.AccountChecks);
        Assert.Equal("SAPISID=stored; __Secure-1PSIDTS=rotated", _vault.Load());
    }

    [Fact]
    public async Task ASessionRotationCantSave_IsExpired_AfterOneTry()
    {
        _server.Status = HttpStatusCode.Unauthorized;
        await _vault.Save("SAPISID=stored");

        await _session.RestoreAsync();

        Assert.Equal(SessionState.Expired, _session.State);
        Assert.Equal(1, _server.Rotations);
        Assert.Equal(1, _server.AccountChecks);
    }

    [Fact]
    public async Task Rotation_HappensAtMostOncePerInterval()
    {
        await SignedInAsync();

        _time.Now += SessionViewModel.RotateEvery - TimeSpan.FromMinutes(1);
        await _session.RotateIfDueAsync();
        Assert.Equal(1, _server.Rotations);

        _time.Now += TimeSpan.FromMinutes(1);
        await _session.RotateIfDueAsync();
        Assert.Equal(2, _server.Rotations);
    }

    [Fact]
    public async Task Rotation_SkipsASessionThatIsntGood()
    {
        _server.Offline = true;
        await _vault.Save("SAPISID=stored");
        await _session.RestoreAsync();
        _server.Offline = false;

        await _session.RotateIfDueAsync();

        Assert.Equal(0, _server.Rotations);
        Assert.Equal("SAPISID=stored", _vault.Load());
    }

    [Fact]
    public async Task ARefusedRotation_KeepsTheStoredCookies()
    {
        await SignedInAsync();
        _time.Now += SessionViewModel.RotateEvery;
        _server.Status = HttpStatusCode.Unauthorized;

        await _session.RotateIfDueAsync();

        Assert.Equal(2, _server.Rotations);
        Assert.Equal("SAPISID=stored", _vault.Load());
        Assert.NotNull(_client.Session);
    }

    [Fact]
    public async Task APastedSession_IsntRotatedStraightAway()
    {
        var dialog = _session.CreateSignInDialog();
        dialog.CookieText = "SAPISID=pasted";

        await dialog.SignInCommand.ExecuteAsync(null);
        await _session.RotateIfDueAsync();

        Assert.Equal(0, _server.Rotations);
        Assert.Equal("SAPISID=pasted", _vault.Load());
    }

    [Fact]
    public async Task ACheckWhoseAnswerRotatesCookies_StillCounts()
    {
        _server.AccountCookies = ["SIDCC=fresh; Domain=.youtube.com"];

        await SignedInAsync();

        Assert.Equal("Listener", _session.AccountLabel);
        Assert.Contains("SIDCC=fresh", _vault.Load());
    }

    [Fact]
    public async Task Rotation_AfterSignOut_AsksNothing()
    {
        await SignedInAsync();
        _session.SignOutCommand.Execute(null);
        _time.Now += SessionViewModel.RotateEvery;

        await _session.RotateIfDueAsync();

        Assert.Equal(1, _server.Rotations);
        Assert.Null(_vault.Load());
        Assert.Null(_client.Session);
    }

    [Theory]
    [InlineData("Demo Listener", "DL")]
    [InlineData("listener", "L")]
    [InlineData("  ada   byron  king ", "AB")]
    [InlineData("émile zola", "ÉZ")]
    [InlineData("山田 太郎", "山太")]
    [InlineData("   ", "")]
    [InlineData(null, "")]
    public void Initials_TakeTheFirstLetterOfTwoWords(string? name, string expected) =>
        Assert.Equal(expected, SessionViewModel.Initials(name));
}

/// <summary>The status strip and the sidebar don't both announce an expired
/// session (the sidebar's account line already says it, with Sign in).</summary>
public class SessionNoticeTests
{
    [Fact]
    public void AnExpiredSession_ShowsOnTheStrip_OnlyWithTheSidebarCollapsed()
    {
        var vm = new MainViewModel();
        vm.Session.Notice = "Your session has expired.";
        vm.Session.NeedsSignIn = true;

        Assert.False(vm.ShowSessionNotice);
        Assert.False(vm.HasStatus);

        vm.IsSidebarOpen = false;
        Assert.True(vm.ShowSessionNotice);
        Assert.True(vm.HasStatus);
    }

    [Fact]
    public void AnUnverifiedSession_AlwaysShowsOnTheStrip()
    {
        var vm = new MainViewModel();
        vm.Session.Notice = "Couldn't reach YouTube Music to verify your session.";

        Assert.True(vm.IsSidebarOpen);
        Assert.True(vm.ShowSessionNotice);
        Assert.True(vm.HasStatus);
    }
}
