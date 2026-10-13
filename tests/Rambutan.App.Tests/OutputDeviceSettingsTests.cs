using Rambutan.App.Services;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>Settings → Output device: the system default unless a device is
/// chosen; a chosen device that isn't connected stays listed, marked so.</summary>
public class OutputDeviceSettingsTests
{
    private static readonly string[] Present = ["EDIFIER M60 Analog Stereo", "Arctis Pro Wireless Game"];

    private sealed class Probe
    {
        public int Persisted;
        public TaskCompletionSource<string?> Applied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static (OutputDeviceSettingsViewModel Vm, AppSettings App, Probe Probe) Make(string? chosen)
    {
        var app = new AppSettings { OutputDevice = chosen };
        var probe = new Probe();
        var vm = new OutputDeviceSettingsViewModel(
            app, () => Present, name => probe.Applied.TrySetResult(name), () => probe.Persisted++);
        vm.Fill(Present);
        return (vm, app, probe);
    }

    [Fact]
    public void ByDefault_TheSystemDefaultIsSelected_AndEveryDeviceIsListed()
    {
        var (vm, _, probe) = Make(null);

        Assert.Same(OutputDeviceOption.SystemDefault, vm.Selected);
        Assert.Equal([null, .. Present], vm.Choices.Select(c => c.Name));
        Assert.Equal(0, probe.Persisted);
    }

    [Fact]
    public void AChosenDeviceThatIsntConnected_StaysListedAndSelected()
    {
        var (vm, _, probe) = Make("USB Headset");

        Assert.Equal("USB Headset", vm.Selected!.Name);
        Assert.False(vm.Selected.IsPresent);
        Assert.Contains("USB Headset", vm.Selected.Label);
        Assert.NotEqual("USB Headset", vm.Selected.Label);
        Assert.False(probe.Applied.Task.IsCompleted);
    }

    [Fact]
    public async Task ChoosingADevice_SavesItAndMovesPlayback()
    {
        var (vm, app, probe) = Make(null);

        vm.Selected = vm.Choices.Single(c => c.Name == "Arctis Pro Wireless Game");

        Assert.Equal("Arctis Pro Wireless Game", app.OutputDevice);
        Assert.Equal(1, probe.Persisted);
        Assert.Equal("Arctis Pro Wireless Game",
            await probe.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ChoosingSystemDefault_ClearsTheChoice()
    {
        var (vm, app, probe) = Make("EDIFIER M60 Analog Stereo");

        vm.Selected = OutputDeviceOption.SystemDefault;

        Assert.Null(app.OutputDevice);
        Assert.Null(await probe.Applied.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
    }
}
