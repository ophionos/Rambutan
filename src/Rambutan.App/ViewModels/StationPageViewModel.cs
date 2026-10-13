namespace Rambutan.App.ViewModels;

/// <summary>
/// A Last.fm station's own page, opened from its shelf's "See all" or
/// from an artist's page. It shows the station's rows, all of them, and
/// its refresh is the shelf's, so the two never show different samples.
/// </summary>
public sealed class StationPageViewModel(StationShelfViewModel station) : ViewModelBase
{
    public StationShelfViewModel Station { get; } = station;
}
