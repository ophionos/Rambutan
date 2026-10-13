using System;
using System.IO;

namespace Rambutan.App.Services;

/// <summary>
/// Minimal rolling file log backing <see cref="Rambutan.Core.Diagnostics.AppLog"/>:
/// one active file plus one rolled predecessor, size-capped, flushed per line so
/// evidence survives a crash. Hand-rolled: NativeAOT rules out reflection-based
/// logging packages, and error-rate volume doesn't justify buffering.
/// Every path swallows I/O failures — logging must never take down playback.
/// </summary>
public sealed class FileLog : IDisposable
{
    private const long MaxBytes = 1 << 20; // per file; ~2 MB total worst case

    private readonly object _sync = new();
    private readonly string _path;
    private readonly string _rolledPath;
    private StreamWriter? _writer;

    public FileLog(string directory)
    {
        _path = Path.Combine(directory, "rambutan.log");
        _rolledPath = Path.Combine(directory, "rambutan.1.log");
        try
        {
            Directory.CreateDirectory(directory);
            _writer = Open();
        }
        catch
        {
            // Disk said no (permissions, full, antivirus lock) — run silent.
        }
    }

    public void Write(string category, string message)
    {
        lock (_sync)
        {
            if (_writer is null)
                return;

            try
            {
                _writer.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss.fff}Z [{category}] {message}");
                if (_writer.BaseStream.Length >= MaxBytes)
                    Roll();
            }
            catch
            {
                _writer = null; // wedged stream — stop trying rather than throw per line
            }
        }
    }

    private void Roll()
    {
        _writer!.Dispose();
        _writer = null;
        File.Move(_path, _rolledPath, overwrite: true);
        _writer = Open();
    }

    private StreamWriter Open() =>
        new(new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read))
        {
            AutoFlush = true,
        };

    public void Dispose()
    {
        lock (_sync)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
