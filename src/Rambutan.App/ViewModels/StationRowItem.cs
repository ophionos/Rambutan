using System;
using Rambutan.App.Localization;
using Rambutan.Core.Models;
using Rambutan.Core.Stations;

namespace Rambutan.App.ViewModels;

/// <summary>
/// One row of a station's page. Every track the station named is
/// listed: one with nothing to play is marked, never dropped, and one that
/// plays Last.fm's own YouTube link says so.
/// </summary>
public sealed class StationRowItem(StationRow row)
{
    public StationRow Row { get; } = row;

    /// <summary>What plays; null when nothing does.</summary>
    public TrackInfo? Track => Row.Track;

    public bool IsPlayable => Row.Track is not null;

    public string Title => Row.Track?.Title ?? Row.Source.Title;

    public string Artist => Row.Track?.ArtistDisplay ?? Row.Source.Artist;

    public TimeSpan? Duration => Row.Track?.Duration ?? Row.Source.Duration;

    public string? ThumbnailUrl => Row.Track?.ThumbnailUrl;

    /// <summary>Why the row isn't a YouTube Music song; null when it is.</summary>
    public string? Note { get; } = row.Match switch
    {
        StationMatch.NotFound => Strings.Get("station.row.notFound"),
        StationMatch.ServiceVideo => Strings.Get("station.row.video"),
        _ => null,
    };

    /// <summary>The screen-reader name: the title, the artist and the note.</summary>
    public string AccessibleName => Note is null ? $"{Title}, {Artist}" : $"{Title}, {Artist}, {Note}";

    public override string ToString() => AccessibleName;
}
