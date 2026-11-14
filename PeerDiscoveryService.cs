using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class PeerDiscoveryService
{
    private readonly int _localPort;
    private readonly SyncLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private const int DiscoveryPort = 7374;

    public PeerDiscoveryService(int localPort, SyncLogger logger)
    {
        _localPort = localPort;
        _logger = logger;
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
                        var friendCode = parts[1];
                        var portStr = parts[2];
                        var username = parts[3];

                        if (int.TryParse(portStr, out var peerPort))
                        {
                            var addrObj = result.RemoteEndPoint.Address;
                            if (addrObj.IsIPv4MappedToIPv6)
                            {
                                addrObj = addrObj.MapToIPv4();
                            }
                            var senderIp = addrObj.ToString();

                            // SECURITY CHECK: Only auto-share IP if this peer is marked as TRUSTED!
                            var friend = AppConfig.Current.Friends.FirstOrDefault(f => 
                                f.FriendCode.Equals(friendCode, StringComparison.OrdinalIgnoreCase));

                            if (friend != null && friend.IsTrusted)
                            {
                                var autoAddress = $"http://{senderIp}:{peerPort}";
                                if (friend.LastAddress != autoAddress)
                                {
                                    friend.LastAddress = autoAddress;
                                    AppConfig.Save();
                                    _logger.LogInfo($"[PEER DISCOVERY] Auto-discovered live LAN IP for Trusted Friend '{friend.Username}' ({friend.FriendCode}) -> {autoAddress}");
                                }
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
