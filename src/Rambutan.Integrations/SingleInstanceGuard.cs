using System.IO.Pipes;
using System.Text;

namespace Rambutan.Integrations;

/// <summary>
/// Single instance through a per-name lock, plus a named-pipe channel through
/// which a secondary launch forwards its args to the primary, rambutan:// links
/// among them.
/// </summary>
public sealed class SingleInstanceGuard : IDisposable
{
    private readonly string _pipeName;
    private readonly IDisposable? _lock;
    private readonly CancellationTokenSource _cts = new();

    public bool IsPrimary => _lock is not null;

    /// <summary>Raised on the primary (from a worker thread) when a secondary launches.</summary>
    public event Action<string[]>? ArgsReceived;

    private SingleInstanceGuard(string name)
    {
        _pipeName = $"{name}.instance";
        _lock = TryLock(name);
        if (IsPrimary)
            _ = ListenAsync(_cts.Token);
    }

    public static SingleInstanceGuard Acquire(string name) => new(name);

    /// <summary>
    /// Takes another instance's lock without listening for its launches, so it
    /// can't start while its data is changed (deleting a profile). Null when that
    /// instance is running. Dispose on the thread that reserved.
    /// </summary>
    public static IDisposable? TryReserve(string name) => TryLock(name);

    /// <summary>
    /// The instance's lock, or null when another process holds it. On Windows it
    /// is a session-scoped mutex (<c>Local\</c>), and the session is the user's
    /// logon. Elsewhere it is an exclusive lock on a file in the user's runtime
    /// folder: there .NET scopes a named mutex to the process's Unix session
    /// (every terminal, and every app a launcher or a browser starts with setsid),
    /// and under NativeAOT, which the release build is, to the process itself.
    /// Either way a rambutan:// link started a second instance on the same data
    /// folder instead of reaching the running one. The kernel drops the file lock
    /// when the process ends, however it ends.
    /// </summary>
    private static IDisposable? TryLock(string name)
    {
        if (OperatingSystem.IsWindows())
        {
            var mutex = new Mutex(initiallyOwned: true, $@"Local\{name}.single-instance", out var createdNew);
            if (createdNew)
                return new MutexLock(mutex);
            mutex.Dispose();
            return null;
        }

        try
        {
            // FileShare.None takes flock(LOCK_EX | LOCK_NB) on the file.
            return new FileStream(LockPath(name), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            return null;
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    internal static string LockPath(string name) => Path.Combine(LockFolder(), $"{name}.single-instance.lock");

    /// <summary>$XDG_RUNTIME_DIR (per user, on a tmpfs), else a per-user folder
    /// under the temp folder.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static string LockFolder()
    {
        if (Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") is { Length: > 0 } runtime && Directory.Exists(runtime))
            return runtime;
        var folder = Path.Combine(Path.GetTempPath(), $"rambutan-{Environment.UserName}");
        Directory.CreateDirectory(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return folder;
    }

    private sealed class MutexLock(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned by this thread; closing the handle still frees it.
            }
            mutex.Dispose();
        }
    }

    /// <summary>Secondary → primary: forward args (empty still means "focus yourself").</summary>
    public void NotifyPrimary(string[] args)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            client.Connect(2000);
            using var writer = new StreamWriter(client, Encoding.UTF8);
            foreach (var arg in args)
                writer.WriteLine(arg);
        }
        catch
        {
            // The primary vanished between the mutex check and the connect; the
            // user will simply relaunch.
        }
    }

    private static readonly TimeSpan ClientReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>How many forwarded arguments the primary reads, and how long each
    /// may be; the rest are dropped.</summary>
    internal const int MaxArgs = 32;
    internal const int MaxArgLength = 4096;

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(
                    _pipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(ct);

                // The server is single-instance and sequential — a client that
                // connects and never finishes must not block the channel forever.
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                readCts.CancelAfter(ClientReadTimeout);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                // Capped: any local process can write to the pipe, and what it
                // sends may be a rambutan:// link the app acts on.
                var args = new List<string>();
                while (await reader.ReadLineAsync(readCts.Token) is { } line && args.Count < MaxArgs)
                {
                    if (line.Length <= MaxArgLength)
                        args.Add(line);
                }

                ArgsReceived?.Invoke([.. args]);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // A misbehaving or timed-out client must not kill the listener.
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _lock?.Dispose();
        _cts.Dispose();
    }
}
