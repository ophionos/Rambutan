using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Store;

/// <summary>
/// A local track's own lyrics: the <c>.lrc</c> file beside it (<c>song.flac</c>
/// → <c>song.lrc</c>), else an ID3v2 tag's synced lyrics (<c>SYLT</c>), else
/// the other lyrics in its tags. They are read each time
/// they're asked for and never cached, so an edited file shows on the next
/// play. Reading them sends nothing off the machine, so the online-lookup
/// setting doesn't apply. Word timing in them is kept: they are the user's own
/// files.
/// </summary>
/// <param name="pathFor">A local id's audio file; null when the index has none.</param>
/// <param name="tagLyrics">The lyrics texts in a file's tags, in their order;
/// empty for none.</param>
public sealed class LocalLyrics(
    Func<string, string?> pathFor,
    Func<string, CancellationToken, Task<IReadOnlyList<string>>> tagLyrics)
{
    public const string SidecarSourceName = "lrc-file";
    public const string TagsSourceName = "tags";

    /// <summary>A larger ID3v2 tag isn't read for its lyrics: what makes a tag
    /// large is its pictures.</summary>
    private const int MaxTagBytes = 32 * 1024 * 1024;

    /// <summary>Windows matches file names ignoring case already; elsewhere a
    /// name that differs in case needs the folder listed.</summary>
    private static readonly bool CaseSensitiveNames = !OperatingSystem.IsWindows();

    /// <summary>Null for a track that isn't local, and for one with no lyrics of
    /// its own. Throws only <see cref="OperationCanceledException"/>.</summary>
    public async Task<LyricsDocument?> TryGetAsync(string id, CancellationToken ct)
    {
        if (!LocalIds.IsLocal(id) || pathFor(id) is not { } path)
            return null;
        if (ReadSidecar(id, path) is { } text && Interpret([text], SidecarSourceName) is { } fromFile)
            return fromFile;
        if (await Task.Run(() => ReadSylt(id, path), ct) is { } synced)
            return synced;

        IReadOnlyList<string> texts;
        try
        {
            texts = await tagLyrics(path, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Write("lyrics", $"Couldn't read the tags' lyrics for {id}: {ex.GetType().Name}");
            return null;
        }
        return Interpret(texts, TagsSourceName);
    }

    /// <summary>
    /// The first text that is synced LRC, else the first with any words in it
    /// as plain lyrics. Taggers write LRC into the plain lyrics tags too, so
    /// the text decides, not the tag. Null when no text has words.
    /// </summary>
    public static LyricsDocument? Interpret(IReadOnlyList<string> texts, string source)
    {
        foreach (var text in texts)
        {
            if (LrcParser.Parse(text, source) is { IsSynced: true, Lines.Count: > 0 } synced)
                return synced;
        }
        foreach (var text in texts)
        {
            var plain = LyricsDocument.FromPlainText(text, source);
            if (plain.Lines.Any(l => l.Text.Length > 0))
                return plain;
        }
        return null;
    }

    private static string? ReadSidecar(string id, string audioPath)
    {
        try
        {
            return FindSidecar(audioPath) is { } lrc ? LrcFileText.Decode(File.ReadAllBytes(lrc)) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The type only: the message names the path, and with it the user's folders.
            AppLog.Write("lyrics", $"Couldn't read the .lrc file beside {id}: {ex.GetType().Name}");
            return null;
        }
    }

    /// <summary>The <c>.lrc</c> file by its exact name, else, where names are
    /// case-sensitive, one that differs only in case (<c>Song.LRC</c>); the folder
    /// is listed only on that miss. The maintainer's decision. Null for none.</summary>
    private static string? FindSidecar(string audioPath)
    {
        var exact = Path.ChangeExtension(audioPath, ".lrc");
        if (File.Exists(exact))
            return exact;
        if (!CaseSensitiveNames || Path.GetDirectoryName(exact) is not { Length: > 0 } folder)
            return null;
        var name = Path.GetFileName(exact);
        return Directory.EnumerateFiles(folder)
            .Where(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>The synced lyrics of an ID3v2 tag at the start of the file; null
    /// when it has none, or none this reads (<see cref="SyltParser"/>).</summary>
    private static LyricsDocument? ReadSylt(string id, string audioPath)
    {
        try
        {
            using var file = File.OpenRead(audioPath);
            Span<byte> header = stackalloc byte[10];
            if (file.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) < header.Length)
                return null;
            var length = SyltParser.TagLength(header);
            if (length == 0 || length > MaxTagBytes)
                return null;
            var tag = new byte[length];
            header.CopyTo(tag);
            var read = header.Length + file.ReadAtLeast(tag.AsSpan(header.Length), length - header.Length, throwOnEndOfStream: false);
            return SyltParser.Parse(tag.AsSpan(0, read), TagsSourceName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("lyrics", $"Couldn't read the tag of {id}: {ex.GetType().Name}");
            return null;
        }
    }
}
