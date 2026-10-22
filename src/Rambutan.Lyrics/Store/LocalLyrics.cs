using Rambutan.Core.Diagnostics;
using Rambutan.Core.Local;
using Rambutan.Lyrics.Models;
using Rambutan.Lyrics.Parsing;

namespace Rambutan.Lyrics.Store;

/// <summary>
/// A local track's own lyrics: the <c>.lrc</c> file beside it (<c>song.flac</c>
/// → <c>song.lrc</c>), else the lyrics in its tags. They are read each time
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

    /// <summary>Null for a track that isn't local, and for one with no lyrics of
    /// its own. Throws only <see cref="OperationCanceledException"/>.</summary>
    public async Task<LyricsDocument?> TryGetAsync(string id, CancellationToken ct)
    {
        if (!LocalIds.IsLocal(id) || pathFor(id) is not { } path)
            return null;
        if (ReadSidecar(id, path) is { } text && Interpret([text], SidecarSourceName) is { } fromFile)
            return fromFile;

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
        var lrc = Path.ChangeExtension(audioPath, ".lrc");
        try
        {
            return File.Exists(lrc) ? File.ReadAllText(lrc) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The type only: the message names the path, and with it the user's folders.
            AppLog.Write("lyrics", $"Couldn't read the .lrc file beside {id}: {ex.GetType().Name}");
            return null;
        }
    }
}
