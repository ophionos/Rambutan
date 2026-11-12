using System;
using System.IO;
using Rambutan.App.Services;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.Tests;

public sealed class SidebarPinStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private string FilePath => Path.Combine(_dir, "sidebar-pins.json");

    [Fact]
    public void NoFile_GivesNoPins_ThatCanBeChanged()
    {
        var loaded = new SidebarPinStore(_dir).Load();

        Assert.Empty(loaded.Pins);
        Assert.False(loaded.ReadOnly);
    }

    [Fact]
    public void RoundTrips_EveryKind_InOrder()
    {
        SidebarPin[] pins =
        [
            new(PinKind.Playlist, "PL1", "Late Night Coding", "https://lh3/pl"),
            new(PinKind.Album, "MPREb_a", "Discovery", "https://lh3/a"),
            new(PinKind.LocalPlaylist, "0f3a", "On this computer"),
        ];

        new SidebarPinStore(_dir).Save(pins);
        var loaded = new SidebarPinStore(_dir).Load();

        Assert.Equal(pins, loaded.Pins);
        Assert.False(loaded.ReadOnly);
    }

    [Fact]
    public void TheFile_IsVersioned_AndNamesItsKinds()
    {
        new SidebarPinStore(_dir).Save([new(PinKind.LocalPlaylist, "0f3a", "Mine")]);

        Assert.Equal(
            """{"version":1,"pins":[{"kind":"localPlaylist","id":"0f3a","title":"Mine"}]}""",
            File.ReadAllText(FilePath));
    }

    [Fact]
    public void ANewerFile_IsLeftAsItIs_AndReadOnly()
    {
        Directory.CreateDirectory(_dir);
        const string newer = """{"version":2,"pins":[{"kind":"album","id":"a","title":"A","pinnedOn":"x"}]}""";
        File.WriteAllText(FilePath, newer);

        var loaded = new SidebarPinStore(_dir).Load();

        Assert.Empty(loaded.Pins);
        Assert.True(loaded.ReadOnly);
        Assert.Equal(newer, File.ReadAllText(FilePath));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("null")]
    [InlineData("""{"version":0,"pins":[]}""")]
    public void AnUnreadableFile_IsSetAside_AndReadAsEmpty(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, content);

        var loaded = new SidebarPinStore(_dir).Load();

        Assert.Empty(loaded.Pins);
        Assert.False(loaded.ReadOnly);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(content, File.ReadAllText(FilePath + ".unreadable"));
    }

    [Fact]
    public void AnEntryThisBuildCantRead_IsDropped_NotTheFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath,
            """{"version":1,"pins":[{"kind":"artist","id":"UC1","title":"Someone"},null,{"kind":"album","id":"","title":"No id"},{"kind":"album","id":"a","title":"A"}]}""");

        var loaded = new SidebarPinStore(_dir).Load();

        Assert.Equal([new SidebarPin(PinKind.Album, "a", "A")], loaded.Pins);
        Assert.False(loaded.ReadOnly);
    }
}
