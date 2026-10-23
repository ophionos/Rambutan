using System.Buffers.Binary;
using System.Text;
using Rambutan.Core.Diagnostics;

namespace Rambutan.Streams;

/// <summary>
/// Adds iTunes freeform tags (<c>----</c> atoms under <c>com.apple.iTunes</c>)
/// to an .m4a that ffmpeg wrote. ffmpeg's MP4 muxers write only the tags they
/// know; <c>-movflags use_metadata_tags</c> writes the others, but moves every
/// tag, the title too, into QuickTime <c>mdta</c> keys that most players don't
/// read. The new atoms go at the end of <c>moov/udta/meta/ilst</c>.
/// </summary>
internal static class Mp4FreeformTags
{
    private const string Mean = "com.apple.iTunes";

    /// <summary>
    /// Adds the tags to the file in place. Only when <c>moov</c> is the file's
    /// last box, as ffmpeg writes it without <c>faststart</c>: growing it then
    /// moves no audio, so the sample offsets stay right. False, with the file
    /// unchanged, when it isn't laid out so or has no tag list.
    /// </summary>
    public static bool Add(string path, IReadOnlyList<(string Name, string Value)> tags)
    {
        if (tags.Count == 0)
            return true;
        using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        if (FindLastMoov(file) is not { } moovAt)
        {
            AppLog.Write("export", "an .m4a's moov box isn't last; its YouTube ids weren't written");
            return false;
        }
        var moov = new byte[file.Length - moovAt];
        file.Position = moovAt;
        file.ReadExactly(moov);
        if (WithItems(moov, Items(tags)) is not { } changed)
        {
            AppLog.Write("export", "an .m4a has no tag list; its YouTube ids weren't written");
            return false;
        }
        file.Position = moovAt;
        file.Write(changed);
        file.SetLength(moovAt + changed.Length);
        return true;
    }

    /// <summary>The <c>moov</c> box with the items added to its tag list; null
    /// when it has no <c>udta/meta/ilst</c> or a box's size doesn't fit.</summary>
    internal static byte[]? WithItems(byte[] moov, byte[] items) =>
        Change(moov, 8, "udta", udta =>
            Change(udta, 8, "meta", meta =>
                // meta is a full box: a version and flags come before its children.
                Change(meta, 12, "ilst", ilst => Sized([.. ilst, .. items]))));

    /// <summary>The <c>----</c> atoms for the tags, one each, as UTF-8 text.</summary>
    internal static byte[] Items(IReadOnlyList<(string Name, string Value)> tags) =>
    [
        .. tags.SelectMany(t => Box("----",
        [
            .. Box("mean", [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(Mean)]),
            .. Box("name", [0, 0, 0, 0, .. Encoding.UTF8.GetBytes(t.Name)]),
            // Type 1 is UTF-8 text; then a locale of 0.
            .. Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. Encoding.UTF8.GetBytes(t.Value)]),
        ])),
    ];

    /// <summary>The top-level <c>moov</c>'s offset when it ends the file; null otherwise.</summary>
    private static long? FindLastMoov(FileStream file)
    {
        Span<byte> header = stackalloc byte[16];
        long at = 0;
        long? moov = null;
        while (at + 8 <= file.Length)
        {
            file.Position = at;
            file.ReadExactly(header[..8]);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            if (size == 1)
            {
                // A 64-bit size follows the type (a large mdat).
                file.ReadExactly(header[8..16]);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header[8..]);
            }
            else if (size == 0)
                size = file.Length - at;
            if (size < 8 || at + size > file.Length)
                return null;
            moov = header[4..8].SequenceEqual("moov"u8) ? at : null;
            at += size;
        }
        return at == file.Length ? moov : null;
    }

    /// <summary>The box with its first child of <paramref name="type"/> replaced
    /// by what <paramref name="change"/> makes of it, and its size set again;
    /// null when there is no such child or a size doesn't fit.</summary>
    private static byte[]? Change(byte[] box, int childrenAt, string type, Func<byte[], byte[]?> change)
    {
        var name = Encoding.ASCII.GetBytes(type);
        for (var at = childrenAt; at + 8 <= box.Length;)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(at)), int.MaxValue);
            if (size < 8 || at + size > box.Length)
                return null;
            if (box.AsSpan(at + 4, 4).SequenceEqual(name))
            {
                if (change(box[at..(at + size)]) is not { } changed)
                    return null;
                return Sized([.. box.AsSpan(0, at), .. changed, .. box.AsSpan(at + size)]);
            }
            at += size;
        }
        return null;
    }

    private static byte[] Box(string type, byte[] content) =>
        Sized([0, 0, 0, 0, .. Encoding.ASCII.GetBytes(type), .. content]);

    /// <summary>The box with its size field set to its length.</summary>
    private static byte[] Sized(byte[] box)
    {
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        return box;
    }
}
