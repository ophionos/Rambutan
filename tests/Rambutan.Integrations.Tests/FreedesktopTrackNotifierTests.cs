using Rambutan.Integrations.Linux;
using Rambutan.Integrations.Notifications;
using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Tests;

public sealed class FreedesktopTrackNotifierTests
{
    private static async Task<(PrivateBus Bus, DBusConnection Daemon, FakeNotifications Fake)> DaemonAsync()
    {
        var bus = await PrivateBus.StartAsync();
        var daemon = new DBusConnection(bus.Address);
        await daemon.ConnectAsync();
        var fake = new FakeNotifications();
        daemon.AddMethodHandler(fake);
        await daemon.RequestNameAsync(FreedesktopTrackNotifier.Service, RequestNameOptions.None);
        return (bus, daemon, fake);
    }

    [Fact]
    public async Task Shows_the_track_with_its_cover_silently_from_the_apps_entry()
    {
        var (bus, daemon, fake) = await DaemonAsync();
        using var _ = bus;
        using var __ = daemon;
        using var notifier = new FreedesktopTrackNotifier("Rambutan", bus.Address);

        notifier.Show(new TrackNotification("Rock & Roll", "<Artist> · Album", "/tmp/covers/a b.jpg"));
        await notifier.Idle;

        var shown = Assert.Single(fake.Notified);
        Assert.Equal("Rambutan", shown.App);
        Assert.Equal(0u, shown.ReplacesId);
        Assert.Equal("Rock & Roll", shown.Summary);
        Assert.Equal("&lt;Artist&gt; · Album", shown.Body);
        Assert.Equal("rambutan", shown.Hints["desktop-entry"]);
        Assert.Equal("file:///tmp/covers/a%20b.jpg", shown.Hints["image-path"]);
        Assert.Equal("True", shown.Hints["suppress-sound"]);
    }

    [Fact]
    public async Task Each_one_closes_the_last_rather_than_replacing_it()
    {
        var (bus, daemon, fake) = await DaemonAsync();
        using var _ = bus;
        using var __ = daemon;
        using var notifier = new FreedesktopTrackNotifier("Rambutan", bus.Address);

        notifier.Show(new TrackNotification("One", "", null));
        notifier.Show(new TrackNotification("Two", "", null));
        notifier.Show(new TrackNotification("Three", "", null));
        await notifier.Idle;

        Assert.Equal(["One", "Two", "Three"], fake.Notified.Select(n => n.Summary));
        Assert.All(fake.Notified, n => Assert.Equal(0u, n.ReplacesId));
        Assert.Equal([1u, 2u], fake.Closed);
    }

    [Fact]
    public async Task Close_takes_the_shown_one_away_once()
    {
        var (bus, daemon, fake) = await DaemonAsync();
        using var _ = bus;
        using var __ = daemon;
        using var notifier = new FreedesktopTrackNotifier("Rambutan", bus.Address);

        notifier.Show(new TrackNotification("One", "", null));
        notifier.Close();
        notifier.Close();
        await notifier.Idle;

        Assert.Equal([1u], fake.Closed);
    }

    [Fact]
    public async Task Without_a_daemon_nothing_throws()
    {
        using var bus = await PrivateBus.StartAsync();
        using var notifier = new FreedesktopTrackNotifier("Rambutan", bus.Address);

        notifier.Show(new TrackNotification("One", "", null));
        await notifier.Idle;
    }

    [Fact]
    public async Task Without_a_bus_nothing_throws()
    {
        using var notifier = new FreedesktopTrackNotifier("Rambutan", "unix:path=/nonexistent/rambutan-test-bus");

        notifier.Show(new TrackNotification("One", "", null));
        await notifier.Idle;
    }

    private sealed record Notified(string App, uint ReplacesId, string Summary, string Body, Dictionary<string, string> Hints);

    /// <summary>Answers Notify with ids from 1 and records CloseNotification.</summary>
    private sealed class FakeNotifications : IPathMethodHandler
    {
        private uint _nextId;

        public List<Notified> Notified { get; } = [];

        public List<uint> Closed { get; } = [];

        public string Path => FreedesktopTrackNotifier.ObjectPath;

        public bool HandlesChildPaths => false;

        public ValueTask HandleMethodAsync(MethodContext context)
        {
            var reader = context.Request.GetBodyReader();
            if (context.Request.MemberAsString == "Notify")
            {
                var app = reader.ReadString();
                var replaces = reader.ReadUInt32();
                reader.ReadString();
                var summary = reader.ReadString();
                var body = reader.ReadString();
                reader.ReadArrayOfString();
                var hints = reader.ReadDictionaryOfStringToVariantValue()
                    .ToDictionary(h => h.Key, h => h.Value.Type == VariantValueType.Bool
                        ? h.Value.GetBool().ToString()
                        : h.Value.GetString());
                reader.ReadInt32();
                lock (Notified)
                    Notified.Add(new(app, replaces, summary, body, hints));
                var writer = context.CreateReplyWriter("u");
                try
                {
                    writer.WriteUInt32(++_nextId);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
            }
            else
            {
                lock (Notified)
                    Closed.Add(reader.ReadUInt32());
                var writer = context.CreateReplyWriter(null);
                try
                {
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
            }
            return default;
        }
    }
}
