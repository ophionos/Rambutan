using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.Integrations.LastFm;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The status strip's "Last.fm refused the API key" slot, so a user who never
/// opens Settings learns that scrobbling stopped (maintainer, 2026-11-26).
/// Dismissing hides it until the key is refused again; it goes by itself once
/// the refusal is no longer true (a scrobble went through, the key changed).
/// </summary>
public sealed partial class LastFmKeyNoticeViewModel : ViewModelBase
{
    private readonly Func<LastFmNotice?> _notice;
    private bool _refused;
    private bool _dismissed;

    /// <param name="notice">The scrobbler's current notice; read on <see cref="Refresh"/>.</param>
    public LastFmKeyNoticeViewModel(Func<LastFmNotice?> notice)
    {
        _notice = notice;
        Refresh();
    }

    public bool IsVisible => _refused && !_dismissed;

    /// <summary>Re-reads the scrobbler's notice. UI thread.</summary>
    public void Refresh()
    {
        var refused = _notice() == LastFmNotice.KeyRefused;
        if (refused == _refused)
            return;
        _refused = refused;
        if (!refused)
            _dismissed = false;
        OnPropertyChanged(nameof(IsVisible));
    }

    [RelayCommand]
    private void Dismiss()
    {
        _dismissed = true;
        OnPropertyChanged(nameof(IsVisible));
    }
}
