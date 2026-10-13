using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Views;

public partial class ArtistPage : UserControl
{
    // The stations the page listens to while it is shown.
    private HomeStations? _stations;

    public ArtistPage()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Listen();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopListening();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        StopListening();
        if (this.IsAttachedToVisualTree())
            Listen();
    }

    // The station button follows the setting and the Last.fm connection, which
    // can change while the page waits in the back stack.
    private void Listen()
    {
        if (DataContext is not ArtistPageViewModel { Stations: { } stations } vm)
            return;
        _stations = stations;
        stations.Changed += OnStationsChanged;
        vm.StationsChanged();
    }

    private void StopListening()
    {
        if (_stations is not null)
            _stations.Changed -= OnStationsChanged;
        _stations = null;
    }

    private void OnStationsChanged() => (DataContext as ArtistPageViewModel)?.StationsChanged();
}
