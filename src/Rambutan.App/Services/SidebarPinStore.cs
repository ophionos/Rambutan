using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Sidebar;

namespace Rambutan.App.Services;

/// <summary>
/// The sidebar's pins, in <c>sidebar-pins.json</c> in the profile's folder. An
/// unreadable file is set aside (<c>.unreadable</c>) and read as empty; a newer
/// build's file, or an unreadable one that couldn't be moved, is read as empty,
/// left as it is, and never written over.
/// </summary>
public sealed class SidebarPinStore(string dataDirectory) : ISidebarPinStore
{
    private readonly string _path = Path.Combine(dataDirectory, "sidebar-pins.json");

    public LoadedSidebarPins Load()
    {
        try
        {
            if (!File.Exists(_path))
                return new([]);
            var saved = JsonSerializer.Deserialize(File.ReadAllText(_path), SidebarPinsJsonContext.Default.SavedSidebarPins);
            if (saved is { Version: > SavedSidebarPins.CurrentVersion })
            {
                AppLog.Write("sidebar", $"sidebar-pins.json is version {saved.Version}, newer than this build's; left as it is");
                return new([], ReadOnly: true);
            }
            if (saved is null || saved.Version < 1)
                throw new InvalidDataException($"version {saved?.Version} isn't one this build reads");
            return new((saved.Pins ?? []).Select(ToPin).OfType<SidebarPin>().ToArray());
        }
        catch (Exception ex)
        {
            AppLog.Write("sidebar", $"couldn't read sidebar-pins.json, set aside: {ex.Message}");
            try
            {
                File.Move(_path, _path + ".unreadable", overwrite: true);
                return new([]);
            }
            catch (Exception moveEx)
            {
                AppLog.Write("sidebar", $"couldn't set sidebar-pins.json aside, left as it is: {moveEx.Message}");
                return new([], ReadOnly: true);
            }
        }
    }

    public void Save(IReadOnlyList<SidebarPin> pins)
    {
        var saved = new SavedSidebarPins
        {
            Pins = [.. pins.Select(p => new SavedSidebarPin { Kind = KindName(p.Kind), Id = p.Id, Title = p.Title, Cover = p.CoverUrl })],
        };
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(saved, SidebarPinsJsonContext.Default.SavedSidebarPins));
        File.Move(temp, _path, overwrite: true);
    }

    // The kinds' names in the file; never rename one. An entry of a kind this
    // build doesn't know is dropped, not the file.
    private static string KindName(PinKind kind) => kind switch
    {
        PinKind.Album => "album",
        PinKind.Playlist => "playlist",
        PinKind.LocalPlaylist => "localPlaylist",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static SidebarPin? ToPin(SavedSidebarPin? p)
    {
        PinKind? kind = p?.Kind switch
        {
            "album" => PinKind.Album,
            "playlist" => PinKind.Playlist,
            "localPlaylist" => PinKind.LocalPlaylist,
            _ => null,
        };
        if (kind is null || string.IsNullOrEmpty(p!.Id) || string.IsNullOrWhiteSpace(p.Title))
            return null;
        return new SidebarPin(kind.Value, p.Id, p.Title, string.IsNullOrEmpty(p.Cover) ? null : p.Cover);
    }
}

/// <summary><c>sidebar-pins.json</c>: the pins in the user's order.</summary>
public sealed class SavedSidebarPins
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public List<SavedSidebarPin?>? Pins { get; set; } = [];
}

public sealed class SavedSidebarPin
{
    public string? Kind { get; set; }
    public string? Id { get; set; }
    public string? Title { get; set; }

    /// <summary>The cover's link; absent for a playlist on this computer.</summary>
    public string? Cover { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedSidebarPins))]
internal sealed partial class SidebarPinsJsonContext : JsonSerializerContext;
