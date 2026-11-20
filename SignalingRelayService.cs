using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
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
    public static SocketsHttpHandler CreateFastIPv4Handler()
    {
        return new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            ConnectTimeout = TimeSpan.FromSeconds(4),
            ConnectCallback = async (context, cancellationToken) =>
            {
                var host = context.DnsEndPoint.Host;
                var port = context.DnsEndPoint.Port;

                IPAddress[] addrs;
                if (IPAddress.TryParse(host, out var parsedIp))
                {
                    addrs = new[] { parsedIp };
                }
                else
                {
                    var entry = await Dns.GetHostEntryAsync(host, cancellationToken);
                    addrs = entry.AddressList;
                }

                // Prioritize IPv4 addresses to bypass broken residential ISP IPv6 routes
                var sorted = addrs
                    .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
                    .ToList();

                Exception? lastEx = null;
                foreach (var ip in sorted)
                {
                    var socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true
                    };

                    try
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(2.5));
                        await socket.ConnectAsync(new IPEndPoint(ip, port), linkedCts.Token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex)
                    {
                        socket.Dispose();
                        lastEx = ex;
                    }
                }

                throw lastEx ?? new SocketException((int)SocketError.HostUnreachable);
            }
        };
    }

    private static readonly HttpClient _http = new(CreateFastIPv4Handler()) { Timeout = TimeSpan.FromSeconds(8.0) };
    private readonly FriendService _friends;
    private readonly SyncLogger? _logger;
    private CancellationTokenSource? _loopCts;
    private Task? _backgroundTask;
    private readonly HashSet<string> _processedMessageIds = new();

    public static Func<string>? ActivityStatusProvider { get; set; }
    public event Action<string, bool>? OnPeerReadyChanged;
    public event Action<string, int, List<string>>? OnPeerRouletteRoll;
    public event Action<string, string, string>? OnPeerActivityUpdated;
    public event Action<string, string>? OnPeerHypeSfx;

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
        var topicKey = CleanTopicCode(friendCode);
        if (string.IsNullOrWhiteSpace(topicKey)) return false;

        var cleanAddr = (address ?? "").Trim();
        if (cleanAddr.Contains("169.254.") || cleanAddr.Contains("127.0.0.1"))
            cleanAddr = "";

        try
        {
            var activity = ActivityStatusProvider?.Invoke() ?? "Online";
            var showcase = AppConfig.Current.ShowcaseSongTitle ?? "";

            var innerJson = JsonSerializer.Serialize(new
            {
                code = NormalizeCode(friendCode),
                username = username ?? "Player",
                address = cleanAddr.TrimEnd('/'),
                activity = activity,
                showcase = showcase,
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var ntfyPayload = new
            {
                topic = $"gh3sync-sig-{topicKey}",
                message = innerJson
            };

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6.0));
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

        // 1. Fast query: past 10 minutes (typical response ~300ms)
        var res = await QueryPeerFromNtfyAsync(normCode, topicKey, "10m");
        if (res.Found) return res;

        // 2. Fallback query: past 2 hours
        res = await QueryPeerFromNtfyAsync(normCode, topicKey, "2h");
        if (res.Found) return res;

        return (false, "", "", normCode);
    }

    private async Task<(bool Found, string Username, string Address, string NormalizedCode)> QueryPeerFromNtfyAsync(string normCode, string topicKey, string since)
    {
        try
        {
            var url = $"https://ntfy.sh/gh3sync-sig-{topicKey}/json?poll=1&since={since}";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6.0));
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
                        string peerActivity = "Online";
                        string peerShowcase = "";

                        try
                        {
                            using var msgDoc = JsonDocument.Parse(msgText);
                            var mr = msgDoc.RootElement;
                            peerAddr = mr.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
                            peerName = mr.TryGetProperty("username", out var u) ? u.GetString() ?? "Player" : "Player";
                            peerCode = mr.TryGetProperty("code", out var c) ? c.GetString() ?? normCode : normCode;
                            if (mr.TryGetProperty("activity", out var ac)) peerActivity = ac.GetString() ?? "Online";
                            if (mr.TryGetProperty("showcase", out var sh)) peerShowcase = sh.GetString() ?? "";
                        }
                        catch
                        {
                            peerAddr = ExtractField(msgText, "address");
                            peerName = ExtractField(msgText, "username");
                            peerCode = ExtractField(msgText, "code");
                            peerActivity = ExtractField(msgText, "activity");
                            if (string.IsNullOrWhiteSpace(peerActivity)) peerActivity = "Online";
                            peerShowcase = ExtractField(msgText, "showcase");
                        }

                        var existingFriend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(peerCode, StringComparison.OrdinalIgnoreCase));
                        if (existingFriend != null)
                        {
                            existingFriend.LastSeenUtc = DateTime.UtcNow;
                            existingFriend.ActivityStatus = peerActivity;
                            if (!string.IsNullOrWhiteSpace(peerAddr) && !peerAddr.Contains("169.254."))
                            {
                                existingFriend.LastAddress = peerAddr;
                            }
                            if (!string.IsNullOrWhiteSpace(peerShowcase)) existingFriend.ShowcaseSong = peerShowcase;
                        }
                        OnPeerActivityUpdated?.Invoke(peerCode, peerActivity, peerShowcase);

                        return (true, string.IsNullOrWhiteSpace(peerName) ? "Player" : peerName, peerAddr, string.IsNullOrWhiteSpace(peerCode) ? normCode : peerCode);
                    }
                }
            }
        }
        catch { }

        return (false, "", "", normCode);
    }

    public async Task<bool> SendFriendRequestPacketAsync(string targetCode, string senderCode, string senderUsername, string senderAddress, string senderToken, bool isTrusted = true)
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
                isTrusted = isTrusted,
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

    public async Task<bool> SendTrustUpgradePacketAsync(string targetCode, string senderCode, string senderUsername, string senderAddress, string senderToken)
    {
        var targetTopic = CleanTopicCode(targetCode);
        if (string.IsNullOrWhiteSpace(targetTopic)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "TRUST_UPGRADE_REQUEST",
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

    public async Task<bool> SendTrustUpgradeAcceptPacketAsync(string targetCode, string myCode, string myUsername, string myAddress, string myToken)
    {
        var targetTopic = CleanTopicCode(targetCode);
        if (string.IsNullOrWhiteSpace(targetTopic)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "TRUST_UPGRADE_ACCEPT",
                targetCode = NormalizeCode(myCode),
                targetUsername = myUsername ?? "Player",
                targetAddress = (myAddress ?? "").TrimEnd('/'),
                targetToken = myToken ?? "",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var ntfyPayload = new
            {
                topic = $"gh3sync-ack-{targetTopic}",
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

    public async Task<bool> SendLobbyReadyPacketAsync(string targetCode, string myCode, bool isReady)
    {
        var targetTopic = CleanTopicCode(targetCode);
        if (string.IsNullOrWhiteSpace(targetTopic)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "LOBBY_READY_STATE",
                senderCode = NormalizeCode(myCode),
                isReady = isReady,
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
        catch { return false; }
    }

    public async Task<bool> SendRouletteRollPacketAsync(string targetCode, string myCode, int seed, List<string> songIds)
    {
        var targetTopic = CleanTopicCode(targetCode);
        if (string.IsNullOrWhiteSpace(targetTopic)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "LOBBY_ROULETTE_ROLL",
                senderCode = NormalizeCode(myCode),
                seed = seed,
                songIds = songIds ?? new List<string>(),
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
        catch { return false; }
    }

    public void TriggerPeerReadyChanged(string code, bool isReady) => OnPeerReadyChanged?.Invoke(code, isReady);
    public void TriggerPeerRouletteRoll(string code, int seed, List<string> songIds) => OnPeerRouletteRoll?.Invoke(code, seed, songIds);

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

    public async Task<bool> SendLobbyHypeSfxPacketAsync(string targetCode, string senderCode, string sfxType)
    {
        var cleanTarget = CleanTopicCode(targetCode);
        if (string.IsNullOrWhiteSpace(cleanTarget)) return false;

        try
        {
            var innerJson = JsonSerializer.Serialize(new
            {
                type = "LOBBY_HYPE_SFX",
                senderCode = NormalizeCode(senderCode),
                sfxType = sfxType ?? "airhorn",
                timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            });

            var ntfyPayload = new
            {
                topic = $"gh3sync-req-{cleanTarget}",
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

    private async Task RunSignalingLoopAsync(CancellationToken ct)
    {
        int cycleIndex = 0;
        while (!ct.IsCancellationRequested)
        {
            cycleIndex++;
            try
            {
                var myCode = AppConfig.Current.FriendCode;
                var myCleanTopic = CleanTopicCode(myCode);
                var myBestAddress = FriendService.BestAddressProvider?.Invoke() ?? "";

                // 1. Publish our live heartbeat
                if (!string.IsNullOrWhiteSpace(myCleanTopic))
                {
                    await PublishHeartbeatAsync(myCode, AppConfig.Current.Username, myBestAddress);
                }

                // 2. Poll for incoming friend requests and acceptances
                if (!string.IsNullOrWhiteSpace(myCleanTopic))
                {
                    await PollRequestsTopicAsync(myCleanTopic, ct);
                    await PollAcceptTopicAsync(myCleanTopic, ct);
                }

                // 3. Guaranteed background delivery for Outgoing Pending Requests (Xbox-style offline-tolerant sync)
                var outgoingPending = AppConfig.Current.FriendRequests
                    .Where(r => r.Direction.Equals("Outgoing", StringComparison.OrdinalIgnoreCase) && r.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                foreach (var pending in outgoingPending)
                {
                    var targetClean = CleanTopicCode(pending.FriendCode);
                    if (!string.IsNullOrWhiteSpace(targetClean))
                    {
                        await SendFriendRequestPacketAsync(pending.FriendCode, myCode, AppConfig.Current.Username, myBestAddress, pending.ProposedTrustToken, pending.IsTrustedRequest);
                    }
                }

                // 4. Staggered address update for existing friends (every 2nd cycle to stay well within rate limits)
                if (cycleIndex % 2 == 0)
                {
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
            }
            catch { }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(16.0), ct);
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
            var url = $"https://ntfy.sh/gh3sync-req-{myCleanTopic}/json?poll=1&since=24h";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6.0));

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

                        bool isTrusted = true;
                        try
                        {
                            using var msgDoc = JsonDocument.Parse(msgText);
                            var mr = msgDoc.RootElement;
                            type = mr.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                            sCode = mr.TryGetProperty("senderCode", out var sc) ? sc.GetString() ?? "" : "";
                            sName = mr.TryGetProperty("senderUsername", out var sn) ? sn.GetString() ?? "Friend" : "Friend";
                            sAddr = mr.TryGetProperty("senderAddress", out var sa) ? sa.GetString() ?? "" : "";
                            sTok = mr.TryGetProperty("senderToken", out var st) ? st.GetString() ?? "" : "";
                            if (mr.TryGetProperty("isTrusted", out var it)) isTrusted = it.GetBoolean();
                        }
                        catch
                        {
                            type = ExtractField(msgText, "type");
                            sCode = ExtractField(msgText, "senderCode");
                            sName = ExtractField(msgText, "senderUsername");
                            sAddr = ExtractField(msgText, "senderAddress");
                            sTok = ExtractField(msgText, "senderToken");
                            var trField = ExtractField(msgText, "isTrusted");
                            if (bool.TryParse(trField, out var parsedTr)) isTrusted = parsedTr;
                        }

                        if (type == "FRIEND_REQUEST" && !string.IsNullOrWhiteSpace(sCode) && !sCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
                        {
                            _friends.ReceiveIncomingRequest(sCode, sName, sAddr, sTok, isTrusted);
                            _logger?.LogInfo($"[SIGNALING] Received friend request from {sName} ({sCode}) at {sAddr} [Trusted: {isTrusted}]");
                        }
                        else if (type == "TRUST_UPGRADE_REQUEST" && !string.IsNullOrWhiteSpace(sCode) && !sCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
                        {
                            _friends.ReceiveTrustUpgradeRequest(sCode, sName, sAddr, sTok);
                            _logger?.LogInfo($"[SIGNALING] Received trust upgrade request from {sName} ({sCode}) at {sAddr}");
                        }
                        else if (type == "LOBBY_READY_STATE" && !string.IsNullOrWhiteSpace(sCode) && !sCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
                        {
                            bool isReady = false;
                            try
                            {
                                using var msgDoc = JsonDocument.Parse(msgText);
                                if (msgDoc.RootElement.TryGetProperty("isReady", out var rp))
                                    isReady = rp.GetBoolean();
                            }
                            catch { }

                            var existingFriend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(sCode, StringComparison.OrdinalIgnoreCase));
                            if (existingFriend != null)
                            {
                                existingFriend.IsLobbyReady = isReady;
                            }
                            OnPeerReadyChanged?.Invoke(sCode, isReady);
                            _logger?.LogInfo($"[SIGNALING] Received lobby ready state from {sCode}: {isReady}");
                        }
                        else if (type == "LOBBY_ROULETTE_ROLL" && !string.IsNullOrWhiteSpace(sCode) && !sCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
                        {
                            int seed = 0;
                            var songIds = new List<string>();
                            try
                            {
                                using var msgDoc = JsonDocument.Parse(msgText);
                                var mr = msgDoc.RootElement;
                                if (mr.TryGetProperty("seed", out var sp)) seed = sp.GetInt32();
                                if (mr.TryGetProperty("songIds", out var sa) && sa.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var item in sa.EnumerateArray())
                                    {
                                        var sId = item.GetString();
                                        if (!string.IsNullOrWhiteSpace(sId)) songIds.Add(sId);
                                    }
                                }
                            }
                            catch { }

                            OnPeerRouletteRoll?.Invoke(sCode, seed, songIds);
                            _logger?.LogInfo($"[SIGNALING] Received party roulette roll from {sCode} with {songIds.Count} songs (seed {seed})");
                        }
                        else if (type == "LOBBY_HYPE_SFX" && !string.IsNullOrWhiteSpace(sCode) && !sCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
                        {
                            string sfxType = "airhorn";
                            try
                            {
                                using var msgDoc = JsonDocument.Parse(msgText);
                                var mr = msgDoc.RootElement;
                                if (mr.TryGetProperty("sfxType", out var st)) sfxType = st.GetString() ?? "airhorn";
                            }
                            catch { }

                            OnPeerHypeSfx?.Invoke(sCode, sfxType);
                            _logger?.LogInfo($"[SIGNALING] Received lobby hype sound effect '{sfxType}' from {sCode}");
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
            var url = $"https://ntfy.sh/gh3sync-ack-{myCleanTopic}/json?poll=1&since=24h";
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6.0));

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
                        else if (type == "TRUST_UPGRADE_ACCEPT" && !string.IsNullOrWhiteSpace(tCode))
                        {
                            _friends.ProcessTrustUpgradeAccepted(tCode, tName, tAddr, tTok);
                            _logger?.LogInfo($"[SIGNALING] Remote friend {tName} ({tCode}) accepted trust upgrade!");
                        }
                    }
                }
            }
        }
        catch { }
    }
}
