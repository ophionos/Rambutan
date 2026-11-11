using Rambutan.Core.Layout;

namespace Rambutan.Core.Tests;

/// <summary>A track row's columns at fixed positions: the liked mark, then the
/// album, each only while the title keeps its minimum; the title and album grow
/// together to their maximums, and past that the row ends.</summary>
public class TrackRowFitTests
{
    /// <summary>The album page's fixed parts: number 40, actions 112, duration 56.</summary>
    private const double AlbumFixed = 208;

    /// <summary>Library's Songs: cover 52, marker 14, actions 112, duration 56.</summary>
    private const double SongsFixed = 234;

    private const double MaxContent =
        SongsFixed + TrackRowColumns.MaxTitle + TrackRowColumns.Liked + TrackRowColumns.AlbumGap + TrackRowColumns.MaxAlbum;

    [Fact]
    public void Narrow_OnlyTheTitleAndTheFixedParts_FillingTheRow()
    {
        var fit = TrackRowColumns.Fit(500, AlbumFixed, hasAlbum: false, hasLiked: true);

        Assert.False(fit.ShowsLiked);
        Assert.False(fit.ShowsAlbum);
        Assert.Equal(500 - AlbumFixed, fit.Title, 6);
        Assert.Equal(500, fit.Content, 6);
    }

    [Fact]
    public void NarrowerThanTheFixedParts_TheRowStillFitsTheList()
    {
        var fit = TrackRowColumns.Fit(150, SongsFixed, hasAlbum: true, hasLiked: true);

        Assert.Equal(0, fit.Title);
        Assert.Equal(150, fit.Content, 6);
    }

    [Fact]
    public void Mid_TheLikedMarkShowsFirst_ThenTheAlbumWithBothAboveTheirMinimums()
    {
        // 800 - 234 = 566: the mark takes 24, which leaves the album its 16 + 160
        // and the title 366, so 46 to share.
        var fit = TrackRowColumns.Fit(800, SongsFixed, hasAlbum: true, hasLiked: true);

        Assert.True(fit.ShowsLiked);
        Assert.True(fit.ShowsAlbum);
        Assert.Equal(320 + 46 * 200.0 / 360, fit.Title, 6);
        Assert.Equal(160 + 46 * 160.0 / 360, fit.Album, 6);
        Assert.Equal(800, fit.Content, 6);
    }

    [Fact]
    public void AnAlbumPageAtMidWidth_StopsTheTitleAtItsMaximum()
    {
        var fit = TrackRowColumns.Fit(900, AlbumFixed, hasAlbum: false, hasLiked: true);

        Assert.True(fit.ShowsLiked);
        Assert.False(fit.ShowsAlbum);
        Assert.Equal(TrackRowColumns.MaxTitle, fit.Title);
        Assert.Equal(AlbumFixed + TrackRowColumns.MaxTitle + TrackRowColumns.Liked, fit.Content, 6);
    }

    [Fact]
    public void A1720WidePane_EndsTheRowWellBeforeItsEdge()
    {
        // The pane less the list's padding (4 + 16) and the item's (8 + 8).
        var fit = TrackRowColumns.Fit(1720 - 20 - 16, SongsFixed, hasAlbum: true, hasLiked: true);

        Assert.Equal(TrackRowColumns.MaxTitle, fit.Title, 6);
        Assert.Equal(TrackRowColumns.MaxAlbum, fit.Album, 6);
        Assert.True(fit.ShowsLiked);
        Assert.Equal(MaxContent, fit.Content, 6);
        Assert.Equal(1114, fit.Content, 6);
    }

    [Theory]
    [InlineData(1300)]
    [InlineData(2560)]
    [InlineData(5000)]
    public void VeryWide_TheRowKeepsItsMaximum(double width)
    {
        var fit = TrackRowColumns.Fit(width, SongsFixed, hasAlbum: true, hasLiked: true);

        Assert.Equal(MaxContent, fit.Content, 6);
    }

