using Rambutan.App.Services;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Integrations.Notifications;

namespace Rambutan.App.Tests;

/// <summary>
/// The track notification: off by default, shown only while the app is out of
/// focus (the rule's own cases are Core's), with the cover copied under a name
/// of its own, and taken away when playback stops.
/// </summary>
public sealed class TrackNotificationsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-notify-tests-" + Guid.NewGuid().ToString("N"));
    private readonly QueueModel _queue = new();
    private readonly PlaybackCoordinator _coordinator;
    private readonly AppSettings _settings = new();
    private readonly FakeNotifier _notifier = new();
    private bool _focused;
    private Func<TrackInfo, CancellationToken, Task<string?>> _cover = (_, _) => Task.FromResult<string?>(null);

    public TrackNotificationsTests()
    {
        Directory.CreateDirectory(_dir);
        _coordinator = FakePlayback.Coordinator(_queue);
    }

    private TrackNotifications Create() => new(
        _coordinator, _settings, _notifier, () => _focused, track => track,
        (track, ct) => _cover(track, ct), Path.Combine(_dir, "notification"), action => action());

    private static TrackInfo Track(string id, string? album = "Tidelines") => new()
    {
        VideoId = id,
        Title = "Title " + id,
        Artists = [new ArtistRef("The Quiet Harbour")],
        Album = album,
    };

    private string File(string name, byte[] bytes)
    {
        var path = Path.Combine(_dir, name);
        System.IO.File.WriteAllBytes(path, bytes);
        return path;
    }

    [Fact]
    public async Task Off_by_default_nothing_shows()
    {
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1")]);
        await notifications.Pending;

        Assert.False(_settings.NotifyOnTrackChange);
        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task On_and_out_of_focus_each_new_track_shows()
    {
        _settings.NotifyOnTrackChange = true;
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1"), Track("t2", album: null)]);
        await notifications.Pending;
        await _coordinator.NextAsync();
        await notifications.Pending;

        Assert.Equal(
            [("Title t1", "The Quiet Harbour · Tidelines"), ("Title t2", "The Quiet Harbour")],
            _notifier.Shown.Select(n => (n.Title, n.Body)));
    }

    [Fact]
    public async Task A_song_picked_in_the_focused_app_shows_nothing()
    {
        _settings.NotifyOnTrackChange = true;
        _focused = true;
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1")]);
        await notifications.Pending;

        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task Where_it_can_never_show_nothing_is_asked()
    {
        _settings.NotifyOnTrackChange = true;
        _notifier.CanShow = false;
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1")]);
        await notifications.Pending;

        Assert.Empty(_notifier.Shown);
    }

    [Fact]
    public async Task Stopping_takes_it_away()
    {
        _settings.NotifyOnTrackChange = true;
        using var notifications = Create();
        await _coordinator.PlayQueueAsync([Track("t1")]);
        await notifications.Pending;

        _coordinator.Stop();

        for (var i = 0; i < 200 && _notifier.Closes == 0; i++)
            await Task.Delay(10);
        Assert.Equal(1, _notifier.Closes);
    }

    [Fact]
    public async Task The_cover_is_a_copy_named_for_its_format_and_the_last_copy_goes()
    {
        _settings.NotifyOnTrackChange = true;
        var jpeg = File("cover.img", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0, 0, 0]);
        var png = File("other.img", [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        _cover = (track, _) => Task.FromResult<string?>(track.VideoId == "t1" ? jpeg : png);
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1"), Track("t2")]);
        await notifications.Pending;
        var first = _notifier.Shown[0].CoverFile;
        await _coordinator.NextAsync();
        await notifications.Pending;
        var second = _notifier.Shown[1].CoverFile;

        Assert.EndsWith(".jpg", first);
        Assert.EndsWith(".png", second);
        Assert.False(System.IO.File.Exists(first));
        Assert.True(System.IO.File.Exists(second));
        Assert.True(System.IO.File.Exists(jpeg));
    }

    [Fact]
    public async Task A_cover_in_a_format_toasts_dont_show_is_left_out()
    {
        _settings.NotifyOnTrackChange = true;
        var webp = File("cover.img", "RIFF\0\0\0\0WEBP"u8.ToArray());
        _cover = (_, _) => Task.FromResult<string?>(webp);
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1")]);
        await notifications.Pending;

        Assert.Null(Assert.Single(_notifier.Shown).CoverFile);
    }

    [Fact]
    public async Task A_cover_that_fails_still_shows_the_track()
    {
        _settings.NotifyOnTrackChange = true;
        _cover = (_, _) => throw new HttpRequestException("offline");
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1")]);
        await notifications.Pending;

        Assert.Null(Assert.Single(_notifier.Shown).CoverFile);
    }

    [Fact]
    public async Task A_track_that_moved_on_while_its_cover_loaded_shows_nothing()
    {
        _settings.NotifyOnTrackChange = true;
        var slow = new TaskCompletionSource<string?>();
        _cover = (track, _) => track.VideoId == "t1" ? slow.Task : Task.FromResult<string?>(null);
        using var notifications = Create();

        await _coordinator.PlayQueueAsync([Track("t1"), Track("t2")]);
        var first = notifications.Pending;
        await _coordinator.NextAsync();
        await notifications.Pending;
        slow.SetResult(null);
        await first;

        Assert.Equal(["Title t2"], _notifier.Shown.Select(n => n.Title));
    }

    [Theory]
    [InlineData("A", "B", "A · B")]
    [InlineData("A", null, "A")]
    [InlineData("", "B", "B")]
    [InlineData("", null, "")]
    public void The_body_is_the_artist_and_album_it_has(string artist, string? album, string body)
    {
        var track = new TrackInfo
        {
            VideoId = "t1",
            Title = "Song",
            Artists = artist.Length > 0 ? [new ArtistRef(artist)] : [],
            Album = album,
        };

        Assert.Equal(body, TrackNotifications.Body(track));
    }

    public void Dispose()
    {
        _coordinator.Dispose();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeNotifier : ITrackNotifier
    {
        public bool CanShow { get; set; } = true;

        public List<TrackNotification> Shown { get; } = [];

        public int Closes { get; private set; }

        public void Show(TrackNotification notification) => Shown.Add(notification);

        public void Close() => Closes++;

        public void Dispose() { }
    }
}
