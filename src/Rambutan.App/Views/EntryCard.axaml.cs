using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Rambutan.App.Views;

/// <summary>
/// A catalog card (album, playlist, artist, song). The host page supplies what
/// a click does (<see cref="OpenCommand"/>) and what the hover play button does
/// (<see cref="PlayCommand"/>); both receive the card's <c>CatalogEntry</c>.
/// </summary>
public partial class EntryCard : UserControl
{
    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<EntryCard, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> PlayCommandProperty =
        AvaloniaProperty.Register<EntryCard, ICommand?>(nameof(PlayCommand));

    public ICommand? OpenCommand
    {
        get => GetValue(OpenCommandProperty);
        set => SetValue(OpenCommandProperty, value);
    }

    public ICommand? PlayCommand
    {
        get => GetValue(PlayCommandProperty);
        set => SetValue(PlayCommandProperty, value);
    }

    public EntryCard()
    {
        InitializeComponent();
    }
}
