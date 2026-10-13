using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Rambutan.Core.Downloads;

namespace Rambutan.App.Services;

/// <summary>
/// Export of downloads, shared live between Settings and the Downloads page: the
/// page's Export buttons appear the moment the setting is turned on. Reads and
/// writes straight through to <see cref="AppSettings"/> and saves on each change.
/// Off by default (maintainer, 2026-10-03).
/// </summary>
public sealed class ExportSettings(AppSettings app, Action persist) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public bool Enabled
    {
        get => app.ExportEnabled;
        set => Set(app.ExportEnabled != value, () => app.ExportEnabled = value);
    }

    public ExportFormat Format
    {
        get => ParseFormat(app.ExportFormat);
        set => Set(Format != value, () => app.ExportFormat = FormatName(value));
    }

    /// <summary>The folder the last export went to, offered first next time.</summary>
    public string? LastFolder
    {
        get => app.ExportFolder;
        set => Set(app.ExportFolder != value, () => app.ExportFolder = value);
    }

    public static ExportFormat ParseFormat(string? name) =>
        string.Equals(name, "mp3", StringComparison.OrdinalIgnoreCase) ? ExportFormat.Mp3 : ExportFormat.Original;

    public static string FormatName(ExportFormat format) => format is ExportFormat.Mp3 ? "mp3" : "original";

    private void Set(bool changed, Action write, [CallerMemberName] string? name = null)
    {
        if (!changed)
            return;
        write();
        persist();
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
