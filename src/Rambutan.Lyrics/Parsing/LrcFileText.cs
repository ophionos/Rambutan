using System.Text;
using System.Text.Unicode;

namespace Rambutan.Lyrics.Parsing;

/// <summary>
/// The text of a lyrics file from its bytes. A byte order mark names the
/// encoding. Without one, bytes that are valid UTF-8 are UTF-8, and anything
/// else is in the legacy code page older <c>.lrc</c> files were written in: the
/// system's ANSI code page on Windows (GBK, Big5, Shift-JIS, Windows-1252…),
/// and Windows-1252 elsewhere, which has none. The maintainer's decision.
/// </summary>
public static class LrcFileText
{
    private const int Windows1252 = 1252;
    private const int Utf8CodePage = 65001;

    private static readonly Lazy<Encoding> Legacy = new(LegacyEncoding);

    private static ReadOnlySpan<byte> Utf8Bom => [0xEF, 0xBB, 0xBF];
    private static ReadOnlySpan<byte> Utf32LeBom => [0xFF, 0xFE, 0x00, 0x00];
    private static ReadOnlySpan<byte> Utf16LeBom => [0xFF, 0xFE];
    private static ReadOnlySpan<byte> Utf16BeBom => [0xFE, 0xFF];

    public static string Decode(ReadOnlySpan<byte> bytes) => Decode(bytes, Legacy.Value);

    /// <summary>As <see cref="Decode(ReadOnlySpan{byte})"/>, with the legacy
    /// encoding given.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Encoding legacy)
    {
        if (bytes.StartsWith(Utf8Bom))
            return Encoding.UTF8.GetString(bytes[Utf8Bom.Length..]);
        // Before UTF-16's: UTF-32's mark starts with it.
        if (bytes.StartsWith(Utf32LeBom))
            return Encoding.UTF32.GetString(bytes[Utf32LeBom.Length..]);
        if (bytes.StartsWith(Utf16LeBom))
            return Encoding.Unicode.GetString(bytes[Utf16LeBom.Length..]);
        if (bytes.StartsWith(Utf16BeBom))
            return Encoding.BigEndianUnicode.GetString(bytes[Utf16BeBom.Length..]);
        return Utf8.IsValid(bytes) ? Encoding.UTF8.GetString(bytes) : legacy.GetString(bytes);
    }

    private static Encoding LegacyEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        // Code page 0 is the system's ANSI code page (GetACP), not the user's
        // display language; there is none outside Windows.
        var codePage = OperatingSystem.IsWindows()
            ? CodePagesEncodingProvider.Instance.GetEncoding(0)?.CodePage ?? Windows1252
            : Windows1252;
        try
        {
            // Windows' "Use Unicode UTF-8" setting makes UTF-8 the ANSI code page,
            // but these bytes are already known not to be UTF-8.
            return Encoding.GetEncoding(codePage == Utf8CodePage ? Windows1252 : codePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return Encoding.GetEncoding(Windows1252);
        }
    }
}
