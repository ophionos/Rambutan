using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Rambutan.Core.Diagnostics;

namespace Rambutan.Streams;

/// <summary>
/// Manages the shared ffmpeg libraries used for decode (via FFmpeg.AutoGen in
/// Rambutan.Audio), and the ffmpeg executable that export runs. Windows and Linux download the
/// BtbN LGPL shared build pinned to the binding's major version (distro packages
/// are typically several majors behind). macOS: system libraries for now.
/// </summary>
public sealed class FfmpegManager(string toolsDirectory, HttpClient? http = null)
{
    // Pinned to n9.0 — must match the FFmpeg.AutoGen binding major in Rambutan.Audio.
    private const string BuildVersion = "9.0";
    private const string WindowsAssetUrl =
        $"https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-n{BuildVersion}-latest-win64-lgpl-shared-{BuildVersion}.zip";
    private const string LinuxAssetUrl =
        $"https://github.com/BtbN/FFmpeg-Builds/releases/latest/download/ffmpeg-n{BuildVersion}-latest-linux64-lgpl-shared-{BuildVersion}.tar.xz";

    // The newest releases, dated ones included. BtbN publishes each build as a
    // dated release first, then deletes and re-creates 'latest'; for those minutes
    // the URLs above answer 404.
    internal const string ReleasesApiUrl = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases?per_page=5";

    /// <summary>
    /// Everything FfmpegDecoder touches, with the library major the n9.0 build ships
    /// and the binding loads; avdevice/avfilter/swscale ship too but are unused. The
    /// majors make the presence check version-aware: a folder from an older pin has
    /// other file names, fails the check, and is replaced. App.Tests checks these
    /// against the binding's constants.
    /// </summary>
    public static IReadOnlyList<(string Name, int Major)> RequiredLibraries { get; } =
        [("avcodec", 63), ("avformat", 63), ("avutil", 61), ("swresample", 7)];

    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string LibrariesDirectory { get; } = Path.Combine(toolsDirectory, "ffmpeg");

