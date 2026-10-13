using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Integrations.Linux;

namespace Rambutan.App.Tests;

/// <summary>
/// Profiles: the versioned index in the root data folder, folders derived
/// from ids, choosing a profile at launch, and deleting one: its keyring items,
/// its folder and its entry, and never anything else. The keyring is a fake.
/// </summary>
public sealed class ProfilesTests : IDisposable
{
    private sealed class FakeKeyring : ISecretStore
    {
        public readonly List<IReadOnlyDictionary<string, string>> Deleted = [];
        public bool FailDeletes { get; set; }

        public string? Read(IReadOnlyDictionary<string, string> attributes) => null;

        public void Write(IReadOnlyDictionary<string, string> attributes, string label, string secret)
        {
        }

        public void Delete(IReadOnlyDictionary<string, string> attributes)
        {
            if (FailDeletes)
                throw new SecretStoreException("locked");
            Deleted.Add(new Dictionary<string, string>(attributes));
        }
    }

    private sealed class Lock : IDisposable
    {
        public bool Released { get; private set; }
        public void Dispose() => Released = true;
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "rambutan-profiles-" + Guid.NewGuid().ToString("N"));
    private readonly FakeKeyring _fake = new();

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private AppDataLocation Root => new(_root, Path.Combine(_root, "tools"), "Rambutan-test");

    private ProfileStore Store() => new(Root);

    private string IndexPath => Path.Combine(_root, ProfileStore.FileName);

    private SystemKeyring Keyring => new(Task.FromResult<ISecretStore?>(_fake));

    private static IDisposable? Free(string _) => new Lock();

    [Fact]
    public void WithNoIndex_TheDefaultProfileIsTheRootFolder_AndNothingIsWritten()
    {
        var store = Store();

        var profile = store.ChooseAtLaunch(null, out var unknown);
        store.SetLastUsed(profile.Id);

        Assert.Equal(ProfileStore.DefaultId, Assert.Single(store.Load()).Id);
        Assert.True(profile.IsDefault);
        Assert.Null(unknown);
        Assert.Equal(ProfileIndexState.Ok, store.State);
        Assert.Equal(Root, store.LocationFor(profile));
        Assert.Equal("Default", profile.DisplayName);
        Assert.False(File.Exists(IndexPath));
    }

    [Fact]
    public void ANewProfile_GetsItsOwnFolderAndInstance_AndSharesTheSidecars()
    {
        var store = Store();

        var (created, error) = store.Create("  Work  ");

        Assert.Equal(ProfileError.None, error);
        Assert.NotNull(created);
        Assert.Equal("Work", created.Name);
        Assert.True(ProfileStore.IsValidId(created.Id));
        var folder = store.FolderFor(created.Id);
        Assert.Equal(Path.Combine(_root, "profiles", created.Id), folder);
        Assert.True(Directory.Exists(folder));
        var location = store.LocationFor(created);
        Assert.Equal(folder, location.DataDir);
        Assert.Equal(Root.ToolsDir, location.ToolsDir);
        Assert.StartsWith("Rambutan-", location.InstanceName);
        Assert.NotEqual(Root.InstanceName, location.InstanceName);
        Assert.Equal(["default", created.Id], Store().Load().Select(p => p.Id));
    }

    [Fact]
    public void TheIndex_IsVersionedJson_ThatReadsBack()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        store.Rename(ProfileStore.DefaultId, "Home");
        store.SetLastUsed(work!.Id);

