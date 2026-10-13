using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Remote;
using Rambutan.Integrations.LastFm;
using Rambutan.Integrations.Linux;

namespace Rambutan.App.Tests;

/// <summary>
/// Where credentials are kept: the system keyring when there is one, the
/// file otherwise, the move from one to the other, and saying so when something
/// fails. The keyring is a fake: tests never touch the real one.
/// </summary>
public sealed class CredentialStorageTests : IDisposable
{
    private sealed class FakeKeyring : ISecretStore
    {
        public readonly Dictionary<string, (IReadOnlyDictionary<string, string> Attributes, string Label, string Secret)> Items = new();
        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public bool FailDeletes { get; set; }

        /// <summary>Returns this instead of what was stored (a keyring that mangles).</summary>
        public string? ReadBackOverride { get; set; }

        private static string Key(IReadOnlyDictionary<string, string> attributes) =>
            string.Join(";", attributes.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => $"{a.Key}={a.Value}"));

        public string? Read(IReadOnlyDictionary<string, string> attributes)
        {
            if (FailReads)
                throw new SecretStoreException("locked");
            return Items.TryGetValue(Key(attributes), out var item) ? ReadBackOverride ?? item.Secret : null;
        }

        public void Write(IReadOnlyDictionary<string, string> attributes, string label, string secret)
        {
            if (FailWrites)
                throw new SecretStoreException("dismissed");
            Items[Key(attributes)] = (new Dictionary<string, string>(attributes), label, secret);
        }

        public void Delete(IReadOnlyDictionary<string, string> attributes)
        {
            if (FailDeletes)
                throw new SecretStoreException("locked");
            Items.Remove(Key(attributes));
        }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-credentials-" + Guid.NewGuid().ToString("N"));
    private readonly FakeKeyring _fake = new();
    private readonly List<string> _warnings = [];

