using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Rambutan.App.Localization;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.Linux;

namespace Rambutan.App.Services;

/// <summary>On-disk shape of <c>profiles.json</c>, in the root data folder.</summary>
public sealed class SavedProfiles
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// <summary>The profile a launch without <c>--profile</c> opens.</summary>
    public string? LastUsed { get; set; }

    public List<SavedProfile> Profiles { get; set; } = [];
}

public sealed class SavedProfile
{
    public string? Id { get; set; }

    /// <summary>Null only for the default profile until it is renamed: it shows
    /// as the localized "Default".</summary>
    public string? Name { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SavedProfiles))]
internal sealed partial class ProfilesJsonContext : JsonSerializerContext;

/// <summary>One profile: a data folder with its own settings, sign-ins, caches and downloads.</summary>
public sealed record Profile(string Id, string? Name)
{
    public bool IsDefault => Id == ProfileStore.DefaultId;

    public string DisplayName => Name ?? Strings.Get("profiles.defaultName");
}

/// <summary>Whether <c>profiles.json</c> can be used, and changed.</summary>
public enum ProfileIndexState
{
    /// <summary>Read (or absent, which means "only the default profile").</summary>
    Ok,

    /// <summary>Unreadable or malformed: left untouched, only the default profile runs.</summary>
    Unreadable,

    /// <summary>Written by a newer build: left untouched, only the default profile runs.</summary>
    Newer,
}

public enum ProfileError
{
    None,
    NameEmpty,
    NameTooLong,
    NameTaken,
    NotFound,
    IndexLocked,
    IsDefault,
    IsCurrent,
    Running,
    KeyringFailed,
    KeyringMissing,
    FilesFailed,
}

/// <summary>
/// The profiles of one root data folder. The default profile is the root
/// folder itself, so a user from before profiles keeps everything where it is.
/// Every other profile lives in <c>profiles/&lt;id&gt;</c>: the folder is derived
/// from the id, which must match <see cref="IsValidId"/>, and never read from the
/// file, so a hand-edited index can't point a delete anywhere else. Names are for
/// people; renaming never moves a folder.
/// <para>
/// Every change reads the file again first, so two profiles running at once
/// don't undo each other's edits. A file this build can't read, or one from a
/// newer build, is never written: <see cref="State"/> says why, and only the
/// default profile is offered.
/// </para>
/// </summary>
public sealed partial class ProfileStore(AppDataLocation root)
{
    public const string DefaultId = "default";
    public const string FileName = "profiles.json";
    public const string FolderName = "profiles";
    public const int MaxNameLength = 40;

    private readonly object _gate = new();

    public AppDataLocation Root { get; } = root;

    public string IndexPath => Path.Combine(Root.DataDir, FileName);

    public ProfileIndexState State { get; private set; }

    private string ProfilesDir => Path.Combine(Root.DataDir, FolderName);

