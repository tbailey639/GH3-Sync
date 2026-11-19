using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class DiscoveredPeerInfo
{
    public string FriendCode { get; set; } = "";
    public string Username { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}

public class PeerDiscoveryService
{
    public static PeerDiscoveryService? Instance { get; private set; }

    private readonly int _localPort;
    private readonly SyncLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private const int DiscoveryPort = 7374;
    private readonly ConcurrentDictionary<string, DiscoveredPeerInfo> _discoveredPeers = new(StringComparer.OrdinalIgnoreCase);

    public PeerDiscoveryService(int localPort, SyncLogger logger)
    {
        _localPort = localPort;
        _logger = logger;
        Instance = this;
    }

    public void Start()
    {
        Task.Run(RunListener);
        Task.Run(RunBroadcaster);
    }

    public void Stop()
    {
        _cts.Cancel();
    }

    public DiscoveredPeerInfo? LookupPeer(string friendCode)
    {
        if (string.IsNullOrWhiteSpace(friendCode)) return null;
        var cleanCode = friendCode.Trim().ToUpperInvariant();
        if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

        if (_discoveredPeers.TryGetValue(cleanCode, out var peer))
        {
            if ((DateTime.UtcNow - peer.LastSeen).TotalSeconds < 180)
            {
                return peer;
            }
        }
        return null;
    }

    public IReadOnlyList<DiscoveredPeerInfo> GetAllDiscoveredPeers()
    {
        var threshold = DateTime.UtcNow.AddSeconds(-180);
        return _discoveredPeers.Values.Where(p => p.LastSeen > threshold).ToList();
    }

    private async Task RunListener()
    {
        using var udp = new UdpClient();
        try
        {
            udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[PEER DISCOVERY] Listener bind error (port {DiscoveryPort}): {ex.Message}");
            return;
        }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var result = await udp.ReceiveAsync(_cts.Token);
                var message = Encoding.UTF8.GetString(result.Buffer);

                if (message.StartsWith("GH3_BEACON:"))
                {
                    var parts = message.Split(':');
                    if (parts.Length >= 4)
                    {
                        var rawCode = parts[1].Trim().ToUpperInvariant();
                        var cleanCode = rawCode.StartsWith("GH3-") ? rawCode : "GH3-" + rawCode;
                        var portStr = parts[2].Trim();
                        var username = parts[3].Trim();

                        if (int.TryParse(portStr, out var peerPort))
                        {
                            var addrObj = result.RemoteEndPoint.Address;
                            if (addrObj.IsIPv4MappedToIPv6)
                            {
                                addrObj = addrObj.MapToIPv4();
                            }
                            var senderIp = addrObj.ToString();
                            var autoAddress = $"http://{senderIp}:{peerPort}";

                            // Update live LAN peer cache for instant username & address auto-detection
                            _discoveredPeers[cleanCode] = new DiscoveredPeerInfo
                            {
                                FriendCode = cleanCode,
                                Username = string.IsNullOrWhiteSpace(username) ? "Player" : username,
                                Address = autoAddress,
                                LastSeen = DateTime.UtcNow
                            };

                            // If this peer is already an added friend, keep their last known address fresh
                            var friend = AppConfig.Current.Friends.FirstOrDefault(f => 
                                f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

                            if (friend != null && friend.LastAddress != autoAddress)
                            {
                                friend.LastAddress = autoAddress;
                                AppConfig.Save();
                                _logger.LogInfo($"[PEER DISCOVERY] Live LAN IP updated for Friend '{friend.Username}' ({friend.FriendCode}) -> {autoAddress}");
                            }
                        }
                    }
                }
            }
            catch when (_cts.IsCancellationRequested)
            {
                break;
            }
            catch { }
        }
    }

    private async Task RunBroadcaster()
    {
        using var udp = new UdpClient();
        try
        {
            udp.EnableBroadcast = true;
        }
        catch { }

        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var myCode = AppConfig.Current.FriendCode;
                var myUser = AppConfig.Current.Username;
                var payload = $"GH3_BEACON:{myCode}:{_localPort}:{myUser}";
                var bytes = Encoding.UTF8.GetBytes(payload);

                await udp.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, DiscoveryPort));
            }
            catch { }

            try
            {
                await Task.Delay(8000, _cts.Token);
            }
            catch when (_cts.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
