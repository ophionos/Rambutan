using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Controls;

/// <summary>
/// Keeps a page's scroll position on its view model, so Back returns to where the
/// page was left instead of its top. Pages are rebuilt from their
/// DataTemplate whenever they become current again, and a rebuilt list starts at
/// zero; a recycled one (album to album) would otherwise carry the previous page's
/// offset into the next.
/// <code>
/// &lt;ListBox controls:ScrollMemory.IsEnabled="True" … /&gt;   (or a ScrollViewer)
/// </code>
/// The offset is saved into <see cref="ViewModelBase.SavedScrollOffset"/> when the
/// control's page changes or its view leaves the tree, and restored as soon as the
/// content is tall enough to hold it. A wheel turn before that cancels the restore,
/// so a slow page never yanks the list away from the user.
/// </summary>
public static class ScrollMemory
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsEnabled", typeof(ScrollMemory));

    private static readonly AttachedProperty<Tracker?> TrackerProperty =
        AvaloniaProperty.RegisterAttached<Control, Tracker?>("Tracker", typeof(ScrollMemory));

    static ScrollMemory()
    {
        IsEnabledProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            control.GetValue(TrackerProperty)?.Detach();
            control.SetValue(TrackerProperty, e.NewValue is true ? new Tracker(control) : null);
        });
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);

    public static void SetIsEnabled(Control control, bool value) =>
        control.SetValue(IsEnabledProperty, value);

    private sealed class Tracker
    {
        private readonly Control _control;
        private ViewModelBase? _page;

        /// <summary>An offset still waiting for content tall enough to hold it.
        /// While one is pending, the control's own offset belongs to no page and is
        /// never saved.</summary>
        private double? _pending;

        public Tracker(Control control)
        {
            _control = control;
            control.DataContextChanged += OnDataContextChanged;
            control.DetachedFromVisualTree += OnDetached;
            control.AttachedToVisualTree += OnAttached;
            control.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, handledEventsToo: true);
            Adopt(control.DataContext as ViewModelBase);
        }

        public void Detach()
        {
            _control.DataContextChanged -= OnDataContextChanged;
            _control.DetachedFromVisualTree -= OnDetached;
            _control.AttachedToVisualTree -= OnAttached;
            _control.RemoveHandler(InputElement.PointerWheelChangedEvent, OnWheel);
            _control.LayoutUpdated -= OnLayoutUpdated;
        }

        private ScrollViewer? Viewer =>
            _control as ScrollViewer ??
            _control.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();

        private void OnDataContextChanged(object? sender, System.EventArgs e)
        {
            if (ReferenceEquals(_control.DataContext, _page))
                return;
            Save();
            Adopt(_control.DataContext as ViewModelBase);
        }

        private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            Save();
            _control.LayoutUpdated -= OnLayoutUpdated;
        }

        private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        {
            if (_pending is not null)
                _control.LayoutUpdated += OnLayoutUpdated;
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e) => Settle();

        private void Save()
        {
            if (_page is not null && _pending is null && Viewer is { } viewer)
                _page.SavedScrollOffset = viewer.Offset.Y;
        }

        private void Adopt(ViewModelBase? page)
        {
            _page = page;
            _pending = page?.SavedScrollOffset ?? 0;
            _control.LayoutUpdated -= OnLayoutUpdated;
            _control.LayoutUpdated += OnLayoutUpdated;
            TryRestore();
        }

        private void OnLayoutUpdated(object? sender, System.EventArgs e) => TryRestore();

        private void TryRestore()
        {
            if (_pending is not { } target || Viewer is not { } viewer)
                return;

            // Wait for content that can hold the offset: set too early, the viewer
            // clamps it, and the list lands short of where it was left.
            var room = viewer.Extent.Height - viewer.Viewport.Height;
            if (target > 0 && room < target - 0.5)
                return;

            viewer.Offset = viewer.Offset.WithY(target);
            Settle();
        }

        private void Settle()
        {
            _pending = null;
            _control.LayoutUpdated -= OnLayoutUpdated;
        }
    }
}
