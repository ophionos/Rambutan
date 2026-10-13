namespace Rambutan.Integrations.Tests;

/// <summary>A second launch hands its arguments, a rambutan:// link among them,
/// to the running instance.</summary>
public class SingleInstanceGuardTests : IDisposable
{
    private readonly List<string> _names = [];

    private string UniqueName()
    {
        var name = "rambutan-test-" + Guid.NewGuid().ToString("N");
        _names.Add(name);
        return name;
    }

    /// <summary>Off Windows the lock is a file in the runtime folder; the tests
    /// remove theirs.</summary>
    public void Dispose()
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var name in _names)
            File.Delete(SingleInstanceGuard.LockPath(name));
    }

    private async Task<string[]> ForwardAsync(string[] args)
    {
        var name = UniqueName();
        using var primary = SingleInstanceGuard.Acquire(name);
        var received = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        primary.ArgsReceived += a => received.TrySetResult(a);

        using var secondary = SingleInstanceGuard.Acquire(name);
        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);

        await Task.Run(() => secondary.NotifyPrimary(args), TestContext.Current.CancellationToken);
        return await received.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ASecondLaunch_ForwardsItsLink()
    {
        string[] args = ["--deep-link", "rambutan://search?q=daft+punk"];

        Assert.Equal(args, await ForwardAsync(args));
    }

    [Fact]
    public async Task APlainSecondLaunch_StillReachesThePrimary() =>
        Assert.Empty(await ForwardAsync([]));

    [Fact]
    public async Task ForwardedArguments_AreCapped()
    {
        var many = Enumerable.Range(0, SingleInstanceGuard.MaxArgs + 10).Select(i => $"a{i}").ToArray();
        var received = await ForwardAsync(many);
        Assert.Equal(SingleInstanceGuard.MaxArgs, received.Length);

        var huge = new string('x', SingleInstanceGuard.MaxArgLength + 1);
        Assert.Equal(["before", "after"], await ForwardAsync(["before", huge, "after"]));
    }

    [Fact]
    public void TheLock_IsFreedWhenThePrimaryCloses()
    {
        var name = UniqueName();
        var primary = SingleInstanceGuard.Acquire(name);
        Assert.True(primary.IsPrimary);
        Assert.Null(SingleInstanceGuard.TryReserve(name));

        primary.Dispose();

        using var next = SingleInstanceGuard.Acquire(name);
        Assert.True(next.IsPrimary);
    }

    [Fact]
    public void AReservation_KeepsAnInstanceFromStarting()
    {
        var name = UniqueName();
        using (var reservation = SingleInstanceGuard.TryReserve(name))
        {
            Assert.NotNull(reservation);
            using var blocked = SingleInstanceGuard.Acquire(name);
            Assert.False(blocked.IsPrimary);
        }

        using var started = SingleInstanceGuard.Acquire(name);
        Assert.True(started.IsPrimary);
    }
}
