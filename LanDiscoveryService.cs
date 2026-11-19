using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

/// <summary>
/// Offline Local LAN UDP Auto-Discovery:
/// Discovers other GH3-Sync players on the local subnet (Wi-Fi/Ethernet) without needing internet,
/// Cloudflare tunnels, or signaling relays. Perfect for LAN parties and offline play.
/// </summary>
public class LanDiscoveryService : IDisposable
{
    private const int DiscoveryPort = 7373;
    private const string BeaconPrefix = "GH3_SYNC_BEACON_V1";
    private readonly SyncLogger? _logger;
    private readonly int _httpPort;
    private UdpClient? _udpClient;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, LanPeer> _discoveredPeers = new();

    public record LanPeer(string FriendCode, string Username, string IpAddress, int Port, DateTime LastSeen);

    public event Action<LanPeer>? PeerDiscovered;

    public IReadOnlyCollection<LanPeer> ActivePeers => _discoveredPeers.Values.ToList();

    public LanDiscoveryService(int httpPort, SyncLogger? logger = null)
    {
        _httpPort = httpPort;
        _logger = logger;
    }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();

        try
        {
            _udpClient = new UdpClient();
            _udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            _udpClient.EnableBroadcast = true;
            _udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            // Start listening loop
            _ = Task.Run(() => ListenLoopAsync(_cts.Token));

            // Start beacon broadcast loop (every 4 seconds)
            _ = Task.Run(() => BroadcastLoopAsync(_cts.Token));

            _logger?.LogInfo($"[LAN DISCOVERY] Offline LAN auto-discovery active on UDP port {DiscoveryPort}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[LAN DISCOVERY] Could not bind UDP discovery socket: {ex.Message}");
        }
    }

    private async Task ListenLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var result = await _udpClient.ReceiveAsync(token);
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (message.StartsWith(BeaconPrefix))
                {
                    // Format: GH3_SYNC_BEACON_V1|friendCode|username|port
                    var parts = message.Split('|');
                    if (parts.Length >= 4)
                    {
                        var friendCode = parts[1];
                        var username = parts[2];
                        if (int.TryParse(parts[3], out var peerPort) && friendCode != AppConfig.Current.FriendCode)
                        {
                            var peer = new LanPeer(friendCode, username, result.RemoteEndPoint.Address.ToString(), peerPort, DateTime.UtcNow);
                            bool isNew = !_discoveredPeers.ContainsKey(friendCode);
                            _discoveredPeers[friendCode] = peer;

                            if (isNew)
                            {
                                _logger?.LogInfo($"[LAN DISCOVERY] Found local LAN player: {username} ({friendCode}) at {peer.IpAddress}:{peerPort}");
                                PeerDiscovered?.Invoke(peer);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception)
            {
                if (token.IsCancellationRequested) break;
                await Task.Delay(1000, token);
            }
        }
    }

    private async Task BroadcastLoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested && _udpClient != null)
        {
            try
            {
                var payload = $"{BeaconPrefix}|{AppConfig.Current.FriendCode}|{AppConfig.Current.Username}|{_httpPort}";
                var bytes = Encoding.UTF8.GetBytes(payload);
                var endpoint = new IPEndPoint(IPAddress.Broadcast, DiscoveryPort);
                await _udpClient.SendAsync(bytes, bytes.Length, endpoint);
            }
            catch { }

            try
            {
                await Task.Delay(4000, token);
            }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _udpClient?.Dispose();
        _udpClient = null;
    }
}
