using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.ViewModels;

/// <summary>A page that can be pinned to the sidebar: an album, a YouTube Music
/// playlist or a playlist on this computer.</summary>
public interface IPinnablePage
{
    PinKind PinKind { get; }

    /// <summary>The id its pin has (<see cref="SidebarPin.Id"/>).</summary>
    string PinId { get; }

    /// <summary>The header's Pin entry; null where pins aren't composed.</summary>
    SidebarPinToggle? Pin { get; }
}

/// <summary>
/// The Pin to sidebar / Unpin from sidebar entry of a page's header. The pin
/// takes the page's title and cover as they are when it is pressed. The state
/// is read when asked; the page showing is told when the pins change
/// (<see cref="SidebarPinsViewModel"/>), so a page in the history holds no
/// handler on the pins. UI thread only.
/// </summary>
public sealed partial class SidebarPinToggle : ObservableObject
{
    private readonly CollectionActionsViewModel _collections;
    private readonly Func<string?> _title;
    private readonly Func<string?> _cover;

    public SidebarPinToggle(
        CollectionActionsViewModel collections, PinKind kind, string id, Func<string?> title, Func<string?> cover)
    {
        _collections = collections;
        Kind = kind;
        Id = id;
        _title = title;
        _cover = cover;
        Strings.WhenChanged(this, static toggle => toggle.OnPropertyChanged(nameof(Name)));
    }

    public PinKind Kind { get; }
    public string Id { get; }

    /// <summary>The entry shows: pins can change, and the page has a title to
    /// pin it under (a page still loading, or one that failed, has none).</summary>
    public bool CanPin => _collections.CanPin && !string.IsNullOrWhiteSpace(_title());

    public bool IsPinned => _collections.IsPinned(Kind, Id);

    /// <summary>What a press does now: the entry's label and accessible name.</summary>
    public string Name => Strings.Get(IsPinned ? "sidebar.unpin" : "sidebar.pin");

    /// <summary>The header entry's name while its page has no toggle (no pin
    /// library): the entry is hidden then, but every entry of the More menu is
    /// named.</summary>
    public static string FallbackName => Strings.Get("sidebar.pin");

    [RelayCommand]
    private void Toggle()
    {
        if (!CanPin)
            return;
        _collections.PinOrUnpin(new SidebarPin(Kind, Id, _title()!.Trim(), _cover()));
        Update();
    }

    /// <summary>The pins or the page's title changed: reads the state again.</summary>
    public void Update()
    {
        OnPropertyChanged(nameof(CanPin));
        OnPropertyChanged(nameof(IsPinned));
        OnPropertyChanged(nameof(Name));
    }

    /// <summary>The page loaded or was renamed: a pin of it takes its title and
    /// cover now, so the sidebar follows a rename or a new cover.</summary>
    public void Refresh()
    {
        _collections.Pins?.Refresh(Kind, Id, _title(), _cover());
        Update();
    }
}
