using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rambutan.Core.Diagnostics;
using Rambutan.Core.Remote;
using Rambutan.LocalApi.Overlay;

namespace Rambutan.LocalApi;

public sealed record RemoteArtwork(byte[] Bytes, string ContentType);

/// <summary>How large a cover the client draws.</summary>
public enum ArtworkSize
{
    /// <summary>The now-playing cover: the largest the host has.</summary>
    Full,

    /// <summary>A list row's small cover (<c>?size=row</c>): a small copy when the
    /// host can get one, so a long queue doesn't send hundreds of large images.</summary>
    Row,
}

public sealed record LocalApiOptions
{
    /// <summary>Where to listen: <see cref="IPAddress.Any"/> for the LAN (only
    /// while the remote is on), <see cref="IPAddress.Loopback"/> otherwise.</summary>
    public required IPAddress Address { get; init; }

    public int Port { get; init; } = LocalApiHost.DefaultPort;

    /// <summary>When <see cref="Port"/> is taken, listen on a port the system
    /// picks rather than fail. The pairing link carries the port, so a phone
    /// still finds it.</summary>
    public bool FallBackToAnyPort { get; init; }

    public required IRemotePlayer Player { get; init; }

    /// <summary>The queue and search; their routes exist only when this is set.</summary>
    public IRemoteLibrary? Library { get; init; }

    /// <summary>The token a client must present; read on every request, so a
    /// rotated token applies at once. Null refuses everyone.</summary>
    public required Func<string?> Token { get; init; }

    public Func<string, ArtworkSize, CancellationToken, Task<RemoteArtwork?>>? Artwork { get; init; }

    /// <summary>Serve the phone remote's page and API. The OBS overlay's host
    /// doesn't: it serves only the overlay, on loopback.</summary>
    public bool ServeRemote { get; init; } = true;

    /// <summary>The OBS overlay's routes, or null for a host without them.</summary>
    public OverlayOptions? Overlay { get; init; }
}

public enum LocalApiFailure
{
    /// <summary>Another program holds the port.</summary>
    PortInUse,

    /// <summary>The address isn't this machine's, or the system refused the bind.</summary>
    AddressUnavailable,

    Other,
}

public sealed record LocalApiStartResult(LocalApiHost? Host, LocalApiFailure? Failure = null, string? Detail = null)
{
    /// <summary>The configured port was taken and the host fell back to another.</summary>
    public bool PreferredPortBusy { get; init; }
}

/// <summary>
/// The in-process HTTP server for clients on other devices. Started on demand,
/// never at launch unless a feature that needs it is on; it serves the remote's
/// page to anyone who reaches it, and the player's state and commands only to a
/// client that presents the pairing token.
/// </summary>
public sealed class LocalApiHost : IAsyncDisposable
{
    /// <summary>Fixed, so a phone's bookmark keeps working across restarts.</summary>
    public const int DefaultPort = 41741;

    private readonly WebApplication _app;
    private readonly RemoteEndpoints _remote;
    private readonly OverlayEndpoints? _overlay;
    private int _disposed;

    private LocalApiHost(WebApplication app, RemoteEndpoints remote, OverlayEndpoints? overlay, IPAddress address, int port)
    {
        _app = app;
        _remote = remote;
        _overlay = overlay;
        Address = address;
        Port = port;
    }

    public IPAddress Address { get; }
    public int Port { get; }

    /// <summary>Clients holding a live-updates connection now.</summary>
    public int ConnectedClients => _remote.ConnectedClients;

    /// <summary>Overlay pages holding a live-updates connection now.</summary>
    public int OverlayClients => _overlay?.ConnectedClients ?? 0;

    /// <summary>Raised on a thread-pool thread when <see cref="ConnectedClients"/>
    /// or <see cref="OverlayClients"/> changes.</summary>
    public event Action? ClientsChanged
    {
        add
        {
            _remote.ClientsChanged += value;
            _overlay?.ClientsChanged += value;
        }
        remove
        {
            _remote.ClientsChanged -= value;
            _overlay?.ClientsChanged -= value;
        }
    }

