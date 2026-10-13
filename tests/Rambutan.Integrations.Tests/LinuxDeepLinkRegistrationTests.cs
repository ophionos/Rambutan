using Rambutan.Integrations.Linux;

namespace Rambutan.Integrations.Tests;

/// <summary>The Linux scheme registration, against a fake desktop. Tests
/// never touch the real applications folder or xdg-mime.</summary>
public class LinuxDeepLinkRegistrationTests
{
    private const string AppImage = "/home/u/Applications/Rambutan.AppImage";

    private sealed class FakeDesktop : IDesktopHandlers
    {
        public Dictionary<string, string> Files { get; } = [];
        public Dictionary<string, string> Defaults { get; } = [];
        public int Writes { get; private set; }
        public int DefaultsSet { get; private set; }

        public Task<string?> QueryDefaultAsync(string mimeType) =>
            Task.FromResult(Defaults.TryGetValue(mimeType, out var file) ? file + "\n" : null);

        public Task SetDefaultAsync(string desktopFile, string mimeType)
        {
            Defaults[mimeType] = desktopFile;
            DefaultsSet++;
            return Task.CompletedTask;
        }

        public string? Read(string desktopFile) => Files.GetValueOrDefault(desktopFile);

        public void Write(string desktopFile, string contents)
        {
            Files[desktopFile] = contents;
            Writes++;
        }
    }

    [Fact]
    public void Entry_IsAHandlerOnly_ThatPassesTheLinkAfterTheOption()
    {
        var entry = LinuxDeepLinkRegistration.Entry(AppImage)!;

        Assert.Contains($"Exec={AppImage} --deep-link %u\n", entry);
        Assert.Contains("MimeType=x-scheme-handler/rambutan;\n", entry);
        Assert.Contains("NoDisplay=true\n", entry);
        Assert.StartsWith("[Desktop Entry]\n", entry);
    }

    [Theory]
    [InlineData("/home/u/Applications/Rambutan.AppImage", "/home/u/Applications/Rambutan.AppImage")]
    [InlineData("/a b/R.AppImage", "\"/a b/R.AppImage\"")]
    [InlineData("/a'b", "\"/a'b\"")]
    [InlineData("/a\"b", "\"/a\\\\\"b\"")]
    [InlineData("/a$b`c", "\"/a\\\\$b\\\\`c\"")]
    [InlineData("/a\\b", "\"/a\\\\\\\\b\"")]
    public void ExecQuote_FollowsTheDesktopEntrySpec(string path, string expected) =>
        Assert.Equal(expected, LinuxDeepLinkRegistration.ExecQuote(path));

    [Theory]
    [InlineData("")]
    [InlineData("/a\nb")]
    [InlineData("/a\rb")]
    [InlineData("/100%/R.AppImage")]
    public void ExecQuote_RefusesWhatADesktopFileCantCarry(string path) =>
        Assert.Null(LinuxDeepLinkRegistration.ExecQuote(path));

    [Fact]
    public async Task Register_WritesTheFileAndTakesAnUnclaimedScheme()
    {
        var desktop = new FakeDesktop();

        var result = await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);

        Assert.Equal(LinkRegistrationResult.Registered, result);
        Assert.Equal(LinuxDeepLinkRegistration.Entry(AppImage), desktop.Files["rambutan-links.desktop"]);
        Assert.Equal("rambutan-links.desktop", desktop.Defaults["x-scheme-handler/rambutan"]);
    }

    [Fact]
    public async Task Register_AgainChangesNothing()
    {
        var desktop = new FakeDesktop();
        await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);

        var result = await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);

        Assert.Equal(LinkRegistrationResult.Unchanged, result);
        Assert.Equal(1, desktop.Writes);
        Assert.Equal(1, desktop.DefaultsSet);
    }

    [Fact]
    public async Task Register_FollowsTheAppImageWhenItMoves()
    {
        var desktop = new FakeDesktop();
        await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);

        var result = await LinuxDeepLinkRegistration.RegisterAsync(desktop, "/opt/Rambutan.AppImage");

        Assert.Equal(LinkRegistrationResult.Registered, result);
        Assert.Contains("Exec=/opt/Rambutan.AppImage --deep-link", desktop.Files["rambutan-links.desktop"]);
        Assert.Equal(1, desktop.DefaultsSet);
    }

    [Fact]
    public async Task Register_WritesTheFileAgainWhenItWasDeleted()
    {
        var desktop = new FakeDesktop();
        await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);
        desktop.Files.Clear();

        var result = await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);

        Assert.Equal(LinkRegistrationResult.Registered, result);
        Assert.True(desktop.Files.ContainsKey("rambutan-links.desktop"));
    }

    [Fact]
    public async Task Register_LeavesASchemeAnotherApplicationHandles()
    {
        var desktop = new FakeDesktop();
        desktop.Defaults["x-scheme-handler/rambutan"] = "other.desktop";

        var result = await LinuxDeepLinkRegistration.RegisterAsync(desktop, AppImage);

        Assert.Equal(LinkRegistrationResult.TakenByOther, result);
        Assert.Empty(desktop.Files);
        Assert.Equal("other.desktop", desktop.Defaults["x-scheme-handler/rambutan"]);
    }

    [Fact]
    public async Task Register_RefusesAPathItCantWrite()
    {
        var desktop = new FakeDesktop();

        var result = await LinuxDeepLinkRegistration.RegisterAsync(desktop, "/a\nb");

        Assert.Equal(LinkRegistrationResult.Unusable, result);
        Assert.Empty(desktop.Files);
        Assert.Empty(desktop.Defaults);
    }
}
