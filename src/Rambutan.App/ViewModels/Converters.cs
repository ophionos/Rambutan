using Avalonia.Data.Converters;
using Avalonia.Media;
using Rambutan.App.Localization;
using Rambutan.Core.Local;

namespace Rambutan.App.ViewModels;

public static class Converters
{
    public static readonly IValueConverter BoldWhenTrue =
        new FuncValueConverter<bool, FontWeight>(isTrue => isTrue ? FontWeight.Bold : FontWeight.Normal);

    // The sidebar toggle is an icon, so the tooltip carries the whole
    // affordance: it names the action, not the current state.
    public static readonly IValueConverter SidebarToggleTip =
        new FuncValueConverter<bool, string>(isOpen => Strings.Get(isOpen ? "shell.hideSidebar" : "shell.showSidebar"));

    /// <summary>A shelf's "See all", named with the shelf's title, so two of
    /// them on a page aren't the same name to a screen reader.</summary>
    public static readonly IValueConverter SeeAllName =
        new FuncValueConverter<string?, string>(title => Strings.Format("shelf.seeAllNamed", ("title", title ?? "")));

    /// <summary>A Home mood chip's screen-reader name, which says when Home is filtered to it.</summary>
    public static readonly IValueConverter HomeChipName =
        new FuncValueConverter<Rambutan.Core.Models.HomeChip?, string>(chip =>
            chip is null ? "" : chip.IsSelected ? Strings.Format("home.chip.selected", ("title", chip.Title)) : chip.Title);

    /// <summary>A mood tile's stripe: YouTube Music's colour for it, or the faint
    /// overlay where it sent none.</summary>
    public static readonly IValueConverter MoodStripe =
        new FuncValueConverter<uint?, IBrush?>(argb =>
            argb is { } c ? new SolidColorBrush(Color.FromUInt32(c)) : null);

    /// <summary>Album and playlist cards get a hover play button.</summary>
    public static readonly IValueConverter HasCardPlayButton =
        new FuncValueConverter<Rambutan.Core.Models.CatalogEntry?, bool>(EntryPlayback.HasPlayButton);

    /// <summary>A song card's track, for <c>TrackMenu.Track</c>; null on any other
    /// card, which so gets no track menu.</summary>
    public static readonly IValueConverter SongTrack =
        new FuncValueConverter<Rambutan.Core.Models.CatalogEntry?, Rambutan.Core.Models.TrackInfo?>(
            entry => (entry as Rambutan.Core.Models.SongEntry)?.Track);

    /// <summary>Artist cards get round art.</summary>
    public static readonly IValueConverter IsArtist =
        new FuncValueConverter<Rambutan.Core.Models.CatalogEntry?, bool>(entry => entry is Rambutan.Core.Models.ArtistEntry);

    /// <summary>Toasts: an error interrupts a screen reader, anything else waits
    /// for a pause.</summary>
    public static readonly IValueConverter LiveSettingFor =
        new FuncValueConverter<bool, Avalonia.Automation.AutomationLiveSetting>(isError =>
            isError ? Avalonia.Automation.AutomationLiveSetting.Assertive
                    : Avalonia.Automation.AutomationLiveSetting.Polite);

    public static readonly IValueConverter SearchTabName =
        new FuncValueConverter<SearchTab, string>(tab => Strings.Get(tab?.Filter switch
        {
            null => "search.filter.all",
            Rambutan.Core.Catalog.SearchFilter.Videos => "search.filter.videos",
            Rambutan.Core.Catalog.SearchFilter.Albums => "search.filter.albums",
            Rambutan.Core.Catalog.SearchFilter.Artists => "search.filter.artists",
            Rambutan.Core.Catalog.SearchFilter.Playlists => "search.filter.playlists",
            _ => "search.filter.songs",
        }));

    /// <summary>A Library tab's chip label, which also names its list.</summary>
    public static readonly IValueConverter LibrarySourceName =
        new FuncValueConverter<LibrarySource, string>(source => Strings.Get(source == LibrarySource.ThisComputer
            ? "library.source.thisComputer"
            : "library.source.youTubeMusic"));

    public static readonly IValueConverter LocalSourceName =
        new FuncValueConverter<LocalSource, string>(source => Strings.Get(source switch
        {
            LocalSource.Folders => "library.local.filter.folders",
            LocalSource.Downloads => "library.local.filter.downloads",
            LocalSource.Playlists => "library.local.filter.playlists",
            _ => "library.local.filter.all",
        }));

    public static readonly IValueConverter LibrarySectionName =
        new FuncValueConverter<LibrarySectionKind, string>(kind => Strings.Get(kind switch
        {
            LibrarySectionKind.Albums => "library.section.albums",
            LibrarySectionKind.Artists => "library.section.artists",
            LibrarySectionKind.Songs => "library.section.songs",
            LibrarySectionKind.History => "library.section.history",
            _ => "library.section.playlists",
        }));

    public static readonly IValueConverter LibraryEmptyTitle =
        new FuncValueConverter<LibrarySectionKind, string>(kind => Strings.Get(kind switch
        {
            LibrarySectionKind.Albums => "library.albums.emptyTitle",
            LibrarySectionKind.Artists => "library.artists.emptyTitle",
            LibrarySectionKind.Songs => "library.songs.emptyTitle",
            LibrarySectionKind.History => "library.history.emptyTitle",
            _ => "library.emptyTitle",
        }));

    public static readonly IValueConverter LibraryEmptyHint =
        new FuncValueConverter<LibrarySectionKind, string>(kind => Strings.Get(kind switch
        {
            LibrarySectionKind.Albums => "library.albums.emptyHint",
            LibrarySectionKind.Artists => "library.artists.emptyHint",
            LibrarySectionKind.Songs => "library.songs.emptyHint",
            LibrarySectionKind.History => "library.history.emptyHint",
            _ => "library.emptyHint",
        }));

    /// <summary>The album toggle names the action it takes now.</summary>
    public static readonly IValueConverter AlbumLibraryAction =
        new FuncValueConverter<bool?, string>(saved => Strings.Get(saved == true ? "album.remove" : "album.save"));

    /// <summary>The subscribe button's label shows the state, as YouTube Music's does.</summary>
    public static readonly IValueConverter SubscribeLabel =
        new FuncValueConverter<bool?, string>(subscribed =>
            Strings.Get(subscribed == true ? "artist.subscribed" : "artist.subscribe"));

    /// <summary>Its screen-reader name and tooltip name the action it takes now.</summary>
    public static readonly IValueConverter SubscribeAction =
        new FuncValueConverter<bool?, string>(subscribed =>
            Strings.Get(subscribed == true ? "artist.unsubscribe" : "artist.subscribe"));

    public static readonly IValueConverter QueuePillTip =
        new FuncValueConverter<bool, string>(isOpen => Strings.Get(isOpen ? "shell.hideQueue" : "shell.showQueue"));
}
