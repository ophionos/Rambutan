using System.Text.Json.Serialization;

namespace Rambutan.InnerTube;

// Request payloads sent to youtubei/v1 endpoints. Response parsing deliberately
// does NOT use typed DTOs: InnerTube responses are deep, undocumented, and drift —
// parsers traverse JsonDocument instead (reflection-free, AOT-safe) and are pinned
// by recorded-fixture tests.

internal sealed record SearchRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("params")] string? Params);

internal sealed record SearchSuggestionsRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("input")] string Input);

internal sealed record BrowseRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("browseId")] string? BrowseId,
    [property: JsonPropertyName("continuation")] string? Continuation = null,
    [property: JsonPropertyName("params")] string? Params = null);

internal sealed record RateRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("target")] RateTarget Target);

internal sealed record RateTarget(
    [property: JsonPropertyName("videoId")] string? VideoId = null,
    [property: JsonPropertyName("playlistId")] string? PlaylistId = null);

internal sealed record FeedbackRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("feedbackTokens")] IReadOnlyList<string> FeedbackTokens);

internal sealed record SubscriptionRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("channelIds")] IReadOnlyList<string> ChannelIds);

internal sealed record CreatePlaylistRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("privacyStatus")] string PrivacyStatus);

internal sealed record DeletePlaylistRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("playlistId")] string PlaylistId);

internal sealed record EditPlaylistRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("playlistId")] string PlaylistId,
    [property: JsonPropertyName("actions")] IReadOnlyList<PlaylistEditAction> Actions);

internal sealed record PlaylistEditAction(
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("addedVideoId")] string? AddedVideoId = null,
    [property: JsonPropertyName("removedVideoId")] string? RemovedVideoId = null,
    [property: JsonPropertyName("setVideoId")] string? SetVideoId = null,
    [property: JsonPropertyName("playlistName")] string? PlaylistName = null,
    [property: JsonPropertyName("movedSetVideoIdSuccessor")] string? MovedSetVideoIdSuccessor = null);

// The watch-queue request the web player sends for radio/autoplay. The tuner
// setting and panel flags match the official client; without them the response
// is a bare single-video queue instead of an automix.
internal sealed record NextRequest(
    [property: JsonPropertyName("context")] RequestContext Context,
    [property: JsonPropertyName("videoId")] string VideoId,
    [property: JsonPropertyName("playlistId")] string PlaylistId,
    [property: JsonPropertyName("enablePersistentPlaylistPanel")] bool EnablePersistentPlaylistPanel,
    [property: JsonPropertyName("isAudioOnly")] bool IsAudioOnly,
    [property: JsonPropertyName("tunerSettingValue")] string TunerSettingValue);

internal sealed record RequestContext(
    [property: JsonPropertyName("client")] ClientInfo Client);

internal sealed record ClientInfo(
    [property: JsonPropertyName("clientName")] string ClientName,
    [property: JsonPropertyName("clientVersion")] string ClientVersion,
    [property: JsonPropertyName("hl")] string Hl,
    [property: JsonPropertyName("gl")] string Gl,
    [property: JsonPropertyName("visitorData")] string? VisitorData = null);

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SearchRequest))]
[JsonSerializable(typeof(SearchSuggestionsRequest))]
[JsonSerializable(typeof(BrowseRequest))]
[JsonSerializable(typeof(RateRequest))]
[JsonSerializable(typeof(FeedbackRequest))]
[JsonSerializable(typeof(SubscriptionRequest))]
[JsonSerializable(typeof(CreatePlaylistRequest))]
[JsonSerializable(typeof(DeletePlaylistRequest))]
[JsonSerializable(typeof(EditPlaylistRequest))]
[JsonSerializable(typeof(NextRequest))]
internal sealed partial class InnerTubeJsonContext : JsonSerializerContext;
