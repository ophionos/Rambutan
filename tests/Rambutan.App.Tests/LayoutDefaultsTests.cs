using Rambutan.App.Services;

namespace Rambutan.App.Tests;

/// <summary>The panels a fresh install opens with, and that a saved choice
/// outlives a change of default.</summary>
public sealed class LayoutDefaultsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-layout-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void AFreshInstall_OpensTheSidebar_AndClosesTheQueue()
    {
        var settings = new AppSettingsStore(_dir).Load();

        Assert.False(settings.SidebarCollapsed);
        Assert.True(settings.QueueCollapsed);
    }

    [Fact]
    public void ASavedOpenQueue_StaysOpen()
    {
        var store = new AppSettingsStore(_dir);
        var settings = store.Load();
        settings.QueueCollapsed = false;
        store.Save(settings);

        Assert.False(new AppSettingsStore(_dir).Load().QueueCollapsed);
    }
}
