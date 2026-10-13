using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Rambutan.Core.Diagnostics;

namespace Rambutan.App.Services;

/// <summary>
/// The last crash, <c>crash.json</c> in the profile's data folder.
/// Versioned like every format the app writes. One crash is kept: a newer one
/// replaces it, and the notice on the next start deletes it once the user has
/// reported it or dismissed it.
/// </summary>
public sealed class SavedCrash
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    public DateTimeOffset At { get; set; }
    public string? AppVersion { get; set; }
    public string? Os { get; set; }
    public string? Runtime { get; set; }
    public string? Exception { get; set; }
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SavedCrash))]
internal sealed partial class CrashJsonContext : JsonSerializerContext;

/// <summary>
/// Reads and writes <see cref="SavedCrash"/>. Never throws: it runs while the
/// process is going down, and a missing, corrupt or newer file reads as no crash.
/// </summary>
public sealed class CrashFile(string dataDirectory)
{
    public string Path { get; } = System.IO.Path.Combine(dataDirectory, "crash.json");

    /// <summary>Folders the user chose (downloads, export), read when the crash
    /// happens; they can sit outside the home folder and still name the person.</summary>
    public Func<IEnumerable<string?>>? UserFolders { get; set; }

    /// <summary>Keeps <paramref name="exception"/> as the last crash, its text
    /// scrubbed of this machine's folders and names.</summary>
    public void Record(object exception)
    {
        try
        {
            Write(CrashReport.Create(
                DateTimeOffset.UtcNow,
                AppVersion(),
                RuntimeInformation.OSDescription,
                $"{RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}",
                exception.ToString() ?? "unknown",
                PersonalValues(dataDirectory, UserFolders?.Invoke() ?? [])));
        }
        catch (Exception ex)
        {
            AppLog.Write("crash", $"Couldn't keep the crash file: {ex.Message}");
        }
    }

    public void Write(CrashReport report)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            var saved = new SavedCrash
            {
                At = report.At,
                AppVersion = report.AppVersion,
                Os = report.Os,
                Runtime = report.Runtime,
                Exception = report.Exception,
            };
            File.WriteAllText(Path, JsonSerializer.Serialize(saved, CrashJsonContext.Default.SavedCrash));
        }
        catch (Exception ex)
        {
            AppLog.Write("crash", $"Couldn't write the crash file: {ex.Message}");
        }
    }

    /// <summary>The last crash, or null when there is none to report.</summary>
    public CrashReport? Read()
    {
        try
        {
            if (!File.Exists(Path))
                return null;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(Path), CrashJsonContext.Default.SavedCrash);
            if (saved is null || saved.Version is < 1 or > SavedCrash.CurrentVersion ||
                string.IsNullOrWhiteSpace(saved.Exception))
            {
                return null;
            }
            return new CrashReport(
                saved.At, saved.AppVersion ?? "unknown", saved.Os ?? "unknown",
                saved.Runtime ?? "unknown", saved.Exception);
        }
        catch (Exception ex)
        {
            AppLog.Write("crash", $"Couldn't read the crash file: {ex.Message}");
            return null;
        }
    }

    public void Delete()
    {
        try
        {
            File.Delete(Path);
        }
        catch (Exception ex)
        {
            AppLog.Write("crash", $"Couldn't delete the crash file: {ex.Message}");
        }
    }

    /// <summary>The build, with the commit it came from when the build says.</summary>
    private static string AppVersion() =>
        Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
        ?? "unknown";

    /// <summary>What <see cref="CrashReport.Scrub"/> takes out on this machine.</summary>
    internal static IEnumerable<KeyValuePair<string, string>> PersonalValues(
        string dataDirectory, IEnumerable<string?> userFolders)
    {
        foreach (var folder in userFolders)
        {
            if (!string.IsNullOrWhiteSpace(folder))
                yield return new(folder, "<folder>");
        }
        yield return new(dataDirectory, "<data>");
        yield return new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "~");
        yield return new(Environment.UserName, "<user>");
        yield return new(Environment.MachineName, "<machine>");
    }
}
