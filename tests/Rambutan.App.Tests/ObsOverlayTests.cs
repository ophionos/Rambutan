using System.Net;
using System.Net.Http.Headers;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Queue;
using Rambutan.LocalApi;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.Tests;

/// <summary>The OBS overlay's app side: its own loopback host, its own
/// key, and the style file.</summary>
public sealed class ObsOverlayTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-overlay-");
    private readonly ObsOverlayService _overlay;

    public ObsOverlayTests() => _overlay = Create();

    private ObsOverlayService Create() => new(
        FakePlayback.Coordinator(new QueueModel()),
        new RemoteTokenVault(_dir.FullName, null, "overlay-link", "keyring.label.overlay"),
        new OverlayStyleStore(_dir.FullName),
        new CoverSource((_, _) => Task.FromResult(Array.Empty<byte>()), _ => null),
        _ => -1, port: 0);

    public void Dispose()
    {
        _overlay.StopAsync().GetAwaiter().GetResult();
        _dir.Delete(recursive: true);
    }

    private string KeyFile => Path.Combine(_dir.FullName, "overlay-link.bin");
    private string StyleFile => Path.Combine(_dir.FullName, "overlay-style.json");

    [Fact]
    public void Off_MakesNoKeyAndNoStyleFile()
    {
        Assert.Equal(OverlayStatus.Off, _overlay.Status);
        Assert.Null(_overlay.Url);
        Assert.False(File.Exists(KeyFile));
        Assert.False(File.Exists(StyleFile));
        Assert.Equal(OverlayConfig.Default, _overlay.Style);
        Assert.False(_overlay.IsCustomStyle);
    }

    [Fact]
    public async Task On_ServesTheOverlayOnLoopback_WithTheKeyInTheUrl()
    {
        await _overlay.SetEnabledAsync(true);

        Assert.Equal(OverlayStatus.Listening, _overlay.Status);
        Assert.True(File.Exists(KeyFile));
        var url = _overlay.Url!;
        Assert.StartsWith($"http://127.0.0.1:{_overlay.Port}/overlay/#t=", url);
        Assert.Equal($"http://127.0.0.1:{_overlay.Port}/overlay/", _overlay.Address);
        Assert.Equal(HttpStatusCode.OK, await ConfigStatusAsync(TokenOf(url)));
    }

    [Fact]
    public async Task TheKey_IsNotTheRemotesPairing()
    {
        await _overlay.SetEnabledAsync(true);

        Assert.False(File.Exists(Path.Combine(_dir.FullName, "remote-pairing.bin")));
    }

    [Fact]
    public async Task Off_StopsServing()
    {
        await _overlay.SetEnabledAsync(true);
        var port = _overlay.Port;

        await _overlay.SetEnabledAsync(false);

        Assert.Equal(OverlayStatus.Off, _overlay.Status);
        Assert.Null(_overlay.Url);
        using var http = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/overlay/"));
    }

    [Fact]
    public async Task TheKey_LastsAcrossRestarts_UntilReset()
    {
        await _overlay.SetEnabledAsync(true);
        var first = TokenOf(_overlay.Url!);
        await _overlay.SetEnabledAsync(false);
        await _overlay.SetEnabledAsync(true);
        Assert.Equal(first, TokenOf(_overlay.Url!));

        await _overlay.ResetLinkAsync();

        var second = TokenOf(_overlay.Url!);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, await ConfigStatusAsync(first));
        Assert.Equal(HttpStatusCode.OK, await ConfigStatusAsync(second));
    }

    [Fact]
    public void AnImportedStyle_IsKept_AndLoadsAtTheNextStart()
    {
        var problem = _overlay.ImportStyle("""{"format":"rambutan-overlay","version":1,"align":"right"}""");

        Assert.Equal(OverlayConfigProblem.None, problem);
        Assert.True(_overlay.IsCustomStyle);
        Assert.Equal(OverlayAlign.Right, _overlay.Style.Align);
        Assert.True(File.Exists(StyleFile));

        var next = Create();
        Assert.True(next.IsCustomStyle);
        Assert.Equal(OverlayAlign.Right, next.Style.Align);
    }

    [Fact]
    public void ABadImport_ChangesNothing()
    {
        _overlay.ImportStyle("""{"format":"rambutan-overlay","version":1,"scale":2}""");

        Assert.Equal(OverlayConfigProblem.NewerVersion,
            _overlay.ImportStyle("""{"format":"rambutan-overlay","version":99}"""));
        Assert.Equal(OverlayConfigProblem.NotAnOverlayStyle, _overlay.ImportStyle("""{"theme":"dark"}"""));
        Assert.Equal(OverlayConfigProblem.TooLarge,
            _overlay.ImportStyle(new string(' ', OverlayConfigFormat.MaxBytes + 1)));

        Assert.Equal(2, _overlay.Style.Scale);
        Assert.Equal(2, Create().Style.Scale);
    }

    [Fact]
    public void TheDefaultStyle_RemovesTheFile()
    {
        _overlay.ImportStyle("""{"format":"rambutan-overlay","version":1,"scale":2}""");

        _overlay.ResetStyle();

        Assert.False(_overlay.IsCustomStyle);
        Assert.Equal(OverlayConfig.Default, _overlay.Style);
        Assert.False(File.Exists(StyleFile));
    }

    [Fact]
    public void AnOversizedSavedStyle_IsLeftAlone_AndSaidSo()
    {
        var huge = new string(' ', OverlayConfigFormat.MaxBytes + 1);
        File.WriteAllText(StyleFile, huge);

        var overlay = Create();

        Assert.Equal(OverlayConfig.Default, overlay.Style);
        Assert.Equal(OverlayConfigProblem.TooLarge, overlay.StyleProblem);
        Assert.Equal(huge, File.ReadAllText(StyleFile));
    }

    [Fact]
    public void ASavedStyleFromANewerVersion_IsLeftAlone_AndSaidSo()
    {
        const string newer = """{"format":"rambutan-overlay","version":2,"scale":2}""";
        File.WriteAllText(StyleFile, newer);

        var overlay = Create();

        Assert.Equal(OverlayConfig.Default, overlay.Style);
        Assert.Equal(OverlayConfigProblem.NewerVersion, overlay.StyleProblem);
        Assert.Equal(newer, File.ReadAllText(StyleFile));
        Assert.StartsWith("Your saved style was made by a newer version",
            OverlaySettingsViewModel.StyleText(overlay.IsCustomStyle, overlay.StyleProblem));
    }

    [Fact]
    public void TheExport_IsTheVersionedFile()
    {
        _overlay.ImportStyle("""{"format":"rambutan-overlay","version":1,"cornerRadius":4}""");

        var parsed = OverlayConfigFormat.Parse(_overlay.ExportStyle());

        Assert.Equal(4, parsed.Config!.CornerRadius);
        Assert.Contains("\"version\": 1", _overlay.ExportStyle());
    }

    [Fact]
    public void TheStatus_SaysWhatIsHappening()
    {
        Assert.Equal("Off. Rambutan doesn't serve the overlay.",
            OverlaySettingsViewModel.StatusText(OverlayStatus.Off, null));
        Assert.Equal("Couldn't start: another program is using the overlay's port.",
            OverlaySettingsViewModel.StatusText(OverlayStatus.Failed, LocalApiFailure.PortInUse));
        Assert.Equal("That style was made by a newer version of Rambutan. Update Rambutan to use it.",
            OverlaySettingsViewModel.ProblemText(OverlayConfigProblem.NewerVersion));
        Assert.Equal("That file is too large to be a Rambutan overlay style.",
            OverlaySettingsViewModel.ProblemText(OverlayConfigProblem.TooLarge));
    }

    private static string TokenOf(string url) => url[(url.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private async Task<HttpStatusCode> ConfigStatusAsync(string token)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_overlay.Port}/api/v1/overlay/config");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await http.SendAsync(request)).StatusCode;
    }
}
