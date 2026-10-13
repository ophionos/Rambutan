using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Rambutan.Integrations.LastFm;

/// <summary>
/// Hand-rolled Last.fm API 2.0 client: the desktop auth flow,
/// now-playing and batched scrobbles. Every call is a signed, form-encoded POST
/// with <c>format=json</c>. It translates, it doesn't decide: every failure comes
/// back as a <see cref="LastFmOutcome"/> and <see cref="LastFmScrobbler"/> chooses
/// what to do about it. Never throws for network or API errors.
/// </summary>
public sealed class LastFmClient
{
    public static readonly Uri Endpoint = new("https://ws.audioscrobbler.com/2.0/");

    /// <summary>track.scrobble takes at most 50 plays per call.</summary>
    public const int MaxBatch = 50;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly string _apiKey;
    private readonly string _secret;
    private readonly HttpClient _http;

    public LastFmClient(string apiKey, string sharedSecret, HttpClient? http = null)
    {
        _apiKey = apiKey;
        _secret = sharedSecret;
        _http = http ?? new HttpClient();
        var version = typeof(LastFmClient).Assembly.GetName().Version;
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("Rambutan", version is null ? "dev" : version.ToString(3)));
    }

    /// <summary>The page where the user approves <paramref name="token"/> for this app.</summary>
    public string AuthorizeUrl(string token) =>
        $"https://www.last.fm/api/auth/?api_key={Uri.EscapeDataString(_apiKey)}&token={Uri.EscapeDataString(token)}";

    public async Task<LastFmResult<string>> GetTokenAsync(CancellationToken ct = default)
    {
        var result = await CallAsync("auth.getToken", [], LastFmJsonContext.Default.TokenDto, ct);
        return result.IsOk && !string.IsNullOrEmpty(result.Value?.Token)
            ? new(LastFmOutcome.Ok, result.Value.Token)
            : Fail<string, TokenDto>(result);
    }

    public async Task<LastFmResult<LastFmSession>> GetSessionAsync(string token, CancellationToken ct = default)
    {
        var result = await CallAsync(
            "auth.getSession", [new("token", token)], LastFmJsonContext.Default.SessionEnvelopeDto, ct);
        if (result.IsOk && result.Value?.Session is { Name: { Length: > 0 } name, Key: { Length: > 0 } key })
            return new(LastFmOutcome.Ok, new LastFmSession(name, key));

        // Approval errors only mean "not yet" or "start over" on this method.
        return result.ErrorCode switch
        {
            14 => new(LastFmOutcome.NotApproved, null, 14, result.Message),
            4 or 15 => new(LastFmOutcome.TokenExpired, null, result.ErrorCode, result.Message),
            _ => Fail<LastFmSession, SessionEnvelopeDto>(result),
        };
    }

    public async Task<LastFmResult<bool>> UpdateNowPlayingAsync(
        string sessionKey, ScrobbleEntry track, CancellationToken ct = default)
    {
        var parameters = new List<KeyValuePair<string, string>>
        {
            new("sk", sessionKey),
            new("artist", track.Artist),
            new("track", track.Track),
        };
        if (!string.IsNullOrEmpty(track.Album))
            parameters.Add(new("album", track.Album));
        if (track.DurationSeconds is { } duration)
            parameters.Add(new("duration", duration.ToString(CultureInfo.InvariantCulture)));

        var result = await CallAsync("track.updateNowPlaying", parameters, LastFmJsonContext.Default.LastFmErrorDto, ct);
        return result.IsOk ? new(LastFmOutcome.Ok, true) : Fail<bool, LastFmErrorDto>(result);
    }

    public async Task<LastFmResult<ScrobbleBatchResult>> ScrobbleAsync(
        string sessionKey, IReadOnlyList<ScrobbleEntry> batch, CancellationToken ct = default)
    {
        if (batch.Count is 0 or > MaxBatch)
            throw new ArgumentOutOfRangeException(nameof(batch), batch.Count, $"1 to {MaxBatch} plays per call.");

        var parameters = new List<KeyValuePair<string, string>> { new("sk", sessionKey) };
        for (var i = 0; i < batch.Count; i++)
        {
            var entry = batch[i];
            parameters.Add(new($"artist[{i}]", entry.Artist));
            parameters.Add(new($"track[{i}]", entry.Track));
            parameters.Add(new($"timestamp[{i}]", entry.Timestamp.ToString(CultureInfo.InvariantCulture)));
            if (!string.IsNullOrEmpty(entry.Album))
                parameters.Add(new($"album[{i}]", entry.Album));
            if (entry.DurationSeconds is { } duration)
                parameters.Add(new($"duration[{i}]", duration.ToString(CultureInfo.InvariantCulture)));
        }

        var result = await CallAsync("track.scrobble", parameters, LastFmJsonContext.Default.ScrobblesEnvelopeDto, ct);
        if (!result.IsOk)
            return Fail<ScrobbleBatchResult, ScrobblesEnvelopeDto>(result);

        var attr = result.Value?.Scrobbles?.Attr;
        return new(LastFmOutcome.Ok, new ScrobbleBatchResult(attr?.Accepted ?? batch.Count, attr?.Ignored ?? 0));
    }

    public static LastFmOutcome Classify(int errorCode) => errorCode switch
    {
        9 => LastFmOutcome.InvalidSession,
        8 or 11 or 16 => LastFmOutcome.RetryLater,
        29 => LastFmOutcome.RateLimited,
        10 or 13 or 26 => LastFmOutcome.ClientMisconfigured,
        _ => LastFmOutcome.Rejected,
    };

    /// <summary>Carries a failure over to another result type; an "ok" that lacked
    /// the fields the caller needs counts as a bad response, to retry later.</summary>
    private static LastFmResult<T> Fail<T, TDto>(LastFmResult<TDto> result) => result.IsOk
        ? new(LastFmOutcome.RetryLater, default, null, "Last.fm answered without the expected fields.")
        : new(result.Outcome, default, result.ErrorCode, result.Message);

    private async Task<LastFmResult<TDto>> CallAsync<TDto>(
        string method, List<KeyValuePair<string, string>> parameters, JsonTypeInfo<TDto> typeInfo,
        CancellationToken ct)
    {
        parameters.Add(new("method", method));
        parameters.Add(new("api_key", _apiKey));
        parameters.Add(new("api_sig", LastFmSignature.Sign(parameters, _secret)));
        parameters.Add(new("format", "json"));

        string body;
        bool success;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var content = new FormUrlEncodedContent(parameters);
            using var response = await _http.PostAsync(Endpoint, content, timeout.Token);
            success = response.IsSuccessStatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new(LastFmOutcome.RetryLater, default, null, $"network: {ex.Message}");
        }

        // Errors arrive as {"error":N,"message":…}, usually with a 4xx/5xx status
        // but not always, so the body decides.
        try
        {
            if (JsonSerializer.Deserialize(body, LastFmJsonContext.Default.LastFmErrorDto) is { Error: { } code } error)
                return new(Classify(code), default, code, error.Message);

            if (!success)
                return new(LastFmOutcome.RetryLater, default, null, "HTTP error without a Last.fm error body");

            return new(LastFmOutcome.Ok, JsonSerializer.Deserialize(body, typeInfo));
        }
        catch (JsonException ex)
        {
            return new(LastFmOutcome.RetryLater, default, null, $"unreadable response: {ex.Message}");
        }
    }
}
