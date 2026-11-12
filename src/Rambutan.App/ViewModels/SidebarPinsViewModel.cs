using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.Core.Models;
using Rambutan.Core.Playlists;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.ViewModels;

/// <summary>One pin in the sidebar, as it shows now.</summary>
public sealed partial class SidebarPinRowViewModel : ObservableObject
{
    internal SidebarPinRowViewModel(SidebarPinsViewModel owner, SidebarPin pin)
    {
        Owner = owner;
        Pin = pin;
        Strings.WhenChanged(this, static row => row.OnPropertyChanged(nameof(AccessibleName)));
    }

    /// <summary>The section, whose commands the row's button and menu use.</summary>
    public SidebarPinsViewModel Owner { get; }

    public SidebarPin Pin { get; internal set; }

    /// <summary>A playlist on this computer shows its current name; any other
    /// pin the title it was pinned (or last seen) with.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private string _title = "";

    /// <summary>An album's or YouTube Music playlist's cover; null for a
    /// playlist on this computer.</summary>
    [ObservableProperty] private string? _coverUrl;

    /// <summary>A playlist on this computer's first track, whose cover it shows.</summary>
    [ObservableProperty] private TrackInfo? _localCover;

    /// <summary>A playlist on this computer that no longer exists. A YouTube
    /// pin can't be known missing without a request, so it never is: its page
    /// says what it can't load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AccessibleName))]
    private bool _isMissing;

    /// <summary>Its page is the one showing.</summary>
    [ObservableProperty] private bool _isActive;

    /// <summary>The dimming is visual only, so a missing playlist says so in its name.</summary>
    public string AccessibleName => IsMissing ? Strings.Format("sidebar.pins.missing", ("title", Title)) : Title;
}

/// <summary>
/// The sidebar's Pinned section: the pinned albums and playlists in the user's
/// order, opened, moved and unpinned from their rows. It also tells the page
/// showing when the pins change, so its header's Pin entry reads right. UI
/// thread only; the local playlists' changes may come from any thread and are
/// posted to it.
/// </summary>
public sealed partial class SidebarPinsViewModel : ObservableObject
{
    private readonly SidebarPinLibrary _pins;
    private readonly LocalPlaylistLibrary? _local;
    private readonly Action<SidebarPin> _open;
    private object? _current;

    /// <param name="local">The playlists on this computer; null where they
    /// aren't composed, and then a pin of one reads as missing.</param>
    /// <param name="open">Opens a pin's page (<see cref="NavigationService.OpenPin"/>).</param>
    /// <param name="post">Runs on the UI thread; tests pass their own.</param>
    public SidebarPinsViewModel(
        SidebarPinLibrary pins, LocalPlaylistLibrary? local, Action<SidebarPin> open, Action<Action>? post = null)
    {
        _pins = pins;
        _local = local;
        _open = open;
        var toUi = post ?? (action => Dispatcher.UIThread.Post(action));
        pins.Changed += OnPinsChanged;
        if (local is not null)
            local.Changed += () => toUi(OnLocalChanged);
        Rebuild();
        FollowLocalNames();
    }

    public ObservableCollection<SidebarPinRowViewModel> Rows { get; } = [];

