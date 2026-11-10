using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;

namespace Rambutan.Streams;

/// <summary>
/// Manages the shared ffmpeg libraries used for decode (via FFmpeg.AutoGen in
/// Rambutan.Audio), and the ffmpeg executable that export runs. Windows and Linux download one
/// dated BtbN LGPL shared build of the binding's major version (distro packages
/// are typically several majors behind) and check its SHA-256, since the
/// libraries load into this process. macOS: system libraries for now.
/// </summary>
public sealed class FfmpegManager(string toolsDirectory, HttpClient? http = null)
{
    // Pinned to n9.0 — must match the FFmpeg.AutoGen binding major in Rambutan.Audio.
    private const string BuildVersion = "9.0";

    // One dated build, never 'latest': 'latest' is replaced each day, so no hash
    // in source could name it. BtbN keeps the last build of each month for two
    // years and the daily ones for two weeks, so pin a month's last build.
    // Change the release, the build and both hashes together; the hashes are
    // sha256sum of the two assets, which the release's checksums.sha256 lists.
    internal const string Release = "autobuild-2026-10-31-12-59";
    private const string Build = "n9.0.2-39-g7adfbe40dc";
    private const string AssetBase =
        $"https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/{Release}/ffmpeg-{Build}-";

    /// <summary>The pinned build's archive for a platform, and its SHA-256.</summary>
    internal static (string Url, string Sha256) Asset(bool isWindows) => isWindows
        ? ($"{AssetBase}win64-lgpl-shared-{BuildVersion}.zip",
            "e907b01fa4783913151237e37df2a10fa97d99a1598a021bf59d4ae62ffed4c0")
        : ($"{AssetBase}linux64-lgpl-shared-{BuildVersion}.tar.xz",
            "5a58e2dffad4835ee3418939f2676d6898489f8e047299f5f62ee0da3f8dd5dc");

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

        var (url, expected) = Asset(isWindows);
        try
        {
            var actual = await VerifiedDownload.ToFileAsync(_http, url, path, timeout.Token);
            VerifiedDownload.Require("ffmpeg", expected, actual, path);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException(
                $"ffmpeg build not found at '{url}'. BtbN has removed the pinned release: " +
                "update the pin and its hashes to a newer month's last build.", e);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"ffmpeg download timed out after {DownloadTimeout.TotalMinutes:0} minutes.");
        }
    }

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
