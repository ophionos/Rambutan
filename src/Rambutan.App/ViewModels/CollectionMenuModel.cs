using System.Collections.Generic;
using Rambutan.App.Localization;
using Rambutan.Core.Models;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The context menu of an album, playlist or artist card, built in one place.
/// Groups come back in display order; the control draws a separator between
/// them. A song card has the track menu instead, so it gets no groups here.
/// </summary>
public static class CollectionMenuModel
{
    /// <param name="inLibrary">The card is on a Library tab, so it is known to be
    /// saved or subscribed. Elsewhere a card doesn't know, and the menu offers
    /// only to add it.</param>
    public static IReadOnlyList<IReadOnlyList<MenuEntry>> Build(
        CatalogEntry entry, CollectionActionsViewModel actions, bool inLibrary = false)
    {
        if (entry is SongEntry)
            return [];

        // Entries that load the page are disabled while it loads: the card shows
        // the load, and a second request for it would be dropped.
        var idle = !actions.IsLoading(entry);
        var groups = new List<IReadOnlyList<MenuEntry>>();
        var library = new List<MenuEntry>();
        switch (entry)
        {
            case AlbumEntry or PlaylistEntry:
                groups.Add(
                [
                    new(Strings.Get("common.play"), "Icon.Play", actions.PlayCommand, entry, idle),
                    new(Strings.Get("common.shuffle"), "Icon.Shuffle", actions.ShuffleCommand, entry, idle),
                    new(Strings.Get("menu.playNext"), "Icon.Next", actions.PlayNextCommand, entry, idle),
                    new(Strings.Get("common.addToQueue"), "Icon.Add", actions.AddToQueueCommand, entry, idle),
                ]);
                // A playlist on the Library tab may be the user's own, which is
                // deleted rather than removed, so it gets neither entry.
                if (actions.CanChangeLibrary && !(inLibrary && entry is PlaylistEntry))
                {
                    library.Add(inLibrary
                        ? new(Strings.Get("menu.removeFromLibrary"), "Icon.LibraryAdded",
                            actions.RemoveFromLibraryCommand, entry, idle)
                        : new(Strings.Get("menu.saveToLibrary"), "Icon.LibraryAdd",
                            actions.SaveToLibraryCommand, entry, idle));
                }
                if (actions.CanDownload)
                    library.Add(new(Strings.Get("menu.download"), "Icon.Download", actions.DownloadAllCommand, entry, idle));
                break;

            case ArtistEntry:
                // A card doesn't know whether the artist's page has a mix or a
                // shuffle; one without says so when asked.
                List<MenuEntry> play =
                [
                    new(Strings.Get("artist.playTopSongs"), "Icon.Play", actions.PlayCommand, entry, idle),
                    new(Strings.Get("menu.startMix"), "Icon.Radio", actions.StartMixCommand, entry, idle),
                    new(Strings.Get("artist.shuffleSongs"), "Icon.Shuffle", actions.ShuffleSongsCommand, entry, idle),
                ];
                if (actions.CanOpenStation)
                    play.Add(new(Strings.Get("artist.lastFmStation"), "Icon.Radio", actions.OpenStationCommand, entry));
                groups.Add(play);
                if (actions.CanChangeLibrary)
                {
                    library.Add(inLibrary
                        ? new(Strings.Get("artist.unsubscribe"), "Icon.LibraryAdded",
                            actions.RemoveFromLibraryCommand, entry, idle)
                        : new(Strings.Get("artist.subscribe"), "Icon.LibraryAdd",
                            actions.SaveToLibraryCommand, entry, idle));
                }
                break;
        }
        if (library.Count > 0)
            groups.Add(library);

        // An id whose link wouldn't open keeps the entry, disabled, as the track
        // menu does for a track without an album.
        var navigate = new List<MenuEntry>();
        if (entry is AlbumEntry)
            navigate.Add(new(Strings.Get("common.goToArtist"), "Icon.Person", actions.GoToArtistCommand, entry, idle));
        navigate.Add(new(Strings.Get("menu.share"), "Icon.Share", actions.ShareCommand, entry,
            IsEnabled: CollectionActionsViewModel.ShareLinkFor(entry) is not null));
        groups.Add(navigate);
        return groups;
    }
}
