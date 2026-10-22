using System.Text;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Models;
using Rambutan.Core.Queue;
using Tmds.DBus.Protocol;

namespace Rambutan.Integrations.Linux;

/// <summary>
/// MPRIS: the Linux media session, so desktop media keys, panel widgets and
/// tools such as playerctl can see and drive playback. Serves
/// <c>/org/mpris/MediaPlayer2</c> on the session bus with Tmds.DBus.Protocol
/// (the library Avalonia already ships for its own Linux integration).
/// <see cref="MprisPlayerState"/> holds the rules; this class translates.
/// <para>
/// Without a session bus it logs why and does nothing, like the null session.
/// Position is answered when asked and never sent in PropertiesChanged; a jump
/// is announced with the Seeked signal, as the spec asks.
/// </para>
/// </summary>
public sealed class MprisMediaSessionService : IMediaSessionService, IPathMethodHandler
{
    public const string BusName = "org.mpris.MediaPlayer2.rambutan";
    private const string ObjectPathName = "/org/mpris/MediaPlayer2";
    private const string RootInterface = "org.mpris.MediaPlayer2";
    private const string PlayerInterface = "org.mpris.MediaPlayer2.Player";
    private const string PropertiesInterface = "org.freedesktop.DBus.Properties";

    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private static readonly string[] RootProperties =
        ["CanQuit", "CanRaise", "HasTrackList", "Identity", "SupportedUriSchemes", "SupportedMimeTypes"];

    private static readonly string[] PlayerProperties =
    [
        "PlaybackStatus", "Rate", "Metadata", "Position", "MinimumRate", "MaximumRate",
        "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek", "CanControl", "Volume",
        "Shuffle", "LoopStatus",
    ];

    /// <summary>What changes with the track: everything but the constants and Position.</summary>
    private static readonly string[] TrackProperties =
        ["PlaybackStatus", "Metadata", "CanGoNext", "CanGoPrevious", "CanPlay", "CanPause", "CanSeek"];

    private readonly string _identity;
    private readonly string? _address;
    private readonly MprisPlayerState _state;
    private readonly Lock _sync = new();
    private DBusConnection? _connection;
    private bool _disposed;

    /// <param name="identity">The player's name, as panels show it.</param>
    /// <param name="address">The bus to use; the session bus when null (tests pass a private one).</param>
    /// <param name="localFile">A local track's file and cover paths, by id
    /// (<see cref="MprisPlayerState"/>).</param>
    public MprisMediaSessionService(
        string identity, string? address = null, TimeProvider? clock = null,
        Func<string, (string Path, string? Cover)?>? localFile = null)
    {
        _identity = identity;
        _address = address;
        _state = new MprisPlayerState(clock ?? TimeProvider.System, localFile);
    }

    public event Action? PlayPauseRequested;
    public event Action? PlayRequested;
    public event Action? PauseRequested;
    public event Action? StopRequested;
    public event Action? NextRequested;
    public event Action? PreviousRequested;
    public event Action<TimeSpan>? SeekRequested;
    public event Action? RaiseRequested;
    public event Action? QuitRequested;
    public event Action<double>? VolumeRequested;
    public event Action<bool>? ShuffleRequested;
    public event Action<RepeatMode>? RepeatRequested;

    /// <summary>The bus name this player got, once connected; null before, or without a bus.</summary>
    public string? OwnedName { get; private set; }

    string IPathMethodHandler.Path => ObjectPathName;

    bool IPathMethodHandler.HandlesChildPaths => false;

    public void Initialize(nint windowHandle) =>
        // On the thread pool: the connection's message loop must not be tied to
        // the UI thread's synchronization context (see SecretServiceStore).
        _ = Task.Run(ConnectAsync);

    /// <summary>
    /// Connects, serves the player object and takes a bus name: the plain one,
    /// or, while another instance (another data folder) holds it, one with
    /// this process's id, as the spec allows. Returns the name, or null when
    /// there is no bus to serve on.
    /// </summary>
    public async Task<string?> ConnectAsync()
    {
        if ((_address ?? DBusAddress.Session) is not { Length: > 0 } address)
        {
            AppLog.Write("mpris", "No D-Bus session bus; no media session.");
            return null;
        }

        var connection = new DBusConnection(address);
        try
        {
            await connection.ConnectAsync().AsTask().WaitAsync(CallTimeout).ConfigureAwait(false);
            connection.AddMethodHandler(this);
            var name = BusName;
            if (!await connection.TryRequestNameAsync(name, RequestNameOptions.None).WaitAsync(CallTimeout).ConfigureAwait(false))
            {
                name = $"{BusName}.instance{Environment.ProcessId}";
                await connection.RequestNameAsync(name, RequestNameOptions.None).WaitAsync(CallTimeout).ConfigureAwait(false);
            }

            lock (_sync)
            {
                if (_disposed)
                {
                    connection.Dispose();
                    return null;
                }
                _connection = connection;
                OwnedName = name;
            }
            AppLog.Write("mpris", $"Media session on the session bus as {name}.");
            return name;
        }
        catch (Exception e)
        {
            AppLog.Write("mpris", $"No media session: {e.Message}");
            connection.Dispose();
            return null;
        }
    }

