using System;
using System.IO;
using Rambutan.Core.Diagnostics;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.App.Services;

/// <summary>The style loaded at start, and whether it is the user's own.</summary>
/// <param name="Problem">Why the saved file couldn't be used; the default is used instead.</param>
public sealed record LoadedOverlayStyle(OverlayConfig Config, bool IsCustom, OverlayConfigProblem Problem);

/// <summary>
/// The OBS overlay's style as a file in the data folder,
/// <c>overlay-style.json</c>, in the versioned format of
/// <see cref="OverlayConfigFormat"/>. There is no file until the user imports a
/// style: the default needs none, and a later default then reaches them.
/// </summary>
public sealed class OverlayStyleStore(string dataDirectory)
{
    public string FilePath { get; } = Path.Combine(dataDirectory, "overlay-style.json");

    public LoadedOverlayStyle Load()
    {
        string text;
        try
        {
            if (!File.Exists(FilePath))
                return new(OverlayConfig.Default, false, OverlayConfigProblem.None);
            if (new FileInfo(FilePath).Length > OverlayConfigFormat.MaxBytes)
                return Unusable(OverlayConfigProblem.TooLarge);
            text = File.ReadAllText(FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("overlay", $"style unreadable: {ex.Message}");
            return Unusable(OverlayConfigProblem.NotJson);
        }

        var result = OverlayConfigFormat.Parse(text);
        return result.Config is { } config ? new(config, true, OverlayConfigProblem.None) : Unusable(result.Problem);

        LoadedOverlayStyle Unusable(OverlayConfigProblem problem)
        {
            // The file stays as it is: a newer version's style comes back when
            // that version runs again.
            AppLog.Write("overlay", $"saved style not used ({problem}); using the default");
            return new(OverlayConfig.Default, false, problem);
        }
    }

    /// <summary>Keeps <paramref name="config"/> as the user's style, written whole
    /// or not at all.</summary>
    public void Save(OverlayConfig config)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, OverlayConfigFormat.ToFileJson(config));
        File.Move(temp, FilePath, overwrite: true);
    }

    /// <summary>Back to the default style.</summary>
    public void Delete()
    {
        if (File.Exists(FilePath))
            File.Delete(FilePath);
    }
}
