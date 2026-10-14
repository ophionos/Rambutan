using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Rambutan.App.Controls;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class NowPlayingView : UserControl
{
    /// <summary>The widest the lyrics column gets: wider, a line is hard to
    /// follow from one end to the other.</summary>
    public const double LyricsMaxWidth = 760;

    /// <summary>Below this stage width the visualizer band shrinks, so the lyrics
    /// keep a few more lines.</summary>
    public const double CompactWidth = 720;

    private NowPlayingViewModel? _vm;

    public NowPlayingView()
    {
        InitializeComponent();
        Stage.SizeChanged += (_, _) => ApplyLayout();
    }

    /// <summary>How the stage is arranged now.</summary>
    public enum StageLayout { ArtOnly, Overlay }

    public StageLayout CurrentLayout { get; private set; } = StageLayout.ArtOnly;

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_vm is not null)
            _vm.PropertyChanged -= OnViewModelChanged;
        _vm = DataContext as NowPlayingViewModel;
        if (_vm is not null)
            _vm.PropertyChanged += OnViewModelChanged;

        base.OnDataContextChanged(e);
        ApplyLayout();
    }

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(NowPlayingViewModel.IsLyricsPaneVisible) or nameof(NowPlayingViewModel.ShowsRing))
            ApplyLayout();
    }

    private void ApplyLayout()
    {
        var width = Stage.Bounds.Width;
        var layout = _vm?.IsLyricsPaneVisible == true ? StageLayout.Overlay : StageLayout.ArtOnly;
        CurrentLayout = layout;

        Stage.RowDefinitions.Clear();
        switch (layout)
        {
            case StageLayout.ArtOnly:
                // Every child back in a cell that exists: a Grid measures hidden
                // children too, and a stale row index throws.
                Place(Identity, 0);
                Place(LyricsHost, 0);
                Identity.VerticalAlignment = VerticalAlignment.Center;
                Identity.Margin = default;
                SetArt(300);
                TitleLink.FontSize = 24;
                break;

            case StageLayout.Overlay:
                // The lyrics take the stage: a centred column over the shell's
                // blurred artwork, under a compact title. The artwork itself is left
                // to the backdrop and the player bar rather than drawn a second time.
                Stage.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                Stage.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
                Place(Identity, 0);
                Place(LyricsHost, 1);
                Identity.VerticalAlignment = VerticalAlignment.Top;
                Identity.Margin = new Thickness(0, 0, 0, 16);
                SetArt(0);
                TitleLink.FontSize = 20;
                break;
        }

        // Narrow, the lyrics need the height more than the visualizer does.
        var compact = layout is StageLayout.Overlay && width > 0 && width < CompactWidth;
        VisualizerBand.Height = compact ? 64 : 110;
        VisualizerBand.Margin = new Thickness(24, compact ? 16 : 28, 24, 0);
    }

    /// <summary>Sizes the artwork; zero folds it away. (Its IsVisible stays bound
    /// to whether there is artwork at all.)</summary>
    private void SetArt(double size)
    {
        Art.Width = Art.Height = size;
        // The ring reaches past the artwork; the slot keeps the title, and the
        // header above, clear of it.
        var ring = size > 0 && _vm?.ShowsRing == true ? RadialRenderer.Margin : 0;
        ArtSlot.Margin = size > 0 ? new Thickness(0, ring, 0, 28 + ring) : default;
        Ring.Margin = new Thickness(-RadialRenderer.Margin);
    }

    private static void Place(Control control, int row) => Grid.SetRow(control, row);
}
