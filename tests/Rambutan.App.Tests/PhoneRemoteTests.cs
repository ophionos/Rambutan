using System.Net;
using System.Net.Http.Headers;
using Rambutan.App.Services;
using Rambutan.App.ViewModels;
using Rambutan.Core.Queue;
using Rambutan.LocalApi;

namespace Rambutan.App.Tests;

/// <summary>The phone remote's app side. The service listens on loopback
/// here; the app passes every address.</summary>
public sealed class PhoneRemoteTests : IDisposable
{
    private readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rambutan-remote-");
    private readonly PhoneRemoteService _remote;

    public PhoneRemoteTests()
    {
        _remote = new PhoneRemoteService(
            FakePlayback.Coordinator(new QueueModel()), action => action(), new RemoteTokenVault(_dir.FullName),
            new CoverSource((_, _) => Task.FromResult(Array.Empty<byte>()), _ => null), IPAddress.Loopback);
    }

    public void Dispose()
    {
        _remote.StopAsync().GetAwaiter().GetResult();
        _dir.Delete(recursive: true);
    }

    private string PairingFile => Path.Combine(_dir.FullName, "remote-pairing.bin");

    [Fact]
    public void Off_MakesNoPairing()
    {
        Assert.Equal(PhoneRemoteStatus.Off, _remote.Status);
        Assert.Null(_remote.LinkFor(IPAddress.Loopback));
        Assert.False(File.Exists(PairingFile));
    }

    [Fact]
    public async Task On_ListensAndLinksThePageWithTheToken()
    {
        await _remote.SetEnabledAsync(true);

        Assert.Equal(PhoneRemoteStatus.Listening, _remote.Status);
        Assert.True(File.Exists(PairingFile));
        var link = _remote.LinkFor(IPAddress.Loopback)!;
        Assert.StartsWith($"http://127.0.0.1:{_remote.Port}/remote/#t=", link);
        Assert.Equal(HttpStatusCode.OK, await StateStatusAsync(TokenOf(link)));
    }

