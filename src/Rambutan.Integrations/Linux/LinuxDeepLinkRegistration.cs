using System.Diagnostics;
using System.Text;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Links;

namespace Rambutan.Integrations.Linux;

/// <summary>What the registration needs from the desktop: the user's applications
/// folder and xdg-mime. Tests pass a fake.</summary>
public interface IDesktopHandlers
{
    /// <summary>The desktop file that opens <paramref name="mimeType"/> now, or null.</summary>
    Task<string?> QueryDefaultAsync(string mimeType);

    Task SetDefaultAsync(string desktopFile, string mimeType);

    /// <summary>The contents of a desktop file in the user's applications folder, or null.</summary>
    string? Read(string desktopFile);

    void Write(string desktopFile, string contents);
}

public enum LinkRegistrationResult
{
    /// <summary>The scheme opens this AppImage now.</summary>
    Registered,

    /// <summary>It already did; nothing was written.</summary>
    Unchanged,

    /// <summary>Another application handles the scheme, and keeps it.</summary>
    TakenByOther,

    /// <summary>The AppImage's path can't be written into a desktop file.</summary>
    Unusable,
}

/// <summary>
/// Registers the <c>rambutan://</c> scheme on Linux, per user. Velopack has
/// no install hooks there, so the AppImage registers itself each time it starts:
/// a handler-only desktop file (<c>NoDisplay</c>, so the menu doesn't list the app
/// twice) in the user's applications folder, opening
/// <c>"$APPIMAGE" --deep-link %u</c>, set as the scheme's default through xdg-mime.
/// A scheme another application handles is left to it. Only the installed app
/// registers: an AppImage run on its default data folder, never a dev run, a
/// demo or a <c>RAMBUTAN_DATA_DIR</c> run. Deleting the AppImage leaves the file
/// behind (maintainer, 2026-10-09).
/// </summary>
public static class LinuxDeepLinkRegistration
{
    public const string DesktopFile = "rambutan-links.desktop";
    public const string MimeType = "x-scheme-handler/" + DeepLinkParser.Scheme;

    /// <summary>The desktop file for an AppImage at <paramref name="appImagePath"/>;
    /// null for a path a desktop file can't carry. Lines end in LF on every
    /// platform: the raw string takes the source file's line ends, and a Windows
    /// checkout gives it CRLF.</summary>
    public static string? Entry(string appImagePath) =>
        ExecQuote(appImagePath) is { } exe
            ? $"""
              [Desktop Entry]
              Type=Application
              Name=Rambutan
              Exec={exe} {DeepLinkArgs.Option} %u
              Terminal=false
              NoDisplay=true
              MimeType={MimeType};

              """.ReplaceLineEndings("\n")
            : null;

    /// <summary>
    /// A path for an Exec key, quoted only when it holds a character the spec
    /// reserves: xdg-open on a desktop it doesn't know (COSMIC among them) takes
    /// the key's first word as the program, quotes and all, and opens the link
    /// in the browser when no such program exists. Quoting is in double
    /// quotes, with <c>"</c>, <c>`</c>,
    /// <c>$</c> and <c>\</c> escaped by a backslash (the Desktop Entry spec's rules
    /// for arguments). The key's value is a string, so each backslash is then
    /// escaped once more. A line break can't be carried at all, and neither can
    /// <c>%</c> in practice: the spec doubles it, but GLib looks for the program
    /// before undoing that, doesn't find it, and drops the whole file.
    /// </summary>
    private static readonly System.Buffers.SearchValues<char> Reserved =
        System.Buffers.SearchValues.Create(" \t\"'\\><~|&;$*?#()`");

