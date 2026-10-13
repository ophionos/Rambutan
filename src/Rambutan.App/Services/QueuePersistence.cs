using System;
using Avalonia.Threading;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Queue;

namespace Rambutan.App.Services;

/// <summary>
/// Keeps <c>queue.json</c> current: restores it into the queue at startup (not
/// playing; Play picks up where the queue left off) and saves a couple of seconds
/// after the queue settles, so a crash loses at most that much. Exit flushes.
/// </summary>
public sealed class QueuePersistence
{
    private static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    private readonly QueueModel _queue;
    private readonly QueueStore _store;
    private DispatcherTimer? _timer;
    private bool _dirty;

    private QueuePersistence(QueueModel queue, QueueStore store)
    {
        _queue = queue;
        _store = store;
    }

    /// <summary>Restores the saved queue into <paramref name="queue"/>, then starts
    /// saving its changes. Call before anything shows the queue.</summary>
    public static QueuePersistence Attach(QueueModel queue, QueueStore store)
    {
        if (store.Load() is { } saved)
            queue.Restore(saved);

        var persistence = new QueuePersistence(queue, store);
        // Changed fires on whichever thread edited the queue (engine threads
        // advance it); the timer lives on the UI thread.
        queue.Changed += () => Dispatcher.UIThread.Post(persistence.Schedule);
        return persistence;
    }

    private void Schedule()
    {
        _dirty = true;
        if (_timer is null)
        {
            _timer = new DispatcherTimer { Interval = Debounce };
            _timer.Tick += (_, _) => Flush();
        }
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Writes now if anything changed since the last write.</summary>
    public void Flush()
    {
        _timer?.Stop();
        if (!_dirty)
            return;
        _dirty = false;

        try
        {
            _store.Save(_queue.Snapshot());
        }
        catch (Exception ex)
        {
            // Disk full or read-only profile: the queue still works this run.
            AppLog.Write("queue", $"Couldn't save the queue: {ex.Message}");
        }
    }
}
