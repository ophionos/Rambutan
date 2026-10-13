using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.LocalApi;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings → OBS overlay: the switch, how to add the overlay to OBS, the
/// URL to paste, and the style file (import, export, default). Off by default;
/// nothing is served until the user turns it on, and then only to this computer.
/// </summary>
public partial class OverlaySettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly ObsOverlayService _overlay;
    private readonly Action _persist;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _warning;

    /// <summary>The overlay's address without its token: the screen may be on stream.</summary>
    [ObservableProperty] private string? _address;
    [ObservableProperty] private string _clients = "";
    [ObservableProperty] private string _styleStatus = "";
    [ObservableProperty] private bool _isCustomStyle;
    [ObservableProperty] private string? _notice;
    [ObservableProperty] private bool _noticeIsError;

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    /// <summary>Set by the view: asks for a style file and returns its text, or
    /// null when the user cancels. Throws <see cref="IOException"/> when it can't be read.</summary>
    public Func<Task<string?>>? PickStyleToImport { get; set; }

    /// <summary>Set by the view: asks where to save the text; false when cancelled.</summary>
    public Func<string, Task<bool>>? SaveStyleAs { get; set; }

    /// <summary>Set by the view: shows the style editor and returns when it closes.</summary>
    public Func<OverlayStyleEditorViewModel, Task>? OpenEditor { get; set; }

    /// <summary>For the editor's preview, whose bars stand still under reduced motion.</summary>
    public AppearanceSettings? Appearance { get; init; }

    public OverlaySettingsViewModel(AppSettings app, ObsOverlayService overlay, Action persist)
    {
        _app = app;
        _overlay = overlay;
        _persist = persist;
        _enabled = app.OverlayEnabled;
        Refresh();
        overlay.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Strings.WhenChanged(this, static vm => vm.Refresh());
    }

    public bool IsListening => _overlay.Status == OverlayStatus.Listening;

    partial void OnEnabledChanged(bool value)
    {
        _app.OverlayEnabled = value;
        _persist();
        Notice = null;
        _ = _overlay.SetEnabledAsync(value);
        Refresh();
    }

    [RelayCommand]
    private async Task CopyUrl()
    {
        if (_overlay.Url is not { } url || CopyToClipboard is null)
            return;
        await CopyToClipboard(url);
        Say(Strings.Get("settings.overlay.copied"));
    }

    [RelayCommand]
    private async Task ResetLink()
    {
        await _overlay.ResetLinkAsync();
        Say(Strings.Get("settings.overlay.resetDone"));
    }

    [RelayCommand]
    private async Task ImportStyle()
    {
        if (PickStyleToImport is null)
            return;
        try
        {
            if (await PickStyleToImport() is not { } json)
                return;
            var problem = _overlay.ImportStyle(json);
            if (problem == OverlayConfigProblem.None)
                Say(Strings.Get("settings.overlay.style.imported"));
            else
                Say(ProblemText(problem), error: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(Strings.Get("settings.overlay.style.ioFailed"), error: true);
        }
    }

    [RelayCommand]
    private async Task ExportStyle()
    {
        if (SaveStyleAs is null)
            return;
        try
        {
            if (await SaveStyleAs(_overlay.ExportStyle()))
                Say(Strings.Get("settings.overlay.style.exported"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(Strings.Get("settings.overlay.style.ioFailed"), error: true);
        }
    }

    [RelayCommand]
    private async Task EditStyle()
    {
        if (OpenEditor is null)
            return;
        Notice = null;
        using var editor = new OverlayStyleEditorViewModel(_overlay, Appearance);
        await OpenEditor(editor);
        Refresh();
    }

    [RelayCommand]
    private void ResetStyle()
    {
        try
        {
            _overlay.ResetStyle();
            Say(Strings.Get("settings.overlay.style.resetDone"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say(Strings.Get("settings.overlay.style.ioFailed"), error: true);
        }
    }

    [RelayCommand]
    private void DismissNotice() => Notice = null;

    private void Say(string text, bool error = false)
    {
        NoticeIsError = error;
        Notice = text;
    }

    private void Refresh()
    {
        var listening = _overlay.Status == OverlayStatus.Listening;
        Status = StatusText(_overlay.Status, _overlay.Failure);
        Warning = listening && _overlay.PreferredPortBusy
            ? Strings.Format("settings.overlay.portBusy", ("usual", _overlay.PreferredPort), ("port", _overlay.Port))
            : null;
        Address = listening ? _overlay.Address : null;
        Clients = listening
            ? Strings.Format("settings.overlay.clients", ("count", _overlay.ConnectedClients))
            : "";
        IsCustomStyle = _overlay.IsCustomStyle;
        StyleStatus = StyleText(_overlay.IsCustomStyle, _overlay.StyleProblem);
        OnPropertyChanged(nameof(IsListening));
    }

    public static string StatusText(OverlayStatus status, LocalApiFailure? failure) => status switch
    {
        OverlayStatus.Starting => Strings.Get("settings.overlay.status.starting"),
        OverlayStatus.Listening => Strings.Get("settings.overlay.status.listening"),
        OverlayStatus.Failed => failure switch
        {
            LocalApiFailure.PortInUse => Strings.Get("settings.overlay.status.portInUse"),
            _ => Strings.Get("settings.overlay.status.failed"),
        },
        _ => Strings.Get("settings.overlay.status.off"),
    };

    public static string StyleText(bool custom, OverlayConfigProblem problem) => problem switch
    {
        OverlayConfigProblem.None => Strings.Get(custom ? "settings.overlay.style.custom" : "settings.overlay.style.default"),
        OverlayConfigProblem.NewerVersion => Strings.Get("settings.overlay.style.savedNewer"),
        _ => Strings.Get("settings.overlay.style.savedUnreadable"),
    };

    public static string ProblemText(OverlayConfigProblem problem) => problem switch
    {
        OverlayConfigProblem.NewerVersion => Strings.Get("settings.overlay.style.newer"),
        OverlayConfigProblem.NotJson => Strings.Get("settings.overlay.style.notJson"),
        OverlayConfigProblem.TooLarge => Strings.Get("settings.overlay.style.tooLarge"),
        _ => Strings.Get("settings.overlay.style.notAStyle"),
    };
}
