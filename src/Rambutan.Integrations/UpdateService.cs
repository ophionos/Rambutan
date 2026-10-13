using System.Reflection;
using Velopack;
using Velopack.Sources;

namespace Rambutan.Integrations;

/// <summary>
/// Velopack update flow: check and download in the background, stage the
/// update to install silently on exit, and offer restart-now. Inert in dev runs
/// (unpackaged) or when no feed is configured. A feed that names a GitHub
/// repository reads its published releases, prereleases included; the channel a
/// build was packed with (stable or beta, per OS) keeps them apart.
/// </summary>
public sealed class UpdateService
{
    private readonly UpdateManager? _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UpdateInfo? _staged;

    /// <summary>Where releases are published: the feed when settings name none.</summary>
    public const string ReleasesFeed = "https://github.com/ophionos/Rambutan";

    public UpdateService(string? feedUrlOrPath)
    {
        if (string.IsNullOrWhiteSpace(feedUrlOrPath))
            return;
        _manager = IsGitHubRepository(feedUrlOrPath)
            ? new UpdateManager(new GithubSource(feedUrlOrPath, accessToken: null, prerelease: true))
            : new UpdateManager(feedUrlOrPath);
    }

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
        var app = VelopackApp.Build().SetArgs(args);
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

    public bool IsPackaged => _manager?.IsInstalled ?? false;

    /// <summary>Installed version, falling back to the assembly version in dev runs.</summary>
    public string CurrentVersion =>
        _manager?.CurrentVersion?.ToString()
        ?? Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "unknown";

    public string? StagedVersion => _staged?.TargetFullRelease.Version.ToString();

    public event Action? StagedChanged;

    /// <summary>
    /// Checks the feed and, when an update exists, downloads it and schedules a
    /// silent install for after the app exits. Returns the staged version, null
    /// when up to date (or not packaged).
    /// </summary>
    public async Task<string?> CheckAndStageAsync(CancellationToken ct = default)
    {
        if (_manager is not { IsInstalled: true } manager)
            return null;

        // Startup check and a Settings "check now" can overlap — never
        // download or schedule the same update twice.
        await _gate.WaitAsync(ct);
        try
        {
            if (_staged is null)
            {
                var info = await manager.CheckForUpdatesAsync();
                if (info is null)
                    return null;

                await manager.DownloadUpdatesAsync(info, cancelToken: ct);
                manager.WaitExitThenApplyUpdates(info, silent: true, restart: false);
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

    /// <summary>Installs the staged update and starts the app again, with
    /// <paramref name="args"/> (a profile switch passes <c>--profile</c>).</summary>
    public void RestartNow(string[]? args = null)
    {
        if (_staged is { } staged)
            _manager!.ApplyUpdatesAndRestart(staged, args);
    }
}
