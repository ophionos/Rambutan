using System;
using Rambutan.Core.Models;

namespace Rambutan.App.ViewModels;

/// <summary>
/// One of the two mixes an artist page's header starts, and the toasts that say
/// how it went. Each key takes the same arguments as its counterpart: Starting
/// <c>{title}</c>, Started <c>{title}</c> and <c>{count}</c>, None <c>{name}</c>,
/// Failed <c>{error}</c>.
/// </summary>
public sealed record ArtistMix(
    Func<ArtistPage, MixSeed?> SeedOf, string Starting, string Started, string None, string Failed)
{
    /// <summary>YouTube Music's Mix: the artist's radio, with similar artists.</summary>
    public static readonly ArtistMix Radio = new(
        a => a.Mix, "toast.mix.starting", "toast.mix.started", "toast.artist.noMix", "toast.mix.failed");

    /// <summary>YouTube Music's Shuffle: a mix of the artist's own songs. Not the
    /// album and playlist Shuffle, which shuffles the tracks the page lists.</summary>
    public static readonly ArtistMix Shuffle = new(
        a => a.Shuffle, "toast.artist.shuffleStarting", "toast.artist.shuffleStarted",
        "toast.artist.noShuffle", "toast.artist.shuffleFailed");
}
