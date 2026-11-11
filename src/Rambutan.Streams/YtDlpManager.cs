using System.Runtime.InteropServices;

namespace Rambutan.Streams;

/// <summary>
/// Manages the yt-dlp sidecar binary: download on first use, self-update via yt-dlp's
/// own release channel (keeping it current IS the countermeasure strategy).
/// </summary>
public sealed class YtDlpManager(string toolsDirectory, HttpClient? http = null)
{
    private const string ReleaseBase = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/";
    internal const string ChecksumsName = "SHA2-256SUMS";
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    private readonly HttpClient _http = http ?? new HttpClient();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public string BinaryPath { get; } = Path.Combine(toolsDirectory, BinaryName());

    private static string BinaryName() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "yt-dlp.exe"
        : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "yt-dlp_macos"
        : "yt-dlp";

    public async Task<string> EnsureAsync(CancellationToken ct = default)
    {
        if (File.Exists(BinaryPath))
            return BinaryPath;

        await _gate.WaitAsync(ct);
        try
        {
            if (!File.Exists(BinaryPath))
                await DownloadAsync(ct);
            return BinaryPath;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string> UpdateAsync(CancellationToken ct = default)
    {
        var binary = await EnsureAsync(ct);
        var result = await ProcessRunner.RunAsync(binary, ["-U"], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp -U failed: {result.StdErr.Trim()}");
        return result.StdOut.Trim();
    }

    public async Task<string> GetVersionAsync(CancellationToken ct = default)
    {
        var binary = await EnsureAsync(ct);
        var result = await ProcessRunner.RunAsync(binary, ["--version"], ct);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"yt-dlp --version failed: {result.StdErr}");
        return result.StdOut.Trim();
    }

    private async Task DownloadAsync(CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BinaryPath)!);

        var url = ReleaseBase + BinaryName();
        var temp = BinaryPath + ".download";

        // HttpClient.Timeout covers headers only for streamed bodies; the linked
        // timeout keeps a stalled connection from hanging first-run setup forever.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DownloadTimeout);

        try
        {
            // The release's own list, from the same host: it catches a damaged
            // download and a swapped file, not a release that is itself bad.
            // yt-dlp checks its later updates against the same list by itself.
            var checksums = await _http.GetStringAsync(ReleaseBase + ChecksumsName, timeout.Token);
            var expected = VerifiedDownload.HashFor(checksums, BinaryName())
                ?? throw new InvalidOperationException(
                    $"yt-dlp was not installed: its release's {ChecksumsName} has no line for {BinaryName()}.");
            var actual = await VerifiedDownload.ToFileAsync(_http, url, temp, timeout.Token);
            VerifiedDownload.Require("yt-dlp", expected, actual, temp);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"yt-dlp download timed out after {DownloadTimeout.TotalMinutes:0} minutes.");
        }

        File.Move(temp, BinaryPath, overwrite: true);

        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            File.SetUnixFileMode(BinaryPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
    }
}
