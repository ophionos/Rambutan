using System.Buffers.Binary;
using System.Text;

namespace Rambutan.Streams.Tests;

public sealed class Mp4FreeformTagsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    public Mp4FreeformTagsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static byte[] Box(string type, params byte[][] children)
    {
        byte[] box = [0, 0, 0, 0, .. Encoding.Latin1.GetBytes(type), .. children.SelectMany(c => c)];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        return box;
    }

    private static readonly byte[] Title = Box("©nam", Box("data", [0, 0, 0, 1, 0, 0, 0, 0, .. "Hello"u8]));

    private static byte[] Moov(bool withTags = true) =>
        Box("moov", Box("mvhd", new byte[100]),
            withTags
                ? Box("udta", Box("meta", [0, 0, 0, 0], Box("hdlr", new byte[25]), Box("ilst", Title)))
                : Box("trak", new byte[8]));

    private string Write(params byte[][] boxes)
    {
        var path = Path.Combine(_dir, "a.m4a");
        File.WriteAllBytes(path, [.. boxes.SelectMany(b => b)]);
        return path;
    }

    /// <summary>The boxes along the path, read from the file: each box's size
    /// must cover exactly its children.</summary>
    private static List<(string Type, byte[] Box)> Children(byte[] box, int at)
    {
        var children = new List<(string, byte[])>();
        while (at < box.Length)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(box.AsSpan(at));
            Assert.InRange(size, 8, box.Length - at);
            children.Add((Encoding.Latin1.GetString(box, at + 4, 4), box[at..(at + size)]));
            at += size;
        }
        return children;
    }

    [Fact]
    public void The_tags_go_at_the_end_of_the_tag_list_and_every_size_grows_with_them()
    {
        byte[] ftyp = Box("ftyp", new byte[16]), mdat = Box("mdat", new byte[64]);
        var path = Write(ftyp, mdat, Moov());

        Assert.True(Mp4FreeformTags.Add(path, [("YOUTUBE_VIDEO_ID", "YQHsXMglC9A"), ("YOUTUBE_SONG_ID", "khnokW3Mw24")]));

        var file = File.ReadAllBytes(path);
        var top = Children(file, 0);
        Assert.Equal(["ftyp", "mdat", "moov"], top.Select(b => b.Type));
        Assert.Equal(mdat, top[1].Box); // the audio didn't move
        var udta = Children(top[2].Box, 8).Single(b => b.Type == "udta").Box;
        var meta = Children(udta, 8).Single().Box;
        var ilst = Children(meta, 12).Single(b => b.Type == "ilst").Box;
        var items = Children(ilst, 8);
        Assert.Equal(Title, items[0].Box);
        Assert.Equal(["----", "----"], items.Skip(1).Select(i => i.Type));
        var parts = Children(items[1].Box, 8);
        Assert.Equal(["mean", "name", "data"], parts.Select(p => p.Type));
        Assert.Equal("com.apple.iTunes", Encoding.UTF8.GetString(parts[0].Box, 12, parts[0].Box.Length - 12));
        Assert.Equal("YOUTUBE_VIDEO_ID", Encoding.UTF8.GetString(parts[1].Box, 12, parts[1].Box.Length - 12));
        Assert.Equal("YQHsXMglC9A", Encoding.UTF8.GetString(parts[2].Box, 16, parts[2].Box.Length - 16));
    }

    [Fact]
    public void A_moov_before_the_audio_is_left_alone()
    {
        // Growing it would move the audio that the sample offsets point at.
        byte[] content = [.. Box("ftyp", new byte[16]), .. Moov(), .. Box("mdat", new byte[64])];
        var path = Write(content);

        Assert.False(Mp4FreeformTags.Add(path, [("YOUTUBE_VIDEO_ID", "YQHsXMglC9A")]));

        Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_file_without_a_tag_list_is_left_alone()
    {
        byte[] content = [.. Box("ftyp", new byte[16]), .. Box("mdat", new byte[64]), .. Moov(withTags: false)];
        var path = Write(content);

        Assert.False(Mp4FreeformTags.Add(path, [("YOUTUBE_VIDEO_ID", "YQHsXMglC9A")]));

        Assert.Equal(content, File.ReadAllBytes(path));
    }

    [Fact]
    public void A_box_whose_size_runs_past_the_file_is_left_alone()
    {
        byte[] content = [.. Box("ftyp", new byte[16]), .. Moov()];
        BinaryPrimitives.WriteUInt32BigEndian(content.AsSpan(24), 100_000);
        var path = Write(content);

        Assert.False(Mp4FreeformTags.Add(path, [("YOUTUBE_VIDEO_ID", "YQHsXMglC9A")]));

        Assert.Equal(content, File.ReadAllBytes(path));
    }
}
