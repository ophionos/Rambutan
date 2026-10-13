namespace Rambutan.Core.Playback;

public enum Reachability
{
    Reachable,
    /// <summary>The connection itself failed: no network, no DNS, a dead proxy.</summary>
    Unreachable,
    /// <summary>No answer in time. A slow network looks the same, so it changes nothing.</summary>
    Unknown,
}

/// <summary>
/// Whether YouTube Music can be reached, as last observed, so the UI can say
/// beforehand what won't play instead of only after a skip.
/// <para>
/// It learns from checks (the app runs one at startup and on each network change)
/// and from failures already seen (<see cref="ReportOffline"/>). Once offline, it
/// checks again every <c>recheckEvery</c> until the network answers, so the state
/// expires when it is no longer true; online, it never checks on its own.
/// </para>
/// </summary>
public sealed class NetworkStatus
{
    public static readonly TimeSpan DefaultRecheck = TimeSpan.FromSeconds(30);

    private readonly Func<CancellationToken, Task<Reachability>> _check;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _recheckEvery;
    private readonly object _sync = new();
    private bool _isOffline;
    private bool _rechecking;

    public NetworkStatus(
        Func<CancellationToken, Task<Reachability>> check,
        TimeSpan? recheckEvery = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _check = check;
        _recheckEvery = recheckEvery ?? DefaultRecheck;
        _delay = delay ?? Task.Delay;
    }

    public bool IsOffline
    {
        get { lock (_sync) return _isOffline; }
    }

    /// <summary>Raised with the new value when <see cref="IsOffline"/> changes, on
    /// the thread that noticed. Subscribers marshal.</summary>
    public event Action<bool>? Changed;

    public async Task CheckAsync(CancellationToken ct = default)
    {
        Reachability result;
        try
        {
            result = await _check(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = Reachability.Unknown;
        }
        Apply(result);
    }

    /// <summary>Something already failed for want of the network (a track skipped
    /// offline); no need to ask.</summary>
    public void ReportOffline() => Apply(Reachability.Unreachable);

    private void Apply(Reachability result)
    {
        if (result == Reachability.Unknown)
            return;

        var offline = result == Reachability.Unreachable;
        var startRecheck = false;
        lock (_sync)
        {
            if (_isOffline == offline)
                return;
            _isOffline = offline;
            if (offline && !_rechecking)
                startRecheck = _rechecking = true;
        }

        Changed?.Invoke(offline);
        if (startRecheck)
            _ = RecheckAsync();
    }

    private async Task RecheckAsync()
    {
        while (true)
        {
            await _delay(_recheckEvery, CancellationToken.None);
            await CheckAsync();
            lock (_sync)
            {
                if (!_isOffline)
                {
                    _rechecking = false;
                    return;
                }
            }
        }
    }
}
