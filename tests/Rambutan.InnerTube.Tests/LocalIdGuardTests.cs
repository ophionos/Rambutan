using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.InnerTube.Tests;

/// <summary>An id that isn't YouTube's never reaches a request, signed in or not.</summary>
public sealed class LocalIdGuardTests
{
    private const string Local = "local:1";

    public static TheoryData<string, Func<InnerTubeClient, Task>> Calls => new()
    {
        { "radio", c => c.GetRadioAsync(Local) },
        { "mix", c => c.GetMixAsync(new MixSeed(Local, "RDAMVM" + Local)) },
        { "rate", c => c.RateAsync(Local, Rating.Like) },
        { "add", c => c.AddToPlaylistAsync("PLabc", ["khnokW3Mw24", Local]) },
        { "remove", c => c.RemoveFromPlaylistAsync("PLabc", Local, "SET1") },
        { "insert", c => c.InsertIntoPlaylistAsync("PLabc", Local, "SET1") },
    };

    [Theory]
    [MemberData(nameof(Calls))]
    public async Task ALocalId_IsRefused_BeforeAnyRequest(string name, Func<InnerTubeClient, Task> call)
    {
        _ = name;
        foreach (var signedIn in new[] { true, false })
        {
            var (client, handler) = RecordingHandler.Client(signedIn);

            await Assert.ThrowsAsync<NotAYouTubeTrackException>(() => call(client));
            Assert.Empty(handler.Requests);
        }
    }

    [Fact]
    public async Task TheRatingOfALocalId_IsUnknown_WithoutARequest()
    {
        var (client, handler) = RecordingHandler.Client();

        Assert.Null(await client.GetRatingAsync(Local));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AYouTubeId_StillGoesThrough()
    {
        var (client, handler) = RecordingHandler.Client();

        await client.RateAsync("khnokW3Mw24", Rating.Like);
        await client.AddToPlaylistAsync("PLabc", ["khnokW3Mw24"]);

        Assert.Equal(["like/like", "browse/edit_playlist"], handler.Requests.Select(r => r.Endpoint));
    }
}