    /// <summary>The pins file is a newer build's, or unreadable and left in
    /// place: nothing is listed or offered, and the section says why.</summary>
    public bool IsReadOnly => _pins.IsReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsEmpty))]
    private bool _hasPins;

    /// <summary>Nothing pinned: the section says how to pin.</summary>
    public bool ShowsEmpty => !HasPins && !IsReadOnly;

    /// <summary>The page now showing; its row reads as active, and its header
    /// follows the pins.</summary>
    public void SetCurrent(object? page)
    {
        _current = page;
        (page as IPinnablePage)?.Pin?.Update();
        MarkActive();
    }

    [RelayCommand]
    private void Open(SidebarPinRowViewModel? row)
    {
        // A playlist that is gone has no page; its row stays so it can be unpinned.
        if (row is null || row.IsMissing)
            return;
        _open(row.Pin);
    }

    [RelayCommand]
    private void Unpin(SidebarPinRowViewModel? row)
    {
        if (row is not null)
            _pins.Unpin(row.Pin.Kind, row.Pin.Id);
    }

    [RelayCommand]
    private void MoveUp(SidebarPinRowViewModel? row) => Move(row, -1);

    [RelayCommand]
    private void MoveDown(SidebarPinRowViewModel? row) => Move(row, 1);

    /// <summary>Moves the row's pin up (<paramref name="by"/> below zero) or
    /// down. False when it doesn't move.</summary>
    public bool Move(SidebarPinRowViewModel? row, int by) =>
        row is not null && _pins.Move(row.Pin.Kind, row.Pin.Id, by);

    /// <summary>The row's context menu: Open, the moves, then Unpin.</summary>
    public IReadOnlyList<IReadOnlyList<MenuEntry>> MenuFor(SidebarPinRowViewModel row) =>
    [
        [new(Strings.Get("common.open"), "Icon.ArrowForward", OpenCommand, row, !row.IsMissing)],
        [
            new(Strings.Get("sidebar.moveUp"), "Icon.ArrowUp", MoveUpCommand, row,
                _pins.CanMove(row.Pin.Kind, row.Pin.Id, -1)),
            new(Strings.Get("sidebar.moveDown"), "Icon.ArrowDown", MoveDownCommand, row,
                _pins.CanMove(row.Pin.Kind, row.Pin.Id, 1)),
        ],
        [new(Strings.Get("sidebar.unpin"), "Icon.PinFilled", UnpinCommand, row)],
    ];

    private void OnPinsChanged()
    {
        Rebuild();
        (_current as IPinnablePage)?.Pin?.Update();
    }

    private void OnLocalChanged()
    {
        Rebuild();
        FollowLocalNames();
    }

    /// <summary>Makes the rows match the pins. A row that stays is moved, not
    /// made again, so its button keeps the focus.</summary>
    private void Rebuild()
    {
        var pins = _pins.Pins;
        var existing = Rows.ToDictionary(r => (r.Pin.Kind, r.Pin.Id));
        for (var i = 0; i < pins.Count; i++)
        {
            var pin = pins[i];
            if (existing.TryGetValue((pin.Kind, pin.Id), out var row))
            {
                var at = Rows.IndexOf(row);
                if (at != i)
                    Rows.Move(at, i);
            }
            else
            {
                row = new SidebarPinRowViewModel(this, pin);
                Rows.Insert(i, row);
            }
            Fill(row, pin);
        }
        while (Rows.Count > pins.Count)
            Rows.RemoveAt(Rows.Count - 1);
        HasPins = Rows.Count > 0;
        MarkActive();
    }

    private void Fill(SidebarPinRowViewModel row, SidebarPin pin)
    {
        row.Pin = pin;
        if (pin.Kind == PinKind.LocalPlaylist)
        {
            var found = _local?.Find(pin.Id);
            row.IsMissing = found is null;
            row.Title = found?.Name ?? pin.Title;
            row.LocalCover = found?.Items.FirstOrDefault()?.Track;
            row.CoverUrl = null;
        }
        else
        {
            row.IsMissing = false;
            row.Title = pin.Title;
            row.CoverUrl = pin.CoverUrl;
            row.LocalCover = null;
        }
    }

    /// <summary>A playlist on this computer that was renamed: its pin takes the
    /// new name, so the file is right where the playlist can't be read. Not
    /// from <see cref="Rebuild"/>: the pins' change comes back into it.</summary>
    private void FollowLocalNames()
    {
        var renamed = Rows
            .Where(r => r.Pin.Kind == PinKind.LocalPlaylist && !r.IsMissing && r.Title != r.Pin.Title)
            .Select(r => (r.Pin.Id, r.Title))
            .ToList();
        foreach (var (id, name) in renamed)
            _pins.Refresh(PinKind.LocalPlaylist, id, name, null);
    }

    private void MarkActive()
    {
        var page = _current as IPinnablePage;
        foreach (var row in Rows)
            row.IsActive = page is not null && page.PinKind == row.Pin.Kind && page.PinId == row.Pin.Id;
    }
}
