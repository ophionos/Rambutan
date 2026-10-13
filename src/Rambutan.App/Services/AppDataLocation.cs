using System.Security.Cryptography;
using System.Text;

namespace Rambutan.App.Services;

/// <summary>
/// Where the app keeps its settings, cookies, caches and logs, and the
/// single-instance name that goes with that place. Two runs share an instance
/// exactly when they share a data folder, so a second launch on the same data
/// focuses the first window, and a run on other data starts beside it.
/// </summary>
/// <remarks>
/// <c>RAMBUTAN_DATA_DIR</c> moves the data folder, for running several copies at
/// once (one per worktree, say) without them reading each other's settings. The
/// sidecars stay shared under the default folder: they are large, and every copy
/// runs the same ones. <c>RAMBUTAN_TOOLS_DIR</c> moves them too, so a run that
/// pins another ffmpeg can't replace the installed app's.
/// </remarks>
public sealed record AppDataLocation(string DataDir, string ToolsDir, string InstanceName)
{
    public const string OverrideVariable = "RAMBUTAN_DATA_DIR";
    public const string ToolsOverrideVariable = "RAMBUTAN_TOOLS_DIR";

    /// <summary>The installed app's instance: the default data folder.</summary>
    public const string DefaultInstanceName = "Rambutan";

    public static AppDataLocation FromEnvironment(bool demo) =>
        Resolve(demo, Environment.GetEnvironmentVariable(OverrideVariable),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetEnvironmentVariable(ToolsOverrideVariable));

    public static AppDataLocation Resolve(bool demo, string? overrideDir, string localAppData, string? toolsOverride = null)
    {
        var baseDir = Path.Combine(localAppData, "Rambutan");
        var toolsDir = string.IsNullOrWhiteSpace(toolsOverride)
            ? Path.Combine(baseDir, "tools")
            : Path.GetFullPath(toolsOverride);

        if (!string.IsNullOrWhiteSpace(overrideDir))
        {
            var dataDir = Path.GetFullPath(overrideDir);
            return new AppDataLocation(dataDir, toolsDir, $"Rambutan-{ShortHash(dataDir)}");
        }

        // Demo mode keeps its own settings, cookies and caches, and is its own
        // instance, so it can run beside the real app.
        return demo
            ? new AppDataLocation(Path.Combine(baseDir, "demo"), toolsDir, "Rambutan-demo")
            : new AppDataLocation(baseDir, toolsDir, DefaultInstanceName);
    }

    /// <summary>
    /// The ports the phone remote and the OBS overlay try first. The
    /// installed app's folder keeps the documented 41741 and 41742; any other
    /// folder (a profile, the demo, <c>RAMBUTAN_DATA_DIR</c>) gets a pair of its
    /// own from its instance name, so its links stay the same from run to run and
    /// a paired phone or an OBS source doesn't reach another instance. A clash
    /// with something else still falls back to any free port.
    /// </summary>
    public (int Remote, int Overlay) PreferredPorts => PortsFor(InstanceName);

    internal const int FirstDerivedPort = 41750, DerivedPairs = 100;

    internal static (int Remote, int Overlay) PortsFor(string instanceName)
    {
        if (instanceName == DefaultInstanceName)
            return (LocalApi.LocalApiHost.DefaultPort, LocalApi.Overlay.OverlayOptions.DefaultPort);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(instanceName));
        var remote = FirstDerivedPort + 2 * (int)(BitConverter.ToUInt32(hash) % DerivedPairs);
        return (remote, remote + 1);
    }

    /// <summary>The same root's sidecars, with another profile's folder
    /// and the single-instance name for that folder.</summary>
    public AppDataLocation ForProfileFolder(string profileDir)
    {
        var dataDir = Path.GetFullPath(profileDir);
        return this with { DataDir = dataDir, InstanceName = $"Rambutan-{ShortHash(dataDir)}" };
    }

    private static string ShortHash(string path)
    {
        // Windows paths are case-insensitive: E:\x and e:\X are the same folder.
        var key = Path.TrimEndingDirectorySeparator(path);
        if (OperatingSystem.IsWindows())
            key = key.ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..12];
    }
}
