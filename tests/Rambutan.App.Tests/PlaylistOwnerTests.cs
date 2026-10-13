using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>
/// The playlist header names its owner ahead of "Playlist • 2026", unless
/// the playlist is collaborative or its subtitle already names the owner.
/// </summary>
public class PlaylistOwnerTests
{
    private static PlaylistPage Page(ArtistRef? author, string? subtitle = "Playlist • 2026") => new()
    {
        PlaylistId = "PL1", Title = "Road trip", SubtitleInfo = subtitle, Author = author, Tracks = [],
    };

    [Fact]
    public async Task TheHeader_NamesTheOwner_WithItsChannel()
    {
        var catalog = new FakeCatalog { Playlist = Page(new ArtistRef("Daft Punk", "UCdaft")) };
        var page = new PlaylistPageViewModel(catalog, null!, null!, null!, "PL1");

        await page.LoadAsync();

        Assert.Equal(new ArtistRef("Daft Punk", "UCdaft"), page.Owner);
        Assert.Equal("Playlist • 2026", page.Details);
    }

    [Fact]
    public void AnOwnerWithoutAChannel_IsStillNamed()
    {
        Assert.Equal(new ArtistRef("YouTube Music", null), PlaylistPageViewModel.OwnerOf(Page(new ArtistRef("YouTube Music", null))));
    }

    [Fact]
    public void ACollaborativePlaylist_HasNoOwner()
    {
        Assert.Null(PlaylistPageViewModel.OwnerOf(Page(null)));
    }

    [Fact]
    public void ASubtitleThatAlreadyNamesTheOwner_IsNotRepeated()
    {
        Assert.Null(PlaylistPageViewModel.OwnerOf(Page(new ArtistRef("Mira", "UCmira"), "Playlist • Mira • 2024")));
        // A title-like word inside another part is not the owner's part.
        Assert.NotNull(PlaylistPageViewModel.OwnerOf(Page(new ArtistRef("Play", "UCplay"))));
    }
}
