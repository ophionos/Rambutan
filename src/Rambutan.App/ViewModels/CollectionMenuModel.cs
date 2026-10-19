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
    public static IReadOnlyList<IReadOnlyList<MenuEntry>> Build(CatalogEntry entry, CollectionActionsViewModel actions)
    {
        if (entry is SongEntry)
            return [];

        var groups = new List<IReadOnlyList<MenuEntry>>();
        if (entry is AlbumEntry or PlaylistEntry)
        {
            // Disabled while this album or playlist loads: the card shows the load,
            // and a second request for it would be dropped.
            var idle = !actions.IsLoading(entry);
            groups.Add(
            [
                new(Strings.Get("common.play"), "Icon.Play", actions.PlayCommand, entry, idle),
                new(Strings.Get("common.shuffle"), "Icon.Shuffle", actions.ShuffleCommand, entry, idle),
                new(Strings.Get("common.addToQueue"), "Icon.Add", actions.AddToQueueCommand, entry, idle),
            ]);
        }

        // An id whose link wouldn't open keeps the entry, disabled, as the track
        // menu does for a track without an album.
        groups.Add(
        [
            new(Strings.Get("menu.share"), "Icon.Share", actions.ShareCommand, entry,
                IsEnabled: CollectionActionsViewModel.ShareLinkFor(entry) is not null),
        ]);
        return groups;
    }
}
