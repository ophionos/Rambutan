using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Views;

public partial class PlaylistPickDialog : Window
{
    // Designer only
    public PlaylistPickDialog()
    {
        InitializeComponent();
    }

    public PlaylistPickDialog(IReadOnlyList<PlaylistEntry> playlists) : this()
    {
        List.ItemsSource = playlists;
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is PlaylistEntry playlist)
            Close(new PlaylistPickResult(playlist, null));
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => OnAdd(sender, e);

    private void OnCreate(object? sender, RoutedEventArgs e)
    {
        var name = NewNameBox.Text?.Trim();
        if (!string.IsNullOrEmpty(name))
            Close(new PlaylistPickResult(null, name));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
