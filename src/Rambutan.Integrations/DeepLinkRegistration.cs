using Microsoft.Win32;
using Rambutan.Core.Links;
using Velopack.Locators;

namespace Rambutan.Integrations;

/// <summary>The few registry calls the registration needs; tests pass a fake.</summary>
public interface IRegistryKeys
{
    void SetValue(string subKey, string name, string value);
    string? GetValue(string subKey, string name);
    void DeleteTree(string subKey);
}

/// <summary>
/// Registers the <c>rambutan://</c> scheme with Windows, per user:
/// <c>HKCU\Software\Classes\rambutan</c>, opening <c>"&lt;exe&gt;" --deep-link "%1"</c>.
/// Only an installed build registers, from Velopack's install and update hooks;
/// uninstall removes the key if it still points at this install. Dev and portable
/// runs never touch the registry. Linux registers at startup instead
/// (<see cref="Linux.LinuxDeepLinkRegistration"/>).
/// </summary>
public static class DeepLinkRegistration
{
    public const string ClassKey = @"Software\Classes\" + DeepLinkParser.Scheme;
    public const string CommandKey = ClassKey + @"\shell\open\command";
    public const string IconKey = ClassKey + @"\DefaultIcon";

    /// <summary>The command the scheme runs. The link is quoted and always last;
    /// the app keeps only the argument after the option (DeepLinkArgs.Sanitize).</summary>
    public static string Command(string exePath) => $"\"{exePath}\" {DeepLinkArgs.Option} \"%1\"";

    /// <summary>Every value the registration writes, as (key, value name, data);
    /// an empty name is the key's default value.</summary>
    public static IReadOnlyList<(string Key, string Name, string Value)> Entries(string exePath) =>
    [
        (ClassKey, "", "URL:Rambutan link"),
        (ClassKey, "URL Protocol", ""),
        (IconKey, "", $"\"{exePath}\",0"),
        (CommandKey, "", Command(exePath)),
    ];

    public static void Register(IRegistryKeys keys, string exePath)
    {
        foreach (var (key, name, value) in Entries(exePath))
            keys.SetValue(key, name, value);
    }

    /// <summary>Removes the scheme only when it opens this install, by any of the
    /// paths it may have been registered with: another install, or another app
    /// that took the scheme over, keeps it.</summary>
    public static bool Unregister(IRegistryKeys keys, IEnumerable<string> exePaths)
    {
        var command = keys.GetValue(CommandKey, "");
        if (command is null ||
            !exePaths.Any(exe => string.Equals(command, Command(exe), StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }
        keys.DeleteTree(ClassKey);
        return true;
    }

    /// <summary>Velopack's after-install and after-update hooks. Never throws: a
    /// failed registration must not fail an install.</summary>
    public static void RegisterInstalled()
    {
        if (!OperatingSystem.IsWindows() || InstalledExePaths() is not { } exe)
            return;
        try
        {
            Register(new CurrentUserKeys(), exe.Preferred);
        }
        catch
        {
            // The scheme stays unregistered; links still work as arguments.
        }
    }

    /// <summary>Velopack's before-uninstall hook.</summary>
    public static void UnregisterInstalled()
    {
        if (!OperatingSystem.IsWindows() || InstalledExePaths() is not { } exe)
            return;
        try
        {
            Unregister(new CurrentUserKeys(), exe.All);
        }
        catch
        {
            // Nothing more to do while uninstalling.
        }
    }

    /// <summary>
    /// The exe the scheme should open: Velopack's stub in the install root, which
    /// keeps its path across updates; failing that, this exe under <c>current\</c>,
    /// which Velopack also keeps in place. <c>All</c> holds both, for uninstall to
    /// match whichever was written. Null for a portable build, which updates (and
    /// so runs the update hook) but must never register.
    /// </summary>
    private static (string Preferred, string[] All)? InstalledExePaths()
    {
        var self = Environment.ProcessPath;
        if (self is null)
            return null;
        string? stub = null;
        try
        {
            if (VelopackLocator.IsCurrentSet)
            {
                var locator = VelopackLocator.Current;
                if (locator.IsPortable)
                    return null;
                if (locator.RootAppDir is { } root)
                    stub = Path.Combine(root, Path.GetFileName(self));
            }
        }
        catch
        {
        }
        return stub is null
            ? (self, [self])
            : (File.Exists(stub) ? stub : self, [stub, self]);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private sealed class CurrentUserKeys : IRegistryKeys
    {
        public void SetValue(string subKey, string name, string value)
        {
            using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true);
            key.SetValue(name, value, RegistryValueKind.String);
        }

        public string? GetValue(string subKey, string name)
        {
            using var key = Registry.CurrentUser.OpenSubKey(subKey);
            return key?.GetValue(name) as string;
        }

        public void DeleteTree(string subKey) =>
            Registry.CurrentUser.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false);
    }
}
