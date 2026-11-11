using System.Net;

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

        Assert.Equal(FfmpegManager.Asset(OperatingSystem.IsWindows()).Url, Assert.Single(handler.Requests).ToString());
    }

    [Theory]
    [InlineData(true, ".zip")]
    [InlineData(false, ".tar.xz")]
    public void The_pin_is_one_dated_build_with_a_hash_never_latest(bool isWindows, string extension)
    {
        var (url, sha256) = FfmpegManager.Asset(isWindows);

        Assert.StartsWith(
            $"https://github.com/FFmpeg-Builds/FFmpeg-Builds/releases/download/{FfmpegManager.Release}/ffmpeg-n9.0.", url);
        Assert.StartsWith("autobuild-", FfmpegManager.Release);
        Assert.DoesNotContain("latest", url);
        Assert.EndsWith($"-lgpl-shared-9.0{extension}", url);
        Assert.Matches("^[0-9a-f]{64}$", sha256);
    }

    [Fact]
    public async Task A_download_that_is_not_the_pinned_file_is_deleted_and_never_extracted()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            return; // macOS uses the system libraries.
        // A real zip with the libraries' names: without the check it would install.
        var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(ArchiveWithLibraries()),
        });

        var error = await Assert.ThrowsAsync<SidecarIntegrityException>(
            () => new FfmpegManager(_toolsDir, new HttpClient(handler)).EnsureAsync());

        Assert.Contains(FfmpegManager.Asset(OperatingSystem.IsWindows()).Sha256, error.Message);
        Assert.False(Directory.Exists(LibrariesDir));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_toolsDir));
    }

    private static byte[] ArchiveWithLibraries()
    {
        using var bytes = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(bytes, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, major) in FfmpegManager.RequiredLibraries)
                zip.CreateEntry("ffmpeg/bin/" + FfmpegManager.LibraryFileName(name, major, isWindows: true));
            zip.CreateEntry("ffmpeg/bin/ffmpeg.exe");
        }
        return bytes.ToArray();
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
