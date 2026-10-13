using Rambutan.Core.Diagnostics;

namespace Rambutan.Lyrics.Providers;

/// <summary>
/// Asks providers in order and takes the first answer. Each provider
/// gets its own time budget, so one slow source can't hold the pane in its
/// loading state; a provider that times out or throws is logged and skipped.
/// </summary>
public sealed class LyricsProviderChain(IReadOnlyList<ILyricsProvider> providers, TimeSpan? perProviderTimeout = null)
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(4);

    private readonly TimeSpan _timeout = perProviderTimeout ?? DefaultTimeout;

    /// <summary>
    /// The first provider's answer, <see cref="LyricsStatus.NotFound"/> when every
    /// provider answered without one, or <see cref="LyricsStatus.Unavailable"/>
    /// when none answered with lyrics and at least one couldn't be asked.
    /// Throws only when <paramref name="ct"/> is cancelled.
    /// </summary>
    public async Task<LyricsLookup> LookupAsync(LyricsQuery query, CancellationToken ct)
    {
        var anyFailed = false;
        foreach (var provider in providers)
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            budget.CancelAfter(_timeout);
            try
            {
                if (await provider.TryGetAsync(query, budget.Token) is { } result)
                    return LyricsLookup.From(result);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                anyFailed = true;
                AppLog.Write("lyrics", $"{provider.Name} timed out after {_timeout.TotalSeconds:0.#} s for {query.VideoId}");
            }
            catch (Exception ex)
            {
                anyFailed = true;
                AppLog.Write("lyrics", $"{provider.Name} failed for {query.VideoId}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        return anyFailed ? LyricsLookup.Unavailable : LyricsLookup.NotFound;
    }
}
