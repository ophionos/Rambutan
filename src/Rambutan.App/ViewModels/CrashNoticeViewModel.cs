using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.ViewModels;

/// <summary>
/// The status strip's "closed unexpectedly" slot. It is standing state,
/// not an outcome: it stays until the user reports the crash or dismisses it,
/// and either one deletes the crash file. Nothing is sent: Report opens GitHub's
/// new-issue page in the browser, filled in, for the user to read and send.
/// </summary>
public sealed partial class CrashNoticeViewModel : ViewModelBase
{
    private readonly CrashFile _file;
    private readonly string _repository;
    private readonly Func<string, Task<bool>> _openInBrowser;
    private readonly ToastHostViewModel _toasts;
    private CrashReport? _report;

    /// <param name="repository">Where issues go: the repository releases come from.</param>
    /// <param name="openInBrowser">Opens a URL; false when no browser opened.</param>
    public CrashNoticeViewModel(
        CrashFile file, string repository, Func<string, Task<bool>> openInBrowser, ToastHostViewModel toasts)
    {
        _file = file;
        _repository = repository;
        _openInBrowser = openInBrowser;
        _toasts = toasts;
        _report = file.Read();
        Strings.WhenChanged(this, static vm => vm.OnPropertyChanged(nameof(Text)));
    }

    public bool IsVisible => _report is not null;

    public string Text => _report is { } report
        ? Strings.Format("crash.notice", ("when", report.At.ToLocalTime().ToString("g", Strings.Culture)))
        : "";

    [RelayCommand]
    private async Task ReportAsync()
    {
        if (_report is not { } report)
            return;
        if (!await _openInBrowser(report.IssueUrl(_repository).AbsoluteUri))
        {
            // The notice stays, so the user can try again or dismiss it.
            _toasts.Show(Strings.Get("crash.browserFailed"), ToastKind.Error);
            return;
        }
        Clear();
    }

    [RelayCommand]
    private void Dismiss() => Clear();

    private void Clear()
    {
        _file.Delete();
        _report = null;
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(Text));
    }
}
