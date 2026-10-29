using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.Notifications;
using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// Track notifications through the desktop's notification daemon
/// (<c>org.freedesktop.Notifications</c>). The last one is closed before the
/// next is sent, not replaced through <c>replaces_id</c>: on KDE Plasma a
/// replaced notification showed only the first of a run of skips.
/// </summary>
/// <param name="address">The bus to use; the session bus when null (tests pass a private one).</param>
public sealed class FreedesktopTrackNotifier(string appName, string? address = null) : ITrackNotifier
{
    public const string Service = "org.freedesktop.Notifications";
    public const string ObjectPath = "/org/freedesktop/Notifications";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(2);

    private readonly Lock _sync = new();
    // Calls run one after another, in the order asked, off the UI thread.
    private Task _tail = Task.CompletedTask;
    private DBusConnection? _connection;
    private uint _shownId;
    private bool _disposed;

    // Whether a daemon runs is known only by asking; without one, the call fails and is logged.
    public bool CanShow => true;

    public void Show(TrackNotification notification) => Enqueue(async () =>
    {
        var connection = await ConnectAsync().ConfigureAwait(false);
        await CloseShownAsync(connection).ConfigureAwait(false);
        _shownId = await CallAsync(connection, "Notify", "susssasa{sv}i", (ref MessageWriter w) =>
        {
            w.WriteString(appName);
            w.WriteUInt32(0);
            w.WriteString(LinuxAppEntry.AppId);
            w.WriteString(notification.Title);
            w.WriteString(EscapeBody(notification.Body));
            w.WriteArray(Array.Empty<string>());
            var hints = w.WriteDictionaryStart();
            w.WriteDictionaryEntryStart();
            w.WriteString("desktop-entry");
            w.WriteVariantString(LinuxAppEntry.AppId);
            w.WriteDictionaryEntryStart();
            w.WriteString("suppress-sound");
            w.WriteVariantBool(true);
            if (notification.CoverFile is { } cover)
            {
                w.WriteDictionaryEntryStart();
                w.WriteString("image-path");
                w.WriteVariantString(new Uri(cover).AbsoluteUri);
            }
            w.WriteDictionaryEnd(hints);
            w.WriteInt32(-1);
        }, static (m, _) => m.GetBodyReader().ReadUInt32()).ConfigureAwait(false);
    });

    public void Close() => Enqueue(async () =>
    {
        if (_shownId != 0)
            await CloseShownAsync(await ConnectAsync().ConfigureAwait(false)).ConfigureAwait(false);
    });

    private void Enqueue(Func<Task> work)
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _tail = _tail.ContinueWith(async _ =>
            {
                try
                {
                    await work().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // No daemon, or it refused: the setting stays as it is.
                    AppLog.Write("notify", $"notification failed: {ex.GetType().Name}: {ex.Message}");
                    DropConnection();
                }
            }, TaskScheduler.Default).Unwrap();
        }
    }

    /// <summary>The task of the calls asked for so far (tests wait on it).</summary>
    internal Task Idle
    {
        get { lock (_sync) return _tail; }
    }

    private async Task CloseShownAsync(DBusConnection connection)
    {
        if (_shownId == 0)
            return;
        var id = _shownId;
        _shownId = 0;
        try
        {
            await CallAsync(connection, "CloseNotification", "u",
                (ref MessageWriter w) => w.WriteUInt32(id), static (_, _) => true).ConfigureAwait(false);
        }
        catch (DBusErrorReplyException)
        {
            // Already gone: closed by the user, or expired.
        }
    }

    private async Task<DBusConnection> ConnectAsync()
    {
        if (_connection is { } open)
            return open;
        var bus = address ?? DBusAddress.Session ?? throw new InvalidOperationException("no session bus");
        var connection = new DBusConnection(bus);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
        _connection = connection;
        return connection;
    }

    private void DropConnection()
    {
        _connection?.Dispose();
        _connection = null;
        _shownId = 0;
    }

    /// <summary>The body may be read as markup (the daemon's <c>body-markup</c>),
    /// so the characters markup gives meaning to are escaped.</summary>
    internal static string EscapeBody(string body) =>
        body.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private delegate void WriteArgs(ref MessageWriter writer);

    private static async Task<T> CallAsync<T>(DBusConnection connection, string member, string signature,
        WriteArgs write, MessageValueReader<T> read)
    {
        MessageBuffer message;
        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteMethodCallHeader(Service, ObjectPath, Service, member, signature, MessageFlags.None);
            write(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        var result = await connection.CallMethodAsync(message, read, null).WaitAsync(CallTimeout).ConfigureAwait(false);
        // The reply completes on the connection's receive loop; leave it before going on.
        await Task.Yield();
        return result;
    }

    public void Dispose()
    {
        Task tail;
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            tail = _tail;
        }
        // Take the last one away at exit, as the toast does; briefly, since the app is closing.
        tail.ContinueWith(async _ =>
        {
            try
            {
                if (_connection is { } connection)
                    await CloseShownAsync(connection).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
            DropConnection();
        }, TaskScheduler.Default).Unwrap().Wait(CallTimeout);
    }
}
