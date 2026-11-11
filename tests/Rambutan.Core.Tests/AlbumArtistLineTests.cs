using Rambutan.Core.Models;

namespace Rambutan.Core.Tests;

/// <summary>An album row leaves out its artist line only when it would repeat
/// the album's artist.</summary>
public class AlbumArtistLineTests
{
    private static readonly ArtistRef DaftPunk = new("Daft Punk", "UCdaft");

    [Fact]
    public void WithoutAnAlbumArtist_TheLineShows()
    {
        Assert.False(AlbumArtistLine.Hides(null, [DaftPunk]));
        Assert.False(AlbumArtistLine.Hides(new ArtistRef(" "), [new ArtistRef(" ")]));
    }

    [Fact]
    public void TheAlbumArtistAlone_IsHidden()
    {
        Assert.True(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("Daft Punk", "UCdaft")]));
    }

    [Fact]
    public void ByBrowseId_WhenBothHaveOne_WhateverTheName()
    {
        Assert.True(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("DAFT PUNK (FR)", "UCdaft")]));
    }

    [Fact]
    public void ByName_WhenTheIdsDiffer_ForAnArtistWithTwoChannels()
    {
        Assert.True(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("Daft Punk", "UCother")]));
        Assert.False(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("Justice", "UCother")]));
    }

    [Fact]
    public void ByName_WhenEitherHasNoBrowseId_TrimmedAndIgnoringCase()
    {
        Assert.True(AlbumArtistLine.Hides(new ArtistRef("Daft Punk"), [new ArtistRef(" daft punk ", "UCdaft")]));
        Assert.True(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("DAFT PUNK")]));
        Assert.False(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("Justice")]));
    }

    [Fact]
    public void AnotherArtist_Shows()
    {
        Assert.False(AlbumArtistLine.Hides(DaftPunk, [new ArtistRef("Justice", "UCjustice")]));
    }

    [Fact]
    public void AFeature_Shows_EvenWithTheAlbumArtistAmongTheCredits()
    {
        Assert.False(AlbumArtistLine.Hides(DaftPunk, [DaftPunk, new ArtistRef("Pharrell Williams", "UCpharrell")]));
    }

    [Fact]
    public void CreditsThatJoinToTheAlbumArtistsName_AreHidden()
    {
        var both = new ArtistRef("Simon, Garfunkel");
        Assert.True(AlbumArtistLine.Hides(both, [new ArtistRef("Simon", "UCs"), new ArtistRef("Garfunkel", "UCg")]));
        Assert.False(AlbumArtistLine.Hides(both, [new ArtistRef("Garfunkel"), new ArtistRef("Simon")]));
    }

    [Fact]
    public void ARowWithNoCredits_Shows()
    {
        Assert.False(AlbumArtistLine.Hides(DaftPunk, []));
        Assert.False(AlbumArtistLine.Hides(DaftPunk, null));
    }
}
