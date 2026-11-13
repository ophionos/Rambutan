using System.Diagnostics;
using System.Reflection;
using Rambutan.Core.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace Rambutan.Integrations;

/// <summary>
/// Velopack update flow: check and download in the background, install the
/// downloaded update silently once the app has quit, and offer restart-now.
/// Inert in dev runs (unpackaged) or when no feed is configured. A feed that
/// names a GitHub repository reads its published releases, prereleases
/// included; the channel a build was packed with (stable or beta, per OS) keeps
/// them apart.
/// </summary>
/// <remarks>
/// Nothing starts Velopack's updater while the app runs: <c>Update.exe apply
/// --waitPid</c> waits only 60 s for the process, then kills it and installs,
/// which ended running sessions mid-playback. So the updater starts from
/// <see cref="ApplyOnExit"/> or <see cref="RestartNow"/>, after everything is
/// saved, and at most once per process. Velopack's own apply on startup is
/// off for the same reason: it runs before the single-instance check, so a
/// second launch (a shortcut, a rambutan:// link, an opened file) would kill
/// the running app to install. A download left by a killed run is found again
/// by the next check and installs when that run quits.
/// </remarks>
public sealed class UpdateService
{
    private readonly IUpdater? _updater;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _staged;
    private int _applied;

    /// <summary>Where releases are published: the feed when settings name none.</summary>
    public const string ReleasesFeed = "https://github.com/ophionos/Rambutan";

    public UpdateService(string? feedUrlOrPath)
    {
        if (string.IsNullOrWhiteSpace(feedUrlOrPath))
            return;
        _updater = new VelopackUpdater(IsGitHubRepository(feedUrlOrPath)
            ? new UpdateManager(new GithubSource(feedUrlOrPath, accessToken: null, prerelease: true))
            : new UpdateManager(feedUrlOrPath));
    }

    private UpdateService(IUpdater updater) => _updater = updater;

    internal static UpdateService Over(IUpdater updater) => new(updater);

    internal static bool IsGitHubRepository(string feed) =>
        Uri.TryCreate(feed, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Velopack's install/update/uninstall hooks — must be the first call in
    /// Main (it exits the process when invoked by the installer). The hooks
    /// register the rambutan:// scheme on Windows (<see cref="DeepLinkRegistration"/>);
    /// they run only in an installed build, never in a dev or portable run.
    /// </summary>
    public static void Startup(string[] args)
    {
        // No apply on startup: see the class remarks.
        var app = VelopackApp.Build().SetArgs(args).SetAutoApplyOnStartup(false);
        // Velopack runs these hooks on Windows only.
        if (OperatingSystem.IsWindows())
        {
            app = app
                .OnAfterInstallFastCallback(_ => DeepLinkRegistration.RegisterInstalled())
                .OnAfterUpdateFastCallback(_ => DeepLinkRegistration.RegisterInstalled())
                .OnBeforeUninstallFastCallback(_ => DeepLinkRegistration.UnregisterInstalled());
        }
        app.Run();
    }

    public bool IsPackaged => _updater?.IsInstalled ?? false;

    /// <summary>Installed version, falling back to the assembly version in dev runs.</summary>
    public string CurrentVersion =>
        _updater?.CurrentVersion
        ?? Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public string? StagedVersion => _staged?.TargetFullRelease.Version.ToString();

    /// <summary>Raised on the thread that finished the download.</summary>
    public event Action? StagedChanged;

    /// <summary>
    /// Checks the feed and, when an update exists, downloads it to install once
    /// the app quits. Returns the staged version, null when up to date (or not
    /// packaged).
    /// </summary>
    public async Task<string?> CheckAndStageAsync(CancellationToken ct = default)
    {
        if (_updater is not { IsInstalled: true } updater)
            return null;

        // Startup check and a Settings "check now" can overlap — never
        // download the same update twice.
        await _gate.WaitAsync(ct);
        try
        {
            if (_staged is null)
            {
                var info = await updater.CheckForUpdatesAsync();
                if (info is null)
                    return null;

                await updater.DownloadUpdatesAsync(info, ct);
                _staged = info;
                StagedChanged?.Invoke();
            }

            return StagedVersion;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Starts the updater to install the staged update once this process has
    /// exited, without starting the app again. Call it last, after the app has
    /// saved everything: the updater kills the process 60 s later. Does nothing
    /// when nothing is staged or an install was already started, and while
    /// another instance runs from the same executable (another profile or
    /// data folder): the updater would kill it too. The last one to quit installs.
    /// </summary>
    public void ApplyOnExit()
    {
        if (_staged is not { } staged)
            return;
        if (_updater!.OtherInstancesRunning())
        {
            AppLog.Write("update", $"{StagedVersion} not installed on exit: another instance is running");
            return;
        }
        if (Claim())
            _updater.WaitExitThenApply(staged.TargetFullRelease);
    }

    /// <summary>Installs the staged update and starts the app again, with
    /// <paramref name="args"/> (a profile switch passes <c>--profile</c>).
    /// Ends the process at once, so call it only after the app has saved
    /// everything. Does nothing when nothing is staged or an install was
    /// already started.</summary>
    public void RestartNow(string[]? args = null)
    {
        if (_staged is { } staged && Claim())
            _updater!.ApplyAndRestart(staged.TargetFullRelease, args);
    }

    // Two updaters for one process kill each other's install.
    private bool Claim() => Interlocked.Exchange(ref _applied, 1) == 0;
}

/// <summary>The part of Velopack's <see cref="UpdateManager"/> the service uses;
/// its apply calls aren't virtual, so tests replace the whole of it.</summary>
internal interface IUpdater
{
    bool IsInstalled { get; }
    string? CurrentVersion { get; }
    Task<UpdateInfo?> CheckForUpdatesAsync();
    Task DownloadUpdatesAsync(UpdateInfo info, CancellationToken ct);
    void WaitExitThenApply(VelopackAsset target);
    void ApplyAndRestart(VelopackAsset target, string[]? args);

    /// <summary>Whether another process runs from this process's executable.</summary>
    bool OtherInstancesRunning();
}

internal sealed class VelopackUpdater(UpdateManager manager) : IUpdater
{
    public bool IsInstalled => manager.IsInstalled;
    public string? CurrentVersion => manager.CurrentVersion?.ToString();
    public Task<UpdateInfo?> CheckForUpdatesAsync() => manager.CheckForUpdatesAsync();
    public Task DownloadUpdatesAsync(UpdateInfo info, CancellationToken ct) =>
        manager.DownloadUpdatesAsync(info, cancelToken: ct);
    public void WaitExitThenApply(VelopackAsset target) =>
        manager.WaitExitThenApplyUpdates(target, silent: true, restart: false);
    public void ApplyAndRestart(VelopackAsset target, string[]? args) =>
        manager.ApplyUpdatesAndRestart(target, args);

    public bool OtherInstancesRunning()
    {
        if (Environment.ProcessPath is not { } path)
            return false;
        using var self = Process.GetCurrentProcess();
        foreach (var process in Process.GetProcessesByName(self.ProcessName))
        {
            using (process)
            {
                if (process.Id == self.Id)
                    continue;
                try
                {
                    if (string.Equals(process.MainModule?.FileName, path, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // Exited meanwhile, or not ours to read.
                }
            }
        }
        return false;
    }
}
