using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;

namespace Rambutan.App.Views;

public partial class PlaylistPickDialog : Window
{
    // Designer only
    public PlaylistPickDialog()
    {
        InitializeComponent();
    }

    public PlaylistPickDialog(PlaylistPickOptions options) : this()
    {
        YouTubeGroup.IsVisible = options.OffersYouTube;
        YouTubeList.ItemsSource = options.YouTube;
        YouTubeList.IsVisible = options.YouTube.Count > 0;
        Show(YouTubeNote, options.YouTubeNote);
        YouTubeCreate.IsVisible = options.CanCreateOnYouTube;

        LocalGroup.IsVisible = options.OffersLocal;
        LocalList.ItemsSource = options.Local;
        LocalList.IsVisible = options.Local.Count > 0;
        LocalList.IsEnabled = !options.LocalReadOnly;
        Show(LocalNote, options.LocalNote);
        LocalCreate.IsVisible = !options.LocalReadOnly;

        // Both groups share the height; one alone takes all of it.
        var both = options.OffersYouTube && options.OffersLocal;
        Grid.SetRow(LocalGroup, both ? 2 : 0);
        if (!both)
        {
            Groups.RowDefinitions = new RowDefinitions("*");
            if (options.OffersLocal)
                Height = 400;
        }
        else if (YouTubeList.IsVisible)
        {
            // The local group is usually the shorter list: it takes what it needs,
            // up to half, and the YouTube Music list scrolls in the rest.
            Groups.RowDefinitions = new RowDefinitions("*,16,Auto");
            Groups.RowDefinitions[2].MaxHeight = 220;
        }
        else
        {
            // Only a note above: the local list takes the room.
            Groups.RowDefinitions = new RowDefinitions("Auto,16,*");
        }
    }

    private static void Show(TextBlock note, string? text)
    {
        note.Text = text;
        note.IsVisible = text is not null;
    }

    private void OnYouTubeSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (YouTubeList.SelectedItem is not null)
            LocalList.SelectedItem = null;
    }

    private void OnLocalSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (LocalList.SelectedItem is not null)
            YouTubeList.SelectedItem = null;
    }

    private void OnAdd(object? sender, RoutedEventArgs e)
    {
        if (YouTubeList.SelectedItem is PlaylistEntry playlist)
            Close(new PlaylistPickResult(playlist, null));
        else if (LocalList.SelectedItem is LocalPlaylist local)
            Close(new PlaylistPickResult(null, null, ExistingLocal: local));
    }

    private void OnListDoubleTapped(object? sender, TappedEventArgs e) => OnAdd(sender, e);

    private void OnCreateOnYouTube(object? sender, RoutedEventArgs e)
    {
        if (LocalPlaylists.CleanName(YouTubeNameBox.Text) is { } name)
            Close(new PlaylistPickResult(null, name));
    }

    private void OnCreateLocal(object? sender, RoutedEventArgs e)
    {
        if (LocalPlaylists.CleanName(LocalNameBox.Text) is { } name)
            Close(new PlaylistPickResult(null, null, NewLocalName: name));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
