using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Integrations;

namespace Rambutan.App.Services;

/// <summary>
/// Adds the songs played in Rambutan to the account's YouTube Music history,
/// only while the setting is on (off by default: it tells Google what the user
/// plays) and signed in. A play goes once it counts (<see cref="HistoryReportGate"/>);
/// a local file never does. Reports run one at a time on their own worker, and
/// one that fails is logged and dropped: the history is YouTube Music's, not a
/// record Rambutan keeps.
/// </summary>
/// <param name="report">Sends one play with the account's cookies (the yt-dlp run).</param>
/// <param name="cookies">The signed-in account's cookies as a Cookie header; null signed out.</param>
public sealed class WatchHistory : IPlaybackListener
{
    private readonly HistoryReportGate _gate;
    private readonly Func<string, string, CancellationToken, Task> _report;
    private readonly Func<string?> _cookies;
    private readonly Channel<string> _plays = Channel.CreateUnbounded<string>(new() { SingleReader = true });

    public WatchHistory(
        Func<string, string, CancellationToken, Task> report, Func<string?> cookies, bool enabled,
        TimeProvider? clock = null)
    {
        _gate = new HistoryReportGate(clock ?? TimeProvider.System);
        _report = report;
        _cookies = cookies;
        Enabled = enabled;
        Worker = Task.Run(RunAsync);
    }

    public bool Enabled { get; set; }

    /// <summary>The worker; it ends only after <see cref="Complete"/>. For tests.</summary>
    internal Task Worker { get; }

    public void OnNowPlayingChanged(TrackInfo? track) => _gate.OnNowPlaying(track);

    public void OnStateChanged(PlaybackState state) => _gate.OnStateChanged(state);

    public void OnProgress(TimeSpan position, TimeSpan? duration)
    {
        // The gate runs while off too, so turning the setting on mid-track
        // doesn't report a play that already counted.
        if (_gate.OnProgress(position, duration) is { } videoId && Enabled)
            _plays.Writer.TryWrite(videoId);
    }

    /// <summary>Stops taking plays; the worker ends after those queued.</summary>
    public void Complete() => _plays.Writer.TryComplete();

    private async Task RunAsync()
    {
        await foreach (var videoId in _plays.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            // Read when it goes, so a sign-out or the setting turned off since
            // the play counted sends nothing.
            if (!Enabled || _cookies() is not { Length: > 0 } cookies)
                continue;
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
                await _report(videoId, cookies, timeout.Token).ConfigureAwait(false);
                AppLog.Write("history", $"added {videoId} to the YouTube Music history");
            }
            catch (Exception ex)
            {
                AppLog.Write("history", $"couldn't add {videoId} to the YouTube Music history: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
