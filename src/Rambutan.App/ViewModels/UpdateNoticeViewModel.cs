using System;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.Integrations;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The status strip's "update ready" slot: standing state while a downloaded
/// update waits for the app to quit. Dismiss hides it for this run only; the
/// update still installs on quit, and Settings still says so.
/// </summary>
public sealed partial class UpdateNoticeViewModel : ViewModelBase
{
    private readonly UpdateService _updates;
    private readonly Action _restart;
    private bool _dismissed;

    /// <param name="restart">Ends the run normally and installs the update,
    /// starting the app again.</param>
    public UpdateNoticeViewModel(UpdateService updates, Action restart)
    {
        _updates = updates;
        _restart = restart;
        // The download finishes on a worker thread.
        updates.StagedChanged += () => Dispatcher.UIThread.Post(Refresh);
        Strings.WhenChanged(this, static vm => vm.OnPropertyChanged(nameof(Text)));
    }

    public bool IsVisible => !_dismissed && _updates.StagedVersion is not null;

    public string Text => _updates.StagedVersion is { } version
        ? Strings.Format("update.notice", ("version", version))
        : "";

    [RelayCommand]
    private void RestartNow() => _restart();

    [RelayCommand]
    private void Dismiss()
    {
        _dismissed = true;
        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Text));
    }
}
