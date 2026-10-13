using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLoaded;

namespace Rambutan.Audio;

public static class FfmpegRuntime
{
    private static readonly object Gate = new();
    private static bool _initialized;

    /// <param name="librariesPath">
    /// Directory containing the ffmpeg shared libraries, or null to use the
    /// platform's default library search path (Linux system packages).
    /// </param>
    public static void Initialize(string? librariesPath)
    {
        lock (Gate)
        {
            if (_initialized)
                return;

            if (librariesPath is not null)
                DynamicallyLoadedBindings.LibrariesPath = librariesPath;

            DynamicallyLoadedBindings.Initialize();
            ffmpeg.av_log_set_level(ffmpeg.AV_LOG_ERROR);
            _initialized = true;
        }
    }

    public static string Version()
    {
        lock (Gate)
        {
            if (!_initialized)
                throw new InvalidOperationException("FfmpegRuntime.Initialize must be called first.");
        }
        return ffmpeg.av_version_info();
    }
}
