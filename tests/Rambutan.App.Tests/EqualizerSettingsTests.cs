using Rambutan.App.Services;
using Rambutan.App.ViewModels;

namespace Rambutan.App.Tests;

/// <summary>Settings → Equalizer: off and flat by default; each change is applied,
/// saved with its frequencies, and read back.</summary>
public sealed class EqualizerSettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rambutan-eq-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class Probe
    {
        public int Persisted;
        public bool On;
        public double[] Gains = [];
        public int Applied;
    }

    private static (EqualizerSettingsViewModel Vm, Probe Probe) Make(AppSettings app)
    {
        var probe = new Probe();
        var vm = new EqualizerSettingsViewModel(app, (on, gains) =>
        {
            probe.On = on;
            probe.Gains = [.. gains];
            probe.Applied++;
        }, () => probe.Persisted++);
        return (vm, probe);
    }

    [Fact]
    public void A_fresh_install_has_it_off_and_flat()
    {
        var app = new AppSettingsStore(_dir).Load();
        var (vm, _) = Make(app);

        Assert.False(vm.Enabled);
        Assert.Equal(10, vm.Bands.Count);
        Assert.All(vm.Bands, b => Assert.Equal(0, b.Gain));
    }

    [Fact]
    public void A_change_is_applied_and_saved_with_its_frequency()
    {
        var app = new AppSettings();
        var (vm, probe) = Make(app);

        vm.Enabled = true;
        vm.Bands[5].Gain = 4;

        Assert.True(probe.On);
        Assert.Equal(4, probe.Gains[5]);
        Assert.Equal(2, probe.Persisted);
        Assert.True(app.EqualizerEnabled);
        Assert.Equal(4, app.EqualizerBands!.Single(b => b.Hz == 1000).Db);
    }

    [Fact]
    public void The_curve_survives_a_save_and_a_load()
    {
        var store = new AppSettingsStore(_dir);
        var app = store.Load();
        var (vm, _) = Make(app);
        vm.Enabled = true;
        vm.Bands[0].Gain = 6;
        vm.Bands[9].Gain = -3;
        store.Save(app);

        var (reloaded, _) = Make(new AppSettingsStore(_dir).Load());

        Assert.True(reloaded.Enabled);
        Assert.Equal(6, reloaded.Bands[0].Gain);
        Assert.Equal(-3, reloaded.Bands[9].Gain);
    }

    [Fact]
    public void Bands_that_dont_match_the_layout_load_flat()
    {
        var app = new AppSettings
        {
            EqualizerEnabled = true,
            EqualizerBands = [new() { Hz = 60, Db = 6 }, new() { Hz = 1000, Db = 3 }],
        };

        var (vm, _) = Make(app);

        Assert.All(vm.Bands, b => Assert.Equal(0, b.Gain));
    }

    [Fact]
    public void Reset_flattens_every_band_in_one_change()
    {
        var app = new AppSettings();
        var (vm, probe) = Make(app);
        vm.Bands[2].Gain = 5;
        vm.Bands[7].Gain = -5;
        var before = probe.Applied;

        vm.ResetCommand.Execute(null);

        Assert.Equal(before + 1, probe.Applied);
        Assert.All(probe.Gains, g => Assert.Equal(0, g));
    }

    [Fact]
    public void Bands_are_named_and_valued_for_screen_readers()
    {
        var (vm, _) = Make(new AppSettings());
        vm.Bands[5].Gain = 3;
        vm.Bands[0].Gain = -4.4;

        Assert.Equal("31 Hz", vm.Bands[0].Label);
        Assert.Equal("1 kHz", vm.Bands[5].Label);
        Assert.Equal("1k", vm.Bands[5].ShortLabel);
        Assert.Equal("125", vm.Bands[2].ShortLabel);
        Assert.Equal("+3", vm.Bands[5].ShortGainText);
        Assert.Equal("Equalizer, 16 kHz", vm.Bands[9].AccessibleName);
        Assert.Equal("+3 dB", vm.Bands[5].GainText);
        Assert.Equal(-4, vm.Bands[0].Gain);   // whole dB only
        Assert.Equal("−4 dB", vm.Bands[0].GainText);
    }
}
