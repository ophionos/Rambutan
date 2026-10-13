using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;

namespace Rambutan.App.ViewModels;

/// <summary>One profile in the account menu and the Profiles dialog.</summary>
public sealed partial class ProfileRowViewModel : ObservableObject
{
    private readonly ProfilesViewModel _owner;

    public ProfileRowViewModel(ProfilesViewModel owner, Profile profile, bool isCurrent)
    {
        _owner = owner;
        Profile = profile;
        IsCurrent = isCurrent;
    }

    public Profile Profile { get; }
    public string Name => Profile.DisplayName;
    public bool IsCurrent { get; }
    public bool CanSwitch => !IsCurrent && _owner.CanManage;
    public bool CanRename => _owner.CanManage;
    public bool CanDelete => !IsCurrent && !Profile.IsDefault && _owner.CanManage;

    /// <summary>What the row says under its name: open now, or the original profile.</summary>
    public string Caption => IsCurrent ? Strings.Get("profiles.caption.current")
        : Profile.IsDefault ? Strings.Get("profiles.caption.default")
        : "";

    public bool HasCaption => Caption.Length > 0;

    public string SwitchName => Strings.Format("profiles.switchTo", ("name", Name));
    public string RenameName => Strings.Format("profiles.renameNamed", ("name", Name));
    public string DeleteName => Strings.Format("profiles.deleteNamed", ("name", Name));

    [RelayCommand]
    private void Switch() => _owner.SwitchTo(this);

    [RelayCommand]
    private Task Rename() => _owner.RenameAsync(this);

    [RelayCommand]
    private Task Delete() => _owner.DeleteAsync(this);
}

/// <summary>
/// Profiles: which one is open, switching to another, and creating,
/// renaming and deleting them. Switching restarts the app into the chosen
/// profile; everything a run holds (sign-in, playback, the remote) belongs to
/// one data folder, and a fresh start is the honest way to change all of it.
/// </summary>
public sealed partial class ProfilesViewModel : ViewModelBase
{
    private readonly ProfileStore _store;
    private readonly string _currentId;
    private readonly SystemKeyring _keyring;
    private readonly Func<string, IDisposable?> _reserve;
    private readonly Action<string> _restartInto;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _problem;

    [ObservableProperty] private bool _busy;

    /// <param name="reserve">Takes a profile's single-instance lock; null when it's running.</param>
    /// <param name="restartInto">Ends this run and starts the app in the profile with this id.</param>
    public ProfilesViewModel(ProfileStore store, Profile current, SystemKeyring keyring,
        Func<string, IDisposable?> reserve, Action<string> restartInto)
    {
        _store = store;
        _currentId = current.Id;
        _keyring = keyring;
        _reserve = reserve;
        _restartInto = restartInto;
        Refresh();
        Strings.WhenChanged(this, static vm => vm.Refresh());
    }

    public ObservableCollection<ProfileRowViewModel> Rows { get; } = [];

    public IReadOnlyList<ProfileRowViewModel> Others => [.. Rows.Where(r => !r.IsCurrent)];

    public bool HasOthers => Others.Count > 0;

    public bool CanManage => _store.State is ProfileIndexState.Ok;

    /// <summary>Why the profiles can't be changed, when they can't.</summary>
    public string? LockedReason => _store.State switch
    {
        ProfileIndexState.Newer => Strings.Format("profiles.locked.newer", ("path", _store.IndexPath)),
        ProfileIndexState.Unreadable => Strings.Format("profiles.locked.unreadable", ("path", _store.IndexPath)),
        _ => null,
    };

    public bool IsLocked => LockedReason is not null;

    public string CurrentName => Rows.FirstOrDefault(r => r.IsCurrent)?.Name ?? "";

    /// <summary>With more than one profile, the open one is named in the
    /// account chip and the window title, so a switch has a visible result.</summary>
    public bool ShowsProfile => Rows.Count > 1;

    public string ChipLine => Strings.Format("profiles.chip", ("name", CurrentName));