    [Fact]
    public async Task It_TriesTheDataFoldersOwnPortFirst()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var free = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var remote = new PhoneRemoteService(
            FakePlayback.Coordinator(new QueueModel()), action => action(), new RemoteTokenVault(_dir.FullName),
            new CoverSource((_, _) => Task.FromResult(Array.Empty<byte>()), _ => null), IPAddress.Loopback, port: free);
        try
        {
            await remote.SetEnabledAsync(true);

            Assert.Equal(free, remote.PreferredPort);
            Assert.Equal(free, remote.Port);
            Assert.False(remote.PreferredPortBusy);
        }
        finally
        {
            await remote.StopAsync();
        }
    }

    [Fact]
    public async Task Off_StopsListening()
    {
        await _remote.SetEnabledAsync(true);
        var port = _remote.Port;

        await _remote.SetEnabledAsync(false);

        Assert.Equal(PhoneRemoteStatus.Off, _remote.Status);
        Assert.Null(_remote.LinkFor(IPAddress.Loopback));
        using var http = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync($"http://127.0.0.1:{port}/remote/"));
    }

    [Fact]
    public async Task AQuickOffAndOn_EndsWithOneHostRunning()
    {
        var on = _remote.SetEnabledAsync(true);
        var off = _remote.SetEnabledAsync(false);
        var again = _remote.SetEnabledAsync(true);
        await Task.WhenAll(on, off, again);

        Assert.Equal(PhoneRemoteStatus.Listening, _remote.Status);
        await _remote.SetEnabledAsync(false);
        Assert.Equal(PhoneRemoteStatus.Off, _remote.Status);
    }

    [Fact]
    public async Task ThePairing_LastsAcrossRestarts_UntilReset()
    {
        await _remote.SetEnabledAsync(true);
        var first = TokenOf(_remote.LinkFor(IPAddress.Loopback)!);
        await _remote.SetEnabledAsync(false);
        await _remote.SetEnabledAsync(true);
        Assert.Equal(first, TokenOf(_remote.LinkFor(IPAddress.Loopback)!));

        await _remote.ResetPairingAsync();

        var second = TokenOf(_remote.LinkFor(IPAddress.Loopback)!);
        Assert.NotEqual(first, second);
        Assert.Equal(HttpStatusCode.Unauthorized, await StateStatusAsync(first));
        Assert.Equal(HttpStatusCode.OK, await StateStatusAsync(second));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "image/jpeg")]
    [InlineData(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A }, "image/png")]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 1, 2, 3, 4, (byte)'W', (byte)'E', (byte)'B', (byte)'P' }, "image/webp")]
    public void Covers_AreServedAsTheirType(byte[] bytes, string type) =>
        Assert.Equal(type, CoverSource.ContentTypeOf(bytes));

    [Fact]
    public void TheStatus_SaysWhyItIsNotListening()
    {
        Assert.Equal("Off. Rambutan doesn't listen on the network.",
            RemoteSettingsViewModel.StatusText(PhoneRemoteStatus.Off, null, hasAddress: true));
        Assert.Equal("Couldn't start: another program is using the network port.",
            RemoteSettingsViewModel.StatusText(PhoneRemoteStatus.Failed, LocalApiFailure.PortInUse, hasAddress: true));
        Assert.StartsWith("On, but this computer isn't connected to a network.",
            RemoteSettingsViewModel.StatusText(PhoneRemoteStatus.Listening, null, hasAddress: false));
    }

    [Fact]
    public void TheChosenAddress_IsKeptAcrossNetworkChanges_UntilItIsGone()
    {
        IPAddress lan = IPAddress.Parse("192.168.1.91"), vpn = IPAddress.Parse("100.108.46.93"),
            wsl = IPAddress.Parse("172.22.64.1");

        Assert.Equal(0, RemoteSettingsViewModel.IndexOfChoice([lan, vpn], null));
        Assert.Equal(1, RemoteSettingsViewModel.IndexOfChoice([lan, vpn], vpn));
        // Reordered by a network change: still the same address.
        Assert.Equal(2, RemoteSettingsViewModel.IndexOfChoice([wsl, lan, vpn], vpn));
        // Gone: back to the best one.
        Assert.Equal(0, RemoteSettingsViewModel.IndexOfChoice([lan, wsl], vpn));
        Assert.Equal(0, RemoteSettingsViewModel.IndexOfChoice([], vpn));
    }

    [Fact]
    public void TheAddressBox_ShowsTheAddressInTheCode() => Headless.Run(() =>
    {
        // As in Settings: the page is open, then the remote is turned on.
        var vm = new RemoteSettingsViewModel(new AppSettings(), _remote, () => { });
        var box = new Avalonia.Controls.ComboBox();
        box.Bind(Avalonia.Controls.ItemsControl.ItemsSourceProperty, new Avalonia.Data.Binding(nameof(vm.AddressChoices)) { Source = vm });
        box.Bind(Avalonia.Controls.Primitives.SelectingItemsControl.SelectedItemProperty,
            new Avalonia.Data.Binding(nameof(vm.SelectedAddress)) { Source = vm, Mode = Avalonia.Data.BindingMode.TwoWay });
        var window = new Avalonia.Controls.Window { Content = box };
        window.Show();

        _remote.SetEnabledAsync(true).GetAwaiter().GetResult();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        if (_remote.Addresses.Count >= 2) // one network here: the box doesn't show
        {
            Assert.Equal(0, box.SelectedIndex);
            Assert.Equal(vm.Address, box.SelectedItem);

            // Picking another address moves the code and link to it, and the
            // pick survives a refresh (here, a pairing reset).
            box.SelectedIndex = 1;
            var second = _remote.Addresses[1];
            Assert.StartsWith(_remote.AddressFor(second), vm.Link);
            _remote.ResetPairingAsync().GetAwaiter().GetResult();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.StartsWith(_remote.AddressFor(second), vm.Link);
            Assert.Equal(1, box.SelectedIndex);
        }
        window.Close();
    });

    private static string TokenOf(string link) => link[(link.IndexOf("#t=", StringComparison.Ordinal) + 3)..];

    private async Task<HttpStatusCode> StateStatusAsync(string token)
    {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{_remote.Port}/api/v1/state");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        using var response = await http.SendAsync(request);
        return response.StatusCode;
    }
}

public sealed class PlayerBarVolumeTests
{
    [Fact]
    public void TheSlider_FollowsAVolumeSetElsewhere() => Headless.Run(() =>
    {
        var coordinator = FakePlayback.Coordinator(new QueueModel());
        var vm = new PlayerBarViewModel(coordinator, new QueueModel(), null);
        var changed = 0;
        vm.PropertyChanged += (_, e) => changed += e.PropertyName == nameof(PlayerBarViewModel.Volume) ? 1 : 0;

        coordinator.Volume = 0.3; // as the phone remote does
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.Equal(0.3, vm.Volume, 3);
        Assert.True(changed > 0);
    });

    [Fact]
    public void RaisingTheVolumeElsewhere_Unmutes() => Headless.Run(() =>
    {
        var coordinator = FakePlayback.Coordinator(new QueueModel());
        var vm = new PlayerBarViewModel(coordinator, new QueueModel(), null);
        vm.ToggleMuteCommand.Execute(null);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        Assert.True(vm.IsMuted);

        coordinator.Volume = 0.5;
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.False(vm.IsMuted);
    });

    [Fact]
    public void AChangeQueuedBeforeAMute_DoesNotUnmute() => Headless.Run(() =>
    {
        var coordinator = FakePlayback.Coordinator(new QueueModel());
        var vm = new PlayerBarViewModel(coordinator, new QueueModel(), null);

        vm.Volume = 0.6;              // a slider drag: its event is queued...
        vm.ToggleMuteCommand.Execute(null); // ...and the mute lands first
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();

        Assert.True(vm.IsMuted);
        Assert.Equal(0, vm.Volume);
    });
}
