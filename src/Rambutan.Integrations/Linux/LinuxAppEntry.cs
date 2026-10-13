using System.Buffers.Binary;
using Rambutan.Core.Diagnostics;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// The desktop file a Linux dock finds the app's icon through. On Wayland
/// a dock matches a window to <c>&lt;app id&gt;.desktop</c> and draws its
/// <c>Icon</c>; COSMIC has no way for a window to send its own. So the app writes
/// <c>rambutan.desktop</c> (<c>NoDisplay</c>, like the links handler, so the menu
/// doesn't list the app) and the icon's sizes into the user's hicolor theme.
///
/// <para>The AppImage owns the entry and rewrites it when it moved; any other run
/// on the installed data folder (a published or dev build) writes it only when
/// there is none, so it never takes the entry from an installed AppImage. A demo
/// or a <c>RAMBUTAN_DATA_DIR</c> run writes nothing.</para>
/// </summary>
public static class LinuxAppEntry
{
    /// <summary>The window's app id, and the desktop file's and icon's name.</summary>
    public const string AppId = "rambutan";
    public const string DesktopFile = AppId + ".desktop";

    /// <summary>The desktop file for the program at <paramref name="exePath"/>;
    /// null for a path a desktop file can't carry. Lines end in LF.</summary>
    public static string? Entry(string exePath) =>
        LinuxDeepLinkRegistration.ExecQuote(exePath) is { } exe
            ? $"""
              [Desktop Entry]
              Type=Application
              Name=Rambutan
              Exec={exe}
              Icon={AppId}
              Terminal=false
              NoDisplay=true

              """.ReplaceLineEndings("\n")
            : null;

    // Not "\x89PNG"u8: that encodes U+0089 as two UTF-8 bytes.
    private static ReadOnlySpan<byte> PngSignature => [0x89, (byte)'P', (byte)'N', (byte)'G'];

    /// <summary>The PNG images in an .ico by pixel size; entries in other formats
    /// (BMP) are skipped. An .ico stores 256 px as 0.</summary>
    public static IReadOnlyList<(int Size, byte[] Png)> PngImages(ReadOnlySpan<byte> ico)
    {
        var images = new List<(int, byte[])>();
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico[2..]) != 1)
            return images;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico[4..]);
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + 16 * i;
            if (entry + 16 > ico.Length)
                break;
            var width = ico[entry] == 0 ? 256 : ico[entry];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(ico[(entry + 8)..]);
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(ico[(entry + 12)..]);
            if (offset + (ulong)length > (ulong)ico.Length)
                continue;
            var image = ico.Slice((int)offset, (int)length);
            if (image.StartsWith(PngSignature))
                images.Add((width, image.ToArray()));
        }
        return images;
    }

    /// <summary>Writes the icons and the entry under <paramref name="dataHome"/>
    /// (<c>$XDG_DATA_HOME</c>), each only when it differs. <paramref name="owns"/>:
    /// replace an entry that differs, as the AppImage does; otherwise only write
    /// a missing one. Icons are the app's whoever wrote the entry, so a new logo
    /// replaces them either way.</summary>
    /// <returns>Whether anything was written.</returns>
    public static bool Write(string dataHome, string exePath, bool owns, ReadOnlySpan<byte> ico)
    {
        if (Entry(exePath) is not { } entry)
            return false;

        var wrote = false;
        foreach (var (size, png) in PngImages(ico))
        {
            var path = Path.Combine(dataHome, "icons", "hicolor", $"{size}x{size}", "apps", AppId + ".png");
            if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(png))
                continue;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            wrote = true;
        }

        // The entry last: a dock that sees it finds the icons already there.
        var file = Path.Combine(dataHome, "applications", DesktopFile);
        var current = File.Exists(file) ? File.ReadAllText(file) : null;
        if (current == entry || (current is not null && !owns))
            return wrote;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, entry);
        return true;
    }

    /// <summary>At startup, in the background, on Linux. Never throws: the window
    /// just keeps a generic icon.</summary>
    /// <param name="installedData">No demo, no other data folder.</param>
    /// <param name="ico">The app icon's .ico.</param>
    public static void Register(bool installedData, Func<byte[]> ico)
    {
        if (!OperatingSystem.IsLinux() || !installedData)
            return;
        var appImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var exe = appImage is { Length: > 0 } ? appImage : Environment.ProcessPath;
        if (exe is null)
            return;
        try
        {
            var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } home
                ? home
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            if (Write(dataHome, exe, owns: appImage is { Length: > 0 }, ico()))
                AppLog.Write("desktop", $"Wrote {DesktopFile} or its icons for {exe}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("desktop", $"{DesktopFile} not written: {ex.Message}");
        }
    }
}