    [GeneratedRegex("^[a-z0-9]{1,32}$")]
    private static partial Regex IdPattern();

    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    /// <summary>The folder of a profile: the root for the default, else
    /// <c>profiles/&lt;id&gt;</c>, checked to be inside it.</summary>
    public string FolderFor(string id)
    {
        if (id == DefaultId)
            return Root.DataDir;
        if (!IsValidId(id))
            throw new ArgumentException($"Not a profile id: {id}", nameof(id));
        var folder = Path.GetFullPath(Path.Combine(ProfilesDir, id));
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProfilesDir)) + Path.DirectorySeparatorChar;
        if (!folder.StartsWith(parent, StringComparison.Ordinal) || folder.Length <= parent.Length)
            throw new ArgumentException($"Not a profile folder: {folder}", nameof(id));
        return folder;
    }

    /// <summary>Where a profile runs: its folder, and the single-instance name that goes with it.</summary>
    public AppDataLocation LocationFor(Profile profile) =>
        profile.IsDefault ? Root : Root.ForProfileFolder(FolderFor(profile.Id));

    /// <summary>The profiles, the default first.</summary>
    public IReadOnlyList<Profile> Load() => Read().Profiles;

    public string? LastUsed => Read().LastUsed;

    /// <summary>
    /// The profile a launch opens: the one asked for by id or name, else the
    /// last used, else the default. <paramref name="unknown"/> is the request
    /// when nothing matched it, so the app can say what it opened instead.
    /// </summary>
    public Profile ChooseAtLaunch(string? requested, out string? unknown)
    {
        var index = Read();
        unknown = null;
        if (!string.IsNullOrWhiteSpace(requested))
        {
            if (Find(index.Profiles, requested.Trim()) is { } asked)
                return asked;
            unknown = requested.Trim();
        }
        return index.Profiles.FirstOrDefault(p => p.Id == index.LastUsed) ?? index.Profiles[0];
    }

    private static Profile? Find(IReadOnlyList<Profile> profiles, string nameOrId) =>
        profiles.FirstOrDefault(p => p.Id == nameOrId)
        ?? profiles.FirstOrDefault(p => string.Equals(p.DisplayName, nameOrId, StringComparison.CurrentCultureIgnoreCase));

    public void SetLastUsed(string id) => Change(index =>
    {
        if (index.Profiles.All(p => p.Id != id) || index.LastUsed == id)
            return (ProfileError.NotFound, false);
        index.LastUsed = id;
        return (ProfileError.None, true);
    });

    public (Profile? Profile, ProfileError Error) Create(string name)
    {
        Profile? created = null;
        var error = Change(index =>
        {
            if (CheckName(index.Profiles, name, exceptId: null, out var clean) is var problem and not ProfileError.None)
                return (problem, false);
            string id;
            do
                id = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            while (index.Profiles.Any(p => p.Id == id) || Directory.Exists(FolderFor(id)));
            created = new Profile(id, clean);
            index.Profiles.Add(created);
            return (ProfileError.None, true);
        });
        if (created is not null)
        {
            try
            {
                Directory.CreateDirectory(FolderFor(created.Id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The app creates what it needs on first start there anyway.
                AppLog.Write("profiles", $"Couldn't create the folder of profile {created.Id}: {e.Message}");
            }
        }
        return (created, error);
    }

    public ProfileError Rename(string id, string name) => Change(index =>
    {
        var at = index.Profiles.FindIndex(p => p.Id == id);
        if (at < 0)
            return (ProfileError.NotFound, false);
        if (CheckName(index.Profiles, name, exceptId: id, out var clean) is var problem and not ProfileError.None)
            return (problem, false);
        index.Profiles[at] = index.Profiles[at] with { Name = clean };
        return (ProfileError.None, true);
    });

    /// <summary>
    /// Deletes a profile: its keyring items, then its folder, then its entry.
    /// Never the default or <paramref name="currentId"/>, and never one running in
    /// another window: <paramref name="reserve"/> takes that profile's
    /// single-instance lock (null when it's held) and keeps it for the whole
    /// delete, so the profile can't start meanwhile. When the keyring items can't
    /// be removed nothing else is; when files can't be, the entry stays so the
    /// delete can be tried again. Blocks on the keyring: run it off the UI thread.
    /// </summary>
    public ProfileError Delete(string id, string currentId, SystemKeyring keyring, Func<string, IDisposable?> reserve)
    {
        if (id == DefaultId)
            return ProfileError.IsDefault;
        if (id == currentId)
            return ProfileError.IsCurrent;
        var index = Read();
        if (State is not ProfileIndexState.Ok)
            return ProfileError.IndexLocked;
        if (index.Profiles.FirstOrDefault(p => p.Id == id) is not { } profile)
            return ProfileError.NotFound;

        var folder = FolderFor(profile.Id);
        using var reservation = reserve(LocationFor(profile).InstanceName);
        if (reservation is null)
            return ProfileError.Running;

        if (DeleteCredentials(folder, keyring) is var problem and not ProfileError.None)
            return problem;

        try
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("profiles", $"Couldn't delete the folder of profile {id}: {e.Message}");
            return ProfileError.FilesFailed;
        }

        return Change(fresh =>
        {
            fresh.Profiles.RemoveAll(p => p.Id == id);
            if (fresh.LastUsed == id)
                fresh.LastUsed = DefaultId;
            return (ProfileError.None, true);
        });
    }

    /// <summary>The keyring items of one data folder, every kind and format
    /// version. Windows keeps credentials as files in the folder; they go with it.</summary>
    private static ProfileError DeleteCredentials(string folder, SystemKeyring keyring)
    {
        if (keyring.Store is { } store)
        {
            try
            {
                store.Delete(StoredCredential.FolderAttributes(folder));
                return ProfileError.None;
            }
            catch (SecretStoreException e)
            {
                AppLog.Write("profiles", $"Couldn't delete a profile's keyring items: {e.Message}");
                return ProfileError.KeyringFailed;
            }
        }
        // Markers say a credential is in a keyring this run can't reach.
        return Directory.Exists(folder) && Directory.EnumerateFiles(folder, "*.keyring.json").Any()
            ? ProfileError.KeyringMissing
            : ProfileError.None;
    }

    private static ProfileError CheckName(IReadOnlyList<Profile> profiles, string? name, string? exceptId, out string clean)
    {
        clean = name?.Trim() ?? "";
        if (clean.Length == 0)
            return ProfileError.NameEmpty;
        if (clean.Length > MaxNameLength)
            return ProfileError.NameTooLong;
        var taken = clean;
        return profiles.Any(p => p.Id != exceptId && string.Equals(p.DisplayName, taken, StringComparison.CurrentCultureIgnoreCase))
            ? ProfileError.NameTaken
            : ProfileError.None;
    }

    private sealed class Index(List<Profile> profiles, string? lastUsed)
    {
        public List<Profile> Profiles { get; } = profiles;
        public string? LastUsed { get; set; } = lastUsed;
    }

    private Index Read()
    {
        lock (_gate)
        {
            string text;
            try
            {
                if (!File.Exists(IndexPath))
                {
                    State = ProfileIndexState.Ok;
                    return OnlyDefault();
                }
                text = File.ReadAllText(IndexPath);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("profiles", $"Couldn't read {FileName}: {e.Message}");
                State = ProfileIndexState.Unreadable;
                return OnlyDefault();
            }

            var (index, state) = Parse(text);
            if (state is not ProfileIndexState.Ok)
                AppLog.Write("profiles", $"{FileName} is {state}; running the default profile only, and leaving the file as it is.");
            State = state;
            return index ?? OnlyDefault();
        }
    }

    private static Index OnlyDefault() => new([new Profile(DefaultId, null)], DefaultId);

    /// <summary>Reads the file's text: the profiles with the default first, or
    /// null and the reason when it can't be used as it is.</summary>
    private static (Index? Index, ProfileIndexState State) Parse(string text)
    {
        SavedProfiles? saved;
        try
        {
            saved = JsonSerializer.Deserialize(text, ProfilesJsonContext.Default.SavedProfiles);
        }
        catch (JsonException)
        {
            return (null, ProfileIndexState.Unreadable);
        }
        if (saved is null || saved.Version < 1)
            return (null, ProfileIndexState.Unreadable);
        if (saved.Version > SavedProfiles.CurrentVersion)
            return (null, ProfileIndexState.Newer);

        var profiles = new List<Profile> { new(DefaultId, null) };
        foreach (var entry in saved.Profiles ?? [])
        {
            // Strict: dropping an entry would lose it on the next write.
            if (entry is null || !IsValidId(entry.Id) || profiles.Skip(1).Any(p => p.Id == entry.Id))
                return (null, ProfileIndexState.Unreadable);
            var name = string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name.Trim();
            if (entry.Id == DefaultId)
                profiles[0] = new Profile(DefaultId, name);
            else if (name is null)
                return (null, ProfileIndexState.Unreadable);
            else
                profiles.Add(new Profile(entry.Id!, name));
        }
        return (new Index(profiles, saved.LastUsed), ProfileIndexState.Ok);
    }

    internal static string Serialize(IEnumerable<Profile> profiles, string? lastUsed) =>
        JsonSerializer.Serialize(new SavedProfiles
        {
            LastUsed = lastUsed,
            Profiles = profiles.Select(p => new SavedProfile { Id = p.Id, Name = p.Name }).ToList(),
        }, ProfilesJsonContext.Default.SavedProfiles);

    private ProfileError Change(Func<Index, (ProfileError Error, bool Write)> change)
    {
        lock (_gate)
        {
            var index = Read();
            if (State is not ProfileIndexState.Ok)
                return ProfileError.IndexLocked;
            var (error, write) = change(index);
            if (!write)
                return error;
            try
            {
                Directory.CreateDirectory(Root.DataDir);
                var temp = IndexPath + ".tmp";
                File.WriteAllText(temp, Serialize(index.Profiles, index.LastUsed));
                File.Move(temp, IndexPath, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                AppLog.Write("profiles", $"Couldn't write {FileName}: {e.Message}");
                return ProfileError.FilesFailed;
            }
            return error;
        }
    }
}

/// <summary>The profile this run opened, chosen in <c>Program.Main</c>.</summary>
public sealed record ProfileLaunch(ProfileStore Store, Profile Profile, AppDataLocation Location, string? UnknownRequest)
{
    /// <summary>Picks the profile for this launch; <paramref name="requested"/> is
    /// the <c>--profile</c> argument, if any.</summary>
    public static ProfileLaunch Resolve(AppDataLocation root, string? requested)
    {
        var store = new ProfileStore(root);
        var profile = store.ChooseAtLaunch(requested, out var unknown);
        return new ProfileLaunch(store, profile, store.LocationFor(profile), unknown);
    }

    /// <summary>The value of <c>--profile &lt;name&gt;</c> or <c>--profile=&lt;name&gt;</c>;
    /// other arguments are left alone.</summary>
    public static string? FindArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i] == "--profile" && i + 1 < args.Count)
                return args[i + 1];
            if (args[i].StartsWith("--profile=", StringComparison.Ordinal))
                return args[i]["--profile=".Length..];
        }
        return null;
    }
}
