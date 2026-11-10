using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Rambutan.Streams.Tests;

/// <summary>The first download of yt-dlp, checked against its release's list of
/// hashes before the file gets its name and can run.</summary>
public sealed class YtDlpManagerTests : IDisposable
{
    private readonly string _toolsDir = Path.Combine(
        Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

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

    private static readonly byte[] Binary = Encoding.ASCII.GetBytes("not really yt-dlp");

    private static string Sha256(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private sealed class Release(string checksums, byte[] binary) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = url.EndsWith("/" + YtDlpManager.ChecksumsName, StringComparison.Ordinal)
                    ? new StringContent(checksums)
                    : new ByteArrayContent(binary),
            });
        }
    }

    private string Name => Path.GetFileName(new YtDlpManager(_toolsDir).BinaryPath);

    [Fact]
    public async Task A_download_that_matches_the_releases_list_is_installed()
    {
        var handler = new Release($"{Sha256([1])}  other-file\n{Sha256(Binary)}  {Name}\n", Binary);
        var manager = new YtDlpManager(_toolsDir, new HttpClient(handler));

        var path = await manager.EnsureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(Binary, File.ReadAllBytes(path));
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, url =>
            Assert.StartsWith("https://github.com/yt-dlp/yt-dlp/releases/latest/download/", url));
    }

    [Fact]
    public async Task A_download_that_differs_from_the_list_is_deleted_and_not_installed()
    {
        var handler = new Release($"{Sha256([1, 2, 3])}  {Name}\n", Binary);
        var manager = new YtDlpManager(_toolsDir, new HttpClient(handler));

        await Assert.ThrowsAsync<SidecarIntegrityException>(
            () => manager.EnsureAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(manager.BinaryPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_toolsDir));
    }

    [Fact]
    public async Task A_list_without_the_file_installs_nothing_and_downloads_nothing()
    {
        // Another file's line must not count: "yt-dlp" is the start of every name.
        var handler = new Release($"{Sha256(Binary)}  {Name}.zip\n{Sha256(Binary)}  x{Name}\n", Binary);
        var manager = new YtDlpManager(_toolsDir, new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => manager.EnsureAsync(TestContext.Current.CancellationToken));

        Assert.False(File.Exists(manager.BinaryPath));
        Assert.Single(handler.Requests);
    }

    [Theory]
    [InlineData("e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp", "yt-dlp", true)]
    [InlineData("e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3 *yt-dlp", "yt-dlp", true)]
    [InlineData("E51E69E89489F199530F13D32A4342DA3DDB2CF34842F364603A066DB05AFBF3  yt-dlp\r", "yt-dlp", true)]
    [InlineData("e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp.exe", "yt-dlp", false)]
    [InlineData("e51e69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp", "yt-dlp.exe", false)]
    [InlineData("e51e69e8  yt-dlp", "yt-dlp", false)]
    [InlineData("zzze69e89489f199530f13d32a4342da3ddb2cf34842f364603a066db05afbf3  yt-dlp", "yt-dlp", false)]
    [InlineData("", "yt-dlp", false)]
    public void HashFor_TakesOnlyAWholeLineForTheExactName(string line, string name, bool found) =>
        Assert.Equal(found, VerifiedDownload.HashFor("junk\n" + line + "\nmore junk\n", name) is not null);
}
