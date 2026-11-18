using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Rambutan.Integrations.Scrobbling;

namespace Rambutan.Integrations.ListenBrainz;

/// <summary>
/// Hand-rolled ListenBrainz API client: token validation and listen
/// submission, against listenbrainz.org or a self-hosted server. It translates,
/// it does not decide: every failure comes back as a <see cref="ListenBrainzOutcome"/>
/// and <see cref="ListenBrainzScrobbler"/> chooses what to do about it. Never
/// throws for network or API errors, and never puts the token in a result.
/// </summary>
public sealed class ListenBrainzClient
{
    public static readonly Uri DefaultServer = new("https://api.listenbrainz.org/");

    /// <summary>The API takes up to 1000 listens per request; 100 keeps a request small.</summary>
    public const int MaxBatch = 100;

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(20);

    private readonly Uri _server;
    private readonly string _clientVersion;
    private readonly HttpClient _http;

    /// <param name="server">A base URL ending in <c>/</c>, as <see cref="TryParseServer"/> returns it.</param>
    public ListenBrainzClient(Uri server, string clientVersion, HttpClient? http = null)
    {
        _server = server;
        _clientVersion = clientVersion;
        _http = http ?? new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Rambutan", clientVersion));
    }

    /// <summary>
    /// Reads a server address the user typed: an absolute http or https URL with
    /// a host and nothing else but a path. The path gets a trailing slash so the
    /// API's relative paths resolve under it (a server behind a path prefix works).
    /// </summary>
    public static bool TryParseServer(string? text, out Uri? server)
    {
        server = null;
        if (string.IsNullOrWhiteSpace(text)
            || !Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrEmpty(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
            return false;

        server = uri.AbsolutePath.EndsWith('/')
            ? uri
            : new UriBuilder(uri) { Path = uri.AbsolutePath + "/" }.Uri;
        return true;
    }

    /// <summary>Plain http: the token and the listens cross the network unencrypted.</summary>
    public static bool IsUnencrypted(Uri server) => server.Scheme == Uri.UriSchemeHttp;

    /// <summary>Checks <paramref name="token"/> and returns the user name it belongs to.</summary>
    public async Task<ListenBrainzResult<string>> ValidateTokenAsync(string token, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server, "1/validate-token"));
        var (status, body, failure) = await SendAsync(request, token, ct);
        if (failure is not null)
            return new(ListenBrainzOutcome.RetryLater, null, null, failure);

        if (Classify(status, body) is { } failed)
            return new(failed.Outcome, null, status, failed.Message);

        ValidateTokenDto? dto = null;
        try
        {
            dto = JsonSerializer.Deserialize(body, ListenBrainzJsonContext.Default.ValidateTokenDto);
        }
        catch (JsonException)
        {
            // Not JSON at all: handled below with the other wrong shapes.
        }

        return dto switch
        {
            { Valid: true, UserName: { Length: > 0 } name } => new(ListenBrainzOutcome.Ok, name, status),
            { Valid: false } => new(ListenBrainzOutcome.InvalidToken, null, status, "The server says the token is not valid."),
            _ => new(ListenBrainzOutcome.NotAServer, null, status, "The answer is not a ListenBrainz token check."),
        };
    }

    /// <summary>
    /// Sends listens. <see cref="ListenType.PlayingNow"/> and <see cref="ListenType.Single"/>
    /// take exactly one entry; <see cref="ListenType.Import"/> takes 1 to <see cref="MaxBatch"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The entry count does not fit the type.</exception>
    public async Task<ListenBrainzResult<bool>> SubmitAsync(
        string token, ListenType type, IReadOnlyList<ScrobbleEntry> entries, CancellationToken ct = default)
    {
        var max = type == ListenType.Import ? MaxBatch : 1;
        if (entries.Count < 1 || entries.Count > max)
            throw new ArgumentException($"{type} takes 1 to {max} listens, not {entries.Count}.", nameof(entries));

        var dto = new SubmitDto
        {
            ListenType = type switch
            {
                ListenType.PlayingNow => "playing_now",
                ListenType.Single => "single",
                _ => "import",
            },
            Payload = [.. entries.Select(e => ToDto(e, type))],
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_server, "1/submit-listens"))
        {
            Content = new StringContent(
                JsonSerializer.Serialize(dto, ListenBrainzJsonContext.Default.SubmitDto), Encoding.UTF8, "application/json"),
        };
        var (status, body, failure) = await SendAsync(request, token, ct);
        if (failure is not null)
            return new(ListenBrainzOutcome.RetryLater, false, null, failure);

        return Classify(status, body) is { } failed
            ? new(failed.Outcome, false, status, failed.Message)
            : new(ListenBrainzOutcome.Ok, true, status);
    }

    private ListenDto ToDto(ScrobbleEntry entry, ListenType type) => new()
    {
        ListenedAt = type == ListenType.PlayingNow ? null : entry.Timestamp,
        TrackMetadata = new TrackMetadataDto
        {
            ArtistName = entry.Artist,
            TrackName = entry.Track,
            ReleaseName = string.IsNullOrEmpty(entry.Album) ? null : entry.Album,
            AdditionalInfo = new AdditionalInfoDto
            {
                MediaPlayer = "Rambutan",
                SubmissionClient = "Rambutan",
                SubmissionClientVersion = _clientVersion,
                DurationMs = entry.DurationSeconds is { } seconds ? seconds * 1000L : null,
            },
        },
    };

    /// <summary>Maps a non-2xx status to an outcome; null for success.</summary>
    private static (ListenBrainzOutcome Outcome, string Message)? Classify(int status, string body)
    {
        if (status is >= 200 and < 300)
            return null;

        var detail = ErrorText(body);
        var message = detail is null ? $"HTTP {status}" : $"HTTP {status}: {detail}";
        return status switch
        {
            401 => (ListenBrainzOutcome.InvalidToken, message),
            429 => (ListenBrainzOutcome.RateLimited, message),
            >= 500 => (ListenBrainzOutcome.RetryLater, message),
            _ => (ListenBrainzOutcome.Rejected, message),
        };
    }

    /// <summary>The server's own error text when the body is its JSON error; null otherwise (an HTML page is not logged).</summary>
    private static string? ErrorText(string body)
    {
        try
        {
            var error = JsonSerializer.Deserialize(body, ListenBrainzJsonContext.Default.ErrorDto);
            var text = error?.Error ?? error?.Message;
            return text is { Length: > 300 } ? text[..300] : text;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task<(int Status, string Body, string? Failure)> SendAsync(
        HttpRequestMessage request, string token, CancellationToken ct)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", token);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using var response = await _http.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            return ((int)response.StatusCode, body, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return (0, "", $"network: {ex.Message}");
        }
    }
}
