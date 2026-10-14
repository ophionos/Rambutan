using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rambutan.App.Services;

public sealed class AppSettings
{
    /// <summary>Version 2 split looking lyrics up online from showing them
    /// (<see cref="LookUpLyricsOnline"/>); see <see cref="AppSettingsStore.Migrate"/>.</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    public bool CrossfadeEnabled { get; set; }
    public double CrossfadeSeconds { get; set; } = 6;
    public bool CrossfadeOnSkip { get; set; } = true;
    public bool NormalizeVolume { get; set; } = true;
    public double Volume { get; set; } = 1.0;
    public bool CloseToTray { get; set; }

    /// <summary>A queue that runs out continues with radio. Default on, as
    /// in YouTube Music; repeat overrides it.</summary>
    public bool Autoplay { get; set; } = true;

    /// <summary>Send plays to Last.fm while connected. Separate from the
    /// connection, which is what keeps the integration off by default: nothing
    /// is sent until the user connects an account.</summary>
    public bool LastFmScrobbling { get; set; } = true;

    /// <summary>Show the connected account's Last.fm mix and recommendations on
    /// Home. Off by default: it sends the account name to Last.fm, and
    /// each listed track to YouTube Music as a signed-out search.</summary>
    public bool LastFmStationsOnHome { get; set; }

    /// <summary>Tint the shell with the now-playing artwork. Default on.</summary>
    public bool AmbientBackground { get; set; } = true;

    /// <summary>Show the meter over the player bar's artwork. Default on.</summary>
    public bool ArtworkMeter { get; set; } = true;

    /// <summary>The most redraws a second for the meter and the Now Playing
    /// visualizer: 30, 60, or 0 for the display rate. Default 30.</summary>
    public int VisualizerFrameRate { get; set; } = AppearanceSettings.DefaultFrameRate;

    /// <summary>The Now Playing visualizer's style: "bars", "mirrored", "led",
    /// "line", "area", "radial", "oscilloscope" or "vectorscope". An unknown
    /// value reads as "bars".</summary>
    public string VisualizerStyle { get; set; } = "bars";

    /// <summary>The visualizer's colour: "accent", "artwork" (picked from the
    /// cover) or "custom". An unknown value reads as "accent".</summary>
    public string VisualizerColor { get; set; } = "accent";

    /// <summary>The colour "custom" uses, as #RRGGBB.</summary>
    public string VisualizerCustomColor { get; set; } = AppearanceSettings.DefaultCustomColor;

    /// <summary>"system" (follow the OS's reduce-motion setting), "reduced" or
    /// "full". A string rather than an enum number so the file
    /// stays readable and an unknown value degrades to "system".</summary>
    public string Motion { get; set; } = "system";

    /// <summary>Stream quality preference: "high" or "data-saver". A string,
    /// like <see cref="Motion"/>; an unknown value reads as "high".</summary>
    public string AudioQuality { get; set; } = "high";

    /// <summary>Audio disk cache cap. Zero disables and clears the cache.</summary>
    public int AudioCacheSizeMb { get; set; } = 512;

    /// <summary>The output device's name as the OS shows it, used while that
    /// device is present; null follows the system default.</summary>
    public string? OutputDevice { get; set; }

    /// <summary>The equalizer. Off by default.</summary>
    public bool EqualizerEnabled { get; set; }

    /// <summary>The equalizer's gains, each band with its frequency so a later
    /// change of layout can't misread them; null (or anything that doesn't name
    /// the current bands) is flat.</summary>
    public List<EqualizerBandSetting>? EqualizerBands { get; set; }

    /// <summary>Where new downloads go; null is the default folder under the
    /// cache directory. Changing it leaves existing downloads where they are.</summary>
    public string? DownloadsFolder { get; set; }

    /// <summary>Export of downloads as ordinary files: off by default.
    /// The format is "original" (remux) or "mp3"; the folder is the last one
    /// exported to, null until the first export.</summary>
    public bool ExportEnabled { get; set; }
    public string? ExportFormat { get; set; }
    public string? ExportFolder { get; set; }

