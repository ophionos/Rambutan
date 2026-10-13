using System.Net;
using System.Net.Http;
using System.Text.Json;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;

namespace Rambutan.App.Tests;

public class LoadErrorTests
{
    private static readonly RelayCommand Retry = new(() => { });

    [Fact]
    public void NoAnswerAtAll_ReadsAsOffline_AndKeepsTheRawMessageAsDetail()
    {
        var error = LoadError.From(
            new HttpRequestException("The proxy tunnel request to proxy 'http://127.0.0.1:41087/' failed"),
            LoadSubject.Home, Retry);

        Assert.Equal("Can't reach YouTube Music", error.Title);
        Assert.DoesNotContain("proxy", error.Title + error.Hint);
        Assert.Contains("127.0.0.1", error.Detail);
        Assert.Same(Retry, error.Retry);
    }

    [Fact]
    public void ATimeout_ReadsAsOffline() =>
        Assert.Equal("Can't reach YouTube Music",
            LoadError.From(new TaskCanceledException("timed out"), LoadSubject.Home, Retry).Title);

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "YouTube Music turned the request away")]
    [InlineData(HttpStatusCode.Forbidden, "YouTube Music turned the request away")]
    [InlineData(HttpStatusCode.NotFound, "This album isn't available")]
    [InlineData(HttpStatusCode.TooManyRequests, "YouTube Music is having trouble")]
    [InlineData(HttpStatusCode.BadGateway, "YouTube Music is having trouble")]
    public void AnHttpStatus_ReadsAsWhatItMeans(HttpStatusCode status, string title)
    {
        var error = LoadError.From(new HttpRequestException("x", null, status), LoadSubject.Album, Retry);
        Assert.Equal(title, error.Title);
    }

    [Fact]
    public void AResponseThatDoesntParse_SaysSo()
    {
        Assert.Equal("Couldn't read this playlist",
            LoadError.From(new JsonException("bad"), LoadSubject.Playlist, Retry).Title);
        Assert.Equal("Couldn't read this playlist",
            LoadError.From(new KeyNotFoundException(), LoadSubject.Playlist, Retry).Title);
    }

    [Fact]
    public void NotSignedIn_AsksForSignIn_NotAParseError()
    {
        // NotSignedInException is an InvalidOperationException; it must not be
        // reported as "couldn't read".
        Assert.Equal("Sign in to see your library",
            LoadError.From(new NotSignedInException(), LoadSubject.Library, Retry).Title);
    }

    [Fact]
    public void AnythingElse_FallsBackToCouldntLoad() =>
        Assert.Equal("Couldn't load Home",
            LoadError.From(new NotSupportedException(), LoadSubject.Home, Retry).Title);
}