    /// <summary>
    /// Ensures ffmpeg shared libraries are available. Returns the directory to load
    /// them from, or null to use the platform's default library search path.
    /// </summary>
    public async Task<string?> EnsureAsync(CancellationToken ct = default)
    {
        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        if (!isWindows && !RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            return null; // macOS: system libraries; revisit when bundling for release.

        if (IsPresent(LibrariesDirectory, isWindows))
            return LibrariesDirectory;

        await _gate.WaitAsync(ct);
        try
        {
            if (!IsPresent(LibrariesDirectory, isWindows))
                await InstallAsync(isWindows, ct);
            return LibrariesDirectory;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The ffmpeg executable for export: the sidecar's own, or the one on
    /// the PATH where the platform's libraries are used (macOS).</summary>
    public async Task<string> EnsureExecutableAsync(CancellationToken ct = default) =>
        await EnsureAsync(ct) is { } directory
            ? Path.Combine(directory, ExecutableName(OperatingSystem.IsWindows()))
            : "ffmpeg";

    /// <summary>What the sidecar's ffmpeg needs to start: on Linux it sits beside
    /// its shared libraries rather than in the archive's bin/ next to lib/.</summary>
    public static IReadOnlyDictionary<string, string>? ProcessEnvironment(string executable) =>
        OperatingSystem.IsLinux() && Path.GetDirectoryName(executable) is { Length: > 0 } directory
            ? new Dictionary<string, string> { ["LD_LIBRARY_PATH"] = directory }
            : null;

    internal static string ExecutableName(bool isWindows) => isWindows ? "ffmpeg.exe" : "ffmpeg";

    /// <summary>The libraries and the executable. An install from before export
    /// (libraries only, on Linux) fails it and is replaced once.</summary>
    internal static bool IsPresent(string directory, bool isWindows) =>
        Directory.Exists(directory) &&
        File.Exists(Path.Combine(directory, ExecutableName(isWindows))) &&
        RequiredLibraries.All(lib => File.Exists(
            Path.Combine(directory, LibraryFileName(lib.Name, lib.Major, isWindows))));

    /// <summary>The file name the binding loads: <c>avcodec-63.dll</c>, <c>libavcodec.so.63</c>.</summary>
    internal static string LibraryFileName(string name, int major, bool isWindows) =>
        isWindows ? $"{name}-{major}.dll" : $"lib{name}.so.{major}";

    /// <summary>
    /// Downloads and extracts into a staging directory, then moves it into place in
    /// one rename — an interrupted install can never leave a half-populated
    /// directory that passes the presence check on the next launch.
    /// </summary>
    private async Task InstallAsync(bool isWindows, CancellationToken ct)
    {
        Directory.CreateDirectory(toolsDirectory);
        var archivePath = Path.Combine(
            toolsDirectory, isWindows ? "ffmpeg.download.zip" : "ffmpeg.download.tar.xz");
        var stagingDir = Path.Combine(toolsDirectory, "ffmpeg.extract");

        try
        {
            await DownloadAsync(isWindows, archivePath, ct);

            if (Directory.Exists(stagingDir))
                Directory.Delete(stagingDir, recursive: true);
            Directory.CreateDirectory(stagingDir);

            if (isWindows)
                ExtractWindows(archivePath, stagingDir);
            else
                await ExtractLinuxAsync(archivePath, stagingDir, ct);

            // A directory that exists but failed IsPresent is a broken partial
            // install from an interrupted extraction, or a build from an older
            // pin — replace it wholesale.
            if (Directory.Exists(LibrariesDirectory))
                Directory.Delete(LibrariesDirectory, recursive: true);
            Directory.Move(stagingDir, LibrariesDirectory);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDir);
        }
    }

    private static void ExtractWindows(string zipPath, string targetDir)
    {
        // Archive layout: ffmpeg-n{BuildVersion}-.../bin/*.dll — flatten bin/ into the target.
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            var segments = entry.FullName.Split('/');
            if (segments.Length >= 2 && segments[^2] == "bin" && entry.Name.Length > 0)
                entry.ExtractToFile(Path.Combine(targetDir, entry.Name), overwrite: true);
        }
    }

    private static async Task ExtractLinuxAsync(string tarPath, string targetDir, CancellationToken ct)
    {
        // Archive layout: ffmpeg-n{BuildVersion}-.../lib/*.so* and .../bin/ffmpeg — flatten
        // both via the system tar (.NET has no xz decoder; tar is universally present on Linux).
        var result = await ProcessRunner.RunAsync("tar",
        [
            "-xJf", tarPath,
            "-C", targetDir,
            "--strip-components=2",
            "--wildcards", "*/lib/*", "*/bin/ffmpeg",
        ], ct);

        if (result.ExitCode != 0)
            throw new InvalidOperationException($"ffmpeg extraction failed: {result.StdErr}");
    }

    private async Task DownloadAsync(bool isWindows, string path, CancellationToken ct)
    {
        // HttpClient.Timeout covers headers only for streamed bodies; the linked
        // timeout keeps a stalled connection from hanging first-run setup forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);

        var url = isWindows ? WindowsAssetUrl : LinuxAssetUrl;
        try
        {
            try
            {
                await CopyToFileAsync(url, path, timeout.Token);
            }
            catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                var dated = await FindDatedAssetAsync(isWindows, timeout.Token)
                    ?? throw new InvalidOperationException(
                        $"ffmpeg build not found at '{url}', and none of BtbN's newest releases " +
                        $"has an n{BuildVersion} {AssetSuffix(isWindows)} asset either. If that lasts " +
                        $"past a day, BtbN has dropped the n{BuildVersion} builds: update the pin (and " +
                        "the FFmpeg.AutoGen binding in Rambutan.Audio) to the current major.", e);
                AppLog.Write("sidecar", $"ffmpeg 'latest' answered 404 (BtbN re-publishing?); using {dated}");
                await CopyToFileAsync(dated, path, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ffmpeg download timed out after {DownloadTimeout.TotalMinutes:0} minutes.");
        }
    }

    private async Task CopyToFileAsync(string url, string path, CancellationToken ct)
    {
        await using var source = await _http.GetStreamAsync(url, ct);
        await using var target = File.Create(path);
        await source.CopyToAsync(target, ct);
    }

    /// <summary>The newest fully uploaded build of the pin, from the release list;
    /// null when the list can't be read or holds none.</summary>
    private async Task<string?> FindDatedAssetAsync(bool isWindows, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApiUrl);
            request.Headers.UserAgent.ParseAdd("Rambutan"); // the API refuses requests without one
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            await using var body = await response.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
            return PickAsset(doc.RootElement, isWindows);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            AppLog.Write("sidecar", $"ffmpeg release list unavailable: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// From GitHub's release list (newest first), the download URL of the first
    /// uploaded asset of the pinned build for this platform. A 'latest' being
    /// re-created has no assets or half of them, so it's passed over until its
    /// file is up; the suffix keeps arm64, GPL and static builds out.
    /// </summary>
    internal static string? PickAsset(JsonElement releases, bool isWindows)
    {
        if (releases.ValueKind != JsonValueKind.Array)
            return null;
        var suffix = AssetSuffix(isWindows);
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True)
                continue;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var asset in assets.EnumerateArray())
            {
                var name = Text(asset, "name");
                if (name is null
                    || !(name.StartsWith($"ffmpeg-n{BuildVersion}-", StringComparison.Ordinal)
                         || name.StartsWith($"ffmpeg-n{BuildVersion}.", StringComparison.Ordinal))
                    || !name.EndsWith(suffix, StringComparison.Ordinal)
                    || Text(asset, "state") != "uploaded"
                    || Text(asset, "browser_download_url") is not { } url
                    || !url.StartsWith("https://github.com/BtbN/FFmpeg-Builds/", StringComparison.Ordinal))
                {
                    continue;
                }

                return url;
            }
        }

        return null;
    }

    private static string AssetSuffix(bool isWindows) => isWindows
        ? $"-win64-lgpl-shared-{BuildVersion}.zip"
        : $"-linux64-lgpl-shared-{BuildVersion}.tar.xz";

    private static string? Text(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
