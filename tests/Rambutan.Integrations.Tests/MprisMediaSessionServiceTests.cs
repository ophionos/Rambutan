using Rambutan.Core.Queue;
using Rambutan.Integrations.Linux;
using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Tests;

/// <summary>Over a real bus, each test's own (<see cref="PrivateBus"/>).</summary>
public sealed class MprisMediaSessionServiceTests : IAsyncLifetime
{
    private const string Path = "/org/mpris/MediaPlayer2";
    private const string Player = "org.mpris.MediaPlayer2.Player";
    private const string Root = "org.mpris.MediaPlayer2";
    private const string Properties = "org.freedesktop.DBus.Properties";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private PrivateBus? _bus;
    private string _address = "";
    private DBusConnection? _client;
    private readonly List<MprisMediaSessionService> _services = [];

    public async ValueTask InitializeAsync()
    {
        _bus = await PrivateBus.StartAsync();
        _address = _bus.Address;
        _client = new DBusConnection(_address);
        await _client.ConnectAsync();
    }

    public ValueTask DisposeAsync()
    {
        foreach (var service in _services)
            service.Dispose();
        _client?.Dispose();
        _bus?.Dispose();
        return default;
    }

    private async Task<(MprisMediaSessionService Service, string Name)> StartAsync()
    {
        var service = new MprisMediaSessionService("Rambutan test", _address);
        _services.Add(service);
        var name = await service.ConnectAsync();
        Assert.NotNull(name);
        return (service, name);
    }

    private delegate void WriteArgs(ref MessageWriter writer);

    private Task<T> CallAsync<T>(string destination, string @interface, string member, string? signature,
        WriteArgs? write, MessageValueReader<T> read)
    {
        var writer = _client!.GetMessageWriter();
        MessageBuffer message;
        try
        {
            writer.WriteMethodCallHeader(destination, Path, @interface, member, signature, MessageFlags.None);
            write?.Invoke(ref writer);
            message = writer.CreateMessage();
        }
        finally
        {
            writer.Dispose();
        }
        return _client.CallMethodAsync(message, read, null).WaitAsync(Timeout);
    }

    private Task<bool> CallAsync(string destination, string @interface, string member,
        string? signature = null, WriteArgs? write = null) =>
        CallAsync(destination, @interface, member, signature, write, static (_, _) => true);

    private Task<VariantValue> GetAsync(string name, string @interface, string property) =>
        CallAsync(name, Properties, "Get", "ss",
            (ref MessageWriter w) =>
            {
                w.WriteString(@interface);
                w.WriteString(property);
            },
            static (m, _) => m.GetBodyReader().ReadVariantValue());

    private Task<Dictionary<string, VariantValue>> GetAllAsync(string name, string @interface) =>
        CallAsync(name, Properties, "GetAll", "s",
            (ref MessageWriter w) => w.WriteString(@interface),
            static (m, _) => m.GetBodyReader().ReadDictionaryOfStringToVariantValue());

    [Fact]
    public async Task Serves_the_player_under_the_mpris_name()
    {
        var (_, name) = await StartAsync();

        Assert.Equal(MprisMediaSessionService.BusName, name);
        var root = await GetAllAsync(name, Root);
        Assert.Equal("Rambutan test", root["Identity"].GetString());
        Assert.True(root["CanRaise"].GetBool());
        Assert.False(root["HasTrackList"].GetBool());

        var player = await GetAllAsync(name, Player);
        Assert.Equal("Stopped", player["PlaybackStatus"].GetString());
        Assert.False(player["CanPlay"].GetBool());
        Assert.True(player["CanControl"].GetBool());
        Assert.Equal(1.0, player["Volume"].GetDouble());
        Assert.False(player["Shuffle"].GetBool());
        Assert.Equal("None", player["LoopStatus"].GetString());
    }

    [Fact]
    public async Task A_second_instance_takes_a_name_of_its_own()
    {
        await StartAsync();
        var (_, second) = await StartAsync();

        Assert.Equal($"{MprisMediaSessionService.BusName}.instance{Environment.ProcessId}", second);
    }

