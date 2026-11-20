using Rambutan.Core.Stations;

namespace Rambutan.Core.Tests;

public class StationArtistTests
{
    [Theory]
    [InlineData("Fausto", "fausto")]
    [InlineData("Sérgio Godinho", "Sergio Godinho")]
    [InlineData("Fausto & Sérgio Godinho", "Fausto")]
    [InlineData("Fausto, Sérgio Godinho", "Fausto")]
    [InlineData("Fausto feat. Someone", "Fausto")]
    [InlineData("Fausto ft. Someone", "Fausto")]
    [InlineData("Fausto x Someone", "Fausto")]
    [InlineData("Fausto + Someone", "Fausto")]
    [InlineData("Fausto and Someone", "Fausto")]
    [InlineData("Fausto / Someone", "Fausto")]
    [InlineData("Simon & Garfunkel", "Simon & Garfunkel")]
    [InlineData("Simon & Garfunkel feat. Someone", "Simon & Garfunkel")]
    public void A_credit_that_names_the_artist_matches(string credit, string artist) =>
        Assert.True(StationArtist.Names(credit, artist));

    [Theory]
    [InlineData("Faustones", "Fausto")]
    [InlineData("The Fausto", "Fausto")]
    [InlineData("Someone & Fausto", "Fausto")]
    [InlineData("Fausto Bordalo Dias", "Fausto")]
    [InlineData("", "Fausto")]
    [InlineData("Fausto", "")]
    [InlineData("", "")]
    public void A_credit_that_does_not_name_the_artist_does_not_match(string credit, string artist) =>
        Assert.False(StationArtist.Names(credit, artist));
}
