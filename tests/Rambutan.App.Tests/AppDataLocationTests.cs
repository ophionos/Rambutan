using Rambutan.App.Services;

namespace Rambutan.App.Tests;

public class AppDataLocationTests
{
    private static readonly string LocalAppData = Path.Combine(Path.GetTempPath(), "lad");
    private static readonly string Base = Path.Combine(LocalAppData, "Rambutan");

    [Fact]
    public void TheRealApp_UsesTheDefaultFolder()
    {
        var at = AppDataLocation.Resolve(demo: false, null, LocalAppData);

        Assert.Equal(Base, at.DataDir);
        Assert.Equal(Path.Combine(Base, "tools"), at.ToolsDir);
        Assert.Equal("Rambutan", at.InstanceName);
    }

    [Fact]
    public void ADemo_HasItsOwnFolderAndInstance_ButSharesTheSidecars()
    {
        var at = AppDataLocation.Resolve(demo: true, "  ", LocalAppData);

        Assert.Equal(Path.Combine(Base, "demo"), at.DataDir);
        Assert.Equal(Path.Combine(Base, "tools"), at.ToolsDir);
        Assert.Equal("Rambutan-demo", at.InstanceName);
    }

    [Fact]
    public void TheOverride_MovesTheData_AndNamesAnInstanceForThatFolder()
    {
        var one = Path.Combine(Path.GetTempPath(), "rambutan-one");
        var two = Path.Combine(Path.GetTempPath(), "rambutan-two");

        var a = AppDataLocation.Resolve(demo: true, one, LocalAppData);
        var b = AppDataLocation.Resolve(demo: true, two, LocalAppData);

        Assert.Equal(one, a.DataDir);
        Assert.Equal(Path.Combine(Base, "tools"), a.ToolsDir);
        Assert.StartsWith("Rambutan-", a.InstanceName);
        Assert.NotEqual(a.InstanceName, b.InstanceName);
        Assert.NotEqual("Rambutan-demo", a.InstanceName);
    }

    [Fact]
    public void TheSameFolder_SpelledAnotherWay_IsTheSameInstance()
    {
        var dir = Path.Combine(Path.GetTempPath(), "rambutan-one");
        var a = AppDataLocation.Resolve(demo: false, dir, LocalAppData);
        var b = AppDataLocation.Resolve(demo: true, dir + Path.DirectorySeparatorChar, LocalAppData);

        Assert.Equal(a.InstanceName, b.InstanceName);
        if (OperatingSystem.IsWindows())
            Assert.Equal(a.InstanceName, AppDataLocation.Resolve(false, dir.ToUpperInvariant(), LocalAppData).InstanceName);
    }

    [Fact]
    public void TheToolsOverride_MovesOnlyTheSidecars()
    {
        var tools = Path.Combine(Path.GetTempPath(), "rambutan-tools");

        var at = AppDataLocation.Resolve(demo: true, null, LocalAppData, tools);

        Assert.Equal(tools, at.ToolsDir);
        Assert.Equal(Path.Combine(Base, "demo"), at.DataDir);
        Assert.Equal(Path.Combine(Base, "tools"), AppDataLocation.Resolve(false, null, LocalAppData, " ").ToolsDir);
    }

    [Fact]
    public void TheInstalledApp_KeepsTheDocumentedPorts()
    {
        var at = AppDataLocation.Resolve(demo: false, null, LocalAppData);

        Assert.Equal((LocalApi.LocalApiHost.DefaultPort, LocalApi.Overlay.OverlayOptions.DefaultPort), at.PreferredPorts);
    }

    [Fact]
    public void EveryOtherFolder_HasAStablePairOfItsOwn()
    {
        var root = AppDataLocation.Resolve(demo: false, null, LocalAppData);
        var demo = AppDataLocation.Resolve(demo: true, null, LocalAppData);
        var profile = root.ForProfileFolder(Path.Combine(Base, "profiles", "work"));
        var again = AppDataLocation.Resolve(demo: false, null, LocalAppData).ForProfileFolder(Path.Combine(Base, "profiles", "work"));

        Assert.Equal(profile.PreferredPorts, again.PreferredPorts);
        foreach (var other in new[] { demo, profile })
        {
            var (remote, overlay) = other.PreferredPorts;
            Assert.Equal(remote + 1, overlay);
            Assert.InRange(remote, AppDataLocation.FirstDerivedPort,
                AppDataLocation.FirstDerivedPort + 2 * (AppDataLocation.DerivedPairs - 1));
            Assert.Equal(0, (remote - AppDataLocation.FirstDerivedPort) % 2);
            Assert.DoesNotContain(remote, new[] { root.PreferredPorts.Remote, root.PreferredPorts.Overlay });
        }
    }
}