    [Fact]
    public void TitleAndAlbum_ReachTheirMaximumsTogether()
    {
        var justBelow = TrackRowColumns.Fit(MaxContent - 1, SongsFixed, hasAlbum: true, hasLiked: true);

        Assert.True(justBelow.Title < TrackRowColumns.MaxTitle);
        Assert.True(justBelow.Album < TrackRowColumns.MaxAlbum);
    }

    [Theory]
    [InlineData(AlbumFixed, false, true)]
    [InlineData(SongsFixed, true, true)]
    [InlineData(SongsFixed, true, false)]
    [InlineData(SongsFixed, false, false)]
    public void Widening_NeverRemovesAColumn_NorShrinksTheRow(double fixedWidth, bool hasAlbum, bool hasLiked)
    {
        var before = TrackRowColumns.Fit(1, fixedWidth, hasAlbum, hasLiked);
        for (double width = 1.5; width <= 3000; width += 0.5)
        {
            var fit = TrackRowColumns.Fit(width, fixedWidth, hasAlbum, hasLiked);
            Assert.True(fit.Content >= before.Content, $"{width}: {fit.Content} < {before.Content}");
            Assert.True(fit.ShowsLiked || !before.ShowsLiked, $"{width}: the liked mark went");
            Assert.True(fit.ShowsAlbum || !before.ShowsAlbum, $"{width}: the album went");
            before = fit;
        }
    }

    [Theory]
    [InlineData(AlbumFixed, false, true)]
    [InlineData(SongsFixed, true, true)]
    [InlineData(SongsFixed, true, false)]
    public void AColumnShowsOnlyWhileTheTitleKeepsItsMinimum_AndTheTitleNeverPassesItsMaximum(
        double fixedWidth, bool hasAlbum, bool hasLiked)
    {
        for (double width = 0.5; width <= 3000; width += 0.5)
        {
            var fit = TrackRowColumns.Fit(width, fixedWidth, hasAlbum, hasLiked);
            Assert.True(fit.Title <=TrackRowColumns.MaxTitle, $"{width}: title {fit.Title}");
            Assert.True(fit.Content <= width + 1e-9, $"{width}: content {fit.Content}");
            if (fit.ShowsLiked || fit.ShowsAlbum)
                Assert.InRange(fit.Title, TrackRowColumns.MinTitle, TrackRowColumns.MaxTitle);
            if (fit.ShowsAlbum)
                Assert.InRange(fit.Album, TrackRowColumns.MinAlbum, TrackRowColumns.MaxAlbum);
            else
                Assert.Equal(0, fit.Album);
            // The liked mark comes first: no album without it in a list that has both.
            if (hasLiked && fit.ShowsAlbum)
                Assert.True(fit.ShowsLiked, $"{width}: album without the mark");
            Assert.Equal(fit.Content, Math.Min(width, fixedWidth + fit.Title
                + (fit.ShowsLiked ? TrackRowColumns.Liked : 0)
                + (fit.ShowsAlbum ? TrackRowColumns.AlbumGap + fit.Album : 0)), 6);
        }
    }

    [Fact]
    public void AListWithoutTheColumns_NeverShowsThem()
    {
        var fit = TrackRowColumns.Fit(3000, SongsFixed, hasAlbum: false, hasLiked: false);

        Assert.False(fit.ShowsLiked);
        Assert.False(fit.ShowsAlbum);
        Assert.Equal(SongsFixed + TrackRowColumns.MaxTitle, fit.Content, 6);
    }

    [Theory]
    [InlineData(double.NaN, 208)]
    [InlineData(0, 208)]
    [InlineData(-10, 208)]
    [InlineData(double.PositiveInfinity, 208)]
    [InlineData(1200, double.NaN)]
    public void AWidthThatIsNotOne_LeavesTheRowUnsized(double width, double fixedWidth)
    {
        var fit = TrackRowColumns.Fit(width, fixedWidth, hasAlbum: true, hasLiked: true);

        Assert.True(double.IsNaN(fit.Content));
        Assert.False(fit.ShowsLiked);
        Assert.False(fit.ShowsAlbum);
    }
}
