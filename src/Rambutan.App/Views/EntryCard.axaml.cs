using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.Core.Models;

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

    public static readonly DirectProperty<EntryCard, bool> IsLoadingProperty =
        AvaloniaProperty.RegisterDirect<EntryCard, bool>(nameof(IsLoading), o => o.IsLoading);

    public static readonly DirectProperty<EntryCard, string?> PlayNameProperty =
        AvaloniaProperty.RegisterDirect<EntryCard, string?>(nameof(PlayName), o => o.PlayName);

    private bool _isLoading;
    private string? _playName;

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

    /// <summary>Whether the card's album or playlist is loading for Play, Shuffle
    /// or Add to queue, from this card or another showing the same one.</summary>
    public bool IsLoading
    {
        get => _isLoading;
        private set => SetAndRaise(IsLoadingProperty, ref _isLoading, value);
    }

    /// <summary>The play button's spoken name, which says when it is loading.</summary>
    public string? PlayName
    {
        get => _playName;
        private set => SetAndRaise(PlayNameProperty, ref _playName, value);
    }

    public EntryCard()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => UpdateLoading();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (CollectionMenu.Actions is { } actions)
            actions.LoadingChanged += UpdateLoading;
        UpdateLoading();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (CollectionMenu.Actions is { } actions)
            actions.LoadingChanged -= UpdateLoading;
        base.OnDetachedFromVisualTree(e);
    }

    private void UpdateLoading()
    {
        var entry = DataContext as CatalogEntry;
        IsLoading = CollectionMenu.Actions?.IsLoading(entry) ?? false;
        PlayName = entry is null
            ? null
            : Strings.Format(IsLoading ? "card.loadingNamed" : "common.playNamed", ("value", entry.Title));
    }
}
