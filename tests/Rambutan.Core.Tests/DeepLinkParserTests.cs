using Rambutan.Core.Links;

namespace Rambutan.Core.Tests;

/// <summary>The rambutan:// grammar and the music.youtube.com forms.</summary>
public class DeepLinkParserTests
{
    private const string Video = "khnokW3Mw24";
    private const string Channel = "UC_kRDKYrUlrbtrSiyu5Tflg";

    [Theory]
    [InlineData("rambutan://")]
    [InlineData("rambutan://open")]
    [InlineData("rambutan://open/")]
    [InlineData("RAMBUTAN://Open")]
    [InlineData("https://music.youtube.com")]
    [InlineData("https://music.youtube.com/")]
    public void OpenLinks_OnlyBringTheWindowForward(string link) =>
        Assert.IsType<OpenAppLink>(DeepLinkParser.Parse(link).Link);

    [Theory]
    [InlineData("rambutan://play/" + Video)]
    [InlineData("rambutan://play/" + Video + "/")]
    [InlineData("rambutan://PLAY/" + Video)]
    [InlineData("  rambutan://play/" + Video + "  ")]
    [InlineData("rambutan://play/" + Video + "?t=30#x")]
    [InlineData("https://music.youtube.com/watch?v=" + Video)]
    [InlineData("https://music.youtube.com/watch?v=" + Video + "&list=RDAMVM" + Video)]
    [InlineData("https://MUSIC.youtube.com/watch?feature=share&v=" + Video)]
    [InlineData("http://music.youtube.com/watch?v=" + Video)]
    public void PlayLinks_KeepTheVideoIdAsWritten(string link) =>
        Assert.Equal(new PlayLink(Video), DeepLinkParser.Parse(link).Link);

    [Theory]
    [InlineData("rambutan://album/MPREb_4pL8gzRtw1p", "MPREb_4pL8gzRtw1p")]
    [InlineData("https://music.youtube.com/browse/MPREb_4pL8gzRtw1p", "MPREb_4pL8gzRtw1p")]
    public void AlbumLinks(string link, string id) =>
        Assert.Equal(new AlbumLink(id), DeepLinkParser.Parse(link).Link);

    [Theory]
    [InlineData("rambutan://playlist/PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG", "PLx0sYbCqOb8TBPRdmBHs5Iftvv9TPboYG")]
    [InlineData("rambutan://playlist/VLPLabc", "PLabc")]
    [InlineData("rambutan://playlist/OLAK5uy_kZ4a", "OLAK5uy_kZ4a")]
    [InlineData("rambutan://playlist/LM", "LM")]
    [InlineData("https://music.youtube.com/playlist?list=PLabc", "PLabc")]
    [InlineData("https://music.youtube.com/browse/VLPLabc", "PLabc")]
    public void PlaylistLinks_DropTheBrowsePrefix(string link, string id) =>
        Assert.Equal(new PlaylistLink(id), DeepLinkParser.Parse(link).Link);

    [Theory]
    [InlineData("rambutan://artist/" + Channel)]
    [InlineData("https://music.youtube.com/channel/" + Channel)]
    [InlineData("https://music.youtube.com/browse/" + Channel)]
    public void ArtistLinks(string link) =>
        Assert.Equal(new ArtistLink(Channel), DeepLinkParser.Parse(link).Link);

