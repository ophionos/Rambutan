using Rambutan.Integrations.LastFm;

namespace Rambutan.Integrations.Tests.LastFm;

/// <summary>
/// The worked approach of last.fm/api/authspec §8, with the expected digests
/// computed by hand outside .NET (md5 of the concatenated string).
/// </summary>
public class LastFmSignatureTests
{
    [Fact]
    public void Signs_SortedNameValuePairs_ThenTheSecret()
    {
        // md5("api_keyxxxxxxxxxxmethodauth.getSessiontokenyyyyyyyyyymysecret")
        var signature = LastFmSignature.Sign(
        [
            new("token", "yyyyyyyyyy"),
            new("method", "auth.getSession"),
            new("api_key", "xxxxxxxxxx"),
        ], "mysecret");

        Assert.Equal("b8f15cdadc94c10cd7f8073ff9bfb7fd", signature);
    }

    [Fact]
    public void LeavesOutFormatAndCallback()
    {
        KeyValuePair<string, string>[] bare =
        [
            new("method", "auth.getSession"), new("api_key", "xxxxxxxxxx"), new("token", "yyyyyyyyyy"),
        ];
        KeyValuePair<string, string>[] withExtras =
        [
            .. bare, new("format", "json"), new("callback", "cb"),
        ];

        Assert.Equal(LastFmSignature.Sign(bare, "mysecret"), LastFmSignature.Sign(withExtras, "mysecret"));
    }

    [Fact]
    public void BatchParameters_SortByOrdinalName_AndHashAsUtf8()
    {
        // md5("album[0]Discoveryapi_keyk123artist[0]Daft Punkartist[1]Justicemethodtrack.scrobble"
        //   + "skSESSIONtimestamp[0]1700000000timestamp[1]1700000300track[0]One More Time"
        //   + "track[1]D.A.N.C.E.s3crét")
        var signature = LastFmSignature.Sign(
        [
            new("method", "track.scrobble"),
            new("api_key", "k123"),
            new("sk", "SESSION"),
            new("artist[0]", "Daft Punk"),
            new("track[0]", "One More Time"),
            new("timestamp[0]", "1700000000"),
            new("album[0]", "Discovery"),
            new("artist[1]", "Justice"),
            new("track[1]", "D.A.N.C.E."),
            new("timestamp[1]", "1700000300"),
            new("format", "json"),
        ], "s3crét");

        Assert.Equal("36dddf3f3c5c913ce076db57a0d98e5d", signature);
    }
}
