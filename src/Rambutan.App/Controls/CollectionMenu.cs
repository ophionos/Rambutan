using Avalonia;
using Avalonia.Controls;
using Rambutan.App.ViewModels;
using Rambutan.Core.Models;

namespace Rambutan.App.Controls;

/// <summary>
/// The context menu of an album, playlist or artist card. A card opts in with
/// its entry; a song entry gets no entries here, because the card's
/// <see cref="TrackMenu"/> serves it:
/// <code>
/// &lt;Panel controls:CollectionMenu.Entry="{Binding}" /&gt;
/// </code>
/// Entries come from <see cref="CollectionMenuModel"/>; the flyout and its
/// keyboard route are <see cref="ContextMenuHost"/>'s.
/// </summary>
public static class CollectionMenu
{
    /// <summary>Composition-time hook, like <see cref="TrackMenu.Actions"/>.
    /// Without it the menu has nothing to show, and cards show no loading.</summary>
    public static CollectionActionsViewModel? Actions { get; set; }

    public static readonly AttachedProperty<CatalogEntry?> EntryProperty =
        AvaloniaProperty.RegisterAttached<Control, CatalogEntry?>("Entry", typeof(CollectionMenu));

    /// <summary>Set on a list whose cards are all in the library (a Library tab),
    /// so their menus offer to remove them. Inherited by the cards.</summary>
    public static readonly AttachedProperty<bool> InLibraryProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("InLibrary", typeof(CollectionMenu), inherits: true);

    static CollectionMenu()
    {
        ContextMenuHost.AddSource(owner =>
            GetEntry(owner) is { } entry && Actions is { } actions
                ? CollectionMenuModel.Build(entry, actions, GetInLibrary(owner))
                : []);
        EntryProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.NewValue is not null)
                ContextMenuHost.Install(control);
        });
    }

    public static CatalogEntry? GetEntry(Control control) => control.GetValue(EntryProperty);
    public static void SetEntry(Control control, CatalogEntry? value) => control.SetValue(EntryProperty, value);

    public static bool GetInLibrary(Control control) => control.GetValue(InLibraryProperty);
    public static void SetInLibrary(Control control, bool value) => control.SetValue(InLibraryProperty, value);
}
