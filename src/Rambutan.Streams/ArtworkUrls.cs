using System.Text.RegularExpressions;

namespace Rambutan.Streams;

/// <summary>What a download keeps of a cover: the URL to fetch, and the file
/// extension its bytes call for.</summary>
public static partial class ArtworkUrls
{
    /// <summary>The size a kept cover is fetched at: Now Playing draws art at up
    /// to ~320 px, so 544 (YTM's own largest album art) stays sharp at 2×.</summary>
    private const int KeptSize = 544;

    /// <summary>The size a row's cover is fetched at: the phone remote's rows
    /// draw art at 44 px, so 120 stays sharp at 2× and more.</summary>
    public const int RowSize = 120;

    /// <summary>
    /// The same cover at <see cref="KeptSize"/> when the URL names a smaller one.
    /// Google's image hosts take the size as a suffix (<c>=w120-h120-l90-rj</c>,
    /// <c>=s192</c>); a row may carry a 60 px cover that Now Playing would blow up.
    /// Other URLs (video thumbnails on i.ytimg.com) are kept as they are.
    /// </summary>
    public static string Largest(string url) => Resized(url, KeptSize, grow: true);

    /// <summary>
    /// The same cover at <see cref="RowSize"/> when the URL names a larger one, or
    /// no size at all (the host then sends its own default, often 512 px). Other
    /// URLs are kept as they are.
    /// </summary>
    public static string ForRow(string url) => Resized(url, RowSize, grow: false);

    private static string Resized(string url, int size, bool grow)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            !(uri.Host.EndsWith("googleusercontent.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith("ggpht.com", StringComparison.OrdinalIgnoreCase)))
        {
            return url;
        }

        bool Change(int now) => grow ? now < size : now > size;

        var sized = WidthHeight().Match(url);
        if (sized.Success)
        {
            return Change(int.Parse(sized.Groups["w"].ValueSpan))
                ? string.Concat(url.AsSpan(0, sized.Index), $"=w{size}-h{size}", sized.Groups["rest"].Value)
                : url;
        }

        var square = Square().Match(url);
        if (square.Success)
        {
            return Change(int.Parse(square.Groups["s"].ValueSpan))
                ? string.Concat(url.AsSpan(0, square.Index), $"=s{size}", square.Groups["rest"].Value)
                : url;
        }

        return grow || uri.AbsolutePath.Contains('=') || uri.Query.Length > 0 ? url : $"{url}=w{size}-h{size}";
    }

    public static string ExtensionFor(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF]) ? ".jpg"
        : bytes.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) ? ".png"
        : bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes[8..12].SequenceEqual("WEBP"u8) ? ".webp"
        : ".img";

    [GeneratedRegex(@"=w(?<w>\d+)-h(?<h>\d+)(?<rest>[^=/]*)$")]
    private static partial Regex WidthHeight();

    [GeneratedRegex(@"=s(?<s>\d+)(?<rest>[^=/]*)$")]
    private static partial Regex Square();
}
