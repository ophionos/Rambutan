using Microsoft.AspNetCore.Http;

namespace Rambutan.LocalApi;

/// <summary>
/// Serves a cover for the remote's and the overlay's artwork routes. Each route
/// decides which videoIds it may serve; this fetches through the host's own
/// <see cref="LocalApiOptions.Artwork"/> and writes the image, or 404.
/// </summary>
internal static class ArtworkResponse
{
    public static async Task WriteAsync(
        HttpContext ctx, Func<string, ArtworkSize, CancellationToken, Task<RemoteArtwork?>>? artwork, string? videoId, ArtworkSize size = ArtworkSize.Full)
    {
        RemoteArtwork? image = null;
        if (artwork is not null && videoId is not null)
        {
            try
            {
                image = await artwork(videoId, size, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                image = null;
            }
        }

        if (image is null)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        ctx.Response.ContentType = image.ContentType;
        ctx.Response.Headers.CacheControl = "private, max-age=3600";
        await ctx.Response.Body.WriteAsync(image.Bytes, ctx.RequestAborted);
    }
}