        var text = File.ReadAllText(IndexPath);
        Assert.Contains("\"version\": 1", text);
        Assert.Contains("\"lastUsed\": \"" + work.Id + "\"", text);
        var again = Store();
        Assert.Equal(["Home", "Work"], again.Load().Select(p => p.DisplayName));
        Assert.Equal(work.Id, again.ChooseAtLaunch(null, out _).Id);
    }

    [Theory]
    [InlineData("", ProfileError.NameEmpty)]
    [InlineData("   ", ProfileError.NameEmpty)]
    [InlineData("work", ProfileError.NameTaken)]
    [InlineData("DEFAULT", ProfileError.NameTaken)]
    public void Names_AreNeededAndUnique(string name, ProfileError expected)
    {
        var store = Store();
        store.Create("Work");

        Assert.Equal(expected, store.Create(name).Error);
        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public void Names_HaveALimit_AndRenamingKeepsTheFolder()
    {
        var store = Store();
        var (work, _) = store.Create("Work");

        Assert.Equal(ProfileError.NameTooLong, store.Create(new string('x', ProfileStore.MaxNameLength + 1)).Error);
        Assert.Equal(ProfileError.None, store.Rename(work!.Id, "WORK"));
        Assert.Equal(ProfileError.NotFound, store.Rename("nosuch", "Other"));
        Assert.Equal("WORK", store.Load()[1].Name);
        Assert.Equal(store.FolderFor(work.Id), Path.Combine(_root, "profiles", work.Id));
    }

    [Fact]
    public void ALaunch_OpensTheProfileAskedFor_ByIdOrName_ElseSaysSo()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var (home, _) = store.Create("Home");
        store.SetLastUsed(home!.Id);

        Assert.Equal(work!.Id, store.ChooseAtLaunch("work", out var unknown).Id);
        Assert.Null(unknown);
        Assert.Equal(work.Id, store.ChooseAtLaunch(work.Id, out _).Id);
        Assert.Equal(ProfileStore.DefaultId, store.ChooseAtLaunch("Default", out _).Id);
        Assert.Equal(home.Id, store.ChooseAtLaunch(null, out _).Id);

        Assert.Equal(home.Id, store.ChooseAtLaunch(" Nope ", out unknown).Id);
        Assert.Equal("Nope", unknown);
    }

    [Theory]
    [InlineData("""{ "version": 2, "profiles": [] }""", ProfileIndexState.Newer)]
    [InlineData("""{ "version": 0 }""", ProfileIndexState.Unreadable)]
    [InlineData("not json", ProfileIndexState.Unreadable)]
    [InlineData("""{ "version": 1, "profiles": [ { "id": "..", "name": "Up" } ] }""", ProfileIndexState.Unreadable)]
    [InlineData("""{ "version": 1, "profiles": [ { "id": "C:\\", "name": "Drive" } ] }""", ProfileIndexState.Unreadable)]
    [InlineData("""{ "version": 1, "profiles": [ { "id": "abc", "name": "A" }, { "id": "abc", "name": "B" } ] }""", ProfileIndexState.Unreadable)]
    [InlineData("""{ "version": 1, "profiles": [ { "id": "abc" } ] }""", ProfileIndexState.Unreadable)]
    public void AnIndexThisBuildCantUse_IsLeftAlone_AndOnlyTheDefaultRuns(string text, ProfileIndexState state)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(IndexPath, text);
        var store = Store();

        Assert.True(Assert.Single(store.Load()).IsDefault);
        Assert.Equal(state, store.State);
        Assert.Equal(ProfileError.IndexLocked, store.Create("Work").Error);
        Assert.Equal(ProfileError.IndexLocked, store.Rename(ProfileStore.DefaultId, "Home"));
        store.SetLastUsed(ProfileStore.DefaultId);
        Assert.Equal(text, File.ReadAllText(IndexPath));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("a/b")]
    [InlineData("A")]
    [InlineData("")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456789")]
    public void Folders_ComeOnlyFromValidIds(string id)
    {
        Assert.False(ProfileStore.IsValidId(id));
        Assert.Throws<ArgumentException>(() => Store().FolderFor(id));
    }

    [Fact]
    public void Deleting_RemovesTheKeyringItemsOfThatFolder_ThenTheFolder_ThenTheEntry()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var (home, _) = store.Create("Home");
        var folder = store.FolderFor(work!.Id);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(folder, "cache", "downloads"));
        store.SetLastUsed(work.Id);
        string? reserved = null;
        var held = new Lock();

        var error = store.Delete(work.Id, ProfileStore.DefaultId, Keyring, name => { reserved = name; return held; });

        Assert.Equal(ProfileError.None, error);
        Assert.False(Directory.Exists(folder));
        Assert.True(Directory.Exists(store.FolderFor(home!.Id)));
        Assert.Equal(["default", home.Id], store.Load().Select(p => p.Id));
        Assert.Equal(ProfileStore.DefaultId, store.ChooseAtLaunch(null, out _).Id);
        Assert.Equal(store.LocationFor(work).InstanceName, reserved);
        Assert.True(held.Released);
        // Exactly that folder's items, of every kind and version: no wider match.
        var attributes = Assert.Single(_fake.Deleted);
        Assert.Equal(2, attributes.Count);
        Assert.Equal("rambutan", attributes["application"]);
        Assert.Equal(folder, attributes["data-dir"]);
    }

    [Fact]
    public void TheDefault_TheOpenProfile_AndARunningOne_AreNeverDeleted()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var folder = store.FolderFor(work!.Id);

        Assert.Equal(ProfileError.IsDefault, store.Delete(ProfileStore.DefaultId, work.Id, Keyring, Free));
        Assert.Equal(ProfileError.IsCurrent, store.Delete(work.Id, work.Id, Keyring, Free));
        Assert.Equal(ProfileError.Running, store.Delete(work.Id, ProfileStore.DefaultId, Keyring, _ => null));
        Assert.Equal(ProfileError.NotFound, store.Delete("abc", ProfileStore.DefaultId, Keyring, Free));

        Assert.True(Directory.Exists(folder));
        Assert.Equal(2, store.Load().Count);
        Assert.Empty(_fake.Deleted);
    }

    [Fact]
    public void AKeyringThatRefuses_LeavesTheProfileWhole()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        _fake.FailDeletes = true;

        Assert.Equal(ProfileError.KeyringFailed, store.Delete(work!.Id, ProfileStore.DefaultId, Keyring, Free));
        Assert.True(Directory.Exists(store.FolderFor(work.Id)));
        Assert.Equal(2, store.Load().Count);
    }

    [Fact]
    public void SignInsInAKeyringThisRunCantReach_LeaveTheProfileWhole()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var folder = store.FolderFor(work!.Id);
        File.WriteAllText(Path.Combine(folder, "session.keyring.json"), """{ "version": 1 }""");

        var error = store.Delete(work.Id, ProfileStore.DefaultId, SystemKeyring.None, Free);

        // Windows keeps credentials as files in the folder, so it has no markers.
        Assert.Equal(ProfileError.KeyringMissing, error);
        Assert.True(Directory.Exists(folder));
    }

    [Fact]
    public void WithoutAKeyring_TheCredentialFilesGoWithTheFolder()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var folder = store.FolderFor(work!.Id);
        File.WriteAllText(Path.Combine(folder, "session.bin"), "secret");

        Assert.Equal(ProfileError.None, store.Delete(work.Id, ProfileStore.DefaultId, SystemKeyring.None, Free));
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task ACredential_IsStoredUnderItsFoldersAttributes()
    {
        var folder = Path.Combine(_root, "profiles", "abc");
        var vaultKeyring = new RecordingKeyring();
        await new CookieVault(folder, new SystemKeyring(Task.FromResult<ISecretStore?>(vaultKeyring))).Save("SAPISID=x");

        var written = Assert.Single(vaultKeyring.Written);
        foreach (var (key, value) in StoredCredential.FolderAttributes(folder))
            Assert.Equal(value, written[key]);
    }

    private sealed class RecordingKeyring : ISecretStore
    {
        public readonly List<IReadOnlyDictionary<string, string>> Written = [];
        public string? Read(IReadOnlyDictionary<string, string> attributes) => null;
        public void Write(IReadOnlyDictionary<string, string> attributes, string label, string secret) =>
            Written.Add(new Dictionary<string, string>(attributes));
        public void Delete(IReadOnlyDictionary<string, string> attributes) { }
    }

    [Theory]
    [InlineData(new[] { "--profile", "Work" }, "Work")]
    [InlineData(new[] { "--other", "--profile=Home office" }, "Home office")]
    [InlineData(new[] { "--profile" }, null)]
    // A deep-link launch carries only the link: no profile.
    [InlineData(new[] { "--deep-link", "rambutan://play?v=abc" }, null)]
    [InlineData(new string[0], null)]
    public void TheCommandLine_NamesAProfile(string[] args, string? expected) =>
        Assert.Equal(expected, ProfileLaunch.FindArgument(args));

    [Fact]
    public void TheMenu_OffersTheOtherProfiles_AndSwitchingRestartsIntoOne()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        string? restartedInto = null;
        var vm = new ProfilesViewModel(store, store.Load()[0], Keyring, Free, id => restartedInto = id);

        Assert.True(vm.ShowsProfile);
        Assert.Equal("Default", vm.CurrentName);
        var other = Assert.Single(vm.Others);
        Assert.Equal("Work", other.Name);
        Assert.False(vm.Rows[0].CanDelete);
        Assert.True(other.CanDelete);

        other.SwitchCommand.Execute(null);
        Assert.Equal(work!.Id, restartedInto);
    }

    [Fact]
    public void TheDeleteConfirmation_SaysWhatGoes_AndWhatStays()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var folder = store.FolderFor(work!.Id);
        var vm = new ProfilesViewModel(store, store.Load()[0], Keyring, Free, _ => { });

        var plain = vm.DeleteMessage(work);
        Assert.Contains("Work", plain);
        Assert.Contains(folder, plain);
        Assert.Contains("downloads", plain);

        var elsewhere = Path.Combine(_root, "my-music");
        new AppSettingsStore(folder).Save(new AppSettings { DownloadsFolder = elsewhere });
        Assert.Contains(elsewhere, vm.DeleteMessage(work));
    }

    [Fact]
    public async Task DeletingFromTheDialog_AsksFirst_AndReportsWhyNot()
    {
        var store = Store();
        var (work, _) = store.Create("Work");
        var vm = new ProfilesViewModel(store, store.Load()[0], Keyring, _ => null, _ => { });
        var asked = 0;
        vm.Confirm = (_, accept) => { asked++; Assert.Equal("Delete", accept); return Task.FromResult(true); };

        await vm.Rows[1].DeleteCommand.ExecuteAsync(null);

        Assert.Equal(1, asked);
        Assert.True(vm.HasProblem);
        Assert.Contains("open in another window", vm.Problem);
        Assert.True(Directory.Exists(store.FolderFor(work!.Id)));
    }
}
