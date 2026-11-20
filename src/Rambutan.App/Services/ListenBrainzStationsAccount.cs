using System;
using System.Threading;
using System.Threading.Tasks;
using Rambutan.Core.Diagnostics;
using Rambutan.Integrations.ListenBrainz;

namespace Rambutan.App.Services;

/// <summary>What went wrong with the stations' token, for the Settings page to word.</summary>
public enum ListenBrainzStationsNotice
{
    TokenInvalid,
    Unreachable,
    RateLimited,
    Refused,

    /// <summary>A station request was refused with the saved token.</summary>
    TokenRefused,
}

/// <summary>
/// The ListenBrainz account the stations use, apart from the scrobbling
/// connection: LB Radio is only on ListenBrainz itself, while the listens may go
/// to another server. Its token goes only to <see cref="ListenBrainzClient.DefaultServer"/>,
/// and connecting it is what turns the stations on (an outbound integration,
/// off until the user enters a token).
///
/// The vault's calls block on the keyring, so they run off the caller's thread.
/// <see cref="Changed"/> is raised on any thread.
/// </summary>
public sealed class ListenBrainzStationsAccount(IListenBrainzTokenStore vault, Func<ListenBrainzClient> client)
{
    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private volatile ListenBrainzConnection? _connection;
    private volatile bool _busy;
    private readonly object _noticeSync = new();
    private ListenBrainzStationsNotice? _notice;

    public event Action? Changed;

    public bool Connected => _connection is not null;

    public string? Username => _connection?.Username;

    /// <summary>The token the station client sends; null while not connected.</summary>
    public string? Token => _connection?.Token;

    /// <summary>A token check is running.</summary>
    public bool Busy => _busy;

    public ListenBrainzStationsNotice? Notice
    {
        get
        {
            lock (_noticeSync)
                return _notice;
        }
    }

    /// <summary>Reads the saved token. A token saved for another server is ignored.</summary>
    public async Task LoadAsync()
    {
        await _oneAtATime.WaitAsync();
        try
        {
            var saved = await Task.Run(vault.Load);
            _connection = saved is not null && saved.Server == ListenBrainzClient.DefaultServer ? saved : null;
        }
        finally
        {
            _oneAtATime.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>Checks the token with ListenBrainz and keeps it when it is valid;
    /// otherwise <see cref="Notice"/> says why not and nothing is kept.</summary>
    public async Task ConnectAsync(string token, CancellationToken ct = default)
    {
        token = token.Trim();
        await _oneAtATime.WaitAsync(ct);
        try
        {
            _busy = true;
            Changed?.Invoke();
            if (token.Length == 0)
            {
                SetNotice(ListenBrainzStationsNotice.TokenInvalid);
                return;
            }

            var result = await client().ValidateTokenAsync(token, ct);
            if (!result.IsOk)
            {
                AppLog.Write("listenbrainz", $"stations validate-token: {result}");
                SetNotice(result.Outcome switch
                {
                    ListenBrainzOutcome.InvalidToken => ListenBrainzStationsNotice.TokenInvalid,
                    ListenBrainzOutcome.RateLimited => ListenBrainzStationsNotice.RateLimited,
                    ListenBrainzOutcome.RetryLater => ListenBrainzStationsNotice.Unreachable,
                    _ => ListenBrainzStationsNotice.Refused,
                });
                return;
            }

            var connection = new ListenBrainzConnection(ListenBrainzClient.DefaultServer, result.Value!, token);
            await Task.Run(() => vault.Save(connection), CancellationToken.None);
            _connection = connection;
            SetNotice(null);
            AppLog.Write("listenbrainz", "Stations connected.");
        }
        finally
        {
            _busy = false;
            _oneAtATime.Release();
            Changed?.Invoke();
        }
    }

    /// <summary>Forgets the token, here and in the vault.</summary>
    public async Task DisconnectAsync()
    {
        await _oneAtATime.WaitAsync();
        try
        {
            _connection = null;
            SetNotice(null);
            await Task.Run(vault.Clear);
        }
        finally
        {
            _oneAtATime.Release();
        }
        Changed?.Invoke();
    }

    /// <summary>A station request was refused with the saved token. It stays
    /// saved (the refusal may be passing); Settings asks for a new one.</summary>
    public void TokenRefused()
    {
        if (_connection is null || Notice == ListenBrainzStationsNotice.TokenRefused)
            return;
        SetNotice(ListenBrainzStationsNotice.TokenRefused);
        Changed?.Invoke();
    }

    private void SetNotice(ListenBrainzStationsNotice? notice)
    {
        lock (_noticeSync)
            _notice = notice;
    }
}
