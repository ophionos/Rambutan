using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Playback;
using Rambutan.Core.Stats;
using Rambutan.Integrations;

namespace Rambutan.App.Services;

/// <summary>The first line of <c>plays.jsonl</c>.</summary>
public sealed class PlayLogHeader
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // PlayRecord.SongKey is worked out, not kept.
    IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(PlayLogHeader))]
[JsonSerializable(typeof(PlayRecord))]
internal sealed partial class PlayLogJsonContext : JsonSerializerContext;

public enum PlayLogState
{
    Readable,

    /// <summary>A newer build wrote the file: it is left as it is, and nothing is added.</summary>
    Newer,

    /// <summary>The file's header can't be read: it is left as it is, and nothing is
    /// added, until the user clears it.</summary>
    Unreadable,
}

public sealed record PlayLogContents(PlayLogState State, IReadOnlyList<PlayRecord> Plays);

/// <summary>
/// <c>plays.jsonl</c> in the profile's folder: the play log, which never leaves
/// the machine. A version line, then one play per line, appended as plays go on;
/// a later line with a play's id replaces the earlier one. A line cut short by a
/// crash is skipped. Not thread-safe: <see cref="PlayLog"/> calls it from its one
/// worker.
/// </summary>
public sealed class PlayLogStore(string dataDirectory)
{
    /// <summary>Lines kept beyond one per play before the file is written again
    /// without the replaced ones.</summary>
    private const int ReplacedLinesKept = 200;

    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "plays.jsonl");

    private PlayLogState? _state;

    public PlayLogContents Read()
    {
        if (!File.Exists(Path))
        {
            _state = PlayLogState.Readable;
            return new(PlayLogState.Readable, []);
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(Path, Encoding.UTF8);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("plays", $"Couldn't read the play log: {ex.Message}");
            return new(PlayLogState.Unreadable, []);
        }

        _state = HeaderState(lines.FirstOrDefault());
        if (_state != PlayLogState.Readable)
            return new(_state.Value, []);

        var plays = new Dictionary<string, PlayRecord>();
        var skipped = 0;
        foreach (var line in lines.Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line))
                continue;
            try
            {
                if (JsonSerializer.Deserialize(line, PlayLogJsonContext.Default.PlayRecord) is { } play)
                    plays[play.Id] = play;
            }
            catch (JsonException)
            {
                skipped++;
            }
        }
        if (skipped > 0)
            AppLog.Write("plays", $"Skipped {skipped} unreadable line(s) in the play log.");

        var kept = plays.Values.OrderBy(p => p.StartedAt).ToList();
        if (lines.Length - 1 > kept.Count + ReplacedLinesKept)
            Rewrite(kept);
        return new(PlayLogState.Readable, kept);
    }

    /// <summary>Adds or replaces a play; does nothing to a file this build can't read.</summary>
    public void Write(PlayRecord play)
    {
        _state ??= File.Exists(Path) ? HeaderState(FirstLine()) : PlayLogState.Readable;
        if (_state != PlayLogState.Readable)
            return;

        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        using var file = new FileStream(Path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        var text = new StringBuilder();
        if (file.Length == 0)
            text.Append(JsonSerializer.Serialize(new PlayLogHeader(), PlayLogJsonContext.Default.PlayLogHeader)).Append('\n');
        else
        {
            // After a crash mid-line, start on a line of its own rather than
            // join the cut one and be skipped with it.
            file.Seek(-1, SeekOrigin.End);
            if (file.ReadByte() != '\n')
                text.Append('\n');
        }
        text.Append(JsonSerializer.Serialize(play, PlayLogJsonContext.Default.PlayRecord)).Append('\n');
        file.Seek(0, SeekOrigin.End);
        file.Write(Encoding.UTF8.GetBytes(text.ToString()));
    }

    public void Delete()
    {
        File.Delete(Path);
        _state = PlayLogState.Readable;
    }

    private void Rewrite(IReadOnlyList<PlayRecord> plays)
    {
        var text = new StringBuilder();
        text.Append(JsonSerializer.Serialize(new PlayLogHeader(), PlayLogJsonContext.Default.PlayLogHeader)).Append('\n');
        foreach (var play in plays)
            text.Append(JsonSerializer.Serialize(play, PlayLogJsonContext.Default.PlayRecord)).Append('\n');
        var temp = Path + ".tmp";
        File.WriteAllText(temp, text.ToString(), new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    private string? FirstLine()
    {
        using var reader = new StreamReader(Path, Encoding.UTF8);
        return reader.ReadLine();
    }

    private static PlayLogState HeaderState(string? line)
    {
        try
        {
            var header = line is null ? null : JsonSerializer.Deserialize(line, PlayLogJsonContext.Default.PlayLogHeader);
            if (header is null || header.Version < 1)
                return PlayLogState.Unreadable;
            if (header.Version > PlayLogHeader.CurrentVersion)
            {
                AppLog.Write("plays", $"Leaving plays.jsonl version {header.Version} as it is; this build reads {PlayLogHeader.CurrentVersion}.");
                return PlayLogState.Newer;
            }
            return PlayLogState.Readable;
        }
        catch (JsonException)
        {
            return PlayLogState.Unreadable;
        }
    }
}

/// <summary>
/// Records what plays into the play log, while <see cref="Enabled"/>: each play
/// with the audio actually heard (<see cref="ListenMeter"/>). The play running is
/// written again on a pause or stop, every few minutes of listening, when the
/// track changes, before the log is read, and at exit, so a crash loses little.
/// The file is touched only on one worker, never on the listener hub's thread.
/// </summary>
public sealed class PlayLog : IPlaybackListener, IDisposable
{
    /// <summary>How much listening may go unwritten while a play runs.</summary>
    public static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(5);

    /// <summary>Less than this heard isn't worth a line.</summary>
    private static readonly TimeSpan Least = TimeSpan.FromSeconds(1);

    private readonly PlayLogStore _store;
    private readonly TimeProvider _clock;
    private readonly ListenMeter _meter;
    private readonly object _sync = new();
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _pump;

    private bool _enabled;
    private TrackInfo? _track;
    private string _playId = "";
    private DateTimeOffset _startedAt;
    private TimeSpan _written;

    public PlayLog(PlayLogStore store, bool enabled, TimeProvider? clock = null)
    {
        _store = store;
        _enabled = enabled;
        _clock = clock ?? TimeProvider.System;
        _meter = new ListenMeter(_clock);
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>Turning it off forgets the play running; turning it on records the
    /// track playing from that moment.</summary>
    public bool Enabled
    {
        get
        {
            lock (_sync)
                return _enabled;
        }
        set
        {
            lock (_sync)
            {
                if (_enabled == value)
                    return;
                _enabled = value;
                StartPlay(_track, sameTrack: true);
            }
        }
    }

    public void OnNowPlayingChanged(TrackInfo? track)
    {
        lock (_sync)
        {
            SaveCurrent();
            StartPlay(track);
        }
    }

    public void OnStateChanged(PlaybackState state)
    {
        lock (_sync)
        {
            _meter.OnStateChanged(state);
            if (state != PlaybackState.Playing)
                SaveCurrent();
        }
    }

    public void OnProgress(TimeSpan position, TimeSpan? duration)
    {
        lock (_sync)
        {
            if (_track is null)
                return;
            _meter.OnProgress(position);
            if (_meter.Listened - _written >= WriteEvery)
                SaveCurrent();
        }
    }

    /// <summary>The whole log, with the play running as far as it has got.</summary>
    public Task<PlayLogContents> ReadAsync()
    {
        lock (_sync)
            SaveCurrent();
        return Run(() => _store.Read());
    }

    /// <summary>Deletes the log. The play running starts again from nothing, so
    /// what was heard of it before doesn't come back.</summary>
    public Task ClearAsync()
    {
        lock (_sync)
        {
            StartPlay(_track, sameTrack: true);
            return Run(() =>
            {
                _store.Delete();
                return true;
            });
        }
    }

    // Under _sync.
    private void StartPlay(TrackInfo? track, bool sameTrack = false)
    {
        _track = track;
        _playId = Guid.NewGuid().ToString("N");
        _startedAt = _clock.GetUtcNow();
        _written = TimeSpan.Zero;
        if (sameTrack)
            _meter.ResetListened();
        else
            _meter.Restart();
    }

    // Under _sync.
    private void SaveCurrent()
    {
        if (!_enabled || _track is not { } track)
            return;
        var listened = _meter.Listened;
        if (listened < Least || listened == _written)
            return;
        _written = listened;
        var play = PlayRecord.From(_playId, track, _startedAt, listened);
        Post(() =>
        {
            _store.Write(play);
            return Task.CompletedTask;
        });
    }

    private void Post(Func<Task> work) => _work.Writer.TryWrite(work);

    private Task<T> Run<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_work.Writer.TryWrite(() =>
            {
                try
                {
                    done.TrySetResult(work());
                }
                catch (Exception ex)
                {
                    done.TrySetException(ex);
                }
                return Task.CompletedTask;
            }))
            done.TrySetException(new ObjectDisposedException(nameof(PlayLog)));
        return done.Task;
    }

    private async Task PumpAsync()
    {
        await foreach (var work in _work.Reader.ReadAllAsync())
        {
            try
            {
                await work();
            }
            catch (Exception ex)
            {
                AppLog.Write("plays", $"Play log write failed: {ex.Message}");
            }
        }
    }

    /// <summary>Writes the play running and waits a little for the writes queued.</summary>
    public void Dispose()
    {
        lock (_sync)
        {
            SaveCurrent();
            _track = null;
        }
        _work.Writer.TryComplete();
        if (!_pump.Wait(TimeSpan.FromSeconds(2)))
            AppLog.Write("plays", "still writing at exit; leaving it");
    }
}
