using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;

namespace Rambutan.App.Controls;

/// <summary>
/// A track row's "liked" mark: a filled thumb that shows only while the track's
/// rating is Like, from <see cref="TrackRatings"/> as <see cref="RateButton"/>
/// reads it, so the mark and the row's button always agree. An unknown rating
/// (signed out, or a source that didn't say) shows nothing, as unknown must never
/// read as a state. Visual only and never a tab stop: the row's Like button says
/// the state to a screen reader.
/// </summary>
public sealed class LikedMark : PathIcon
{
    public static readonly StyledProperty<TrackInfo?> TrackProperty =
        AvaloniaProperty.Register<LikedMark, TrackInfo?>(nameof(Track));

    private TrackRatings? _subscribed;

    public LikedMark()
    {
        Focusable = false;
        IsHitTestVisible = false;
        Update();
    }

    protected override Type StyleKeyOverride => typeof(PathIcon);

    public TrackInfo? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Per-instance override of <see cref="RateButton.Default"/> (tests).</summary>
    public TrackRatings? Ratings { get; set; }

    private TrackRatings? Source => Ratings ?? RateButton.Default;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackProperty)
            Update();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Static source, so subscribe only while shown: rows are recycled and a
        // detached mark must not stay reachable from the ratings' event.
        _subscribed = Source;
        if (_subscribed is not null)
            _subscribed.Changed += OnRatingChanged;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_subscribed is not null)
            _subscribed.Changed -= OnRatingChanged;
        _subscribed = null;
    }

    private void OnRatingChanged(string videoId)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            if (videoId == Track?.VideoId)
                Update();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (videoId == Track?.VideoId)
                    Update();
            });
        }
    }

    private void Update() =>
        IsVisible = Track is { } track && Source?.Of(track) == Rating.Like;
}