    [Fact]
    public async Task Now_playing_is_announced_and_readable()
    {
        var (service, name) = await StartAsync();
        var changed = new TaskCompletionSource<Dictionary<string, VariantValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await _client!.WatchSignalAsync(null, Path, Properties, "PropertiesChanged",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadString();
                return reader.ReadDictionaryOfStringToVariantValue();
            },
            (Notification<Dictionary<string, VariantValue>> n) =>
            {
                if (n.HasValue && n.Value.ContainsKey("Metadata"))
                    changed.TrySetResult(n.Value);
            },
            ObserverFlags.None, false, null);

        service.UpdateNowPlaying(MprisPlayerStateTests.Track());
        service.UpdatePlaybackState(SessionPlaybackStatus.Playing);

        var announced = await changed.Task.WaitAsync(Timeout);
        Assert.DoesNotContain("Position", announced.Keys);
        var metadata = (await GetAsync(name, Player, "Metadata")).GetDictionary<string, VariantValue>();
        Assert.Equal(MprisPlayerState.TrackIdFor("abc-DEF_123"), metadata["mpris:trackid"].GetObjectPathAsString());
        Assert.Equal("Paper Lanterns", metadata["xesam:title"].GetString());
        Assert.Equal(["The Quiet Harbour", "Guest"], metadata["xesam:artist"].GetArray<string>());
        Assert.Equal(212_000_000, metadata["mpris:length"].GetInt64());
        Assert.Equal("Playing", (await GetAsync(name, Player, "PlaybackStatus")).GetString());
    }

    [Fact]
    public async Task Transport_methods_raise_their_requests()
    {
        var (service, name) = await StartAsync();
        var asked = new List<string>();
        service.PlayPauseRequested += () => asked.Add("PlayPause");
        service.PlayRequested += () => asked.Add("Play");
        service.PauseRequested += () => asked.Add("Pause");
        service.StopRequested += () => asked.Add("Stop");
        service.NextRequested += () => asked.Add("Next");
        service.PreviousRequested += () => asked.Add("Previous");
        service.RaiseRequested += () => asked.Add("Raise");
        service.QuitRequested += () => asked.Add("Quit");

        foreach (var member in new[] { "PlayPause", "Play", "Pause", "Stop", "Next", "Previous" })
            await CallAsync(name, Player, member);
        await CallAsync(name, Root, "Raise");
        await CallAsync(name, Root, "Quit");

        Assert.Equal(["PlayPause", "Play", "Pause", "Stop", "Next", "Previous", "Raise", "Quit"], asked);
    }

    [Fact]
    public async Task Seeking_is_relative_and_set_position_needs_the_current_track()
    {
        var (service, name) = await StartAsync();
        var seeks = new List<TimeSpan>();
        var nexts = 0;
        service.SeekRequested += seeks.Add;
        service.NextRequested += () => nexts++;
        service.UpdateNowPlaying(MprisPlayerStateTests.Track());
        service.UpdatePlaybackState(SessionPlaybackStatus.Paused);
        service.UpdateTimeline(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(212));

        await CallAsync(name, Player, "Seek", "x", (ref MessageWriter w) => w.WriteInt64(10_000_000));
        await CallAsync(name, Player, "Seek", "x", (ref MessageWriter w) => w.WriteInt64(-120_000_000));
        await CallAsync(name, Player, "Seek", "x", (ref MessageWriter w) => w.WriteInt64(500_000_000));
        var current = MprisPlayerState.TrackIdFor("abc-DEF_123");
        await CallAsync(name, Player, "SetPosition", "ox", (ref MessageWriter w) =>
        {
            w.WriteObjectPath(current);
            w.WriteInt64(30_000_000);
        });
        await CallAsync(name, Player, "SetPosition", "ox", (ref MessageWriter w) =>
        {
            w.WriteObjectPath(MprisPlayerState.TrackIdFor("someone-else"));
            w.WriteInt64(40_000_000);
        });

        Assert.Equal([TimeSpan.FromSeconds(70), TimeSpan.Zero, TimeSpan.FromSeconds(30)], seeks);
        Assert.Equal(1, nexts);
        Assert.Equal(60_000_000, (await GetAsync(name, Player, "Position")).GetInt64());
    }

    [Fact]
    public async Task A_jump_in_the_timeline_sends_seeked()
    {
        var (service, _) = await StartAsync();
        var seeked = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await _client!.WatchSignalAsync(null, Path, Player, "Seeked",
            static (m, _) => m.GetBodyReader().ReadInt64(),
            (Notification<long> n) =>
            {
                if (n.HasValue)
                    seeked.TrySetResult(n.Value);
            },
            ObserverFlags.None, false, null);
        service.UpdateNowPlaying(MprisPlayerStateTests.Track());
        service.UpdatePlaybackState(SessionPlaybackStatus.Paused);

        service.UpdateTimeline(TimeSpan.FromSeconds(95), TimeSpan.FromSeconds(212));

        Assert.Equal(95_000_000, await seeked.Task.WaitAsync(Timeout));
    }

    [Fact]
    public async Task Properties_are_read_only_and_unknown_ones_are_an_error()
    {
        var (_, name) = await StartAsync();

        var set = await Assert.ThrowsAsync<DBusErrorReplyException>(() => CallAsync(name, Properties, "Set", "ssv",
            (ref MessageWriter w) =>
            {
                w.WriteString(Player);
                w.WriteString("Rate");
                w.WriteVariantDouble(0.5);
            }));
        Assert.Equal("org.freedesktop.DBus.Error.PropertyReadOnly", set.ErrorName);
        var unknown = await Assert.ThrowsAsync<DBusErrorReplyException>(() => GetAsync(name, Player, "Fullscreen"));
        Assert.Equal("org.freedesktop.DBus.Error.UnknownProperty", unknown.ErrorName);
    }

    [Fact]
    public async Task Volume_is_set_through_the_app_and_announced()
    {
        var (service, name) = await StartAsync();
        var asked = new List<double>();
        service.VolumeRequested += asked.Add;
        var changed = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await _client!.WatchSignalAsync(null, Path, Properties, "PropertiesChanged",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadString();
                return reader.ReadDictionaryOfStringToVariantValue();
            },
            (Notification<Dictionary<string, VariantValue>> n) =>
            {
                if (n.HasValue && n.Value.TryGetValue("Volume", out var volume))
                    changed.TrySetResult(volume.GetDouble());
            },
            ObserverFlags.None, false, null);

        foreach (var volume in new[] { 0.25, -1.0, 3.0 })
            await CallAsync(name, Properties, "Set", "ssv", (ref MessageWriter w) =>
            {
                w.WriteString(Player);
                w.WriteString("Volume");
                w.WriteVariantDouble(volume);
            });
        var wrongType = await Assert.ThrowsAsync<DBusErrorReplyException>(() => CallAsync(name, Properties, "Set", "ssv",
            (ref MessageWriter w) =>
            {
                w.WriteString(Player);
                w.WriteString("Volume");
                w.WriteVariantString("loud");
            }));

        // The service only asks; the volume it reports is what the app then set.
        Assert.Equal([0.25, 0.0, 1.0], asked);
        Assert.Equal("org.freedesktop.DBus.Error.InvalidArgs", wrongType.ErrorName);
        Assert.Equal(1.0, (await GetAsync(name, Player, "Volume")).GetDouble());
        service.UpdateVolume(0.25);
        Assert.Equal(0.25, await changed.Task.WaitAsync(Timeout));
        Assert.Equal(0.25, (await GetAsync(name, Player, "Volume")).GetDouble());
    }

    [Fact]
    public async Task Shuffle_and_loop_are_set_through_the_app_and_announced()
    {
        var (service, name) = await StartAsync();
        var shuffles = new List<bool>();
        var repeats = new List<RepeatMode>();
        service.ShuffleRequested += shuffles.Add;
        service.RepeatRequested += repeats.Add;
        var changed = new TaskCompletionSource<Dictionary<string, VariantValue>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new Dictionary<string, VariantValue>();
        using var watch = await _client!.WatchSignalAsync(null, Path, Properties, "PropertiesChanged",
            static (m, _) =>
            {
                var reader = m.GetBodyReader();
                reader.ReadString();
                return reader.ReadDictionaryOfStringToVariantValue();
            },
            (Notification<Dictionary<string, VariantValue>> n) =>
            {
                if (!n.HasValue)
                    return;
                lock (seen)
                {
                    foreach (var (key, value) in n.Value)
                        seen[key] = value;
                    if (seen.ContainsKey("Shuffle") && seen.ContainsKey("LoopStatus"))
                        changed.TrySetResult(new(seen));
                }
            },
            ObserverFlags.None, false, null);

        await CallAsync(name, Properties, "Set", "ssv", (ref MessageWriter w) =>
        {
            w.WriteString(Player);
            w.WriteString("Shuffle");
            w.WriteVariantBool(true);
        });
        foreach (var loop in new[] { "Track", "Playlist", "None" })
            await CallAsync(name, Properties, "Set", "ssv", (ref MessageWriter w) =>
            {
                w.WriteString(Player);
                w.WriteString("LoopStatus");
                w.WriteVariantString(loop);
            });
        var unknownLoop = await Assert.ThrowsAsync<DBusErrorReplyException>(() => CallAsync(name, Properties, "Set", "ssv",
            (ref MessageWriter w) =>
            {
                w.WriteString(Player);
                w.WriteString("LoopStatus");
                w.WriteVariantString("Forever");
            }));
        var wrongType = await Assert.ThrowsAsync<DBusErrorReplyException>(() => CallAsync(name, Properties, "Set", "ssv",
            (ref MessageWriter w) =>
            {
                w.WriteString(Player);
                w.WriteString("Shuffle");
                w.WriteVariantString("yes");
            }));

        // The service only asks; what it reports is what the app then set.
        Assert.Equal([true], shuffles);
        Assert.Equal([RepeatMode.One, RepeatMode.All, RepeatMode.Off], repeats);
        Assert.Equal("org.freedesktop.DBus.Error.InvalidArgs", unknownLoop.ErrorName);
        Assert.Equal("org.freedesktop.DBus.Error.InvalidArgs", wrongType.ErrorName);
        Assert.False((await GetAsync(name, Player, "Shuffle")).GetBool());
        service.UpdateShuffle(true);
        service.UpdateRepeat(RepeatMode.All);
        var announced = await changed.Task.WaitAsync(Timeout);
        Assert.True(announced["Shuffle"].GetBool());
        Assert.Equal("Playlist", announced["LoopStatus"].GetString());
        Assert.Equal("Playlist", (await GetAsync(name, Player, "LoopStatus")).GetString());
    }

    [Fact]
    public async Task Stopped_is_reported_with_the_track_still_shown()
    {
        var (service, name) = await StartAsync();
        service.UpdateNowPlaying(MprisPlayerStateTests.Track());
        service.UpdatePlaybackState(SessionPlaybackStatus.Playing);
        service.UpdateTimeline(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(212));

        service.UpdatePlaybackState(SessionPlaybackStatus.Stopped);

        var player = await GetAllAsync(name, Player);
        Assert.Equal("Stopped", player["PlaybackStatus"].GetString());
        Assert.Equal(0, player["Position"].GetInt64());
        Assert.True(player["CanPlay"].GetBool());
        Assert.Equal("Paper Lanterns", player["Metadata"].GetDictionary<string, VariantValue>()["xesam:title"].GetString());
    }

    [Fact]
    public async Task Introspection_describes_both_interfaces()
    {
        var (_, name) = await StartAsync();

        var xml = await CallAsync(name, "org.freedesktop.DBus.Introspectable", "Introspect", null, null,
            static (m, _) => m.GetBodyReader().ReadString());

        Assert.Contains("<interface name=\"org.mpris.MediaPlayer2\">", xml);
        Assert.Contains("<interface name=\"org.mpris.MediaPlayer2.Player\">", xml);
        Assert.Contains("<signal name=\"Seeked\">", xml);
        Assert.Contains("<property name=\"Volume\" type=\"d\" access=\"readwrite\"/>", xml);
        Assert.Contains("<property name=\"Shuffle\" type=\"b\" access=\"readwrite\"/>", xml);
        Assert.Contains("<property name=\"LoopStatus\" type=\"s\" access=\"readwrite\"/>", xml);
    }

    [Fact]
    public async Task Disposing_gives_the_name_back()
    {
        var (service, name) = await StartAsync();

        service.Dispose();

        var deadline = DateTime.UtcNow + Timeout;
        while ((await _client!.ListServicesAsync()).Contains(name))
        {
            Assert.True(DateTime.UtcNow < deadline, "The name was still owned.");
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task Without_a_bus_there_is_no_session()
    {
        using var service = new MprisMediaSessionService("Rambutan test", "unix:path=/nonexistent/rambutan-test-bus");

        Assert.Null(await service.ConnectAsync());
        service.UpdateNowPlaying(MprisPlayerStateTests.Track());   // and updates are harmless
    }
}