    public bool HasProblem => Problem is not null;

    /// <summary>Set by the view: asks for a name (title, initial text); null when cancelled.</summary>
    public Func<string, string, Task<string?>>? TextPrompt { get; set; }

    /// <summary>Set by the view: asks to confirm (message, accept label).</summary>
    public Func<string, string, Task<bool>>? Confirm { get; set; }

    /// <summary>Raised to open the Profiles dialog.</summary>
    public event Action? ManageRequested;

    [RelayCommand]
    private void Manage()
    {
        Problem = null;
        Refresh();
        ManageRequested?.Invoke();
    }

    [RelayCommand]
    private async Task Create()
    {
        if (TextPrompt is null || await TextPrompt(Strings.Get("profiles.newTitle"), "") is not { } name)
            return;
        var (_, error) = _store.Create(name);
        Report(error);
        Refresh();
    }

    internal void SwitchTo(ProfileRowViewModel row)
    {
        if (row.IsCurrent || !CanManage)
            return;
        _restartInto(row.Profile.Id);
    }

    internal async Task RenameAsync(ProfileRowViewModel row)
    {
        if (TextPrompt is null
            || await TextPrompt(Strings.Format("profiles.renameNamed", ("name", row.Name)), row.Name) is not { } name)
            return;
        Report(_store.Rename(row.Profile.Id, name));
        Refresh();
    }

    internal async Task DeleteAsync(ProfileRowViewModel row)
    {
        if (!row.CanDelete || Confirm is null)
            return;
        if (!await Confirm(DeleteMessage(row.Profile), Strings.Get("common.delete")))
            return;
        Busy = true;
        try
        {
            var id = row.Profile.Id;
            Report(await Task.Run(() => _store.Delete(id, _currentId, _keyring, _reserve)));
        }
        finally
        {
            Busy = false;
        }
        Refresh();
    }

    /// <summary>What the confirmation says goes: everything in the profile's
    /// folder, named, and where the folder is. Downloads kept in a folder the
    /// profile chose in Settings stay where they are.</summary>
    internal string DeleteMessage(Profile profile)
    {
        var folder = _store.FolderFor(profile.Id);
        var message = Strings.Format("profiles.deleteConfirm", ("name", profile.DisplayName), ("path", folder));
        var downloads = new AppSettingsStore(folder).Load().DownloadsFolder;
        return string.IsNullOrWhiteSpace(downloads)
            ? message
            : message + "\n\n" + Strings.Format("profiles.deleteKeepsDownloads", ("path", downloads));
    }

    private void Report(ProfileError error) => Problem = error switch
    {
        ProfileError.None => null,
        ProfileError.NameEmpty => Strings.Get("profiles.error.nameEmpty"),
        ProfileError.NameTooLong => Strings.Format("profiles.error.nameTooLong", ("count", ProfileStore.MaxNameLength)),
        ProfileError.NameTaken => Strings.Get("profiles.error.nameTaken"),
        ProfileError.Running => Strings.Get("profiles.error.running"),
        ProfileError.KeyringFailed => Strings.Get("profiles.error.keyringFailed"),
        ProfileError.KeyringMissing => Strings.Get("profiles.error.keyringMissing"),
        ProfileError.FilesFailed => Strings.Get("profiles.error.filesFailed"),
        ProfileError.IndexLocked => LockedReason ?? Strings.Get("profiles.error.filesFailed"),
        // The list was out of date (another window changed it); the refresh shows it.
        _ => Strings.Get("profiles.error.changed"),
    };

    private void Refresh()
    {
        var profiles = _store.Load();
        Rows.Clear();
        foreach (var profile in profiles)
            Rows.Add(new ProfileRowViewModel(this, profile, profile.Id == _currentId));
        OnPropertyChanged(nameof(Others));
        OnPropertyChanged(nameof(HasOthers));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(LockedReason));
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(CurrentName));
        OnPropertyChanged(nameof(ShowsProfile));
        OnPropertyChanged(nameof(ChipLine));
    }
}
