using Rambutan.Core.Models;

namespace Rambutan.Core.Downloads;

/// <summary>What an export writes: the download's own audio in a standard
/// container, or MP3 for players that take nothing else.</summary>
public enum ExportFormat
{
    /// <summary>Opus → .ogg, AAC → .m4a: a remux, so no quality is lost.</summary>
    Original,

    /// <summary>Re-encoded, so some quality is lost; the setting says so.</summary>
    Mp3,
}

/// <summary>The container an exported file gets, and whether reaching it means
/// re-encoding the audio.</summary>
public readonly record struct ExportTarget(string Extension, bool Transcodes)
{
    public static readonly ExportTarget Ogg = new(".ogg", false);
    public static readonly ExportTarget M4a = new(".m4a", false);
    public static readonly ExportTarget Mp3 = new(".mp3", true);

    /// <summary>A codec with no standard container of its own (none today) goes
    /// to MP3 rather than into a file other players can't open.</summary>
    public static ExportTarget For(string audioCodec, ExportFormat format) =>
        format is ExportFormat.Mp3 ? Mp3
        : audioCodec.StartsWith("opus", StringComparison.OrdinalIgnoreCase) ? Ogg
        : audioCodec.StartsWith("mp4a", StringComparison.OrdinalIgnoreCase) ? M4a
        : Mp3;
}

public sealed record ExportJob(
    TrackInfo Track, string AudioPath, string AudioCodec, string? ArtworkPath,
    ExportTarget Target, string OutputPath);

/// <param name="AudioPath">Null when the download's file is gone.</param>
public sealed record ExportSource(TrackInfo Track, string? AudioPath, string AudioCodec, string? ArtworkPath);

/// <summary>Writes one exported file. Throws when it can't, and leaves no partial
/// file behind.</summary>
public interface IExportWriter
{
    Task WriteAsync(ExportJob job, CancellationToken ct);
}

/// <summary>How an export went. <paramref name="Failed"/> names the tracks that
/// weren't written; a cancelled export counts what was written before it stopped.</summary>
public sealed record ExportResult(int Written, IReadOnlyList<TrackInfo> Failed, bool Cancelled);

/// <summary>
/// Copies downloads out as ordinary files, one at a time, into one folder. It
/// never downloads again: a download without its file is reported as failed.
/// Names are "Artist - Title" and never replace a file already there, the user's
/// own or an earlier export's: a clash gets " (2)", " (3)" and so on.
/// </summary>
public sealed class DownloadExport(IExportWriter writer, Func<string, bool> fileExists)
{
    /// <summary>Longest file name stem; leaves room for the folder within the
    /// classic 260-character Windows path limit.</summary>
    public const int MaxStemLength = 120;

    /// <param name="progress">Reports how many are done (written or failed).</param>
    public async Task<ExportResult> RunAsync(
        IReadOnlyList<ExportSource> downloads,
        string folder, ExportFormat format, IProgress<int>? progress, CancellationToken ct)
    {
        var written = 0;
        var failed = new List<TrackInfo>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < downloads.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return new ExportResult(written, failed, Cancelled: true);

            var (track, audio, codec, artwork) = downloads[i];
            if (audio is null)
            {
                failed.Add(track);
                progress?.Report(i + 1);
                continue;
            }

            var target = ExportTarget.For(codec, format);
            var output = UniquePath(folder, FileStem(track), target.Extension, taken);
            taken.Add(output);
            try
            {
                await writer.WriteAsync(new ExportJob(track, audio, codec, artwork, target, output), ct);
                written++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return new ExportResult(written, failed, Cancelled: true);
            }
            catch (Exception ex)
            {
                Diagnostics.AppLog.Write("export", $"couldn't export {track.VideoId}: {ex.Message}");
                failed.Add(track);
            }
            progress?.Report(i + 1);
        }

        return new ExportResult(written, failed, Cancelled: false);
    }

    private string UniquePath(string folder, string stem, string extension, HashSet<string> taken)
    {
        for (var n = 1; ; n++)
        {
            var name = n == 1 ? stem + extension : $"{stem} ({n}){extension}";
            var path = Path.Combine(folder, name);
            if (!taken.Contains(path) && !fileExists(path))
                return path;
        }
    }

    /// <summary>"Artist - Title", made safe as a file name on Windows and Linux
    /// alike (an export folder may be a shared drive or a USB stick).</summary>
    public static string FileStem(TrackInfo track)
    {
        var artist = track.ArtistDisplay;
        var raw = string.IsNullOrWhiteSpace(artist) ? track.Title : $"{artist} - {track.Title}";
        return SafeName(raw);
    }

    /// <summary>The text made safe as a file name on Windows and Linux alike.</summary>
    public static string SafeName(string raw)
    {
        var chars = raw.Select(c => c < 32 || InvalidChars.Contains(c) ? '_' : c).ToArray();
        var name = new string(chars).Trim();
        if (name.Length > MaxStemLength)
        {
            // Never split a surrogate pair at the cut.
            var cut = char.IsLowSurrogate(name[MaxStemLength]) ? MaxStemLength - 1 : MaxStemLength;
            name = name[..cut].TrimEnd();
        }

        // Windows drops trailing dots and spaces, and reserves some device names.
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0)
            return "_";
        var device = name.Split('.')[0].TrimEnd();
        return ReservedNames.Contains(device) ? "_" + name : name;
    }

    private static readonly HashSet<char> InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };
}
