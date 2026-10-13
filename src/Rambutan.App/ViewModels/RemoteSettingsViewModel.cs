using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Rambutan.App.Controls;
using Rambutan.App.Localization;
using Rambutan.App.Services;
using Rambutan.LocalApi;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings → Phone remote: the switch, what turning it on exposes, and,
/// while it's on, the pairing code and link. Off by default; nothing listens on
/// the network until the user turns it on.
/// </summary>
public partial class RemoteSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly PhoneRemoteService _remote;
    private readonly Action _persist;

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string? _warning;

    /// <summary>The page's address as text (no token), while listening on a network.</summary>
    [ObservableProperty] private string? _address;

    /// <summary>The pairing link, token included: what the code and Copy carry.</summary>
    [ObservableProperty] private string? _link;
    [ObservableProperty] private QrCode? _code;
    [ObservableProperty] private string _codeName = "";

    /// <summary>Every address of this machine, best first, when there's more than
    /// one: the user picks the one the phone can reach, and the code and link
    /// follow. Each address is its own origin to the page, so the token must
    /// come with the address the phone opens.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAddressChoices))]
    private IReadOnlyList<string> _addressChoices = [];

    /// <summary>The chosen entry of <see cref="AddressChoices"/>. By item, not index:
    /// a ComboBox clears its index when its items are replaced.</summary>
    [ObservableProperty] private string? _selectedAddress;

    public bool HasAddressChoices => AddressChoices.Count > 1;

    /// <summary>The address the user picked; kept by value across network changes.</summary>
    private IPAddress? _chosen;
    private bool _refreshing;
    [ObservableProperty] private string _clients = "";
    [ObservableProperty] private string? _notice;

    /// <summary>Set by the view: puts text on the clipboard.</summary>
    public Func<string, Task>? CopyToClipboard { get; set; }

    public RemoteSettingsViewModel(AppSettings app, PhoneRemoteService remote, Action persist)
    {
        _app = app;
        _remote = remote;
        _persist = persist;
        _enabled = app.RemoteEnabled;
        Refresh();
        remote.Changed += () => Dispatcher.UIThread.Post(Refresh);
        Strings.WhenChanged(this, static vm => vm.Refresh());
    }

    public bool IsListening => _remote.Status == PhoneRemoteStatus.Listening;

    partial void OnSelectedAddressChanged(string? value)
    {
        var index = value is null ? -1 : Array.IndexOf(AddressChoices.ToArray(), value);
        if (_refreshing || index < 0 || index >= _remote.Addresses.Count)
            return;
        _chosen = _remote.Addresses[index];
        Refresh();
    }

    /// <summary>Where the chosen address is in a new list; the best one (first)
    /// when it's gone or nothing was chosen.</summary>
    public static int IndexOfChoice(IReadOnlyList<IPAddress> addresses, IPAddress? chosen)
    {
        for (var i = 0; chosen is not null && i < addresses.Count; i++)
        {
            if (addresses[i].Equals(chosen))
                return i;
        }
        return 0;
    }

    partial void OnEnabledChanged(bool value)
    {
        _app.RemoteEnabled = value;
        _persist();
        Notice = null;
        _ = _remote.SetEnabledAsync(value);
        Refresh();
    }

    [RelayCommand]
    private async Task CopyLink()
    {
        if (Link is null || CopyToClipboard is null)
            return;
        await CopyToClipboard(Link);
        Notice = Strings.Get("settings.remote.copied");
    }

    [RelayCommand]
    private async Task ResetPairing()
    {
        await _remote.ResetPairingAsync();
        Notice = Strings.Get("settings.remote.resetDone");
    }

    private void Refresh()
    {
        var addresses = _remote.Addresses;
        var listening = _remote.Status == PhoneRemoteStatus.Listening;
        Status = StatusText(_remote.Status, _remote.Failure, addresses.Count > 0);
        Warning = listening && _remote.PreferredPortBusy
            ? Strings.Format("settings.remote.portBusy", ("usual", _remote.PreferredPort), ("port", _remote.Port))
            : null;

        var index = IndexOfChoice(addresses, _chosen);
        var link = listening && addresses.Count > 0 ? _remote.LinkFor(addresses[index]) : null;
        if (link != Link)
        {
            Link = link;
            Code = link is null ? null : QrCode.Encode(link);
        }
        Address = link is null ? null : _remote.AddressFor(addresses[index]);
        CodeName = Address is null ? "" : Strings.Format("settings.remote.codeName", ("address", Address));
        _refreshing = true;
        var choices = link is null ? [] : addresses.Select(_remote.AddressFor).ToArray();
        if (!choices.SequenceEqual(AddressChoices))
            AddressChoices = choices;
        SelectedAddress = choices.Length > 0 ? choices[index] : null;
        _refreshing = false;
        Clients = listening
            ? Strings.Format("settings.remote.clients", ("count", _remote.ConnectedClients))
            : "";
        if (!listening)
            Notice = null;
        OnPropertyChanged(nameof(IsListening));
    }

    public static string StatusText(PhoneRemoteStatus status, LocalApiFailure? failure, bool hasAddress) => status switch
    {
        PhoneRemoteStatus.Starting => Strings.Get("settings.remote.status.starting"),
        PhoneRemoteStatus.Listening when !hasAddress => Strings.Get("settings.remote.status.noNetwork"),
        PhoneRemoteStatus.Listening => Strings.Get("settings.remote.status.listening"),
        PhoneRemoteStatus.Failed => failure switch
        {
            LocalApiFailure.PortInUse => Strings.Get("settings.remote.status.portInUse"),
            LocalApiFailure.AddressUnavailable => Strings.Get("settings.remote.status.refused"),
            _ => Strings.Get("settings.remote.status.failed"),
        },
        _ => Strings.Get("settings.remote.status.off"),
    };
}
