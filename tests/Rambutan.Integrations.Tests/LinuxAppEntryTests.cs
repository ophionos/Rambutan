using System.Buffers.Binary;
using Rambutan.Integrations.Linux;

namespace Rambutan.Integrations.Tests;

/// <summary>The desktop file and icons a Linux dock finds the window's icon
/// through, written to a temporary folder, never the user's.</summary>
public sealed class LinuxAppEntryTests : IDisposable
{
    private const string AppImage = "/home/u/Applications/Rambutan.AppImage";
    private const string DevBuild = "/home/u/src/Rambutan.App";

    private readonly string _home = Directory.CreateTempSubdirectory("rambutan-entry-").FullName;

    public void Dispose() => Directory.Delete(_home, recursive: true);

    private string EntryPath => Path.Combine(_home, "applications", LinuxAppEntry.DesktopFile);

    private string IconPath(int size) =>
        Path.Combine(_home, "icons", "hicolor", $"{size}x{size}", "apps", "rambutan.png");

    private static byte[] Png(byte marker) => [0x89, (byte)'P', (byte)'N', (byte)'G', marker];

    /// <summary>An .ico with these entries, in order; a width of 256 is stored as 0.</summary>
    private static byte[] Ico(params (int Size, byte[] Image)[] entries)
    {
        var header = new byte[6 + 16 * entries.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), (ushort)entries.Length);
        var offset = header.Length;
        for (var i = 0; i < entries.Length; i++)
        {
            var dir = header.AsSpan(6 + 16 * i, 16);
            dir[0] = dir[1] = (byte)(entries[i].Size == 256 ? 0 : entries[i].Size);
            BinaryPrimitives.WriteUInt32LittleEndian(dir[8..], (uint)entries[i].Image.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(dir[12..], (uint)offset);
            offset += entries[i].Image.Length;
        }
        return [.. header, .. entries.SelectMany(e => e.Image)];
    }

    [Fact]
    public void Entry_IsHidden_AndNamesTheIconByTheAppId()
    {
        var entry = LinuxAppEntry.Entry(AppImage)!;

        Assert.Contains($"Exec={AppImage}\n", entry);
        Assert.Contains("Icon=rambutan\n", entry);
        Assert.Contains("NoDisplay=true\n", entry);
        Assert.DoesNotContain("\r", entry);
        Assert.Equal("rambutan.desktop", LinuxAppEntry.DesktopFile);
    }

    [Fact]
    public void Entry_QuotesAPathWithSpaces_AndRefusesOneItCantCarry()
    {
        Assert.Contains("Exec=\"/home/u/My Apps/Rambutan.AppImage\"\n", LinuxAppEntry.Entry("/home/u/My Apps/Rambutan.AppImage"));
        Assert.Null(LinuxAppEntry.Entry("/home/u/100%/Rambutan.AppImage"));
    }

    [Fact]
    public void PngImages_ReadsEachSize_With256StoredAsZero()
    {
        var images = LinuxAppEntry.PngImages(Ico((16, Png(1)), (256, Png(2))));

        Assert.Equal([16, 256], images.Select(i => i.Size));
        Assert.Equal(Png(2), images[1].Png);
    }

    [Fact]
    public void PngImages_SkipsBitmaps_AndEntriesPastTheEnd()
    {
        var ico = Ico((32, [0x28, 0, 0, 0]), (48, Png(3)));
        Assert.Equal([48], LinuxAppEntry.PngImages(ico).Select(i => i.Size));

        // Cut short inside the PNG: its entry points past the end.
        Assert.Empty(LinuxAppEntry.PngImages(ico.AsSpan(0, ico.Length - 1)));
        Assert.Empty(LinuxAppEntry.PngImages([1, 2, 3]));
    }

    [Fact]
    public void Write_PutsTheIconsInHicolor_AndTheEntryInApplications()
    {
        Assert.True(LinuxAppEntry.Write(_home, AppImage, owns: true, Ico((48, Png(1)), (256, Png(2)))));

        Assert.Equal(Png(1), File.ReadAllBytes(IconPath(48)));
        Assert.Equal(Png(2), File.ReadAllBytes(IconPath(256)));
        Assert.Equal(LinuxAppEntry.Entry(AppImage), File.ReadAllText(EntryPath));
    }

    [Fact]
    public void Write_WritesNothing_WhenAllIsCurrent()
    {
        var ico = Ico((48, Png(1)));
        LinuxAppEntry.Write(_home, AppImage, owns: true, ico);

        Assert.False(LinuxAppEntry.Write(_home, AppImage, owns: true, ico));
    }

    [Fact]
    public void Write_TheAppImage_TakesTheEntryBackFromABuild()
    {
        var ico = Ico((48, Png(1)));
        LinuxAppEntry.Write(_home, DevBuild, owns: false, ico);

        Assert.True(LinuxAppEntry.Write(_home, AppImage, owns: true, ico));
        Assert.Equal(LinuxAppEntry.Entry(AppImage), File.ReadAllText(EntryPath));
    }

    [Fact]
    public void Write_ABuild_LeavesTheAppImagesEntry_ButUpdatesTheIcons()
    {
        LinuxAppEntry.Write(_home, AppImage, owns: true, Ico((48, Png(1))));

        Assert.False(LinuxAppEntry.Write(_home, DevBuild, owns: false, Ico((48, Png(1)))));
        Assert.True(LinuxAppEntry.Write(_home, DevBuild, owns: false, Ico((48, Png(9)))));
        Assert.Equal(LinuxAppEntry.Entry(AppImage), File.ReadAllText(EntryPath));
        Assert.Equal(Png(9), File.ReadAllBytes(IconPath(48)));
    }

    [Fact]
    public void Write_ABuild_WritesAMissingEntry()
    {
        Assert.True(LinuxAppEntry.Write(_home, DevBuild, owns: false, Ico((48, Png(1)))));
        Assert.Equal(LinuxAppEntry.Entry(DevBuild), File.ReadAllText(EntryPath));
    }

    [Fact]
    public void Write_RefusesAPathTheEntryCantCarry()
    {
        Assert.False(LinuxAppEntry.Write(_home, "/home/u/100%/Rambutan.AppImage", owns: true, Ico((48, Png(1)))));
        Assert.False(File.Exists(EntryPath));
    }
}
