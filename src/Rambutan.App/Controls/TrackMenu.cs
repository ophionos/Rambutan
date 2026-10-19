using Avalonia;
using Avalonia.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Controls;

/// <summary>
/// The one track context menu. A row opts in with its track, and
/// a surface with extra entries (the queue, an editable playlist) adds itself as
/// the host:
/// <code>
/// &lt;Grid controls:TrackMenu.Track="{Binding}"
///       controls:TrackMenu.Host="{Binding #Root.((vm:PlaylistPageViewModel)DataContext)}"&gt;
/// </code>
/// Entries come from <see cref="TrackMenuModel"/>; the flyout and its keyboard
/// route are <see cref="ContextMenuHost"/>'s, shared with <see cref="CollectionMenu"/>.
/// </summary>
public static class TrackMenu
{
    /// <summary>Composition-time hook, like <see cref="RemoteImage.Cache"/>: the
    /// shared actions every menu invokes. Without it the menu has nothing to show.</summary>
    public static TrackActionsViewModel? Actions { get; set; }

    public static readonly AttachedProperty<TrackInfo?> TrackProperty =
        AvaloniaProperty.RegisterAttached<Control, TrackInfo?>("Track", typeof(TrackMenu));

    public static readonly AttachedProperty<ITrackMenuHost?> HostProperty =
        AvaloniaProperty.RegisterAttached<Control, ITrackMenuHost?>("Host", typeof(TrackMenu));

    static TrackMenu()
    {
        ContextMenuHost.AddSource(owner =>
            GetTrack(owner) is { } track && Actions is { } actions
                ? TrackMenuModel.Build(track, actions, GetHost(owner)?.MenuExtrasFor(owner.DataContext, track))
                : []);
        TrackProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not null)
                ContextMenuHost.Install(control);
        });
    }

    public static TrackInfo? GetTrack(Control control) => control.GetValue(TrackProperty);
    public static void SetTrack(Control control, TrackInfo? value) => control.SetValue(TrackProperty, value);
    public static ITrackMenuHost? GetHost(Control control) => control.GetValue(HostProperty);
    public static void SetHost(Control control, ITrackMenuHost? value) => control.SetValue(HostProperty, value);
}
