using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Rambutan.Core.Remote;

namespace Rambutan.LocalApi;

internal sealed record QueueItemRequest(int Index, string? VideoId);

internal sealed record QueueMoveRequest(int From, string? VideoId, int To);

internal sealed record QueueAddRequest(string? VideoId, bool Next);

internal sealed record QueuePlayRequest(string? VideoId);

/// <param name="More">Pass as <c>?more=</c> for the next page; null on the last.</param>
internal sealed record SearchResponse(IReadOnlyList<RemoteSearchResult> Results, string? More);

/// <param name="Error">"offline" or "failed"; the client words it.</param>
internal sealed record ErrorResponse(string Error);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(RemoteQueue))]
[JsonSerializable(typeof(QueueItemRequest))]
[JsonSerializable(typeof(QueueMoveRequest))]
[JsonSerializable(typeof(QueueAddRequest))]
[JsonSerializable(typeof(QueuePlayRequest))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(ErrorResponse))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;

/// <summary>
/// The queue and search routes, behind the same token as the rest of
/// <c>/api/</c> (<see cref="RemoteEndpoints"/> checks it before any route runs).
/// Queue commands are accepted rather than done, like the player's: the queue's
/// new version arrives in the state, and the client asks for the queue again.
/// </summary>
internal static class LibraryEndpoints
{
    /// <summary>Longer than any real query; a cap on what goes to the catalog.</summary>
    internal const int MaxQueryLength = 200;

    private const int MaxVideoIdLength = 64;

    public static void Map(WebApplication app, IRemoteLibrary library)
    {
        // One search at a time reaches the catalog, however many phones ask.
        var searching = new SemaphoreSlim(1, 1);

        app.MapGet("/api/v1/queue", (RequestDelegate)(ctx =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return ctx.Response.WriteAsJsonAsync(library.GetQueue(), LibraryJsonContext.Default.RemoteQueue);
        }));

        app.MapPost("/api/v1/queue/jump", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LibraryJsonContext.Default.QueueItemRequest) is { } body && IsVideoId(body.VideoId))
                Accept(ctx, () => library.Execute(new RemoteQueueCommand.Jump(body.Index, body.VideoId!)));
            else
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        }));

        app.MapPost("/api/v1/queue/remove", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LibraryJsonContext.Default.QueueItemRequest) is { } body && IsVideoId(body.VideoId))
                Accept(ctx, () => library.Execute(new RemoteQueueCommand.Remove(body.Index, body.VideoId!)));
            else
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        }));

        app.MapPost("/api/v1/queue/move", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LibraryJsonContext.Default.QueueMoveRequest) is { } body && IsVideoId(body.VideoId))
                Accept(ctx, () => library.Execute(new RemoteQueueCommand.Move(body.From, body.VideoId!, body.To)));
            else
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        }));

        app.MapPost("/api/v1/queue/add", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LibraryJsonContext.Default.QueueAddRequest) is not { } body || !IsVideoId(body.VideoId))
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            else if (library.Add(body.VideoId!, body.Next))
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            else
                // Only songs the host's own searches found can be added.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        }));

        app.MapPost("/api/v1/queue/play", (RequestDelegate)(async ctx =>
        {
            if (await ReadAsync(ctx, LibraryJsonContext.Default.QueuePlayRequest) is not { } body || !IsVideoId(body.VideoId))
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
            else if (library.Play(body.VideoId!))
                ctx.Response.StatusCode = StatusCodes.Status202Accepted;
            else
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
        }));

        // ?q= for a new search; ?more= (from a response) for its next page.
        app.MapGet("/api/v1/search", (RequestDelegate)(async ctx =>
        {
            var query = ctx.Request.Query["q"].ToString().Trim();
            var more = ctx.Request.Query["more"].ToString();
            if (more.Length > MaxVideoIdLength || (more.Length == 0 && query.Length is 0 or > MaxQueryLength))
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            ctx.Response.Headers.CacheControl = "no-store";
            RemoteSearchOutcome? outcome;
            await searching.WaitAsync(ctx.RequestAborted);
            try
            {
                outcome = more.Length > 0
                    ? await library.MoreAsync(more, ctx.RequestAborted)
                    : await library.SearchAsync(query, ctx.RequestAborted);
            }
            finally
            {
                searching.Release();
            }

            if (outcome is null)
            {
                // A page key the host no longer keeps: the client searches again.
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (outcome.Failure is { } failure)
            {
                ctx.Response.StatusCode = StatusCodes.Status502BadGateway;
                await ctx.Response.WriteAsJsonAsync(
                    new ErrorResponse(failure == RemoteSearchFailure.Offline ? "offline" : "failed"),
                    LibraryJsonContext.Default.ErrorResponse);
                return;
            }
            await ctx.Response.WriteAsJsonAsync(new SearchResponse(outcome.Results, outcome.More), LibraryJsonContext.Default.SearchResponse);
        }));
    }

    private static bool IsVideoId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= MaxVideoIdLength;

    private static void Accept(HttpContext ctx, Action command)
    {
        command();
        ctx.Response.StatusCode = StatusCodes.Status202Accepted;
    }

    private static async Task<T?> ReadAsync<T>(HttpContext ctx, JsonTypeInfo<T> type)
        where T : class
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync(type, ctx.RequestAborted);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or BadHttpRequestException)
        {
            return null;
        }
    }
}
