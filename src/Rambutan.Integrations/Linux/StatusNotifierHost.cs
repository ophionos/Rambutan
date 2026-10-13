using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// Whether this Linux desktop shows tray icons. Avalonia publishes the tray
/// icon as a StatusNotifierItem, and only a StatusNotifier host (KDE, most
/// panels, GNOME with the AppIndicator extension) draws it. Without one, a
/// window hidden "to the tray" could never be brought back.
/// </summary>
public static class StatusNotifierHost
{
    private const string Watcher = "org.kde.StatusNotifierWatcher";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(1);

    /// <summary>True when a watcher is running and a host has registered with it.
    /// False when not, and when there is no bus or it doesn't answer in time.</summary>
    /// <param name="address">The bus to ask; the session bus when null.</param>
    public static Task<bool> IsPresentAsync(string? address = null) =>
        // On the thread pool, away from any caller's synchronization context.
        Task.Run(async () =>
        {
            if ((address ?? DBusAddress.Session) is not { Length: > 0 } bus)
                return false;
            using var connection = new DBusConnection(bus);
            try
            {
                await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
                if (!(await connection.ListServicesAsync().WaitAsync(CallTimeout).ConfigureAwait(false)).Contains(Watcher))
                    return false;

                var writer = connection.GetMessageWriter();
                MessageBuffer message;
                try
                {
                    writer.WriteMethodCallHeader(Watcher, "/StatusNotifierWatcher",
                        "org.freedesktop.DBus.Properties", "Get", "ss", MessageFlags.None);
                    writer.WriteString(Watcher);
                    writer.WriteString("IsStatusNotifierHostRegistered");
                    message = writer.CreateMessage();
                }
                finally
                {
                    writer.Dispose();
                }
                return await connection.CallMethodAsync(message,
                    static (m, _) => m.GetBodyReader().ReadVariantValue().GetBool(), null)
                    .WaitAsync(CallTimeout).ConfigureAwait(false);
            }
            catch (Exception)
            {
                return false;
            }
        });
}
