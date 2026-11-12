using System.Collections;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;

namespace Rambutan.App.Views;

/// <summary>An artist's top songs as track rows, for the artist page and the
/// album page's side column.</summary>
public partial class TopSongRows : UserControl
{
    public static readonly StyledProperty<IEnumerable?> SongsProperty =
        AvaloniaProperty.Register<TopSongRows, IEnumerable?>(nameof(Songs));

    /// <summary>Plays from a row: its <c>TrackInfo</c> is the parameter.</summary>
    public static readonly StyledProperty<ICommand?> PlayFromCommandProperty =
        AvaloniaProperty.Register<TopSongRows, ICommand?>(nameof(PlayFromCommand));

    /// <summary>The list's screen-reader name.</summary>
    public static readonly StyledProperty<string?> ListNameProperty =
        AvaloniaProperty.Register<TopSongRows, string?>(nameof(ListName));

    public IEnumerable? Songs
    {
        get => GetValue(SongsProperty);
        set => SetValue(SongsProperty, value);
    }

    public ICommand? PlayFromCommand
    {
        get => GetValue(PlayFromCommandProperty);
        set => SetValue(PlayFromCommandProperty, value);
    }

    public string? ListName
    {
        get => GetValue(ListNameProperty);
        set => SetValue(ListNameProperty, value);
    }

    public TopSongRows()
    {
        InitializeComponent();
    }
}
