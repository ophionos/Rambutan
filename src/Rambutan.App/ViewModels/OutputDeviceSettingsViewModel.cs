using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Rambutan.App.Localization;
using Rambutan.App.Services;

namespace Rambutan.App.ViewModels;

/// <summary>
/// Settings → Playback → Output device. "System default" (the default) follows
/// the device the OS plays through and moves with it; a device chosen by name is
/// used while it is present, and playback falls back to the system default
/// while it isn't. A chosen device that isn't connected stays in the list,
/// marked so, rather than vanishing from the setting.
/// </summary>
public partial class OutputDeviceSettingsViewModel : ViewModelBase
{
    private readonly AppSettings _app;
    private readonly Func<IReadOnlyList<string>> _list;
    private readonly Action<string?> _apply;
    private readonly Action _persist;
    private Task _applying = Task.CompletedTask;
    private bool _filling;

    public ObservableCollection<OutputDeviceOption> Choices { get; } = [OutputDeviceOption.SystemDefault];

    [ObservableProperty] private OutputDeviceOption? _selected = OutputDeviceOption.SystemDefault;

    /// <param name="list">The devices present now, by name. Opens the audio
    /// device the first time, so it runs off the UI thread.</param>
    /// <param name="apply">Moves playback to the named device, or to the system
    /// default for null. A device switch, so it runs off the UI thread.</param>
    public OutputDeviceSettingsViewModel(
        AppSettings app, Func<IReadOnlyList<string>> list, Action<string?> apply, Action persist)
    {
        _app = app;
        _list = list;
        _apply = apply;
        _persist = persist;
        Fill([]);
    }

    /// <summary>Lists the devices again; Settings does it each time it opens.</summary>
    public async Task RefreshAsync() => Fill(await Task.Run(_list));

    internal void Fill(IReadOnlyList<string> present)
    {
        _filling = true;
        try
        {
            Choices.Clear();
            Choices.Add(OutputDeviceOption.SystemDefault);
            foreach (var name in present)
                Choices.Add(new OutputDeviceOption(name, IsPresent: true));
            if (_app.OutputDevice is { Length: > 0 } chosen && !present.Contains(chosen))
                Choices.Add(new OutputDeviceOption(chosen, IsPresent: false));
            Selected = Choices.FirstOrDefault(c => c.Name == _app.OutputDevice) ?? Choices[0];
        }
        finally
        {
            _filling = false;
        }
    }

    partial void OnSelectedChanged(OutputDeviceOption? value)
    {
        if (_filling || value is null || value.Name == _app.OutputDevice)
            return;
        _app.OutputDevice = value.Name;
        _persist();
        // One switch at a time, in the order chosen.
        var name = value.Name;
        _applying = _applying.ContinueWith(_ => _apply(name), TaskScheduler.Default);
    }
}

/// <summary>A choice in the output-device list; a null name follows the system default.</summary>
public sealed record OutputDeviceOption(string? Name, bool IsPresent)
{
    public static OutputDeviceOption SystemDefault { get; } = new(null, IsPresent: true);

    public string Label => Name is null
        ? Strings.Get("settings.playback.outputDefault")
        : IsPresent ? Name : Strings.Format("settings.playback.outputMissing", ("device", Name));

    /// <summary>What the list reads out, for screen readers.</summary>
    public override string ToString() => Label;
}
