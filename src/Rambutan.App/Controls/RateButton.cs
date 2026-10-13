using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Rambutan.Core.Catalog;
using Rambutan.Core.Models;
using Rambutan.App.Localization;

namespace Rambutan.App.Controls;

/// <summary>
/// A like or dislike button that shows the track's current rating:
/// an outline thumb when the rating isn't this button's, a filled accent thumb
/// (pseudo-class <c>:rated</c>) when it is. The state comes from
/// <see cref="TrackRatings"/>, so every button for the same song agrees, including
/// while a press is in flight and after a failed one is undone.
///
/// Only state lives here. The press itself is the bound Command, as on any other
/// row button, so outcomes are reported by the view model.
/// </summary>
public sealed class RateButton : Button
{
    public static readonly StyledProperty<TrackInfo?> TrackProperty =
        AvaloniaProperty.Register<RateButton, TrackInfo?>(nameof(Track));

    public static readonly StyledProperty<Rating> KindProperty =
        AvaloniaProperty.Register<RateButton, Rating>(nameof(Kind), Rating.Like);

    public static readonly StyledProperty<double> IconSizeProperty =
        AvaloniaProperty.Register<RateButton, double>(nameof(IconSize), 20);

    /// <summary>Set once at composition time (App startup), like RemoteImage.Cache.</summary>
    public static TrackRatings? Default { get; set; }

    private readonly PathIcon _icon = new();
    private TrackRatings? _subscribed;

    public RateButton()
    {
        Content = _icon;
        Update();
    }

    // Button's styles (icon, subtle, small) apply unchanged.
    protected override Type StyleKeyOverride => typeof(Button);

    public TrackInfo? Track
    {
        get => GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    /// <summary>Like or Dislike; which rating this button presses.</summary>
    public Rating Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public double IconSize
    {
        get => GetValue(IconSizeProperty);
        set => SetValue(IconSizeProperty, value);
    }

    /// <summary>Per-instance override of <see cref="Default"/> (tests).</summary>
    public TrackRatings? Ratings { get; set; }

    private TrackRatings? Source => Ratings ?? Default;

    /// <summary>True when the track's current rating is this button's kind.</summary>
    public bool IsRated { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TrackProperty || change.Property == KindProperty ||
            change.Property == IconSizeProperty)
        {
            Update();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // Static source, so subscribe only while shown: rows are recycled and a
        // detached button must not stay reachable from the ratings' event.
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

    private void Update()
    {
        var track = Track;
        var current = track is null ? null : Source?.Of(track);
        IsRated = current == Kind && Kind is not Rating.None;
        PseudoClasses.Set(":rated", IsRated);

        var like = Kind is not Rating.Dislike;
        var key = (like ? "Icon.ThumbUp" : "Icon.ThumbDown") + (IsRated ? "" : "Outline");
        _icon.Data = this.TryFindResource(key, out var geometry) ? geometry as Geometry : null;
        _icon.Width = _icon.Height = IconSize;

        // A toggle: the label says what pressing does now.
        var label = Strings.Get((like, IsRated) switch
        {
            (true, false) => "rating.like",
            (true, true) => "rating.removeLike",
            (false, false) => "rating.dislike",
            (false, true) => "rating.removeDislike",
        });
        ToolTip.SetTip(this, label);
        AutomationProperties.SetName(this, label);
    }

    protected override void OnAttachedToLogicalTree(Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        Update(); // resources resolve once the button is in a tree
    }
}