    [Theory]
    [InlineData("rambutan://search?q=daft+punk", "daft punk")]
    [InlineData("rambutan://search?q=daft%20punk", "daft punk")]
    [InlineData("rambutan://search/?q=%E3%82%A2%E3%83%AA", "アリ")]
    [InlineData("rambutan://search?x=1&q=a%2Bb", "a+b")]
    [InlineData("rambutan://search?q=%2541", "%41")]
    [InlineData("https://music.youtube.com/search?q=daft+punk", "daft punk")]
    public void SearchLinks_DecodeTheTextOnce(string link, string query) =>
        Assert.Equal(new SearchLink(query), DeepLinkParser.Parse(link).Link);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("rambutan:play/" + Video)]
    [InlineData("rambutan://play")]
    [InlineData("rambutan://play/")]
    [InlineData("rambutan://play/short")]
    [InlineData("rambutan://play/khnokW3Mw245")]
    [InlineData("rambutan://play/khnokW3Mw2.")]
    [InlineData("rambutan://play/local:1")]
    [InlineData("rambutan://play/local%3A12345")]
    [InlineData("rambutan://play/" + Video + "/extra")]
    [InlineData("rambutan://play//" + Video)]
    [InlineData("rambutan://play/" + Video + "//")]
    [InlineData("rambutan://play/..%2F..%2Fx")]
    [InlineData("rambutan://play/%6B%68%6E%6F%6B%57%33%4D%77%32%34")]
    [InlineData("rambutan://album/../../etc")]
    [InlineData("rambutan://album/PLnotanalbum")]
    [InlineData("rambutan://album/MPRE")]
    [InlineData("rambutan://artist/UCshort")]
    [InlineData("rambutan://artist/XX_kRDKYrUlrbtrSiyu5Tflg")]
    [InlineData("rambutan://playlist/P")]
    [InlineData("rambutan://playlist/VL")]
    [InlineData("rambutan://playlist/PL abc")]
    [InlineData("rambutan://playlist/PL\\abc")]
    [InlineData("rambutan://search")]
    [InlineData("rambutan://search?q=")]
    [InlineData("rambutan://search?q=+++")]
    [InlineData("rambutan://search?q=a%0Ab")]
    [InlineData("rambutan://search/x?q=a")]
    [InlineData("rambutan://open/x")]
    [InlineData("rambutan://play/" + Video + "\n--profile")]
    [InlineData("https://music.youtube.com.evil.example/watch?v=" + Video)]
    [InlineData("https://evil.example/music.youtube.com/watch?v=" + Video)]
    [InlineData("https://user@music.youtube.com/watch?v=" + Video)]
    [InlineData("https://music.youtube.com/watch")]
    [InlineData("https://music.youtube.com/watch?v=short")]
    [InlineData("file:///C:/Windows")]
    [InlineData("javascript:alert(1)")]
    [InlineData("rambutan://play/" + Video + "\"--veloapp-uninstall")]
    public void BrokenLinks_AreMalformed(string? link)
    {
        var result = DeepLinkParser.Parse(link);
        Assert.Null(result.Link);
        Assert.Equal(DeepLinkError.Malformed, result.Error);
    }

    [Fact]
    public void OverlongLinks_AreMalformed()
    {
        var link = "rambutan://search?q=" + new string('a', DeepLinkParser.MaxLinkLength);
        Assert.Equal(DeepLinkError.Malformed, DeepLinkParser.Parse(link).Error);
    }

    [Fact]
    public void OverlongSearchText_IsMalformed()
    {
        Assert.True(DeepLinkParser.Parse("rambutan://search?q=" + new string('a', DeepLinkParser.MaxQueryLength)).IsValid);
        Assert.Equal(DeepLinkError.Malformed,
            DeepLinkParser.Parse("rambutan://search?q=" + new string('a', DeepLinkParser.MaxQueryLength + 1)).Error);
    }

    [Theory]
    [InlineData("rambutan://queue/" + Video, "queue")]
    [InlineData("rambutan://DELETE/PLabc", "delete")]
    [InlineData("rambutan://v2/play/" + Video, "v2")]
    [InlineData("https://music.youtube.com/library", "library")]
    [InlineData("https://music.youtube.com/browse/FEmusic_home", "browse")]
    public void UnknownActions_AreNamed_NotGuessedAt(string link, string action)
    {
        var result = DeepLinkParser.Parse(link);
        Assert.Null(result.Link);
        Assert.Equal(DeepLinkError.UnknownAction, result.Error);
        Assert.Equal(action, result.Action);
    }

    [Theory]
    [InlineData("rambutan://play/x", true)]
    [InlineData("Rambutan:whatever", true)]
    [InlineData("https://music.youtube.com/watch?v=x", true)]
    [InlineData("--profile", false)]
    [InlineData("work", false)]
    [InlineData("https://www.youtube.com/watch?v=x", false)]
    [InlineData("", false)]
    public void LooksLikeLink_ClaimsOnlyLinks(string arg, bool expected) =>
        Assert.Equal(expected, DeepLinkParser.LooksLikeLink(arg));
}
