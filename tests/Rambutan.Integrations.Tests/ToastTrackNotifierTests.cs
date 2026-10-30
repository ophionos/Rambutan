#if WINDOWS
using Rambutan.Integrations.Notifications;

namespace Rambutan.Integrations.Tests;

public sealed class ToastTrackNotifierTests
{
    [Fact]
    public void Text_goes_in_as_text_so_markup_characters_cant_break_the_toast()
    {
        var xml = ToastTrackNotifier.Build(new TrackNotification("Rock & Roll <Live>", "A & B · \"Album\"", null));

        var texts = xml.GetElementsByTagName("text");
        Assert.Equal("Rock & Roll <Live>", texts[0].InnerText);
        Assert.Equal("A & B · \"Album\"", texts[1].InnerText);
        Assert.Contains("Rock &amp; Roll &lt;Live&gt;", xml.GetXml());
        Assert.Equal(0u, xml.GetElementsByTagName("image").Length);
    }

    [Fact]
    public void Is_silent()
    {
        var xml = ToastTrackNotifier.Build(new TrackNotification("Song", "Artist", null));

        Assert.Equal("true", ((Windows.Data.Xml.Dom.XmlElement)xml.GetElementsByTagName("audio")[0]).GetAttribute("silent"));
    }

    [Fact]
    public void The_cover_is_a_file_uri_in_place_of_the_app_logo()
    {
        var xml = ToastTrackNotifier.Build(new TrackNotification("Song", "Artist", @"C:\Covers\a b#1.jpg"));

        var image = (Windows.Data.Xml.Dom.XmlElement)xml.GetElementsByTagName("image")[0];
        Assert.Equal("appLogoOverride", image.GetAttribute("placement"));
        Assert.Equal("file:///C:/Covers/a%20b%231.jpg", image.GetAttribute("src"));
    }

    [Fact]
    public void Without_an_installed_app_id_it_can_never_show()
    {
        using var notifier = new ToastTrackNotifier(null);

        Assert.False(notifier.CanShow);
        notifier.Show(new TrackNotification("Song", "Artist", null));
    }
}
#endif
