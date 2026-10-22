using Rambutan.Core.Local;
using Rambutan.Core.Playback;

namespace Rambutan.LocalLibrary.Tests;

public sealed class LocalFirstSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"rambutan-localsource-{Guid.NewGuid():N}");
    private readonly string _music;
    private readonly LocalFileIndex _index;
    private readonly FakeInner _inner = new();
    private int _decoderWaits;

    public LocalFirstSourceTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(_music);
        _index = new LocalFileIndex(Path.Combine(_root, "data"), new TextReader(), _ => Task.CompletedTask, () => [_music]);
    }

    public void Dispose()
    {
        _index.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private LocalFirstSource Source() => new(_inner, _index, _ =>
    {
        Interlocked.Increment(ref _decoderWaits);
        return Task.CompletedTask;
    });

    private async Task<string> AddAsync(string name, string content)
    {
        var path = Path.Combine(_music, name);
        await File.WriteAllTextAsync(path, content);
        await _index.ScanAsync();
        return _index.Files().Single(f => f.Path == path).Id;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)] // the fault retry: a file has no stream to go round to
    public async Task ALocalId_PlaysItsFile_AndNeverReachesTheStreams(bool bypassCache)
    {
        var id = await AddAsync("a.flac", "gain");

        var media = await Source().ResolveAsync(id, bypassCache);

        Assert.Equal(Path.Combine(_music, "a.flac"), media.Url);
        Assert.Equal("flac", media.AudioCodec);
        Assert.Equal(-4 - -6.0, media.LoudnessDb);
        Assert.Empty(_inner.Resolved);
        Assert.Equal(1, _decoderWaits);
    }

    [Fact]
    public async Task AFileThatIsGone_IsMissing_AndDoesntReachTheStreams()
    {
        var id = await AddAsync("a.flac", "song");
        File.Delete(Path.Combine(_music, "a.flac"));

        var ex = await Assert.ThrowsAsync<LocalFileMissingException>(() => Source().ResolveAsync(id, bypassCache: true));

        Assert.Equal(id, ex.TrackId);
        Assert.Empty(_inner.Resolved);
        Assert.False(Source().IsAvailableOffline(id));
    }

    [Fact]
    public async Task AnIdTheIndexDoesntKnow_IsMissing()
    {
        await Assert.ThrowsAsync<LocalFileMissingException>(() => Source().ResolveAsync("local:99"));
        Assert.Empty(_inner.Resolved);
    }

    [Fact]
    public async Task AFileThatIsntAudio_IsMissing()
    {
        var id = await AddAsync("a.mp3", "bad");

        await Assert.ThrowsAsync<LocalFileMissingException>(() => Source().ResolveAsync(id));
    }

    [Fact]
    public async Task AYouTubeId_GoesToTheStreams_AsItCame()
    {
        var media = await Source().ResolveAsync("dQw4w9WgXcQ", bypassCache: true);

        Assert.Equal("stream:dQw4w9WgXcQ", media.Url);
        Assert.Equal([("dQw4w9WgXcQ", true)], _inner.Resolved);
        Assert.Equal(0, _decoderWaits);
    }

    [Fact]
    public async Task ALocalFile_PlaysOffline_IsAlwaysWarm_AndIsNeverWarmedUpstream()
    {
        var id = await AddAsync("a.flac", "song");
        var source = Source();

        Assert.True(source.IsAvailableOffline(id));
        Assert.True(source.IsWarm(id));
        await source.WarmAsync(id, CancellationToken.None);
        Assert.Empty(_inner.Warmed);

        Assert.False(source.IsAvailableOffline("dQw4w9WgXcQ"));
        Assert.False(source.IsWarm("dQw4w9WgXcQ"));
        await source.WarmAsync("dQw4w9WgXcQ", CancellationToken.None);
        Assert.Equal(["dQw4w9WgXcQ"], _inner.Warmed);
    }

    /// <summary>The file's text is its title; "bad" isn't audio, "gain" has a
    /// track gain of -6 dB.</summary>
    private sealed class TextReader : IMediaFileReader
    {
        public LocalFileTags? Read(string path) => File.ReadAllText(path) switch
        {
            "bad" => null,
            "gain" => new LocalFileTags("flac", Title: "gain", TrackGainDb: -6.0),
            var text => new LocalFileTags("flac", Title: text),
        };
    }

    private sealed class FakeInner : IStreamSource, IStreamWarmer, IOfflineAvailability
    {
        public List<(string Id, bool Bypass)> Resolved { get; } = [];
        public List<string> Warmed { get; } = [];

        public Task<ResolvedMedia> ResolveAsync(string videoId, bool bypassCache = false, CancellationToken ct = default)
        {
            Resolved.Add((videoId, bypassCache));
            return Task.FromResult(new ResolvedMedia("stream:" + videoId, "opus"));
        }

        public bool IsWarm(string videoId) => false;

        public Task WarmAsync(string videoId, CancellationToken ct)
        {
            Warmed.Add(videoId);
            return Task.CompletedTask;
        }

        public bool IsAvailableOffline(string videoId) => false;
    }
}
