using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Controls;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.App.Views;
using Rambutan.Core.Settings;
using Rambutan.Integrations;
using Rambutan.Integrations.Discord;

namespace Rambutan.App.Tests;

/// <summary>The Settings page's Discord section.</summary>
public class DiscordSettingsTests
{
    private static DiscordPresenceService NoDiscord(string? appId = "1") =>
        new(appId, _ => Task.FromResult<Stream?>(null));

    private static SettingsPageViewModel Page(AppSettings app, DiscordPresenceService? discord, Action? persist = null) =>
        new(new PlaybackSettings(), app, new AppearanceSettings(), new UpdateService(null), new SessionViewModel(), persist ?? (() => { }), discord);

    [Fact]
    public void Defaults_AreOff_WithEveryElementReadyToShow()
    {
        var app = new AppSettings();

        Assert.False(app.DiscordEnabled);
        Assert.True(app.DiscordShowTrack && app.DiscordShowArtwork && app.DiscordShowElapsed);
    }

    [Fact]
    public void WithoutAnAppId_TheSectionSaysSo_AndStaysOffEvenIfTheFileSaysOn()
    {
        using var discord = NoDiscord(appId: null);
        var page = Page(new AppSettings { DiscordEnabled = true }, discord);

        Assert.False(page.DiscordAvailable);
        Assert.False(page.DiscordEnabled);
        Assert.Equal(SettingsPageViewModel.UnavailableNote, page.DiscordStatus);
        Assert.Equal(DiscordConnectionState.Off, discord.ConnectionState);
    }

    [Fact]
    public void TurningItOn_SavesStartsTheServiceAndSaysItIsWaiting()
    {
        using var discord = NoDiscord();
        var app = new AppSettings();
        var saves = 0;
        var page = Page(app, discord, () => saves++);
        Assert.Equal("Off. Nothing is sent to Discord.", page.DiscordStatus);

        page.DiscordEnabled = true;

        Assert.True(app.DiscordEnabled);
        Assert.Equal(1, saves);
        Assert.StartsWith("Waiting for Discord.", page.DiscordStatus);
    }

    [Fact]
    public void ElementToggles_SaveAndRewriteWhatTheSectionSaysIsSent()
    {
        using var discord = NoDiscord();
        var app = new AppSettings { DiscordEnabled = true };
        var page = Page(app, discord);
        Assert.Contains("the track's title, artist and album", page.DiscordSharingNote);

        page.DiscordShowTrack = false;
        page.DiscordShowElapsed = false;

        Assert.False(app.DiscordShowTrack);
        Assert.False(app.DiscordShowElapsed);
        Assert.DoesNotContain("title", page.DiscordSharingNote);
        Assert.Contains("on this computer: a link to its artwork", page.DiscordSharingNote);
    }

    [Theory]
    [InlineData(true, true, true, "the track's title, artist and album; a link to its artwork on YouTube Music's image servers; when it started and when it ends")]
    [InlineData(true, false, true, "the track's title, artist and album; when it started and when it ends")]
    [InlineData(false, false, false, "only that you're listening to something")]
    public void SharingNote_ListsExactlyTheEnabledElements(bool track, bool art, bool time, string what) =>
        Assert.Contains($"on this computer: {what}.", SettingsPageViewModel.SharingNote(new DiscordPresenceOptions(track, art, time)));

    [Fact]
    public void SharingNote_SaysDiscordDrawsItsOwnClock_OnlyWhenTheTimesAreOff()
    {
        Assert.DoesNotContain("its own clock", SettingsPageViewModel.SharingNote(new DiscordPresenceOptions()));
        Assert.EndsWith("Discord shows its own clock, which counts up from when the activity appeared.",
            SettingsPageViewModel.SharingNote(new DiscordPresenceOptions(ShowElapsed: false)));
    }

    [Fact]
    public void Status_CoversEveryState()
    {
        Assert.Equal(SettingsPageViewModel.UnavailableNote,
            SettingsPageViewModel.StatusText(false, true, DiscordConnectionState.Connected, false, null));
        Assert.StartsWith("Off.", SettingsPageViewModel.StatusText(true, false, DiscordConnectionState.Off, false, null));
        Assert.StartsWith("Waiting for Discord.", SettingsPageViewModel.StatusText(true, true, DiscordConnectionState.Off, false, null));
        Assert.StartsWith("Waiting for Discord.", SettingsPageViewModel.StatusText(true, true, DiscordConnectionState.Waiting, false, null));
        Assert.Equal("Connected to Discord.", SettingsPageViewModel.StatusText(true, true, DiscordConnectionState.Connected, false, null));
        Assert.Contains("refused the connection (Invalid Client ID)",
            SettingsPageViewModel.StatusText(true, true, DiscordConnectionState.Waiting, true, "Invalid Client ID"));
        Assert.StartsWith("Discord refused the connection. ",
            SettingsPageViewModel.StatusText(true, true, DiscordConnectionState.Waiting, true, null));
    }

    [Fact]
    public void AvailableSection_NamesEveryToggle() => Headless.Run(() =>
    {
        AccessibleNames.Install();
        ShellResources.Ensure();
        using var discord = NoDiscord();
        var page = Page(new AppSettings { DiscordEnabled = true }, discord);
        var view = new SettingsPage { DataContext = page };
        var window = new Window { Width = 900, Height = 1400, Content = view };
        window.Show();
        window.UpdateLayout();

        static string? Spoken(Control c) =>
            Avalonia.Automation.Peers.ControlAutomationPeer.CreatePeerForElement(c).GetName();

        var boxes = view.GetVisualDescendants().OfType<ToggleSwitch>()
            .Where(c => c.IsEffectivelyVisible)
            .Select(Spoken)
            .ToArray();
        Assert.Contains("Show what I'm listening to on Discord", boxes);
        Assert.Contains("Track title, artist and album", boxes);
        Assert.Contains("Album artwork", boxes);
        Assert.Contains("Elapsed and remaining time", boxes);
        Assert.DoesNotContain(boxes, string.IsNullOrWhiteSpace);
        window.Close();
    });
}
