using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>A clock the test advances by hand; stands in for DispatcherTimer.RunOnce.</summary>
internal sealed class ManualClock
{
    private sealed class Entry(TimeSpan due, Action action) : IDisposable
    {
        public TimeSpan Due { get; } = due;
        public Action Action { get; } = action;
        public bool Cancelled { get; private set; }
        public void Dispose() => Cancelled = true;
    }

    private readonly List<Entry> _pending = [];

    public TimeSpan Now { get; private set; }

    public IDisposable Schedule(TimeSpan delay, Action action)
    {
        var entry = new Entry(Now + delay, action);
        _pending.Add(entry);
        return entry;
    }

    public void Advance(TimeSpan by)
    {
        Now += by;
        foreach (var entry in _pending.Where(e => e.Due <= Now && !e.Cancelled).ToArray())
        {
            _pending.Remove(entry);
            entry.Action();
        }
    }
}

public class ToastHostViewModelTests
{
    private readonly ManualClock _clock = new();
    private readonly ToastHostViewModel _host;

    public ToastHostViewModelTests()
    {
        _host = new ToastHostViewModel(_clock.Schedule);
    }

    private string[] Messages() => _host.Toasts.Select(t => t.Message).ToArray();

    [Fact]
    public void PlainToast_ExpiresAfterItsLifetime()
    {
        _host.Show("Added to queue");

        _clock.Advance(ToastHostViewModel.PlainLifetime - TimeSpan.FromMilliseconds(1));
        Assert.Equal(["Added to queue"], Messages());

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public void ErrorsAndActions_StayUpLonger()
    {
        _host.Show("Couldn't rate", ToastKind.Error);
        _host.Offer("Removed", "Undo", () => Task.CompletedTask);

        _clock.Advance(ToastHostViewModel.PlainLifetime);
        Assert.Equal(["Couldn't rate", "Removed"], Messages());

        _clock.Advance(ToastHostViewModel.LongLifetime - ToastHostViewModel.PlainLifetime);
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public void LongPlainMessages_StayUpLonger()
    {
        // The offline skip from the bug bash: two lines, gone before they were read.
        const string skipped = "You're offline. Skipped “Careless Whisper (Official Video)” to the next song that plays without a connection.";
        _host.Show(skipped);

        _clock.Advance(ToastHostViewModel.PlainLifetime);
        Assert.Equal([skipped], Messages());

        _clock.Advance(ToastHostViewModel.LongLifetime - ToastHostViewModel.PlainLifetime);
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public void Stack_IsCapped_OldestGivesWay()
    {
        for (var i = 1; i <= ToastHostViewModel.MaxVisible + 2; i++)
            _host.Show($"message {i}");

        Assert.Equal(["message 3", "message 4", "message 5"], Messages());
    }

    [Fact]
    public void RepeatingTheNewestMessage_RestartsItInsteadOfStacking()
    {
        _host.Show("Playback error");
        _clock.Advance(TimeSpan.FromSeconds(3));
        _host.Show("Playback error");

        Assert.Single(_host.Toasts);
        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Single(_host.Toasts); // 6 s after the first, 3 s after the repeat

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public void Hold_PausesExpiry_ReleaseRestartsTheFullLifetime()
    {
        var toast = _host.Show("Link copied");
        _clock.Advance(TimeSpan.FromSeconds(3));

        _host.Hold(toast);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Single(_host.Toasts);

        _host.Release(toast);
        _clock.Advance(ToastHostViewModel.PlainLifetime - TimeSpan.FromMilliseconds(1));
        Assert.Single(_host.Toasts);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public void NestedHolds_ExpireOnlyAfterTheLastRelease()
    {
        var toast = _host.Show("Liked");
        _host.Hold(toast);   // pointer over
        _host.Hold(toast);   // focus inside
        _host.Release(toast);

        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Single(_host.Toasts);

        _host.Release(toast);
        _clock.Advance(ToastHostViewModel.PlainLifetime);
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public async Task Action_RunsOnce_ThenClosesTheToast()
    {
        var runs = 0;
        var toast = _host.Offer("Removed", "Undo", () => { runs++; return Task.CompletedTask; });

        await toast.ActCommand.ExecuteAsync(null);
        await toast.ActCommand.ExecuteAsync(null);

        Assert.Equal(1, runs);
        Assert.Empty(_host.Toasts);
    }

    [Fact]
    public async Task Action_InFlight_DoesNotExpire()
    {
        var gate = new TaskCompletionSource();
        var toast = _host.Offer("Removed", "Undo", () => gate.Task);

        var running = toast.ActCommand.ExecuteAsync(null);
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Contains(toast, _host.Toasts);

        gate.SetResult();
        await running;
        Assert.DoesNotContain(toast, _host.Toasts);
    }

    [Fact]
    public async Task Action_ThatThrows_BecomesAnErrorToast()
    {
        var toast = _host.Offer("Removed", "Undo", () => throw new InvalidOperationException("offline"));

        await toast.ActCommand.ExecuteAsync(null);

        var error = Assert.Single(_host.Toasts);
        Assert.True(error.IsError);
        Assert.Equal("Undo failed: offline", error.Message);
        Assert.Equal("Error: Undo failed: offline", error.AutomationName);
    }

    [Fact]
    public void Close_DismissesAndCancelsTheTimer()
    {
        var toast = _host.Show("Signed out");
        toast.CloseCommand.Execute(null);
        Assert.Empty(_host.Toasts);

        // The cancelled expiry must not remove a later toast.
        var next = _host.Show("Signed out");
        _clock.Advance(ToastHostViewModel.PlainLifetime - TimeSpan.FromMilliseconds(1));
        Assert.Contains(next, _host.Toasts);
    }
}
