using Avalonia.Automation;
using Avalonia.Controls;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Tests;

/// <summary>Like/dislike as toggles with visible state.</summary>
public class RatingTests
{
    private readonly FakeCatalog _catalog = new();
    private readonly TrackRatings _ratings;
    private readonly TrackActionsViewModel _actions;
    private readonly ToastHostViewModel _toasts = new((_, _) => new NoTimer());
    private readonly List<string> _reports = [];

    public RatingTests()
    {
        _ratings = new TrackRatings(_catalog);
        // Rating never touches the queue or playback.
        _actions = new TrackActionsViewModel(_catalog, null!, null!, _ratings, _toasts);
        _toasts.Toasts.CollectionChanged += (_, e) =>
            _reports.AddRange(e.NewItems?.Cast<Toast>().Select(t => t.Message) ?? []);
    }

    private static TrackInfo Track(Rating? rating = null) =>
        new() { VideoId = "v1", Title = "Instant Crush", Artists = [], Rating = rating };

    [Fact]
    public async Task Like_UnratedTrack_LikesAndSaysSo()
    {
        await _actions.LikeCommand.ExecuteAsync(Track(Rating.None));

        Assert.Equal([("v1", Rating.Like)], _catalog.Rated);
        Assert.Equal(Rating.Like, _ratings.Of(Track(Rating.None)));
        Assert.Equal(["Liked “Instant Crush”"], _reports);
    }

    [Fact]
    public async Task Like_LikedTrack_RemovesTheLike()
    {
        await _actions.LikeCommand.ExecuteAsync(Track(Rating.Like));

        // Re-sending Like was the N3 bug; the second press un-likes.
        Assert.Equal([("v1", Rating.None)], _catalog.Rated);
        Assert.Equal(["Removed your like from “Instant Crush”"], _reports);
    }

    [Fact]
    public async Task Dislike_LikedTrack_SwitchesToDislike()
    {
        await _actions.DislikeCommand.ExecuteAsync(Track(Rating.Like));

        Assert.Equal([("v1", Rating.Dislike)], _catalog.Rated);
        Assert.Equal(["Disliked “Instant Crush”"], _reports);
    }

    [Fact]
    public async Task Like_Failure_RevertsAndSaysWhat()
    {
        _catalog.Rate = (_, _) => Task.FromException(new HttpRequestException("offline"));

        await _actions.LikeCommand.ExecuteAsync(Track(Rating.None));

        Assert.Equal(Rating.None, _ratings.Of(Track(Rating.None)));
        Assert.Equal(["Couldn't like “Instant Crush”, so it's unchanged: offline"], _reports);
        // An error toast (longer-lived, announced as an error), not the status strip.
        Assert.True(_toasts.Toasts[^1].IsError);
    }

    [Fact]
    public async Task Like_SignedOut_RevertsAndAsksToSignIn()
    {
        _catalog.Rate = (_, _) => Task.FromException(new NotSignedInException());

        await _actions.LikeCommand.ExecuteAsync(Track());

        Assert.Null(_ratings.Of(Track()));
        Assert.Equal(["Sign in to rate songs."], _reports);
        Assert.True(_toasts.Toasts[^1].IsError);
    }

    [Fact]
    public void Like_StaysAvailableWhileARequestIsOut()
    {
        var pending = new TaskCompletionSource();
        _catalog.Rate = (_, _) => pending.Task;

        _ = _actions.LikeCommand.ExecuteAsync(Track());

        // One command serves every row: it must not grey out every thumb.
        Assert.True(_actions.LikeCommand.CanExecute(Track()));
        pending.SetResult();
    }

    private (Window Window, RateButton Like, RateButton Dislike) Show(TrackInfo track)
    {
        var like = new RateButton { Kind = Rating.Like, Track = track, Ratings = _ratings };
        var dislike = new RateButton { Kind = Rating.Dislike, Track = track, Ratings = _ratings };
        var window = new Window { Content = new StackPanel { Children = { like, dislike } } };
        window.Show();
        return (window, like, dislike);
    }

    private static string? Name(RateButton button) => AutomationProperties.GetName(button);

    [Fact]
    public void Button_ShowsTheSnapshotRating() => Headless.Run(() =>
    {
        var (window, like, dislike) = Show(Track(Rating.Like));

        Assert.True(like.IsRated);
        Assert.Contains(":rated", like.Classes);
        Assert.Equal("Remove like", Name(like));
        Assert.Equal("Remove like", ToolTip.GetTip(like));
        Assert.False(dislike.IsRated);
        Assert.Equal("Dislike", Name(dislike));
        window.Close();
    });

    [Fact]
    public void Button_UnknownRating_DrawsUnrated() => Headless.Run(() =>
    {
        var (window, like, dislike) = Show(Track(rating: null));

        Assert.False(like.IsRated);
        Assert.False(dislike.IsRated);
        Assert.Equal("Like", Name(like));
        window.Close();
    });

    [Fact]
    public void Button_FollowsPressesOptimisticallyAndUndoesFailures() => Headless.Run(() =>
    {
        var pending = new TaskCompletionSource();
        _catalog.Rate = (_, _) => pending.Task;
        var (window, like, dislike) = Show(Track(Rating.None));

        var press = _ratings.ToggleAsync(Track(Rating.None), Rating.Like);
        Assert.True(like.IsRated); // before the server answers
        Assert.False(dislike.IsRated);

        pending.SetException(new HttpRequestException("offline"));
        Assert.True(press.IsFaulted);
        Assert.False(like.IsRated); // the refused state doesn't stick
        Assert.Equal("Like", Name(like));
        window.Close();
    });

    [Fact]
    public void Button_OtherRecordsOfTheSameSongAgree() => Headless.Run(() =>
    {
        var (window, like, _) = Show(Track(Rating.None));
        var elsewhere = Track(Rating.None) with { Album = "Random Access Memories" };

        _ = _ratings.ToggleAsync(elsewhere, Rating.Like);

        Assert.True(like.IsRated);
        window.Close();
    });

    private sealed class NoTimer : IDisposable
    {
        public void Dispose() { }
    }
}