    internal static string? ExecQuote(string path)
    {
        if (path.Length == 0 || path.AsSpan().IndexOfAny('\n', '\r', '%') >= 0)
            return null;
        if (path.AsSpan().IndexOfAny(Reserved) < 0)
            return path;
        var quoted = new StringBuilder("\"");
        foreach (var c in path)
        {
            switch (c)
            {
                case '"' or '`' or '$':
                    quoted.Append('\\').Append('\\').Append(c);
                    break;
                case '\\':
                    quoted.Append(@"\\\\");
                    break;
                default:
                    quoted.Append(c);
                    break;
            }
        }
        return quoted.Append('"').ToString();
    }

    public static async Task<LinkRegistrationResult> RegisterAsync(IDesktopHandlers desktop, string appImagePath)
    {
        if (Entry(appImagePath) is not { } entry)
            return LinkRegistrationResult.Unusable;

        var current = (await desktop.QueryDefaultAsync(MimeType))?.Trim();
        if (!string.IsNullOrEmpty(current) && current != DesktopFile)
            return LinkRegistrationResult.TakenByOther;

        // Written again when the AppImage moved, or the file went away.
        var wrote = false;
        if (desktop.Read(DesktopFile) != entry)
        {
            desktop.Write(DesktopFile, entry);
            wrote = true;
        }
        if (current != DesktopFile)
            await desktop.SetDefaultAsync(DesktopFile, MimeType);
        return wrote || current != DesktopFile ? LinkRegistrationResult.Registered : LinkRegistrationResult.Unchanged;
    }

    /// <summary>At startup, in the background. Registers when this run is the
    /// installed app (<paramref name="installedData"/>: no demo, no other data
    /// folder) started from an AppImage. Never throws: links still work as arguments.</summary>
    public static async Task RegisterAppImageAsync(bool installedData)
    {
        if (!OperatingSystem.IsLinux() || !installedData ||
            Environment.GetEnvironmentVariable("APPIMAGE") is not { Length: > 0 } appImage)
        {
            return;
        }
        try
        {
            var result = await RegisterAsync(new XdgDesktopHandlers(), appImage);
            if (result != LinkRegistrationResult.Unchanged)
                AppLog.Write("links", $"rambutan:// registration: {result}");
        }
        catch (Exception ex)
        {
            AppLog.Write("links", $"rambutan:// registration failed: {ex.Message}");
        }
    }

    /// <summary>xdg-mime, and <c>$XDG_DATA_HOME/applications</c> (by default
    /// <c>~/.local/share/applications</c>).</summary>
    private sealed class XdgDesktopHandlers : IDesktopHandlers
    {
        private static string ApplicationsDir =>
            Path.Combine(
                Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } dataHome
                    ? dataHome
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share"),
                "applications");

        public async Task<string?> QueryDefaultAsync(string mimeType) =>
            await RunAsync("xdg-mime", "query", "default", mimeType);

        public async Task SetDefaultAsync(string desktopFile, string mimeType)
        {
            // The MIME cache, first and best effort: without xdg-mime (xdg-utils
            // isn't everywhere), GIO still finds the only handler through it.
            await RunAsync("update-desktop-database", ApplicationsDir);
            if (await RunAsync("xdg-mime", "default", desktopFile, mimeType) is null)
                throw new InvalidOperationException("xdg-mime default failed");
        }

        public string? Read(string desktopFile)
        {
            var path = Path.Combine(ApplicationsDir, desktopFile);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }

        public void Write(string desktopFile, string contents)
        {
            Directory.CreateDirectory(ApplicationsDir);
            File.WriteAllText(Path.Combine(ApplicationsDir, desktopFile), contents);
        }

        /// <summary>Standard output, or null when the tool is missing or fails.</summary>
        private static async Task<string?> RunAsync(string fileName, params string[] args)
        {
            var psi = new ProcessStartInfo(fileName)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in args)
                psi.ArgumentList.Add(arg);
            try
            {
                using var process = Process.Start(psi);
                if (process is null)
                    return null;
                var output = process.StandardOutput.ReadToEndAsync();
                _ = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
                return process.ExitCode == 0 ? await output : null;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or OperationCanceledException)
            {
                return null;
            }
        }
    }
}