    /// <summary>Ends every live-updates connection; a client must authenticate
    /// again to reconnect. Called after the token changes.</summary>
    public void DisconnectClients()
    {
        _remote.DisconnectAll();
        _overlay?.DisconnectAll();
    }

    public static async Task<LocalApiStartResult> StartAsync(LocalApiOptions options, CancellationToken ct = default)
    {
        try
        {
            return new(await StartOnAsync(options, options.Port, ct));
        }
        catch (Exception ex) when (Classify(ex) == LocalApiFailure.PortInUse && options.FallBackToAnyPort && options.Port != 0)
        {
            AppLog.Write("localapi", $"port {options.Port} is taken; listening on another");
            try
            {
                return new(await StartOnAsync(options, 0, ct)) { PreferredPortBusy = true };
            }
            catch (Exception inner) when (inner is not OperationCanceledException)
            {
                return Failed(inner);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(ex);
        }

        static LocalApiStartResult Failed(Exception ex)
        {
            AppLog.Write("localapi", $"couldn't start: {ex.Message}");
            return new(null, Classify(ex), ex.Message);
        }
    }

    private static async Task<LocalApiHost> StartOnAsync(LocalApiOptions options, int port, CancellationToken ct)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            Args = [],
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        // Nothing from the environment or an appsettings file may add endpoints
        // (ASPNETCORE_URLS, Kestrel__Endpoints__…): the address is ours alone.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();
        builder.WebHost.UseSetting(WebHostDefaults.PreferHostingUrlsKey, "false");
        builder.Logging.ClearProviders();
        // Inside a desktop app: no console lifetime hooking Ctrl+C or process exit.
        builder.Services.AddSingleton<IHostLifetime, EmbeddedLifetime>();
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxConcurrentConnections = 32;
            kestrel.Limits.MaxRequestBodySize = 4 * 1024;
            kestrel.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            kestrel.Listen(options.Address, port);
        });

        var app = builder.Build();
        var remote = new RemoteEndpoints(options);
        if (options.ServeRemote)
            remote.Map(app);
        else
            app.Use(RemoteEndpoints.SecurityHeaders);
        var overlay = options.Overlay is { } overlayOptions
            ? new OverlayEndpoints(options.Player, overlayOptions, options.Artwork)
            : null;
        overlay?.Map(app, ownsRoot: !options.ServeRemote);

        try
        {
            await app.StartAsync(ct);
        }
        catch
        {
            await app.DisposeAsync();
            remote.Dispose();
            overlay?.Dispose();
            throw;
        }

        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => new Uri(a).Port)
            .FirstOrDefault() ?? port;
        AppLog.Write("localapi", $"listening on {options.Address}:{bound}");
        return new LocalApiHost(app, remote, overlay, options.Address, bound);
    }

    internal static LocalApiFailure Classify(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AddressInUseException || e is SocketException { SocketErrorCode: SocketError.AddressAlreadyInUse })
                return LocalApiFailure.PortInUse;
            if (e is SocketException { SocketErrorCode: SocketError.AddressNotAvailable or SocketError.AccessDenied })
                return LocalApiFailure.AddressUnavailable;
        }
        return LocalApiFailure.Other;
    }

    /// <summary>Stops listening, giving open requests a moment to finish.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _remote.DisconnectAll();
        _overlay?.DisconnectAll();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            await _app.StopAsync(timeout.Token);
        }
        catch (Exception ex)
        {
            AppLog.Write("localapi", $"stop: {ex.Message}");
        }
        await _app.DisposeAsync();
        _remote.Dispose();
        _overlay?.Dispose();
        AppLog.Write("localapi", "stopped");
    }

    private sealed class EmbeddedLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
