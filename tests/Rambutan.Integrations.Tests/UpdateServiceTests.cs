namespace Rambutan.Integrations.Tests;

public class UpdateServiceTests
{
    [Theory]
    [InlineData(UpdateService.ReleasesFeed, true)]
    [InlineData("https://GitHub.com/someone/fork", true)]
    [InlineData("http://github.com/someone/fork", false)]
    [InlineData("https://example.com/releases", false)]
    [InlineData(@"C:\releases", false)]
    [InlineData("/srv/releases", false)]
    public void IsGitHubRepository_OnlyForHttpsGitHubUrls(string feed, bool expected) =>
        Assert.Equal(expected, UpdateService.IsGitHubRepository(feed));
}
