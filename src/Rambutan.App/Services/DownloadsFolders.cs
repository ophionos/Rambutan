using System.IO;

namespace Rambutan.App.Services;

/// <summary>Where new downloads go: the folder the user set in Settings, else a
/// folder under the app's cache directory.</summary>
public static class DownloadsFolders
{
    public static string Default(string dataDirectory) => Path.Combine(dataDirectory, "cache", "downloads");

    public static string Effective(AppSettings settings, string defaultFolder) =>
        string.IsNullOrWhiteSpace(settings.DownloadsFolder) ? defaultFolder : settings.DownloadsFolder;
}
