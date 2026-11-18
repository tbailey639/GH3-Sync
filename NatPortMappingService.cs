using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace GH3_Sync;

public class NatPortMappingService
{
    private readonly SyncLogger _logger;
    private readonly int _localPort;
    private readonly CancellationTokenSource _cts = new();
    private Uri? _upnpControlUri;
    private string? _upnpServiceType;

    public string? PublicIp { get; private set; }
    public int? ExternalPort { get; private set; }
    public bool IsMapped { get; private set; }
    public string MappingMethod { get; private set; } = "None";

    public NatPortMappingService(int localPort, SyncLogger logger)
    {
        _localPort = localPort;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _logger.LogInfo("[NAT/UPnP] Initializing Public IP detection & Port Mapping engine...");

        // 1. Detect Public IP address
        PublicIp = await ResolvePublicIpAsync();
        if (!string.IsNullOrEmpty(PublicIp))
        {
            _logger.LogInfo($"[NAT/UPnP] Detected Public IP: {PublicIp}");
        }

        // 2. Try UPnP Port Forwarding via IGD (Internet Gateway Device)
        try
        {
            var upnpSuccess = await TryUpnpMapPortAsync(_localPort, "GH3-Sync P2P Engine");
            if (upnpSuccess)
            {
                IsMapped = true;
                ExternalPort = _localPort;
                MappingMethod = "UPnP IGD";
                _logger.LogInfo($"[NAT/UPnP] Successfully mapped external port {_localPort} via UPnP IGD.");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[NAT/UPnP] UPnP IGD attempt note: {ex.Message}");
        }

        // 3. Try NAT-PMP (Apple / standard router port mapping protocol on port 5351)
        try
        {
            var natPmpSuccess = await TryNatPmpMapPortAsync(_localPort);
            if (natPmpSuccess)
            {
                IsMapped = true;
                ExternalPort = _localPort;
                MappingMethod = "NAT-PMP";
                _logger.LogInfo($"[NAT/UPnP] Successfully mapped external port {_localPort} via NAT-PMP.");
                return;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[NAT/UPnP] NAT-PMP attempt note: {ex.Message}");
        }

        // 4. Default: Direct Public IP / Custom Port / DDNS fallback
        ExternalPort = _localPort;
        MappingMethod = "Direct/Manual";
        _logger.LogInfo($"[NAT/UPnP] Port mapping completed. Method: {MappingMethod} (Target port: {_localPort})");
    }

    public async Task<string?> ResolvePublicIpAsync()
    {
        // Try multiple reliable, free STUN / IP reflection endpoints with fast timeout
        string[] ipServices = new[]
        {
            "https://api.ipify.org",
            "https://icanhazip.com",
            "https://ifconfig.me/ip",
            "https://checkip.amazonaws.com"
        };

        using var client = new HttpClient();
        client.Timeout = TimeSpan.FromSeconds(3);
        client.DefaultRequestHeaders.Add("User-Agent", "GH3-Sync");

        foreach (var url in ipServices)
        {
            try
            {
                var resp = await client.GetStringAsync(url);
                var ip = resp.Trim();
                if (IPAddress.TryParse(ip, out var parsed) && parsed.AddressFamily == AddressFamily.InterNetwork)
                {
                    return ip;
                }
            }
            catch { }
        }

        return null;
    }

    private async Task<bool> TryUpnpMapPortAsync(int port, string description)
    {
        // Discover UPnP router using SSDP M-SEARCH broadcast
        var query = "M-SEARCH * HTTP/1.1\r\n" +
                    "HOST: 239.255.255.250:1900\r\n" +
                    "ST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n" +
                    "MAN: \"ssdp:discover\"\r\n" +
                    "MX: 2\r\n\r\n";

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2500;
        var queryBytes = Encoding.ASCII.GetBytes(query);
        await udp.SendAsync(queryBytes, queryBytes.Length, new IPEndPoint(IPAddress.Parse("239.255.255.250"), 1900));

        string? locationUrl = null;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        while (sw.ElapsedMilliseconds < 2500)
        {
            try
            {
                var recv = await udp.ReceiveAsync();
                var text = Encoding.ASCII.GetString(recv.Buffer);
                var match = Regex.Match(text, @"LOCATION:\s*(http://[^\r\n]+)", RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    locationUrl = match.Groups[1].Value.Trim();
                    break;
                }
            }
            catch { break; }
        }

        if (string.IsNullOrEmpty(locationUrl))
        {
            return false;
        }

        using var http = new HttpClient();
        http.Timeout = TimeSpan.FromSeconds(3);
        var xmlDesc = await http.GetStringAsync(locationUrl);

        // Find WANIPConnection or WANPPPConnection controlURL
        var controlMatch = Regex.Match(xmlDesc, @"<serviceType>urn:schemas-upnp-org:service:WAN(IP|PPP)Connection:1</serviceType>.*?<controlURL>([^<]+)</controlURL>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (!controlMatch.Success) return false;

        var serviceType = xmlDesc.Contains("WANPPPConnection:1") 
            ? "urn:schemas-upnp-org:service:WANPPPConnection:1" 
            : "urn:schemas-upnp-org:service:WANIPConnection:1";

        var controlUrlPath = controlMatch.Groups[2].Value.Trim();
        var uri = new Uri(new Uri(locationUrl), controlUrlPath);

        // Determine local IP on subnet
        var localIp = GetLocalSubnetIp();

        var soapBody = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                       $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                       $"<s:Body>\r\n" +
                       $"<u:AddPortMapping xmlns:u=\"{serviceType}\">\r\n" +
                       $"<NewRemoteHost></NewRemoteHost>\r\n" +
                       $"<NewExternalPort>{port}</NewExternalPort>\r\n" +
                       $"<NewProtocol>TCP</NewProtocol>\r\n" +
                       $"<NewInternalPort>{port}</NewInternalPort>\r\n" +
                       $"<NewInternalClient>{localIp}</NewInternalClient>\r\n" +
                       $"<NewEnabled>1</NewEnabled>\r\n" +
                       $"<NewPortMappingDescription>{description}</NewPortMappingDescription>\r\n" +
                       $"<NewLeaseDuration>0</NewLeaseDuration>\r\n" +
                       $"</u:AddPortMapping>\r\n" +
                       $"</s:Body>\r\n" +
                       $"</s:Envelope>";

        var req = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(soapBody, Encoding.UTF8, "text/xml")
        };
        req.Headers.Add("SOAPACTION", $"\"{serviceType}#AddPortMapping\"");

        var resp = await http.SendAsync(req);
        if (resp.IsSuccessStatusCode)
        {
            _upnpControlUri = uri;
            _upnpServiceType = serviceType;
        }
        return resp.IsSuccessStatusCode;
    }

    /// <summary>
    /// Removes the UPnP IGD port 7373 rule from the router cleanly on app exit or Offline Mode.
    /// </summary>
    public async Task RemoveMappingAsync()
    {
        if (!IsMapped || _upnpControlUri == null || string.IsNullOrEmpty(_upnpServiceType))
            return;

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var soapBody = $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
                           $"<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">\r\n" +
                           $"<s:Body>\r\n" +
                           $"<u:DeletePortMapping xmlns:u=\"{_upnpServiceType}\">\r\n" +
                           $"<NewRemoteHost></NewRemoteHost>\r\n" +
                           $"<NewExternalPort>{ExternalPort ?? _localPort}</NewExternalPort>\r\n" +
                           $"<NewProtocol>TCP</NewProtocol>\r\n" +
                           $"</u:DeletePortMapping>\r\n" +
                           $"</s:Body>\r\n" +
                           $"</s:Envelope>";

            var req = new HttpRequestMessage(HttpMethod.Post, _upnpControlUri)
            {
                Content = new StringContent(soapBody, Encoding.UTF8, "text/xml")
            };
            req.Headers.Add("SOAPACTION", $"\"{_upnpServiceType}#DeletePortMapping\"");

            var resp = await http.SendAsync(req);
            if (resp.IsSuccessStatusCode)
            {
                IsMapped = false;
                MappingMethod = "Closed";
                _logger.LogInfo($"[NAT/UPnP] Removed UPnP port mapping for TCP {_localPort} cleanly.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug($"[NAT/UPnP] Cleanup note: {ex.Message}");
        }
    }

    private async Task<bool> TryNatPmpMapPortAsync(int port)
    {
        // Standard NAT-PMP gateway address is local gateway (usually 192.168.1.1 or 10.0.0.1)
        var gatewayIp = GetDefaultGateway();
        if (gatewayIp == null) return false;

        using var udp = new UdpClient();
        udp.Client.ReceiveTimeout = 2000;

        // NAT-PMP TCP port mapping request:
        // Version 0, Opcode 2 (TCP), Reserved (2 bytes), Internal Port (2 bytes), External Port (2 bytes), Lifetime (4 bytes)
        var packet = new byte[12];
        packet[0] = 0; // version
        packet[1] = 2; // TCP mapping
        packet[2] = 0; // reserved
        packet[3] = 0;
        packet[4] = (byte)(port >> 8);
        packet[5] = (byte)(port & 0xFF);
        packet[6] = (byte)(port >> 8);
        packet[7] = (byte)(port & 0xFF);
        // Lifetime 3600 seconds
        var lifetime = 3600;
        packet[8] = (byte)(lifetime >> 24);
        packet[9] = (byte)(lifetime >> 16);
        packet[10] = (byte)(lifetime >> 8);
        packet[11] = (byte)(lifetime & 0xFF);

        await udp.SendAsync(packet, packet.Length, new IPEndPoint(gatewayIp, 5351));

        try
        {
            var res = await udp.ReceiveAsync();
            if (res.Buffer.Length >= 16 && res.Buffer[0] == 0 && res.Buffer[1] == 130)
            {
                var resultCode = (res.Buffer[2] << 8) | res.Buffer[3];
                return resultCode == 0;
            }
        }
        catch { }

        return false;
    }

    private string GetLocalSubnetIp()
    {
        try
        {
            using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
            socket.Connect("8.8.8.8", 65530);
            if (socket.LocalEndPoint is IPEndPoint endPoint)
                return endPoint.Address.ToString();
        }
        catch { }
        return "127.0.0.1";
    }

    private IPAddress? GetDefaultGateway()
    {
        try
        {
            foreach (var iface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (iface.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    var props = iface.GetIPProperties();
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == AddressFamily.InterNetwork && !gw.Address.Equals(IPAddress.Any))
                        {
                            return gw.Address;
                        }
                    }
                }
            }
        }
        catch { }
        return null;
    }
}
