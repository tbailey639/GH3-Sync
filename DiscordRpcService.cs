using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

/// <summary>
/// Lightweight, zero-dependency Discord Rich Presence client over local Discord IPC named pipes
/// (\\.\pipe\discord-ipc-0..9). Fails silently when Discord is not running and respects user settings.
/// </summary>
public sealed class DiscordRpcService : IDisposable
{
    // Discord Application Client ID for GH3-Sync / Guitar Hero III presence
    private const string ClientId = "1289451982047125544";

    private readonly SyncLogger _logger;
    private readonly Func<int> _getCustomCount;
    private readonly Func<(int LobbyCount, int LobbyCap)> _getLobbyStats;
    private readonly object _pipeLock = new();
    private NamedPipeClientStream? _pipe;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private long _sessionStartEpoch;
    private string _overrideDetails = "";
    private string _overrideState = "";
    private DateTime _overrideUntilUtc = DateTime.MinValue;
    private bool _wasEnabled;

    public bool IsConnected
    {
        get { lock (_pipeLock) return _pipe != null && _pipe.IsConnected; }
    }

    public DiscordRpcService(
        SyncLogger logger,
        Func<int> getCustomCount,
        Func<(int LobbyCount, int LobbyCap)> getLobbyStats)
    {
        _logger = logger;
        _getCustomCount = getCustomCount;
        _getLobbyStats = getLobbyStats;
        _sessionStartEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public void Start()
    {
        _cts = new CancellationTokenSource();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token));
    }

    public void SetTemporaryActivity(string details, string state, int durationSeconds = 45)
    {
        _overrideDetails = details;
        _overrideState = state;
        _overrideUntilUtc = DateTime.UtcNow.AddSeconds(durationSeconds);
        _ = Task.Run(PushCurrentPresenceSafe);
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                bool enabled = AppConfig.Current.EnableDiscordRichPresence;
                if (!enabled)
                {
                    if (_wasEnabled)
                    {
                        ClearPresenceAndDisconnect();
                        _wasEnabled = false;
                    }
                }
                else
                {
                    _wasEnabled = true;
                    PushCurrentPresenceSafe();
                }
            }
            catch { }

            try { await Task.Delay(TimeSpan.FromSeconds(15), ct); }
            catch (TaskCanceledException) { break; }
        }
    }

    private void PushCurrentPresenceSafe()
    {
        if (!AppConfig.Current.EnableDiscordRichPresence) return;

        lock (_pipeLock)
        {
            if (_pipe == null || !_pipe.IsConnected)
            {
                if (!TryConnectLocked()) return;
            }

            try
            {
                string details;
                string state;
                int customs = 0;
                int lobbyCount = 0;
                int lobbyCap = 10;
                try { customs = _getCustomCount(); } catch { }
                try { (lobbyCount, lobbyCap) = _getLobbyStats(); } catch { }

                bool gh3Running = Process.GetProcessesByName("GH3").Length > 0;
                if (DateTime.UtcNow < _overrideUntilUtc && !string.IsNullOrWhiteSpace(_overrideDetails))
                {
                    details = _overrideDetails;
                    state = _overrideState;
                }
                else if (gh3Running)
                {
                    details = "Playing Guitar Hero III (BetterGH3)";
                    state = AppConfig.Current.P2pOnlineEnabled
                        ? $"GH3-Sync Active • {customs} Customs ({lobbyCount}/{lobbyCap} Lobby)"
                        : $"Offline Mode • {customs} Customs Loaded";
                }
                else
                {
                    details = $"GH3-Sync v{UpdateService.CurrentAppVersion} — Managing Customs";
                    state = AppConfig.Current.P2pOnlineEnabled
                        ? $"{customs} Customs • Lobby Ready ({lobbyCount}/{lobbyCap})"
                        : $"{customs} Customs Installed (Offline Mode)";
                }

                var payload = new
                {
                    cmd = "SET_ACTIVITY",
                    args = new
                    {
                        pid = Environment.ProcessId,
                        activity = new
                        {
                            details,
                            state,
                            timestamps = new { start = _sessionStartEpoch }
                        }
                    },
                    nonce = Guid.NewGuid().ToString("N")
                };

                SendFrameLocked(1, JsonSerializer.Serialize(payload));
                DrainAvailableResponseLocked();
            }
            catch
            {
                DisconnectLocked();
            }
        }
    }

    private bool TryConnectLocked()
    {
        DisconnectLocked();
        for (int i = 0; i < 10; i++)
        {
            NamedPipeClientStream? candidate = null;
            try
            {
                candidate = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.None);
                candidate.Connect(150);
                _pipe = candidate;

                var handshake = JsonSerializer.Serialize(new { v = 1, client_id = ClientId });
                SendFrameLocked(0, handshake);
                DrainAvailableResponseLocked();
                _logger.LogDebug($"[DISCORD RPC] Connected via discord-ipc-{i}.");
                return true;
            }
            catch
            {
                try { candidate?.Dispose(); } catch { }
                _pipe = null;
            }
        }
        return false;
    }

    private void SendFrameLocked(int opcode, string json)
    {
        if (_pipe == null || !_pipe.IsConnected) return;
        var data = Encoding.UTF8.GetBytes(json);
        var header = new byte[8];
        BitConverter.GetBytes(opcode).CopyTo(header, 0);
        BitConverter.GetBytes(data.Length).CopyTo(header, 4);
        _pipe.Write(header, 0, header.Length);
        _pipe.Write(data, 0, data.Length);
        _pipe.Flush();
    }

    private void DrainAvailableResponseLocked()
    {
        try
        {
            if (_pipe == null || !_pipe.IsConnected) return;
            // Best-effort non-blocking check: only read if bytes are already ready
            var header = new byte[8];
            var readTask = _pipe.ReadAsync(header, 0, 8);
            if (!readTask.Wait(200) || readTask.Result < 8) return;
            int len = BitConverter.ToInt32(header, 4);
            if (len > 0 && len < 65536)
            {
                var buf = new byte[len];
                _ = _pipe.ReadAsync(buf, 0, len).Wait(200);
            }
        }
        catch { }
    }

    private void ClearPresenceAndDisconnect()
    {
        lock (_pipeLock)
        {
            try
            {
                if (_pipe != null && _pipe.IsConnected)
                {
                    var clear = JsonSerializer.Serialize(new
                    {
                        cmd = "SET_ACTIVITY",
                        args = new { pid = Environment.ProcessId, activity = (object?)null },
                        nonce = Guid.NewGuid().ToString("N")
                    });
                    SendFrameLocked(1, clear);
                }
            }
            catch { }
            DisconnectLocked();
        }
    }

    private void DisconnectLocked()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        ClearPresenceAndDisconnect();
        try { _cts?.Dispose(); } catch { }
    }
}
