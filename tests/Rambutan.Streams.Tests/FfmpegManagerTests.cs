using System.Net;
using System.Text.Json;

namespace Rambutan.Streams.Tests;

public sealed class FfmpegManagerTests : IDisposable
{
    private readonly string _toolsDir = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    private string LibrariesDir => Path.Combine(_toolsDir, "ffmpeg");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_toolsDir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private void WriteLibraries(bool isWindows, Func<string, int, int> major)
    {
        Directory.CreateDirectory(LibrariesDir);
        foreach (var (name, pinned) in FfmpegManager.RequiredLibraries)
        {
            var file = FfmpegManager.LibraryFileName(name, major(name, pinned), isWindows);
            File.WriteAllBytes(Path.Combine(LibrariesDir, file), []);
        }
        File.WriteAllBytes(Path.Combine(LibrariesDir, FfmpegManager.ExecutableName(isWindows)), []);
    }

    // The n8.1 build: avcodec-62, avformat-62, avutil-60, swresample-6.
    private static int OlderMajor(string name, int pinned) => pinned - 1;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Current_build_is_present(bool isWindows)
    {
        WriteLibraries(isWindows, (_, pinned) => pinned);

        Assert.True(FfmpegManager.IsPresent(LibrariesDir, isWindows));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Build_from_an_older_pin_is_not_present(bool isWindows)
    {
        // The bug: the check matched avcodec-*.dll, so an n8.1 folder passed, nothing
        // was downloaded, and the n9.0 binding failed to load its libraries.
        WriteLibraries(isWindows, OlderMajor);

        Assert.False(FfmpegManager.IsPresent(LibrariesDir, isWindows));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void One_library_on_an_older_major_is_not_present(bool isWindows)
    {
        WriteLibraries(isWindows, (name, pinned) => name == "swresample" ? pinned - 1 : pinned);

        Assert.False(FfmpegManager.IsPresent(LibrariesDir, isWindows));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Libraries_without_the_executable_are_not_present(bool isWindows)
    {
        // A Linux install from before export kept lib/ only; export needs bin/ffmpeg.
        WriteLibraries(isWindows, (_, pinned) => pinned);
        File.Delete(Path.Combine(LibrariesDir, FfmpegManager.ExecutableName(isWindows)));

        Assert.False(FfmpegManager.IsPresent(LibrariesDir, isWindows));
    }

    [Fact]
    public void Missing_folder_is_not_present()
    {
        Assert.False(FfmpegManager.IsPresent(LibrariesDir, isWindows: true));
        Assert.False(FfmpegManager.IsPresent(LibrariesDir, isWindows: false));
    }

    [Fact]
    public async Task Current_build_is_used_without_a_download()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return; // macOS uses the system libraries.
        WriteLibraries(OperatingSystem.IsWindows(), (_, pinned) => pinned);
        var handler = new RecordingHandler();

        var dir = await new FfmpegManager(_toolsDir, new HttpClient(handler)).EnsureAsync();

        Assert.Equal(LibrariesDir, dir);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Build_from_an_older_pin_starts_a_download_of_the_pinned_build()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return; // macOS uses the system libraries.
        WriteLibraries(OperatingSystem.IsWindows(), OlderMajor);
        var handler = new RecordingHandler(); // answers 404 everywhere, which stops the install

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new FfmpegManager(_toolsDir, new HttpClient(handler)).EnsureAsync());

        Assert.Contains("ffmpeg-n9.0-latest-", handler.Requests[0].ToString());
    }

    // Cut from GitHub's release list during BtbN's re-publish window: 'latest'
    // re-created with half its files, newest dated release complete.
    private const string ReleasesDuringRepublish = """
        [
          {"tag_name": "latest", "draft": false, "assets": [
            {"name": "ffmpeg-n9.0-latest-linuxarm64-lgpl-shared-9.0.tar.xz", "state": "uploaded",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-linuxarm64-lgpl-shared-9.0.tar.xz"},
            {"name": "ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip", "state": "starter",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n9.0-latest-win64-lgpl-shared-9.0.zip"}
          ]},
          {"tag_name": "autobuild-2026-10-03-18-14", "draft": false, "assets": [
            {"name": "ffmpeg-n9.0.2-22-g46d8f462ee-winarm64-lgpl-shared-9.0.zip", "state": "uploaded",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-22-g46d8f462ee-winarm64-lgpl-shared-9.0.zip"},
            {"name": "ffmpeg-n9.0.2-22-g46d8f462ee-win64-gpl-shared-9.0.zip", "state": "uploaded",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-22-g46d8f462ee-win64-gpl-shared-9.0.zip"},
            {"name": "ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-shared-9.0.zip", "state": "uploaded",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-shared-9.0.zip"},
            {"name": "ffmpeg-n9.0.2-22-g46d8f462ee-linux64-lgpl-shared-9.0.tar.xz", "state": "uploaded",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-22-g46d8f462ee-linux64-lgpl-shared-9.0.tar.xz"}
          ]},
          {"tag_name": "autobuild-2026-10-01-13-06", "draft": false, "assets": [
            {"name": "ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-shared-9.0.zip", "state": "uploaded",
             "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-01-13-06/ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-shared-9.0.zip"}
          ]}
        ]
        """;

    [Theory]
    [InlineData(true, "autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-22-g46d8f462ee-win64-lgpl-shared-9.0.zip")]
    [InlineData(false, "autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-22-g46d8f462ee-linux64-lgpl-shared-9.0.tar.xz")]
    public void PickAsset_SkipsAHalfUploadedLatest_AndTakesTheNewestDatedBuildForThePlatform(bool isWindows, string expected)
    {
        using var doc = JsonDocument.Parse(ReleasesDuringRepublish);

        Assert.Equal(
            "https://github.com/BtbN/FFmpeg-Builds/releases/download/" + expected,
            FfmpegManager.PickAsset(doc.RootElement, isWindows));
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"message": "API rate limit exceeded"}""")]
    [InlineData("""[{"tag_name": "latest", "assets": []}]""")]
    [InlineData("""[{"tag_name": "x", "assets": [{"name": "ffmpeg-n10.0-latest-win64-lgpl-shared-10.0.zip", "state": "uploaded", "browser_download_url": "https://github.com/BtbN/FFmpeg-Builds/releases/download/x/ffmpeg-n10.0-latest-win64-lgpl-shared-10.0.zip"}]}]""")]
    public void PickAsset_FindsNothing_WhenNoReleaseHasThePinnedBuild(string json)
    {
        using var doc = JsonDocument.Parse(json);

        Assert.Null(FfmpegManager.PickAsset(doc.RootElement, isWindows: true));
    }

    [Fact]
    public async Task A_404_on_latest_falls_back_to_the_newest_dated_build()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return; // macOS uses the system libraries.
        var handler = new RecordingHandler(request => request.RequestUri!.ToString() switch
        {
            var u when u.Contains("/releases/latest/download/") => new HttpResponseMessage(HttpStatusCode.NotFound),
            FfmpegManager.ReleasesApiUrl when request.Headers.UserAgent.Count > 0 =>
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(ReleasesDuringRepublish) },
            var u when u.Contains("/autobuild-") => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent([1, 2, 3]), // not an archive: extraction fails after the fetch
            },
            _ => new HttpResponseMessage(HttpStatusCode.Forbidden),
        });

        await Assert.ThrowsAnyAsync<Exception>(
            () => new FfmpegManager(_toolsDir, new HttpClient(handler)).EnsureAsync());

        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(FfmpegManager.ReleasesApiUrl, handler.Requests[1].ToString());
        Assert.Contains("/autobuild-2026-10-03-18-14/ffmpeg-n9.0.2-", handler.Requests[2].ToString());
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage>? respond = null)
        : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
