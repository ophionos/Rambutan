using Rambutan.Integrations.Linux;
using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Tests;

public sealed class StatusNotifierHostTests
{
    [Fact]
    public async Task Without_a_watcher_there_is_no_tray()
    {
        using var bus = await PrivateBus.StartAsync();

        Assert.False(await StatusNotifierHost.IsPresentAsync(bus.Address));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_watcher_shows_icons_only_once_a_host_registers(bool hostRegistered)
    {
        using var bus = await PrivateBus.StartAsync();
        using var watcher = new DBusConnection(bus.Address);
        await watcher.ConnectAsync();
        watcher.AddMethodHandler(new FakeWatcher(hostRegistered));
        await watcher.RequestNameAsync("org.kde.StatusNotifierWatcher", RequestNameOptions.None);

        Assert.Equal(hostRegistered, await StatusNotifierHost.IsPresentAsync(bus.Address));
    }

    [Fact]
    public async Task Without_a_bus_there_is_no_tray()
    {
        Assert.False(await StatusNotifierHost.IsPresentAsync("unix:path=/nonexistent/rambutan-test-bus"));
    }

    /// <summary>Answers the one property the check reads.</summary>
    private sealed class FakeWatcher(bool hostRegistered) : IPathMethodHandler
    {
        public string Path => "/StatusNotifierWatcher";

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var writer = context.CreateReplyWriter("v");
            try
            {
                writer.WriteVariantBool(hostRegistered);
                context.Reply(writer.CreateMessage());
            }
            finally
            {
                writer.Dispose();
            }
            return default;
        }
    }
}