    /// <summary>Velopack feed (URL, local path or GitHub repository); null means
    /// this repository's GitHub releases.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>Discord Rich Presence. Off by default like every
    /// integration that sends data off the machine; the per-element toggles
    /// default on, so turning it on shows the usual presence.</summary>
    public bool DiscordEnabled { get; set; }
    public bool DiscordShowTrack { get; set; } = true;
    public bool DiscordShowArtwork { get; set; } = true;
    public bool DiscordShowElapsed { get; set; } = true;

    /// <summary>The phone remote: the app listens on the local network
    /// while this is on. Off by default.</summary>
    public bool RemoteEnabled { get; set; }

    /// <summary>The OBS overlay: the app serves the overlay page to this
    /// computer only while this is on. Off by default.</summary>
    public bool OverlayEnabled { get; set; }

    // Sidebar / queue panel collapse state. A fresh install opens the sidebar
    // and closes the queue: an empty queue panel only says "Nothing up next".
    // Saved files carry their own value, so this changes new
    // installs only.
    public bool SidebarCollapsed { get; set; }
    public bool QueueCollapsed { get; set; } = true;

    /// <summary>Now Playing shows the lyrics overlay.
    /// Default off. Showing lyrics sends nothing by itself: the pane shows the
    /// user's own lyrics and any already cached.</summary>
    public bool ShowLyrics { get; set; }

    /// <summary>The lyrics pane may look tracks up on LRCLIB, which sends each
    /// track's title and artist off the machine. Default off, like every
    /// outbound integration.</summary>
    public bool LookUpLyricsOnline { get; set; }

    /// <summary>Display language: a string-table code ("en", "de"), or
    /// null to follow the OS. <c>RAMBUTAN_LOCALE</c> overrides it for a session.</summary>
    public string? Language { get; set; }

    // Last window bounds (null until the first run ends); see WindowStatePersistence.
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>The window is the mini player: the player bar only. The bounds
    /// above stay the full window's; the mini player keeps its own below.</summary>
    public bool MiniPlayer { get; set; }

    /// <summary>The mini player stays on top of other windows. Has no effect on
    /// native Wayland, which has no way to ask for it.</summary>
    public bool MiniPlayerPinned { get; set; }

    // The mini player's own bounds; its height follows its content.
    public double? MiniWindowWidth { get; set; }
    public int? MiniWindowX { get; set; }
    public int? MiniWindowY { get; set; }
}

/// <summary>One equalizer band as stored: its centre and its gain in dB.</summary>
public sealed class EqualizerBandSetting
{
    public int Hz { get; set; }
    public double Db { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJsonContext : JsonSerializerContext;

/// <summary>JSON-file settings persistence with atomic writes.</summary>
public sealed class AppSettingsStore(string dataDirectory)
{
    private readonly string _path = Path.Combine(dataDirectory, "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var loaded = JsonSerializer.Deserialize(
                    File.ReadAllText(_path), SettingsJsonContext.Default.AppSettings);
                if (loaded is not null)
                    return Migrate(loaded);
            }
        }
        catch
        {
            // Corrupt settings fall back to defaults; the next Save rewrites them.
        }

        return new AppSettings();
    }

    /// <summary>
    /// Brings an older file up to <see cref="AppSettings.CurrentVersion"/>.
    /// Version 1 had one lyrics switch that both showed lyrics and looked them up
    /// on LRCLIB; whoever had it on agreed to the lookups, so they keep them.
    /// </summary>
    internal static AppSettings Migrate(AppSettings settings)
    {
        if (settings.Version < 2)
        {
            settings.LookUpLyricsOnline = settings.ShowLyrics;
            settings.Version = 2;
        }

        return settings;
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SettingsJsonContext.Default.AppSettings));
        File.Move(temp, _path, overwrite: true);
    }
}
