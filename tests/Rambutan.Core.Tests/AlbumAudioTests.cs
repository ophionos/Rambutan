using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Queue;
using Rambutan.Core.Settings;

namespace Rambutan.Core.Tests;

/// <summary>
/// Album pages play a row's album audio track (its SongId) instead of the music
/// video they list, and fall back to that video when the audio won't play.
/// </summary>
public class AlbumAudioTests
{
    // "Within" on Random Access Memories: the page lists the Drumless Edition video.
    private const string Omv = "omvWithin01";
    private const string Atv = "atvWithin01";

    private static TrackInfo AlbumRow(string videoId = Omv, string? songId = Atv) =>
        TestTracks.Track(videoId) with { SongId = songId, TrackNumber = 4 };

    [Fact]
    public void A_music_video_row_plays_its_audio_track_and_keeps_the_video_as_fallback()
    {
        var played = AlbumAudio.Prefer(AlbumRow());

        Assert.Equal(Atv, played.VideoId);
        Assert.Equal(Atv, played.SongId);
        Assert.Equal(Omv, played.ListedVideoId);
        Assert.Equal(4, played.TrackNumber);
        Assert.True(played.Identity.SameSong(AlbumRow().Identity));
    }

    [Theory]
    [InlineData(Atv, Atv)]     // already the audio track
    [InlineData(Omv, null)]    // no credits item: counterpart unknown
    [InlineData(Omv, "")]
    public void A_row_with_no_other_audio_track_plays_as_listed(string videoId, string? songId)
    {
        var row = AlbumRow(videoId, songId);

        Assert.Same(row, AlbumAudio.Prefer(row));
    }

    [Fact]
    public void A_music_video_downloaded_before_the_change_stays_what_plays()
    {
        var row = AlbumRow();

        Assert.Same(row, AlbumAudio.Prefer(row, id => id == Omv));
        // Once the audio track is downloaded too, it wins.
        Assert.Equal(Atv, AlbumAudio.Prefer(row, _ => true).VideoId);
        Assert.Equal(Atv, AlbumAudio.Prefer(row, id => id == Atv).VideoId);
    }

    [Fact]
    public void Fallback_is_the_listed_video_once()
    {
        var fallback = AlbumAudio.Fallback(AlbumAudio.Prefer(AlbumRow()))!;

        Assert.Equal(Omv, fallback.VideoId);
        Assert.Equal(Atv, fallback.SongId);
        Assert.Null(fallback.ListedVideoId);
        Assert.Null(AlbumAudio.Fallback(fallback));
        Assert.Null(AlbumAudio.Fallback(AlbumRow()));
    }

    [Fact]
    public void ReplaceCurrent_swaps_only_the_expected_instance_and_keeps_its_place()
    {
        var queue = new QueueModel(new Random(1));
        var album = AlbumAudio.Prefer(AlbumRow());
        queue.SetQueue([TestTracks.Track("t0"), album, TestTracks.Track("t2")], 1);
        queue.SetShuffle(true);
        var index = queue.CurrentIndex;
        var listed = AlbumAudio.Fallback(album)!;

        Assert.False(queue.ReplaceCurrent(TestTracks.Track(Atv), listed));
        Assert.True(queue.ReplaceCurrent(album, listed));

        Assert.Same(listed, queue.Current);
        Assert.Equal(index, queue.CurrentIndex);
        Assert.True(queue.Shuffle);
        queue.SetShuffle(false);
        Assert.Equal(["t0", Omv, "t2"], queue.Items.Select(t => t.VideoId));
    }

    private readonly FakeStreamSource _streams = new();
    private readonly FakeAudioPlayer _player = new();
    private readonly QueueModel _queue = new();

    private PlaybackCoordinator CreateCoordinator() => new(_streams, _player, _queue, new PlaybackSettings());

    [Fact]
    public async Task The_album_audio_track_plays_when_it_resolves()
    {
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync([AlbumAudio.Prefer(AlbumRow())]);

        Assert.Equal(Atv, coordinator.NowPlaying!.VideoId);
        Assert.StartsWith($"{Atv}#", _player.Current!.Key);
        Assert.DoesNotContain(_streams.Calls, c => c.VideoId == Omv);
    }

    [Fact]
    public async Task An_album_audio_track_that_wont_resolve_plays_the_listed_video_in_its_place()
    {
        _streams.FailAlways.Add(Atv);
        using var coordinator = CreateCoordinator();
        var notices = new List<PlaybackNotice>();
        coordinator.Notification += notices.Add;

        await coordinator.PlayQueueAsync([AlbumAudio.Prefer(AlbumRow()), TestTracks.Track("t2")]);

        Assert.Equal(PlaybackState.Playing, coordinator.State);
        Assert.Equal(Omv, coordinator.NowPlaying!.VideoId);
        Assert.StartsWith($"{Omv}#", _player.Current!.Key);
        // The queue holds what plays, so lyrics, downloads and the marker follow it.
        Assert.Equal([Omv, "t2"], _queue.Items.Select(t => t.VideoId));
        Assert.Equal(0, _queue.CurrentIndex);
        Assert.Empty(notices);
    }

    [Fact]
    public async Task An_audio_track_that_resolves_but_wont_open_also_falls_back()
    {
        _player.FailOpensRemaining = 2; // the open and its fresh-URL retry
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync([AlbumAudio.Prefer(AlbumRow())]);

        Assert.Equal(Omv, coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task Offline_the_listed_video_plays_when_only_it_is_available()
    {
        _streams.Offline = true;
        _streams.AvailableOffline.Add(Omv);
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync([TestTracks.Track("t0"), AlbumAudio.Prefer(AlbumRow())]);

        Assert.Equal(Omv, coordinator.NowPlaying!.VideoId);
    }

    [Fact]
    public async Task When_neither_plays_the_row_fails_once_and_the_queue_moves_on()
    {
        _streams.FailAlways.Add(Atv);
        _streams.FailAlways.Add(Omv);
        using var coordinator = CreateCoordinator();
        var notices = new List<PlaybackNotice>();
        coordinator.Notification += notices.Add;

        await coordinator.PlayQueueAsync([AlbumAudio.Prefer(AlbumRow()), TestTracks.Track("t2")]);

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        var failed = Assert.Single(notices, n => n.Kind == PlaybackNoticeKind.TrackFailed);
        Assert.Contains(Omv, failed.Detail);
    }

    [Fact]
    public async Task Other_tracks_have_no_fallback()
    {
        _streams.FailAlways.Add(Omv);
        using var coordinator = CreateCoordinator();

        await coordinator.PlayQueueAsync([AlbumRow(), TestTracks.Track("t2")]);

        Assert.Equal("t2", coordinator.NowPlaying!.VideoId);
        Assert.DoesNotContain(_streams.Calls, c => c.VideoId == Atv);
    }
}