    private string SessionFile => Path.Combine(_dir, "session.bin");
    private string SessionMarker => Path.Combine(_dir, "session.keyring.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private SystemKeyring Keyring => new(Task.FromResult<ISecretStore?>(_fake));

    private CookieVault Vault(SystemKeyring? keyring = null, string? dir = null)
    {
        var vault = new CookieVault(dir ?? _dir, keyring);
        vault.Warning += _warnings.Add;
        return vault;
    }

    [Fact]
    public void WithoutAKeyring_Windows_UsesDataProtection_ElsewhereThePlainFile()
    {
        var vault = Vault();

        Assert.Equal(OperatingSystem.IsWindows() ? CredentialStorage.WindowsDataProtection : CredentialStorage.PlainFile,
            vault.Storage);
    }

    [Fact]
    public async Task WithAKeyring_TheCookiesGoThere_NotIntoAFile()
    {
        var vault = Vault(Keyring);

        await vault.Save("SAPISID=kept");

        Assert.Equal(CredentialStorage.Keyring, vault.Storage);
        var item = Assert.Single(_fake.Items.Values);
        Assert.Equal("SAPISID=kept", item.Secret);
        Assert.Equal(Strings.Get("keyring.label.cookies"), item.Label);
        Assert.Equal("rambutan", item.Attributes["application"]);
        Assert.Equal("youtube-cookies", item.Attributes["kind"]);
        Assert.Equal("1", item.Attributes["version"]);
        Assert.Equal(Path.GetFullPath(_dir), item.Attributes["data-dir"]);
        Assert.False(File.Exists(SessionFile));
        Assert.Equal("""{"version":1,"store":"secret-service"}""", File.ReadAllText(SessionMarker));
        Assert.Equal("SAPISID=kept", await vault.LoadAsync());
        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task EachDataFolder_HasItsOwnItem()
    {
        var other = _dir + "-other";
        try
        {
            await Vault(Keyring).Save("SAPISID=real");
            await Vault(Keyring, other).Save("SAPISID=demo");
            await Vault(Keyring, other).Clear();

            Assert.Equal("SAPISID=real", await Vault(Keyring).LoadAsync());
            Assert.Null(await Vault(Keyring, other).LoadAsync());
        }
        finally
        {
            if (Directory.Exists(other))
                Directory.Delete(other, recursive: true);
        }
    }

    [Fact]
    public async Task AnOldFile_MovesIntoTheKeyring_AndIsDeleted()
    {
        await Vault().Save("SAPISID=old");
        Assert.True(File.Exists(SessionFile));

        Assert.Equal("SAPISID=old", await Vault(Keyring).LoadAsync());

        Assert.Equal("SAPISID=old", Assert.Single(_fake.Items.Values).Secret);
        Assert.False(File.Exists(SessionFile));
        Assert.True(File.Exists(SessionMarker));
        Assert.Equal("SAPISID=old", await Vault(Keyring).LoadAsync());
    }

    [Fact]
    public async Task AFileWrittenWithoutTheKeyring_IsNewer_AndReplacesTheKeyringCopy()
    {
        await Vault(Keyring).Save("SAPISID=older");
        await Vault().Save("SAPISID=newer");   // a run with no keyring

        Assert.Equal("SAPISID=newer", await Vault(Keyring).LoadAsync());
        Assert.Equal("SAPISID=newer", Assert.Single(_fake.Items.Values).Secret);
        Assert.False(File.Exists(SessionFile));
    }

    [Fact]
    public async Task AFailedMove_KeepsTheFile()
    {
        await Vault().Save("SAPISID=old");
        _fake.FailWrites = true;

        Assert.Equal("SAPISID=old", await Vault(Keyring).LoadAsync());

        Assert.True(File.Exists(SessionFile));
        Assert.False(File.Exists(SessionMarker));
    }

    [Fact]
    public async Task AMoveThatReadsBackWrong_KeepsTheFile()
    {
        await Vault().Save("SAPISID=old");
        _fake.ReadBackOverride = "SAPISID=mangled";

        Assert.Equal("SAPISID=old", await Vault(Keyring).LoadAsync());

        Assert.True(File.Exists(SessionFile));
    }

    [Fact]
    public async Task InTheKeyring_ButNoKeyringThisRun_SaysSo()
    {
        await Vault(Keyring).Save("SAPISID=kept");

        Assert.Null(await Vault().LoadAsync());

        Assert.Equal(Strings.Get("toast.signIn.keyringMissing"), Assert.Single(_warnings));
        Assert.True(File.Exists(SessionMarker));   // still there for the run that has the keyring
    }

    [Fact]
    public async Task ALockedKeyring_SaysSo_AndLosesNothing()
    {
        await Vault(Keyring).Save("SAPISID=kept");
        _fake.FailReads = true;

        Assert.Null(await Vault(Keyring).LoadAsync());

        Assert.Equal(Strings.Get("toast.signIn.keyringUnreadable"), Assert.Single(_warnings));
        Assert.Single(_fake.Items);
    }

    [Fact]
    public async Task AKeyringThatRefusesTheSave_SaysSo_AndWritesNoPlaintext()
    {
        _fake.FailWrites = true;

        await Vault(Keyring).Save("SAPISID=pasted");

        Assert.Equal(Strings.Get("toast.signIn.keyringNotSaved"), Assert.Single(_warnings));
        Assert.False(File.Exists(SessionFile));
        Assert.Empty(_fake.Items);
    }

    [Fact]
    public async Task ARotation_IsStoredQuietly()
    {
        var vault = Vault(Keyring);
        await vault.Save("SAPISID=kept");

        await vault.Update("SAPISID=kept; SIDCC=fresh");

        Assert.Equal("SAPISID=kept; SIDCC=fresh", await vault.LoadAsync());
        Assert.Empty(_warnings);
    }

    [Fact]
    public async Task SigningOut_RemovesEveryCopy()
    {
        await Vault().Save("SAPISID=file");
        var vault = Vault(Keyring);
        await vault.Save("SAPISID=kept");

        await vault.Clear();

        Assert.Empty(_fake.Items);
        Assert.False(File.Exists(SessionFile));
        Assert.False(File.Exists(SessionMarker));
        Assert.DoesNotContain(_warnings, w => w != Strings.Get("toast.signIn.unencrypted"));
    }

    [Fact]
    public async Task SigningOut_WhenTheKeyringRefuses_SaysTheCookiesAreStuck()
    {
        var vault = Vault(Keyring);
        await vault.Save("SAPISID=kept");
        _fake.FailDeletes = true;

        await vault.Clear();

        Assert.Equal(Strings.Get("toast.signOut.cookiesStuck"), Assert.Single(_warnings));
    }

    [Fact]
    public async Task SigningOut_WithTheKeyringOutOfReach_SaysTheCookiesAreStuck()
    {
        await Vault(Keyring).Save("SAPISID=kept");

        await Vault().Clear();

        Assert.Equal(Strings.Get("toast.signOut.cookiesStuck"), Assert.Single(_warnings));
        Assert.Single(_fake.Items);
    }

    [Fact]
    public async Task ThePlainFile_WarnsOnSave_ButNotOnARotation()
    {
        if (OperatingSystem.IsWindows())
            return;   // DPAPI there: nothing to warn about
        var vault = Vault();

        await vault.Save("SAPISID=pasted");
        await vault.Update("SAPISID=pasted; SIDCC=fresh");

        Assert.Equal(Strings.Get("toast.signIn.unencrypted"), Assert.Single(_warnings));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(SessionFile));
    }

    [Theory]
    [InlineData(CredentialStorage.WindowsDataProtection, "signIn.storage.encrypted", true)]
    [InlineData(CredentialStorage.Keyring, "signIn.storage.keyring", true)]
    [InlineData(CredentialStorage.PlainFile, "signIn.storage.plain", false)]
    public void TheSignInDialog_SaysWhereTheCookiesGo(CredentialStorage storage, string key, bool encrypted)
    {
        var dialog = new SignInDialogViewModel((_, _) => throw new InvalidOperationException(), storage);

        Assert.Equal(Strings.Get(key), dialog.StorageNote);
        Assert.Equal(encrypted, dialog.IsStorageEncrypted);
    }

    [Fact]
    public async Task TheSignInDialog_OpensWhileTheKeyringSearchRuns_AndSaysSoUntilItAnswers()
    {
        var search = new TaskCompletionSource<ISecretStore?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vault = new CookieVault(_dir, new SystemKeyring(search.Task));

        // This once read Storage, which waits for the search.
        var dialog = new SignInDialogViewModel((_, _) => throw new InvalidOperationException(), vault.GetStorageAsync());

        Assert.Null(dialog.Storage);
        Assert.Equal(Strings.Get("signIn.storage.looking"), dialog.StorageNote);
        Assert.True(dialog.IsStorageEncrypted);   // the footnote's place until it knows

        var changed = new List<string?>();
        dialog.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        search.SetResult(_fake);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (dialog.Storage is null && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.Equal(CredentialStorage.Keyring, dialog.Storage);
        Assert.Equal(Strings.Get("signIn.storage.keyring"), dialog.StorageNote);
        Assert.Contains(nameof(SignInDialogViewModel.StorageNote), changed);
    }

    [Fact]
    public async Task AFailedKeyringSearch_CountsAsNoKeyring()
    {
        var keyring = new SystemKeyring(Task.FromException<ISecretStore?>(new InvalidOperationException("bus")));

        var expected = OperatingSystem.IsWindows() ? CredentialStorage.WindowsDataProtection : CredentialStorage.PlainFile;
        Assert.Equal(expected, await keyring.GetStorageAsync());
    }

    [Fact]
    public void TheLastFmSession_GoesToTheKeyringToo_AsVersionedJson()
    {
        var file = Path.Combine(_dir, "lastfm-session.bin");
        new LastFmSessionVault(_dir).Save(new LastFmSession("DemoListener", "SK"));
        Assert.True(File.Exists(file));

        var vault = new LastFmSessionVault(_dir, Keyring);

        Assert.Equal(new LastFmSession("DemoListener", "SK"), vault.Load());
        var item = Assert.Single(_fake.Items.Values);
        Assert.Equal("lastfm-session", item.Attributes["kind"]);
        Assert.Equal("""{"version":1,"username":"DemoListener","key":"SK"}""", item.Secret);
        Assert.False(File.Exists(file));

        vault.Clear();
        Assert.Empty(_fake.Items);
        Assert.Null(vault.Load());
    }

    [Fact]
    public void ThePhoneRemotePairing_MovesIntoTheKeyring_AndRotatesThere()
    {
        var file = Path.Combine(_dir, "remote-pairing.bin");
        var token = new RemoteTokenVault(_dir).LoadOrCreate();
        Assert.True(File.Exists(file));

        var vault = new RemoteTokenVault(_dir, Keyring);

        Assert.Equal(token, vault.LoadOrCreate());
        var item = Assert.Single(_fake.Items.Values);
        Assert.Equal("remote-pairing", item.Attributes["kind"]);
        Assert.Equal(Strings.Get("keyring.label.remote"), item.Label);
        Assert.Equal($$"""{"version":1,"token":"{{token}}"}""", item.Secret);
        Assert.False(File.Exists(file));

        var rotated = vault.Rotate();
        Assert.NotEqual(token, rotated);
        Assert.Contains(rotated, Assert.Single(_fake.Items.Values).Secret);
        Assert.Equal(rotated, vault.LoadOrCreate());
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ThePhoneRemotePairing_IsNeverWrittenInPlaintext_WhenTheKeyringRefuses()
    {
        _fake.FailWrites = true;
        var vault = new RemoteTokenVault(_dir, Keyring);

        var token = vault.LoadOrCreate();

        // It works for this run; phones pair again after a restart.
        Assert.True(PairingToken.IsWellFormed(token));
        Assert.Empty(_fake.Items);
        Assert.False(File.Exists(Path.Combine(_dir, "remote-pairing.bin")));
    }
}
