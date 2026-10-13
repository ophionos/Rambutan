using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Rambutan.App.Controls;

namespace Rambutan.App.Views;

/// <summary>
/// A titled, horizontally scrolling row of <see cref="EntryCard"/>s. The chevrons
/// page by the visible width (<see cref="ScrollPaging.PageTarget"/>), are shown
/// only when the row overflows, and disable at either end.
/// </summary>
public partial class ShelfView : UserControl
{
    /// <summary>The items panel's spacing, which the page stride includes.</summary>
    private const double CardSpacing = 4;

    public static readonly StyledProperty<ICommand?> OpenCommandProperty =
        AvaloniaProperty.Register<ShelfView, ICommand?>(nameof(OpenCommand));

    public static readonly StyledProperty<ICommand?> PlayCommandProperty =
        AvaloniaProperty.Register<ShelfView, ICommand?>(nameof(PlayCommand));

    /// <summary>"See all" in the header, with <see cref="SeeAllCommandParameter"/>;
    /// no button while null.</summary>
    public static readonly StyledProperty<ICommand?> SeeAllCommandProperty =
        AvaloniaProperty.Register<ShelfView, ICommand?>(nameof(SeeAllCommand));

    public static readonly StyledProperty<object?> SeeAllCommandParameterProperty =
        AvaloniaProperty.Register<ShelfView, object?>(nameof(SeeAllCommandParameter));

    public ICommand? SeeAllCommand
    {
        get => GetValue(SeeAllCommandProperty);
        set => SetValue(SeeAllCommandProperty, value);
    }

    public object? SeeAllCommandParameter
    {
        get => GetValue(SeeAllCommandParameterProperty);
        set => SetValue(SeeAllCommandParameterProperty, value);
    }

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

    public ShelfView()
    {
        InitializeComponent();

        PageBack.Click += (_, e) => Page(forward: false, e);
        PageForward.Click += (_, e) => Page(forward: true, e);
        // Extent and viewport changes arrive here too, so a resize or late-loading
        // cards update the chevrons as well as scrolling does.
        Scroller.ScrollChanged += (_, _) => UpdateChevrons();
    }

    private void Page(bool forward, RoutedEventArgs e)
    {
        e.Handled = true;
        var first = Items.ContainerFromIndex(0);
        var stride = first is null ? 0 : first.Bounds.Width + CardSpacing;
        var target = ScrollPaging.PageTarget(
            Scroller.Offset.X, Scroller.Viewport.Width, Scroller.Extent.Width, stride, forward);
        Scroller.Offset = new Vector(target, Scroller.Offset.Y);
    }

    private void UpdateChevrons()
    {
        var (offset, viewport, extent) = (Scroller.Offset.X, Scroller.Viewport.Width, Scroller.Extent.Width);
        var overflows = extent > viewport + 0.5;

        PageBack.IsVisible = PageForward.IsVisible = overflows;
        PageBack.IsEnabled = ScrollPaging.CanPageBack(offset);
        PageForward.IsEnabled = ScrollPaging.CanPageForward(offset, viewport, extent);
    }
}
