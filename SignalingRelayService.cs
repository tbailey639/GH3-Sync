using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

/// <summary>
/// High-speed zero-config P2P Signaling & Rendezvous Relay.
/// Allows players to connect using just their Friend Code (e.g. 5126-A893)
/// across the Internet (different houses, Wi-Fi, CGNAT, strict routers)
/// with real-time address auto-discovery and live friend updates.
/// </summary>
public class SignalingRelayService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5.0) };
    private readonly FriendService _friends;
    private readonly SyncLogger? _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _backgroundTask;
    private readonly HashSet<string> _processedMessageIds = new();

    public SignalingRelayService(FriendService friends, SyncLogger? logger = null)
    {
        _friends = friends;
        _logger = logger;
    }

    public static string CleanTopicCode(string friendCode)
    {
        var raw = (friendCode ?? "").Trim().ToUpperInvariant();
        if (raw.StartsWith("GH3-")) raw = raw.Substring(4);
        raw = raw.Replace("-", "");
        return raw;
    }

    public static string NormalizeCode(string raw)
    {
        var s = (raw ?? "").Trim().ToUpperInvariant();
        if (s.StartsWith("GH3-")) s = s.Substring(4);
        s = s.Replace("-", "");
        if (s.Length == 8)
        {
            return $"GH3-{s.Substring(0, 4)}-{s.Substring(4, 4)}";
        }
        return $"GH3-{s}";
    }

    public void Start()
    {
        if (_backgroundTask != null) return;
        _loopCts = new CancellationTokenSource();
        _backgroundTask = Task.Run(() => RunSignalingLoopAsync(_loopCts.Token));
        _logger?.LogInfo("[SIGNALING] Global P2P Rendezvous Relay started.");
    }

    public void Stop()
    {
        try
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
        }
        catch { }
        _backgroundTask = null;
    }

    private static string ExtractField(string text, string key)
    {
        try
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, $@"""?{key}""?\s*:\s*""?([^,""\}}]+)""?");
            return m.Success ? m.Groups[1].Value.Trim() : "";
        }
        catch { return ""; }
    }

    public async Task<bool> PublishHeartbeatAsync(string friendCode, string username, string address)
    {
        if (string.IsNullOrWhiteSpace(address) || address.Contains("169.254.") || address.Contains("127.0.0.1"))
            return false;

        var topicKey = CleanTopicCode(friendCode);
        if (string.IsNullOrWhiteSpace(topicKey)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                code = NormalizeCode(friendCode),
                username = username ?? "Player",
                address = address.TrimEnd('/'),
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var ntfyPayload = new
            {
                topic = $"gh3sync-sig-{topicKey}",
                message = innerJson
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4.0));
            var resp = await _http.PostAsJsonAsync("https://ntfy.sh", ntfyPayload, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<(bool Found, string Username, string Address, string NormalizedCode)> ResolvePeerAsync(string rawCode)
    {
        var normCode = NormalizeCode(rawCode);
        var topicKey = CleanTopicCode(normCode);
        if (string.IsNullOrWhiteSpace(topicKey)) return (false, "", "", normCode);

        try
        {
            var url = $"https://ntfy.sh/gh3sync-sig-{topicKey}/json?poll=1";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4.0));
            var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return (false, "", "", normCode);

            var rawJson = await resp.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(rawJson)) return (false, "", "", normCode);

            var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            for (int i = lines.Length - 1; i >= 0; i--)
            {
                var line = lines[i].Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
                {
                    var msgText = msgProp.GetString() ?? "";
                    if (msgText.StartsWith("{") && msgText.EndsWith("}"))
                    {
                        string peerAddr = "";
                        string peerName = "Player";
                        string peerCode = normCode;

                        try
                        {
                            using var msgDoc = JsonDocument.Parse(msgText);
                            var mr = msgDoc.RootElement;
                            peerAddr = mr.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
                            peerName = mr.TryGetProperty("username", out var u) ? u.GetString() ?? "Player" : "Player";
                            peerCode = mr.TryGetProperty("code", out var c) ? c.GetString() ?? normCode : normCode;
                        }
                        catch
                        {
                            peerAddr = ExtractField(msgText, "address");
                            peerName = ExtractField(msgText, "username");
                            peerCode = ExtractField(msgText, "code");
                        }

                        if (!string.IsNullOrWhiteSpace(peerAddr) && !peerAddr.Contains("169.254."))
                        {
                            return (true, string.IsNullOrWhiteSpace(peerName) ? "Player" : peerName, peerAddr, string.IsNullOrWhiteSpace(peerCode) ? normCode : peerCode);
                        }
                    }
                }
            }
        }
        catch { }

        return (false, "", "", normCode);
    }

    public async Task<bool> SendFriendRequestPacketAsync(string targetCode, string senderCode, string senderUsername, string senderAddress, string senderToken)
    {
        var targetTopic = CleanTopicCode(targetCode);
        if (string.IsNullOrWhiteSpace(targetTopic)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "FRIEND_REQUEST",
                senderCode = NormalizeCode(senderCode),
                senderUsername = senderUsername ?? "Player",
                senderAddress = (senderAddress ?? "").TrimEnd('/'),
                senderToken = senderToken ?? "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var ntfyPayload = new
            {
                topic = $"gh3sync-req-{targetTopic}",
                message = innerJson
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
            var resp = await _http.PostAsJsonAsync("https://ntfy.sh", ntfyPayload, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SendFriendAcceptPacketAsync(string senderCode, string myCode, string myUsername, string myAddress, string myToken)
    {
        var senderTopic = CleanTopicCode(senderCode);
        if (string.IsNullOrWhiteSpace(senderTopic)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "FRIEND_ACCEPT",
                targetCode = NormalizeCode(myCode),
                targetUsername = myUsername ?? "Player",
                targetAddress = (myAddress ?? "").TrimEnd('/'),
                targetToken = myToken ?? "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var ntfyPayload = new
            {
                topic = $"gh3sync-ack-{senderTopic}",
                message = innerJson
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5.0));
            var resp = await _http.PostAsJsonAsync("https://ntfy.sh", ntfyPayload, cts.Token);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private async Task RunSignalingLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var myCode = AppConfig.Current.FriendCode;
                var myCleanTopic = CleanTopicCode(myCode);

                // 1. Publish our live heartbeat
                var myBestAddress = FriendService.BestAddressProvider?.Invoke() ?? "";
                if (!string.IsNullOrWhiteSpace(myBestAddress) && !myBestAddress.Contains("169.254.") && !string.IsNullOrWhiteSpace(myCleanTopic))
                {
                    await PublishHeartbeatAsync(myCode, AppConfig.Current.Username, myBestAddress);
                }

                // 2. Poll for incoming friend requests
                if (!string.IsNullOrWhiteSpace(myCleanTopic))
                {
                    await PollRequestsTopicAsync(myCleanTopic, ct);
                    await PollAcceptTopicAsync(myCleanTopic, ct);
                }

                // 3. Keep existing friends' addresses fresh & live
                var friends = AppConfig.Current.Friends.ToList();
                foreach (var friend in friends)
                {
                    var fTopic = CleanTopicCode(friend.FriendCode);
                    if (string.IsNullOrWhiteSpace(fTopic)) continue;

                    var (found, _, liveAddr, _) = await ResolvePeerAsync(friend.FriendCode);
                    if (found && !string.IsNullOrWhiteSpace(liveAddr) && friend.LastAddress != liveAddr)
                    {
                        friend.LastAddress = liveAddr;
                        AppConfig.Save();
                        _logger?.LogInfo($"[SIGNALING] Auto-updated live address for {friend.Username} to: {liveAddr}");
                    }
                }
            }
            catch { }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(6.0), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task PollRequestsTopicAsync(string myCleanTopic, CancellationToken ct)
    {
        try
        {
            var url = $"https://ntfy.sh/gh3sync-req-{myCleanTopic}/json?poll=1";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4.0));

            var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            var rawJson = await resp.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(rawJson)) return;

            var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(id) && _processedMessageIds.Contains(id)) continue;
                if (!string.IsNullOrEmpty(id)) _processedMessageIds.Add(id);

                if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
                {
                    var msgText = msgProp.GetString() ?? "";
                    if (msgText.StartsWith("{") && msgText.EndsWith("}"))
                    {
                        string type = "";
                        string sCode = "";
                        string sName = "Friend";
                        string sAddr = "";
                        string sTok = "";

                        try
                        {
                            using var msgDoc = JsonDocument.Parse(msgText);
                            var mr = msgDoc.RootElement;
                            type = mr.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                            sCode = mr.TryGetProperty("senderCode", out var sc) ? sc.GetString() ?? "" : "";
                            sName = mr.TryGetProperty("senderUsername", out var sn) ? sn.GetString() ?? "Friend" : "Friend";
                            sAddr = mr.TryGetProperty("senderAddress", out var sa) ? sa.GetString() ?? "" : "";
                            sTok = mr.TryGetProperty("senderToken", out var st) ? st.GetString() ?? "" : "";
                        }
                        catch
                        {
                            type = ExtractField(msgText, "type");
                            sCode = ExtractField(msgText, "senderCode");
                            sName = ExtractField(msgText, "senderUsername");
                            sAddr = ExtractField(msgText, "senderAddress");
                            sTok = ExtractField(msgText, "senderToken");
                        }

                        if (type == "FRIEND_REQUEST" && !string.IsNullOrWhiteSpace(sCode) && !sCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
                        {
                            _friends.ReceiveIncomingRequest(sCode, sName, sAddr, sTok);
                            _logger?.LogInfo($"[SIGNALING] Received friend request from {sName} ({sCode}) at {sAddr}");
                        }
                    }
                }
            }
        }
        catch { }
    }

    private async Task PollAcceptTopicAsync(string myCleanTopic, CancellationToken ct)
    {
        try
        {
            var url = $"https://ntfy.sh/gh3sync-ack-{myCleanTopic}/json?poll=1";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4.0));

            var resp = await _http.GetAsync(url, cts.Token);
            if (!resp.IsSuccessStatusCode) return;

            var rawJson = await resp.Content.ReadAsStringAsync(cts.Token);
            if (string.IsNullOrWhiteSpace(rawJson)) return;

            var lines = rawJson.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var ip) ? ip.GetString() ?? "" : "";
                if (!string.IsNullOrEmpty(id) && _processedMessageIds.Contains(id)) continue;
                if (!string.IsNullOrEmpty(id)) _processedMessageIds.Add(id);

                if (root.TryGetProperty("message", out var msgProp) && msgProp.ValueKind == JsonValueKind.String)
                {
                    var msgText = msgProp.GetString() ?? "";
                    if (msgText.StartsWith("{") && msgText.EndsWith("}"))
                    {
                        string type = "";
                        string tCode = "";
                        string tName = "Friend";
                        string tAddr = "";
                        string tTok = "";

                        try
                        {
                            using var msgDoc = JsonDocument.Parse(msgText);
                            var mr = msgDoc.RootElement;
                            type = mr.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                            tCode = mr.TryGetProperty("targetCode", out var tc) ? tc.GetString() ?? "" : "";
                            tName = mr.TryGetProperty("targetUsername", out var tn) ? tn.GetString() ?? "Friend" : "Friend";
                            tAddr = mr.TryGetProperty("targetAddress", out var ta) ? ta.GetString() ?? "" : "";
                            tTok = mr.TryGetProperty("targetToken", out var tt) ? tt.GetString() ?? "" : "";
                        }
                        catch
                        {
                            type = ExtractField(msgText, "type");
                            tCode = ExtractField(msgText, "targetCode");
                            tName = ExtractField(msgText, "targetUsername");
                            tAddr = ExtractField(msgText, "targetAddress");
                            tTok = ExtractField(msgText, "targetToken");
                        }

                        if (type == "FRIEND_ACCEPT" && !string.IsNullOrWhiteSpace(tCode))
                        {
                            _friends.HandleRemoteAcceptance(tCode, tName, tAddr, tTok);
                            _logger?.LogInfo($"[SIGNALING] Remote friend {tName} ({tCode}) accepted friend request!");
                        }
                    }
                }
            }
        }
        catch { }
    }
}
