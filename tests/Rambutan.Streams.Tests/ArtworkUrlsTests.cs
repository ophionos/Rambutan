namespace Rambutan.Streams.Tests;

public class ArtworkUrlsTests
{
    [Theory]
    [InlineData("https://lh3.googleusercontent.com/abc=w60-h60-l90-rj", "https://lh3.googleusercontent.com/abc=w544-h544-l90-rj")]
    [InlineData("https://lh3.googleusercontent.com/abc=w120-h120", "https://lh3.googleusercontent.com/abc=w544-h544")]
    [InlineData("https://yt3.ggpht.com/abc=s192", "https://yt3.ggpht.com/abc=s544")]
    public void A_small_cover_on_a_Google_image_host_is_fetched_at_544(string url, string expected) =>
        Assert.Equal(expected, ArtworkUrls.Largest(url));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/abc=w544-h544-l90-rj")]
    [InlineData("https://yt3.ggpht.com/abc=s1200")]
    [InlineData("https://i.ytimg.com/vi/88sARuFu-tc/hqdefault.jpg?sqp=x&rs=y")]
    [InlineData("https://lh3.googleusercontent.com/abc")]
    [InlineData("not a url")]
    public void Other_covers_are_fetched_as_they_are(string url) =>
        Assert.Equal(url, ArtworkUrls.Largest(url));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/abc=w544-h544-l90-rj", "https://lh3.googleusercontent.com/abc=w120-h120-l90-rj")]
    [InlineData("https://yt3.ggpht.com/abc=s1200", "https://yt3.ggpht.com/abc=s120")]
    [InlineData("https://lh3.googleusercontent.com/abc", "https://lh3.googleusercontent.com/abc=w120-h120")]
    [InlineData("https://lh3.googleusercontent.com/abc=w60-h60-l90-rj", "https://lh3.googleusercontent.com/abc=w60-h60-l90-rj")]
    [InlineData("https://i.ytimg.com/vi/88sARuFu-tc/hqdefault.jpg?sqp=x&rs=y", "https://i.ytimg.com/vi/88sARuFu-tc/hqdefault.jpg?sqp=x&rs=y")]
    [InlineData("not a url", "not a url")]
    public void A_rows_cover_is_fetched_at_120_at_most(string url, string expected) =>
        Assert.Equal(expected, ArtworkUrls.ForRow(url));

    [Fact]
    public void The_extension_follows_the_bytes()
    {
        Assert.Equal(".jpg", ArtworkUrls.ExtensionFor([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal(".png", ArtworkUrls.ExtensionFor([0x89, 0x50, 0x4E, 0x47, 0x0D]));
        Assert.Equal(".webp", ArtworkUrls.ExtensionFor("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Equal(".img", ArtworkUrls.ExtensionFor("GIF89a"u8));
    }
}