    public void UpdateNowPlaying(TrackInfo? track)
    {
        lock (_sync)
        {
            if (!_state.SetTrack(track))
                return;
        }
        EmitChanged(TrackProperties);
    }

    public void UpdatePlaybackState(SessionPlaybackStatus status)
    {
        lock (_sync)
        {
            if (!_state.SetStatus(status))
                return;
        }
        EmitChanged(["PlaybackStatus"]);
    }

    public void UpdateVolume(double volume)
    {
        lock (_sync)
        {
            if (!_state.SetVolume(volume))
                return;
        }
        EmitChanged(["Volume"]);
    }

    public void UpdateShuffle(bool shuffle)
    {
        lock (_sync)
        {
            if (!_state.SetShuffle(shuffle))
                return;
        }
        EmitChanged(["Shuffle"]);
    }

    public void UpdateRepeat(RepeatMode repeat)
    {
        lock (_sync)
        {
            if (!_state.SetRepeat(repeat))
                return;
        }
        EmitChanged(["LoopStatus"]);
    }

    public void UpdateTimeline(TimeSpan position, TimeSpan? duration)
    {
        bool seeked, durationChanged;
        lock (_sync)
            _state.SetTimeline(position, duration, out seeked, out durationChanged);
        if (durationChanged)
            EmitChanged(["Metadata", "CanSeek"]);
        if (seeked)
            EmitSeeked(position);
    }

