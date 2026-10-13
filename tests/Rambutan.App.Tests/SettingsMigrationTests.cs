using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>Older settings files read into the current model (CLAUDE.md: a change
/// of meaning needs a migration).</summary>
public sealed class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private AppSettings LoadFile(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "settings.json"), json);
        return new AppSettingsStore(_dir).Load();
    }

    [Fact]
    public void AFreshInstall_LooksNothingUpOnline()
    {
        var settings = new AppSettingsStore(_dir).Load();

        Assert.False(settings.ShowLyrics);
        Assert.False(settings.LookUpLyricsOnline);
        Assert.Equal(AppSettings.CurrentVersion, settings.Version);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Version1_KeepsLookupsForWhoeverHadLyricsOn(bool showLyrics)
    {
        // In version 1 the one lyrics switch also looked tracks up on LRCLIB.
        var settings = LoadFile($$"""{ "Version": 1, "ShowLyrics": {{(showLyrics ? "true" : "false")}} }""");

        Assert.Equal(showLyrics, settings.ShowLyrics);
        Assert.Equal(showLyrics, settings.LookUpLyricsOnline);
        Assert.Equal(2, settings.Version);
    }

    [Fact]
    public void Version2_IsReadAsSaved()
    {
        var settings = LoadFile("""{ "Version": 2, "ShowLyrics": true, "LookUpLyricsOnline": false }""");

        Assert.True(settings.ShowLyrics);
        Assert.False(settings.LookUpLyricsOnline);
    }
}
