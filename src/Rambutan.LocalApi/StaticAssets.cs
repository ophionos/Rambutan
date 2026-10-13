using System.Collections.Frozen;
using System.Reflection;
using Microsoft.AspNetCore.Http;

namespace Rambutan.LocalApi;

/// <summary>
/// The web apps' build output, embedded in this assembly under <c>wwwroot/</c>,
/// served by path. No file provider or static-files middleware: a lookup in the
/// manifest is all it takes, and it stays trim- and AOT-safe.
/// </summary>
internal static class StaticAssets
{
    private static readonly Assembly Assembly = typeof(StaticAssets).Assembly;

    /// <summary>Request path ("remote/index.html") → resource name. Names are
    /// normalized to forward slashes: RecursiveDir has backslashes on Windows.</summary>
    private static readonly FrozenDictionary<string, string> Resources = Assembly
        .GetManifestResourceNames()
        .Where(n => n.StartsWith("wwwroot/", StringComparison.Ordinal) || n.StartsWith("wwwroot\\", StringComparison.Ordinal))
        .ToFrozenDictionary(n => n["wwwroot/".Length..].Replace('\\', '/'), n => n, StringComparer.Ordinal);

    public static bool Has(string path) => Resources.ContainsKey(path);

    public static async Task ServeAsync(HttpContext ctx, string path)
    {
        if (!Resources.TryGetValue(path, out var name) || Assembly.GetManifestResourceStream(name) is not { } stream)
        {
            ctx.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (stream)
        {
            ctx.Response.ContentType = ContentType(path);
            ctx.Response.ContentLength = stream.Length;
            // Vite names its assets by content hash; the page itself must be
            // fetched fresh, so a new build reaches the phone.
            ctx.Response.Headers.CacheControl = path.Contains("/assets/", StringComparison.Ordinal)
                ? "public, max-age=31536000, immutable"
                : "no-cache";
            if (HttpMethods.IsHead(ctx.Request.Method))
                return;
            await stream.CopyToAsync(ctx.Response.Body, ctx.RequestAborted);
        }
    }

    internal static string ContentType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8",
        ".js" or ".mjs" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".json" => "application/json; charset=utf-8",
        ".webmanifest" => "application/manifest+json",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".ico" => "image/x-icon",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        _ => "application/octet-stream",
    };
}