    public ValueTask HandleMethodAsync(MethodContext context)
    {
        var request = context.Request;
        if (context.IsDBusIntrospectRequest)
        {
            context.ReplyIntrospectXml([IntrospectionXml], ReadOnlySpan<string>.Empty);
            return default;
        }

        var member = request.MemberAsString ?? "";
        switch (request.InterfaceAsString)
        {
            case PropertiesInterface:
                HandleProperties(context, member);
                break;
            case RootInterface:
                Answer(context, member switch
                {
                    "Raise" => RaiseRequested,
                    "Quit" => QuitRequested,
                    _ => null,
                });
                break;
            case PlayerInterface:
                HandlePlayer(context, member);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
        return default;
    }

    private void HandlePlayer(MethodContext context, string member)
    {
        switch (member)
        {
            case "Seek":
            {
                var offset = MprisPlayerState.FromMicroseconds(context.Request.GetBodyReader().ReadInt64());
                TimeSpan? target, duration;
                lock (_sync)
                {
                    target = _state.CanSeek ? _state.Position + offset : null;
                    duration = _state.Duration;
                }
                // The spec: before the start is the start; past the end is the next track.
                if (target is { } t && duration is { } d)
                {
                    if (t >= d)
                        NextRequested?.Invoke();
                    else
                        SeekRequested?.Invoke(t < TimeSpan.Zero ? TimeSpan.Zero : t);
                }
                Reply(context);
                break;
            }
            case "SetPosition":
            {
                var reader = context.Request.GetBodyReader();
                var trackId = reader.ReadObjectPathAsString();
                var position = MprisPlayerState.FromMicroseconds(reader.ReadInt64());
                bool valid;
                lock (_sync)
                {
                    // A stale track id, or a position outside the track, is ignored.
                    valid = _state.CanSeek && trackId == _state.TrackId
                        && position >= TimeSpan.Zero && position <= _state.Duration;
                }
                if (valid)
                    SeekRequested?.Invoke(position);
                Reply(context);
                break;
            }
            case "OpenUri":
                context.ReplyError("org.freedesktop.DBus.Error.NotSupported", "Rambutan opens no URIs.");
                break;
            default:
                Answer(context, member switch
                {
                    "PlayPause" => PlayPauseRequested,
                    "Play" => PlayRequested,
                    "Pause" => PauseRequested,
                    "Stop" => StopRequested,
                    "Next" => NextRequested,
                    "Previous" => PreviousRequested,
                    _ => null,
                });
                break;
        }
    }

    /// <summary>
    /// A method with no arguments: raise its event, then reply. The handlers
    /// only queue work for the UI thread, so the reply isn't held up, and a
    /// caller that has its reply knows the request was taken.
    /// </summary>
    private static void Answer(MethodContext context, Action? requested)
    {
        if (!IsKnown(context))
        {
            context.ReplyUnknownMethodError();
            return;
        }
        requested?.Invoke();
        Reply(context);
    }

    private static bool IsKnown(MethodContext context) => context.Request.MemberAsString switch
    {
        "Raise" or "Quit" => context.Request.InterfaceAsString == RootInterface,
        "PlayPause" or "Play" or "Pause" or "Stop" or "Next" or "Previous" =>
            context.Request.InterfaceAsString == PlayerInterface,
        _ => false,
    };

    private void HandleProperties(MethodContext context, string member)
    {
        var reader = context.Request.GetBodyReader();
        var @interface = reader.ReadString();
        switch (member)
        {
            case "Get":
            {
                var name = reader.ReadString();
                VariantValue? value;
                lock (_sync)
                    value = PropertyValue(@interface, name);
                if (value is not { } v)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.UnknownProperty", $"No property {@interface}.{name}.");
                    return;
                }
                var writer = context.CreateReplyWriter("v");
                try
                {
                    writer.WriteVariant(v);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
                break;
            }
            case "GetAll":
            {
                Dictionary<string, VariantValue> values;
                lock (_sync)
                    values = PropertyValues(@interface, @interface switch
                    {
                        RootInterface => RootProperties,
                        PlayerInterface => PlayerProperties,
                        _ => [],
                    });
                var writer = context.CreateReplyWriter("a{sv}");
                try
                {
                    writer.WriteDictionary(values);
                    context.Reply(writer.CreateMessage());
                }
                finally
                {
                    writer.Dispose();
                }
                break;
            }
            case "Set":
                HandleSet(context, @interface, ref reader);
                break;
            default:
                context.ReplyUnknownMethodError();
                break;
        }
    }

    /// <summary>
    /// Properties.Set. Volume, Shuffle and LoopStatus are writable; the rate
    /// is not offered. A set only asks the app: what is reported afterwards is
    /// what the app then did, pushed back through the Update methods.
    /// </summary>
    private void HandleSet(MethodContext context, string @interface, ref Reader reader)
    {
        var name = reader.ReadString();
        if (@interface != PlayerInterface || name is not ("Volume" or "Shuffle" or "LoopStatus"))
        {
            context.ReplyError("org.freedesktop.DBus.Error.PropertyReadOnly", $"{@interface}.{name} is read-only.");
            return;
        }

        var value = reader.ReadVariantValue();
        switch (name)
        {
            case "Volume":
                if (value.Type != VariantValueType.Double || double.IsNaN(value.GetDouble()))
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Volume is a number (d).");
                    return;
                }
                // The spec: below 0 is 0. The app keeps it within 0 to 1 and reports what it set.
                VolumeRequested?.Invoke(Math.Clamp(value.GetDouble(), 0.0, 1.0));
                break;
            case "Shuffle":
                if (value.Type != VariantValueType.Bool)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "Shuffle is a boolean (b).");
                    return;
                }
                ShuffleRequested?.Invoke(value.GetBool());
                break;
            default:
                if (value.Type != VariantValueType.String || MprisPlayerState.FromLoopStatus(value.GetString()) is not { } repeat)
                {
                    context.ReplyError("org.freedesktop.DBus.Error.InvalidArgs", "LoopStatus is None, Track or Playlist (s).");
                    return;
                }
                RepeatRequested?.Invoke(repeat);
                break;
        }
        Reply(context);
    }

    private Dictionary<string, VariantValue> PropertyValues(string @interface, IEnumerable<string> names)
    {
        var values = new Dictionary<string, VariantValue>();
        foreach (var name in names)
        {
            if (PropertyValue(@interface, name) is { } value)
                values[name] = value;
        }
        return values;
    }

    /// <summary>A property's value now, or null when there is no such property. Call under the lock.</summary>
    private VariantValue? PropertyValue(string @interface, string name) => @interface switch
    {
        RootInterface => name switch
        {
            "CanQuit" or "CanRaise" => (VariantValue?)VariantValue.Bool(true),
            "HasTrackList" => VariantValue.Bool(false),
            "Identity" => VariantValue.String(_identity),
            "SupportedUriSchemes" or "SupportedMimeTypes" => VariantValue.Array(Array.Empty<string>()),
            _ => null,
        },
        PlayerInterface => name switch
        {
            "PlaybackStatus" => (VariantValue?)VariantValue.String(_state.PlaybackStatus),
            "Rate" or "MinimumRate" or "MaximumRate" => VariantValue.Double(1.0),
            "Metadata" => MetadataValue(),
            "Position" => VariantValue.Int64(MprisPlayerState.ToMicroseconds(_state.Position)),
            "CanGoNext" or "CanGoPrevious" or "CanPlay" or "CanPause" => VariantValue.Bool(_state.HasTrack),
            "CanSeek" => VariantValue.Bool(_state.CanSeek),
            "CanControl" => VariantValue.Bool(true),
            "Volume" => VariantValue.Double(_state.Volume),
            "Shuffle" => VariantValue.Bool(_state.Shuffle),
            "LoopStatus" => VariantValue.String(_state.LoopStatus),
            _ => null,
        },
        _ => null,
    };

    private VariantValue MetadataValue()
    {
        var metadata = new Dict<string, VariantValue>();
        foreach (var entry in _state.Metadata())
        {
            var value = entry.Value;
            metadata.Add(entry.Key, value.Kind switch
            {
                'o' => VariantValue.ObjectPath(new ObjectPath(value.Text!)),
                'a' => VariantValue.Array(value.List!.ToArray()),
                'x' => VariantValue.Int64(value.Number),
                _ => VariantValue.String(value.Text!),
            });
        }
        return metadata;
    }

    private static void Reply(MethodContext context)
    {
        if (context.NoReplyExpected)
            return;
        var writer = context.CreateReplyWriter(null!); // null: no body
        try
        {
            context.Reply(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void EmitChanged(IReadOnlyList<string> names)
    {
        DBusConnection? connection;
        Dictionary<string, VariantValue> values;
        lock (_sync)
        {
            connection = _connection;
            if (connection is null)
                return;
            values = PropertyValues(PlayerInterface, names);
        }

        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null!, /* to everyone */ ObjectPathName, PropertiesInterface, "PropertiesChanged", "sa{sv}as");
            writer.WriteString(PlayerInterface);
            writer.WriteDictionary(values);
            writer.WriteArray(Array.Empty<string>());
            connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    private void EmitSeeked(TimeSpan position)
    {
        DBusConnection? connection;
        lock (_sync)
            connection = _connection;
        if (connection is null)
            return;

        var writer = connection.GetMessageWriter();
        try
        {
            writer.WriteSignalHeader(null!, /* to everyone */ ObjectPathName, PlayerInterface, "Seeked", "x");
            writer.WriteInt64(MprisPlayerState.ToMicroseconds(position));
            connection.TrySendMessage(writer.CreateMessage());
        }
        finally
        {
            writer.Dispose();
        }
    }

    public void Dispose()
    {
        DBusConnection? connection;
        lock (_sync)
        {
            _disposed = true;
            connection = _connection;
            _connection = null;
            OwnedName = null;
        }
        // Closing the connection releases the bus name, so panels drop the player.
        connection?.Dispose();
    }

    private static readonly ReadOnlyMemory<byte> IntrospectionXml = """
        <interface name="org.mpris.MediaPlayer2">
          <method name="Raise"/>
          <method name="Quit"/>
          <property name="CanQuit" type="b" access="read"/>
          <property name="CanRaise" type="b" access="read"/>
          <property name="HasTrackList" type="b" access="read"/>
          <property name="Identity" type="s" access="read"/>
          <property name="SupportedUriSchemes" type="as" access="read"/>
          <property name="SupportedMimeTypes" type="as" access="read"/>
        </interface>
        <interface name="org.mpris.MediaPlayer2.Player">
          <method name="Next"/>
          <method name="Previous"/>
          <method name="Pause"/>
          <method name="PlayPause"/>
          <method name="Stop"/>
          <method name="Play"/>
          <method name="Seek"><arg direction="in" name="Offset" type="x"/></method>
          <method name="SetPosition"><arg direction="in" name="TrackId" type="o"/><arg direction="in" name="Position" type="x"/></method>
          <method name="OpenUri"><arg direction="in" name="Uri" type="s"/></method>
          <signal name="Seeked"><arg name="Position" type="x"/></signal>
          <property name="PlaybackStatus" type="s" access="read"/>
          <property name="Rate" type="d" access="read"/>
          <property name="Metadata" type="a{sv}" access="read"/>
          <property name="Position" type="x" access="read">
            <annotation name="org.freedesktop.DBus.Property.EmitsChangedSignal" value="false"/>
          </property>
          <property name="MinimumRate" type="d" access="read"/>
          <property name="MaximumRate" type="d" access="read"/>
          <property name="CanGoNext" type="b" access="read"/>
          <property name="CanGoPrevious" type="b" access="read"/>
          <property name="CanPlay" type="b" access="read"/>
          <property name="CanPause" type="b" access="read"/>
          <property name="CanSeek" type="b" access="read"/>
          <property name="CanControl" type="b" access="read"/>
          <property name="Volume" type="d" access="readwrite"/>
          <property name="Shuffle" type="b" access="readwrite"/>
          <property name="LoopStatus" type="s" access="readwrite"/>
        </interface>

        """u8.ToArray();
}
