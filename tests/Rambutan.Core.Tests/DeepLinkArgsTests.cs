using Rambutan.Core.Links;

namespace Rambutan.Core.Tests;

/// <summary>How a link is found on the command line, beside other options.</summary>
public class DeepLinkArgsTests
{
    [Fact]
    public void ALinkLaunch_KeepsOnlyTheLink()
    {
        // What an unescaped quote in %1 could turn one link into.
        string[] args = ["--deep-link", "rambutan://play/x", "--profile", "evil", "--veloapp-uninstall"];

        Assert.Equal(["--deep-link", "rambutan://play/x"], DeepLinkArgs.Sanitize(args));
    }

    [Fact]
    public void OtherLaunches_AreLeftAlone()
    {
        string[] args = ["--profile", "work", "rambutan://play/x"];

        Assert.Same(args, DeepLinkArgs.Sanitize(args));
        Assert.Empty(DeepLinkArgs.Sanitize([]));
        Assert.Equal(["--deep-link"], DeepLinkArgs.Sanitize(["--deep-link"]));
    }

    [Fact]
    public void Find_TakesTheArgumentAfterTheOption_EvenIfItIsNoLink()
    {
        Assert.Equal("rambutan://play/x", DeepLinkArgs.Find(["--deep-link", "rambutan://play/x"]));
        Assert.Equal("garbage", DeepLinkArgs.Find(["--deep-link", "garbage"]));
        Assert.Equal("", DeepLinkArgs.Find(["--deep-link"]));
    }

    [Fact]
    public void Find_TakesABareLinkAnywhere_AndIgnoresOtherOptions()
    {
        Assert.Equal("rambutan://album/MPREb_a", DeepLinkArgs.Find(["--profile", "work", "rambutan://album/MPREb_a"]));
        Assert.Equal("https://music.youtube.com/watch?v=x",
            DeepLinkArgs.Find(["https://music.youtube.com/watch?v=x", "rambutan://open"]));
    }

    [Fact]
    public void Find_IsNullForAPlainLaunch()
    {
        Assert.Null(DeepLinkArgs.Find([]));
        Assert.Null(DeepLinkArgs.Find(["--profile", "work"]));
    }
}
