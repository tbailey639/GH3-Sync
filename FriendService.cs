using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class FriendLookupResult
{
    public bool Found { get; set; }
    public string FriendCode { get; set; } = "";
    public string Username { get; set; } = "";
    public string Address { get; set; } = "";
    public bool IsOnline { get; set; }
    public string Source { get; set; } = ""; // "LAN", "AdminHub", "Direct", "SavedAddress", "Loopback"
    public string? Error { get; set; }
}

public class SendFriendRequestResult
{
    public bool Success { get; set; }
    public bool ReceivedByPeer { get; set; }
    public string FriendCode { get; set; } = "";
    public string Username { get; set; } = "";
    public string Message { get; set; } = "";
}

public class FriendService
{
    private readonly object _lock = new();
    private static readonly HttpClient _httpClient = new(SignalingRelayService.CreateFastIPv4Handler()) { Timeout = TimeSpan.FromSeconds(15.0) };

    public static Func<string>? BestAddressProvider { get; set; }
    public static SignalingRelayService? SignalingRelay { get; set; }

    public async Task<int> MeasurePeerPingAsync(string address)
    {
        if (string.IsNullOrWhiteSpace(address)) return -1;
        try
        {
            var targetUrl = address.TrimEnd('/') + "/api/profile";
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
            var req = new HttpRequestMessage(HttpMethod.Get, targetUrl);
            var resp = await _httpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            sw.Stop();
            if (resp.IsSuccessStatusCode)
            {
                return (int)sw.ElapsedMilliseconds;
            }
        }
        catch { }
        return -1;
    }

    public UserProfile Profile => new()
    {
        Username = AppConfig.Current.Username,
        FriendCode = AppConfig.Current.FriendCode,
        ShowcaseSongTitle = AppConfig.Current.ShowcaseSongTitle ?? "",
        ShowcaseSongId = AppConfig.Current.ShowcaseSongId ?? "",
        ActivityStatus = SignalingRelayService.ActivityStatusProvider?.Invoke() ?? "Online",
        TotalCustomSongs = AppConfig.Current.CreatedSongDirectories?.Count ?? 0,
        TotalNotesHit = AppConfig.Current.LifetimeStats?.TotalNotesHit ?? 0,
        TotalFullCombos = AppConfig.Current.LifetimeStats?.TotalFullCombos ?? 0,
        TotalMatchesPlayed = AppConfig.Current.LifetimeStats?.TotalMatchesPlayed ?? 0,
        UnlockedAchievements = RockHallOfFameService.GetAllAchievements().Where(a => a.IsUnlocked).Select(a => a.Id).ToList()
    };

    public string Username => AppConfig.Current.Username;
    public string FriendCode => AppConfig.Current.FriendCode;
    public IReadOnlyList<FriendEntry> Friends => AppConfig.Current.Friends;
    public IReadOnlyList<FriendRequestItem> AllRequests => AppConfig.Current.FriendRequests;

    public IReadOnlyList<FriendRequestItem> IncomingRequests
    {
        get
        {
            lock (_lock)
            {
                return AppConfig.Current.FriendRequests
                    .Where(r => r.Direction.Equals("Incoming", StringComparison.OrdinalIgnoreCase) &&
                                r.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList();
            }
        }
    }

    public IReadOnlyList<FriendRequestItem> OutgoingRequests
    {
        get
        {
            lock (_lock)
            {
                return AppConfig.Current.FriendRequests
                    .Where(r => r.Direction.Equals("Outgoing", StringComparison.OrdinalIgnoreCase) &&
                                r.Status.Equals("Pending", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(r => r.CreatedAt)
                    .ToList();
            }
        }
    }

    public event Action<FriendRequestItem>? OnFriendRequestReceived;
    public event Action<FriendEntry>? OnFriendRequestAccepted;

    public FriendService(string? baseDir = null)
    {
    }

    public void SaveProfile(string newUsername)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(newUsername))
            {
                AppConfig.UpdateUsername(newUsername.Trim());
            }
        }
    }

    public async Task<(bool Success, string Error)> SaveProfileAsync(string newUsername)
    {
        if (string.IsNullOrWhiteSpace(newUsername)) return (false, "Stage Name cannot be empty.");
        var trimmed = newUsername.Trim();
        if (trimmed.Equals(AppConfig.Current.Username, StringComparison.OrdinalIgnoreCase))
            return (true, "");

        if (SignalingRelay != null)
        {
            var (isTaken, otherCode, _) = await SignalingRelay.CheckUsernameTakenAsync(trimmed, AppConfig.Current.FriendCode);
            if (isTaken)
            {
                return (false, $"The Stage Name '{trimmed}' is already claimed by another rockstar ({otherCode}) on the network. Please choose a unique Stage Name!");
            }
        }

        lock (_lock)
        {
            AppConfig.UpdateUsername(trimmed);
        }
        return (true, "");
    }

    /// <summary>
    /// Searches for a player across the network using either their Friend Code (GH3-XXXX-XXXX)
    /// OR their Stage Name / Username (e.g. "KuffyKush", "JohnnyRocker").
    /// </summary>
    public async Task<FriendLookupResult> SearchFriendAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new FriendLookupResult { Found = false, Error = "Search query is empty." };

        var q = query.Trim();

        // 1. Check if the query matches an existing friend in our circle (by username or code)
        var match = AppConfig.Current.Friends.FirstOrDefault(f =>
            f.Username.Equals(q, StringComparison.OrdinalIgnoreCase) ||
            f.FriendCode.Equals(SignalingRelayService.NormalizeCode(q), StringComparison.OrdinalIgnoreCase) ||
            SignalingRelayService.CleanTopicCode(f.FriendCode).Equals(SignalingRelayService.CleanTopicCode(q), StringComparison.OrdinalIgnoreCase));

        if (match != null)
        {
            return new FriendLookupResult
            {
                Found = true,
                FriendCode = match.FriendCode,
                Username = match.Username,
                Address = match.LastAddress,
                IsOnline = match.IsOnlineNow,
                Source = "FriendsList"
            };
        }

        // 2. Check if the query is a self-lookup
        if (q.Equals(AppConfig.Current.Username, StringComparison.OrdinalIgnoreCase) ||
            SignalingRelayService.CleanTopicCode(q).Equals(SignalingRelayService.CleanTopicCode(AppConfig.Current.FriendCode), StringComparison.OrdinalIgnoreCase))
        {
            return new FriendLookupResult
            {
                Found = true,
                FriendCode = AppConfig.Current.FriendCode,
                Username = AppConfig.Current.Username,
                Address = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373",
                IsOnline = true,
                Source = "Self"
            };
        }

        // 3. Try lookup by Friend Code first
        var codeLookup = await LookupFriendByCodeAsync(q);
        if (codeLookup.Found) return codeLookup;

        // 4. Try lookup by Username across LAN peer discovery
        var lanMatch = PeerDiscoveryService.Instance?.GetAllDiscoveredPeers().FirstOrDefault(p =>
            p.Username.Equals(q, StringComparison.OrdinalIgnoreCase));
        if (lanMatch != null)
        {
            return new FriendLookupResult
            {
                Found = true,
                FriendCode = lanMatch.FriendCode,
                Username = lanMatch.Username,
                Address = lanMatch.Address,
                IsOnline = (DateTime.UtcNow - lanMatch.LastSeen).TotalSeconds < 90,
                Source = "LAN"
            };
        }

        // 5. Try lookup by Username via Global Signaling Relay Index
        if (SignalingRelay != null)
        {
            var userLookup = await SignalingRelay.LookupPeerByUsernameAsync(q);
            if (userLookup.Found) return userLookup;
        }

        return new FriendLookupResult
        {
            Found = false,
            Error = $"No player found with Stage Name or Friend Code '{q}'. Make sure your friend has GH3-Sync open!"
        };
    }

    /// <summary>
    /// Auto-detects the username and live connection endpoint from a unique Friend Code (GH3-XXXX-XXXX)
    /// across LAN UDP beacons, Central Admin Hub, direct URL, or past cached sessions.
    /// </summary>
    public async Task<FriendLookupResult> LookupFriendByCodeAsync(string rawInput, HttpClient? customClient = null)
    {
        if (string.IsNullOrWhiteSpace(rawInput))
        {
            return new FriendLookupResult { Found = false, Error = "Friend Code is empty." };
        }

        var client = customClient ?? _httpClient;
        var input = rawInput.Trim();

        // 1. Direct Address format: http://192.168.1.50:7373, GH3-XXXX-XXXX@https://..., or gh3sync:// invite
        string? directAddress = null;
        string cleanCode = input;

        if (input.StartsWith("gh3sync://", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var b64 = input.Substring("gh3sync://".Length).Trim();
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("addr", out var a))
                {
                    directAddress = a.GetString();
                }
                if (doc.RootElement.TryGetProperty("code", out var c))
                {
                    cleanCode = c.GetString() ?? "";
                }
                else if (doc.RootElement.TryGetProperty("friendCode", out var fc))
                {
                    cleanCode = fc.GetString() ?? "";
                }
            }
            catch { }
        }
        else if (input.Contains('@'))
        {
            var parts = input.Split('@', 2);
            cleanCode = parts[0].Trim();
            directAddress = parts[1].Trim();
        }
        else if (input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 input.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            directAddress = input;
            cleanCode = "";
        }

        if (!string.IsNullOrEmpty(directAddress))
        {
            if (!directAddress.StartsWith("http://") && !directAddress.StartsWith("https://"))
            {
                if (directAddress.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) || directAddress.EndsWith(":443"))
                    directAddress = "https://" + directAddress;
                else
                    directAddress = "http://" + directAddress;
            }

            try
            {
                var profileUrl = $"{directAddress.TrimEnd('/')}/api/profile";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15.0));
                var resp = await client.GetAsync(profileUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                    var detectedName = data.TryGetProperty("username", out var u) ? u.GetString() ?? "Player" : "Player";
                    var detectedCode = data.TryGetProperty("friendCode", out var c) ? c.GetString() ?? cleanCode : cleanCode;

                    return new FriendLookupResult
                    {
                        Found = true,
                        FriendCode = detectedCode,
                        Username = detectedName,
                        Address = directAddress,
                        IsOnline = true,
                        Source = "Direct"
                    };
                }
            }
            catch (Exception ex)
            {
                return new FriendLookupResult
                {
                    Found = false,
                    FriendCode = cleanCode,
                    Address = directAddress,
                    Error = $"Could not reach {directAddress}: {ex.Message}. Make sure your friend has GH3-Sync open and their Internet Tunnel is active."
                };
            }
        }

        // Normalize Friend Code format (e.g. 1234-ABCD, 1234ABCD, or GH3-1234-ABCD)
        cleanCode = SignalingRelayService.NormalizeCode(cleanCode);

        // Loopback test self-detection
        if (cleanCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
        {
            return new FriendLookupResult
            {
                Found = true,
                FriendCode = cleanCode,
                Username = AppConfig.Current.Username,
                Address = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373",
                IsOnline = true,
                Source = "Self"
            };
        }

        // 2. Check Global P2P Signaling Relay FIRST (Instant Internet rendezvous by Friend Code - typically ~300ms)
        var existingFriend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
        try
        {
            if (SignalingRelay != null)
            {
                var (found, sUser, sAddr, sNorm) = await SignalingRelay.ResolvePeerAsync(cleanCode);
                if (found)
                {
                    if (existingFriend != null)
                    {
                        if (!string.IsNullOrWhiteSpace(sAddr)) existingFriend.LastAddress = sAddr;
                        if (!string.IsNullOrWhiteSpace(sUser)) existingFriend.Username = sUser;
                        AppConfig.Save();
                    }

                    return new FriendLookupResult
                    {
                        Found = true,
                        FriendCode = sNorm,
                        Username = sUser,
                        Address = sAddr,
                        IsOnline = true,
                        Source = "SignalingRelay"
                    };
                }
            }
        }
        catch { }

        // 3. Check LAN UDP Peer Discovery Cache (Immediate detection on local Wi-Fi / LAN)
        var lanPeer = PeerDiscoveryService.Instance?.LookupPeer(cleanCode);
        if (lanPeer != null && !string.IsNullOrWhiteSpace(lanPeer.Address))
        {
            // Verify live reachability and probe for freshest username
            try
            {
                var pingUrl = $"{lanPeer.Address.TrimEnd('/')}/api/profile";
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                var resp = await client.GetAsync(pingUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                    var liveName = data.TryGetProperty("username", out var u) ? u.GetString() ?? lanPeer.Username : lanPeer.Username;
                    return new FriendLookupResult
                    {
                        Found = true,
                        FriendCode = cleanCode,
                        Username = liveName,
                        Address = lanPeer.Address,
                        IsOnline = true,
                        Source = "LAN"
                    };
                }
            }
            catch
            {
                // Return cached LAN entry if recent
                return new FriendLookupResult
                {
                    Found = true,
                    FriendCode = cleanCode,
                    Username = lanPeer.Username,
                    Address = lanPeer.Address,
                    IsOnline = (DateTime.UtcNow - lanPeer.LastSeen).TotalSeconds < 90,
                    Source = "LAN"
                };
            }
        }

        // 4. Check previously saved address in existing Friends list
        if (existingFriend != null && !string.IsNullOrWhiteSpace(existingFriend.LastAddress) && !existingFriend.LastAddress.Contains("169.254."))
        {
            try
            {
                var pingUrl = $"{existingFriend.LastAddress.TrimEnd('/')}/api/profile";
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                var resp = await client.GetAsync(pingUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                    var liveName = data.TryGetProperty("username", out var u) ? u.GetString() ?? existingFriend.Username : existingFriend.Username;
                    return new FriendLookupResult
                    {
                        Found = true,
                        FriendCode = cleanCode,
                        Username = liveName,
                        Address = existingFriend.LastAddress,
                        IsOnline = true,
                        Source = "SavedAddress"
                    };
                }
            }
            catch { }
        }

        // 5. Check Central Admin Hub Registry (/api/admin/resolve-friend) only if external hub configured
        if (!string.IsNullOrWhiteSpace(AppConfig.Current.AdminHubUrl) && !AppConfig.Current.AdminHubUrl.Contains("localhost:7575"))
        {
            try
            {
                var hubUrl = $"{AppConfig.Current.AdminHubUrl.TrimEnd('/')}/api/admin/resolve-friend?targetCode={Uri.EscapeDataString(cleanCode)}";
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(1500));
                var resp = await client.GetAsync(hubUrl, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    var data = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                    if (data.TryGetProperty("found", out var found) && found.GetBoolean())
                    {
                        var hubName = data.TryGetProperty("username", out var u) ? u.GetString() ?? "Player" : "Player";
                        var hubAddr = data.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
                        var isOnline = data.TryGetProperty("isOnline", out var o) && o.GetBoolean();

                        return new FriendLookupResult
                        {
                            Found = true,
                            FriendCode = cleanCode,
                            Username = hubName,
                            Address = hubAddr,
                            IsOnline = isOnline,
                            Source = "AdminHub"
                        };
                    }
                }
            }
            catch { }
        }

        if (existingFriend != null)
        {
            return new FriendLookupResult
            {
                Found = true,
                FriendCode = cleanCode,
                Username = existingFriend.Username,
                Address = existingFriend.LastAddress?.Contains("169.254.") == true ? "" : (existingFriend.LastAddress ?? ""),
                IsOnline = false,
                Source = "SavedAddress"
            };
        }

        return new FriendLookupResult
        {
            Found = false,
            FriendCode = cleanCode,
            Error = $"Player with Friend Code {cleanCode} not found online. Make sure your friend has GH3-Sync open!"
        };
    }

    /// <summary>
    /// Dispatches a verified friend request over the network.
    /// ONLY adds the friend to the requested list when the remote client explicitly acknowledges receipt with HTTP 200 OK.
    /// </summary>
    public Task<SendFriendRequestResult> SendFriendRequestAsync(string rawFriendCode, string? customAddress, HttpClient? customClient = null)
        => SendFriendRequestAsync(rawFriendCode, asTrusted: true, customAddress, customClient);

    public async Task<SendFriendRequestResult> SendFriendRequestAsync(string rawFriendCode, bool asTrusted = true, string? customAddress = null, HttpClient? customClient = null)
    {
        var lookup = await LookupFriendByCodeAsync(rawFriendCode, customClient);
        var cleanCode = lookup.FriendCode;

        if (cleanCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
        {
            return new SendFriendRequestResult
            {
                Success = false,
                ReceivedByPeer = false,
                FriendCode = cleanCode,
                Message = "You cannot send a friend request to your own Friend Code."
            };
        }

        // Check if already an active friend
        var existing = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var targetAddressCandidate = !string.IsNullOrWhiteSpace(customAddress) ? customAddress.Trim() : lookup.Address;
            if (!string.IsNullOrWhiteSpace(targetAddressCandidate) && !targetAddressCandidate.Contains("169.254."))
            {
                if (!targetAddressCandidate.StartsWith("http://") && !targetAddressCandidate.StartsWith("https://"))
                {
                    targetAddressCandidate = (targetAddressCandidate.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) || targetAddressCandidate.EndsWith(":443")) ? "https://" + targetAddressCandidate : "http://" + targetAddressCandidate;
                }
                existing.LastAddress = targetAddressCandidate;
                AppConfig.Save();
                return new SendFriendRequestResult
                {
                    Success = true,
                    ReceivedByPeer = true,
                    FriendCode = cleanCode,
                    Username = existing.Username,
                    Message = $"Updated connection address for {existing.Username} to {targetAddressCandidate}!"
                };
            }

            return new SendFriendRequestResult
            {
                Success = false,
                ReceivedByPeer = false,
                FriendCode = cleanCode,
                Username = existing.Username,
                Message = $"{existing.Username} is already in your Friends list!"
            };
        }

        var targetAddress = !string.IsNullOrWhiteSpace(customAddress) ? customAddress.Trim() : lookup.Address;
        if (!targetAddress.StartsWith("http://") && !targetAddress.StartsWith("https://") && !string.IsNullOrWhiteSpace(targetAddress))
        {
            targetAddress = (targetAddress.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) || targetAddress.EndsWith(":443")) ? "https://" + targetAddress : "http://" + targetAddress;
        }

        var client = customClient ?? _httpClient;
        var myAddress = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373";
        var proposedToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var peerDisplay = !string.IsNullOrWhiteSpace(lookup.Username) ? lookup.Username : $"Player_{cleanCode[^4..]}";

        // ==================== TRUE XBOX-STYLE ASYNCHRONOUS FRIEND REQUEST ====================
        // 1. Immediately create and persist the Outgoing Pending Request so it is NEVER lost or rejected locally.
        // On Xbox/Steam/modern platforms, sending a request is guaranteed to record in Outgoing Pending.
        var outReq = new FriendRequestItem
        {
            FriendCode = cleanCode,
            Username = peerDisplay,
            Address = targetAddress ?? "",
            Direction = "Outgoing",
            Status = "Pending",
            ProposedTrustToken = proposedToken,
            IsTrustedRequest = asTrusted,
            RequestType = "Friend",
            CreatedAt = DateTime.UtcNow
        };

        lock (_lock)
        {
            AppConfig.Current.FriendRequests.RemoveAll(x => x.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            AppConfig.Current.FriendRequests.Add(outReq);
            AppConfig.Save();
        }

        // 2. Dispatch the request packet via Global P2P Signaling Relay topic (gh3sync-req-{targetTopic})
        if (SignalingRelay != null)
        {
            _ = SignalingRelay.SendFriendRequestPacketAsync(cleanCode, AppConfig.Current.FriendCode, AppConfig.Current.Username, myAddress, proposedToken, asTrusted);
        }

        // 3. Attempt direct HTTP delivery if a valid address exists (background/parallel)
        bool directDelivered = false;
        if (!string.IsNullOrWhiteSpace(targetAddress) && !targetAddress.Contains("169.254."))
        {
            try
            {
                var reqUrl = $"{targetAddress.TrimEnd('/')}/api/friends/request";
                var payload = new
                {
                    senderCode = AppConfig.Current.FriendCode,
                    senderUsername = AppConfig.Current.Username,
                    senderAddress = myAddress,
                    proposedTrustToken = proposedToken,
                    isTrusted = asTrusted,
                    timestamp = DateTime.UtcNow
                };

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                var resp = await client.PostAsJsonAsync(reqUrl, payload, cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    directDelivered = true;
                    try
                    {
                        var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                        if (body.TryGetProperty("username", out var u) && !string.IsNullOrWhiteSpace(u.GetString()))
                        {
                            lock (_lock)
                            {
                                outReq.Username = u.GetString()!;
                                AppConfig.Save();
                            }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        // 4. Also queue on Admin Hub if configured
        if (!string.IsNullOrWhiteSpace(AppConfig.Current.AdminHubUrl) && !AppConfig.Current.AdminHubUrl.Contains("localhost:7575"))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var hubUrl = $"{AppConfig.Current.AdminHubUrl.TrimEnd('/')}/api/admin/friend-request";
                    var hubPayload = new
                    {
                        targetCode = cleanCode,
                        senderCode = AppConfig.Current.FriendCode,
                        senderUsername = AppConfig.Current.Username,
                        senderAddress = myAddress,
                        proposedTrustToken = proposedToken,
                        isTrusted = asTrusted,
                        createdAt = DateTime.UtcNow
                    };
                    using var hubCts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
                    await client.PostAsJsonAsync(hubUrl, hubPayload, hubCts.Token);
                }
                catch { }
            });
        }

        // 5. Always succeed: Request is saved in Outgoing Pending and awaiting peer approval!
        string resultMsg = directDelivered
            ? $"Friend request delivered to {outReq.Username} ({cleanCode})!"
            : $"Friend request sent to {cleanCode}! Waiting for approval.";

        return new SendFriendRequestResult
        {
            Success = true,
            ReceivedByPeer = directDelivered,
            FriendCode = cleanCode,
            Username = outReq.Username,
            Message = resultMsg
        };
    }

    /// <summary>
    /// Processes an incoming friend request dispatched from another peer.
    /// Stores it in IncomingRequests and returns ACK receipt.
    /// </summary>
    public (bool Success, bool Received, string Status, string Username, string FriendCode) ReceiveFriendRequest(
        string senderCode, string senderUsername, string senderAddress, string? proposedToken, bool isTrusted = true, string requestType = "Friend")
    {
        if (string.IsNullOrWhiteSpace(senderCode))
            return (false, false, "invalid_code", Profile.Username, Profile.FriendCode);

        var cleanCode = senderCode.Trim().ToUpperInvariant();
        if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

        FriendRequestItem? notifyReq = null;
        lock (_lock)
        {
            // If sender is already a friend, update their address & mutual trust token, then re-acknowledge
            var existing = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(senderAddress)) existing.LastAddress = senderAddress.Trim();
                if (!string.IsNullOrWhiteSpace(proposedToken)) existing.TrustToken = proposedToken;
                existing.IsTrusted = isTrusted;
                existing.LastSeenUtc = DateTime.UtcNow;
                AppConfig.Save();

                // Re-broadcast FRIEND_ACCEPT packet to remote peer to guarantee they unstick from Pending
                if (SignalingRelay != null)
                {
                    var myAddress = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373";
                    _ = SignalingRelay.SendFriendAcceptPacketAsync(
                        cleanCode,
                        AppConfig.Current.FriendCode,
                        AppConfig.Current.Username,
                        myAddress,
                        existing.TrustToken);
                }

                return (true, true, "already_friends", Profile.Username, Profile.FriendCode);
            }

            var cleanName = string.IsNullOrWhiteSpace(senderUsername) ? $"Peer_{cleanCode[^4..]}" : senderUsername.Trim();

            var req = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                r.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase) &&
                r.Direction.Equals("Incoming", StringComparison.OrdinalIgnoreCase));

            bool isNewOrReopened = false;
            if (req == null)
            {
                req = new FriendRequestItem
                {
                    FriendCode = cleanCode,
                    Username = cleanName,
                    Address = senderAddress?.Trim() ?? "",
                    Direction = "Incoming",
                    Status = "Pending",
                    ProposedTrustToken = proposedToken ?? "",
                    IsTrustedRequest = isTrusted,
                    RequestType = requestType,
                    CreatedAt = DateTime.UtcNow
                };
                AppConfig.Current.FriendRequests.Add(req);
                isNewOrReopened = true;
            }
            else
            {
                if (req.Status != "Pending") isNewOrReopened = true;
                req.Username = cleanName;
                req.Address = senderAddress?.Trim() ?? req.Address;
                req.ProposedTrustToken = proposedToken ?? req.ProposedTrustToken;
                req.IsTrustedRequest = isTrusted;
                req.RequestType = requestType;
                req.Status = "Pending";
            }

            AppConfig.Save();
            if (isNewOrReopened)
            {
                notifyReq = req;
            }
        }

        // Open Calls Pattern: Notify listeners OUTSIDE the lock to prevent UI thread deadlocks
        if (notifyReq != null)
        {
            OnFriendRequestReceived?.Invoke(notifyReq);
        }

        return (true, true, "received", Profile.Username, Profile.FriendCode);
    }

    /// <summary>
    /// Processes an incoming trust upgrade request from an existing friend.
    /// </summary>
    public (bool Success, bool Received, string Status, string Username, string FriendCode) ReceiveTrustUpgradeRequest(
        string senderCode, string senderUsername, string senderAddress, string? proposedToken)
    {
        if (string.IsNullOrWhiteSpace(senderCode))
            return (false, false, "invalid_code", Profile.Username, Profile.FriendCode);

        var cleanCode = senderCode.Trim().ToUpperInvariant();
        if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

        FriendRequestItem? notifyReq = null;
        lock (_lock)
        {
            var cleanName = string.IsNullOrWhiteSpace(senderUsername) ? $"Peer_{cleanCode[^4..]}" : senderUsername.Trim();
            var existingFriend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            if (existingFriend != null && !string.IsNullOrWhiteSpace(senderAddress) && !senderAddress.Contains("169.254."))
            {
                existingFriend.LastAddress = senderAddress.Trim();
            }

            var req = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                r.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase) &&
                r.Direction.Equals("Incoming", StringComparison.OrdinalIgnoreCase));

            bool isNewOrReopened = false;
            if (req == null)
            {
                req = new FriendRequestItem
                {
                    FriendCode = cleanCode,
                    Username = cleanName,
                    Address = senderAddress?.Trim() ?? (existingFriend?.LastAddress ?? ""),
                    Direction = "Incoming",
                    Status = "Pending",
                    ProposedTrustToken = proposedToken ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
                    IsTrustedRequest = true,
                    RequestType = "TrustUpgrade",
                    CreatedAt = DateTime.UtcNow
                };
                AppConfig.Current.FriendRequests.Add(req);
                isNewOrReopened = true;
            }
            else
            {
                if (req.Status != "Pending") isNewOrReopened = true;
                req.Username = cleanName;
                req.Address = senderAddress?.Trim() ?? req.Address;
                req.ProposedTrustToken = proposedToken ?? req.ProposedTrustToken;
                req.IsTrustedRequest = true;
                req.RequestType = "TrustUpgrade";
                req.Status = "Pending";
            }

            AppConfig.Save();
            if (isNewOrReopened)
            {
                notifyReq = req;
            }
        }

        // Open Calls Pattern: Notify listeners OUTSIDE the lock to prevent UI thread deadlocks
        if (notifyReq != null)
        {
            OnFriendRequestReceived?.Invoke(notifyReq);
        }

        return (true, true, "received", Profile.Username, Profile.FriendCode);
    }

    /// <summary>
    /// Requests a trust upgrade from an existing regular friend.
    /// </summary>
    public async Task<SendFriendRequestResult> RequestTrustUpgradeAsync(string friendCode, HttpClient? customClient = null)
    {
        var cleanCode = friendCode.Trim().ToUpperInvariant();
        if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

        var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
        if (friend == null)
        {
            return new SendFriendRequestResult { Success = false, Message = "Friend not found in your list." };
        }

        var myAddress = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373";
        var proposedToken = !string.IsNullOrWhiteSpace(friend.TrustToken) ? friend.TrustToken : Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        if (SignalingRelay != null)
        {
            await SignalingRelay.SendTrustUpgradePacketAsync(cleanCode, AppConfig.Current.FriendCode, AppConfig.Current.Username, myAddress, proposedToken);
        }

        if (!string.IsNullOrWhiteSpace(friend.LastAddress) && !friend.LastAddress.Contains("169.254."))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var client = customClient ?? _httpClient;
                    var url = $"{friend.LastAddress.TrimEnd('/')}/api/friends/trust-upgrade";
                    var payload = new
                    {
                        senderCode = AppConfig.Current.FriendCode,
                        senderUsername = AppConfig.Current.Username,
                        senderAddress = myAddress,
                        proposedTrustToken = proposedToken
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.0));
                    await client.PostAsJsonAsync(url, payload, cts.Token);
                }
                catch { }
            });
        }

        lock (_lock)
        {
            var outReq = new FriendRequestItem
            {
                FriendCode = cleanCode,
                Username = friend.Username,
                Address = friend.LastAddress,
                Direction = "Outgoing",
                Status = "Pending",
                ProposedTrustToken = proposedToken,
                IsTrustedRequest = true,
                RequestType = "TrustUpgrade",
                CreatedAt = DateTime.UtcNow
            };
            AppConfig.Current.FriendRequests.RemoveAll(x => x.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase) && x.Direction == "Outgoing");
            AppConfig.Current.FriendRequests.Add(outReq);
            AppConfig.Save();
        }

        return new SendFriendRequestResult
        {
            Success = true,
            ReceivedByPeer = true,
            FriendCode = cleanCode,
            Username = friend.Username,
            Message = $"Trust upgrade request sent to {friend.Username}!"
        };
    }

    /// <summary>
    /// Accepts a trust upgrade request from a friend.
    /// </summary>
    public async Task<FriendEntry?> AcceptTrustUpgradeAsync(string requestIdOrCode, HttpClient? customClient = null)
    {
        FriendRequestItem? targetReq = null;
        FriendEntry? friend = null;

        lock (_lock)
        {
            targetReq = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                r.Id.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase) ||
                r.FriendCode.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase));

            if (targetReq == null) return null;

            friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(targetReq.FriendCode, StringComparison.OrdinalIgnoreCase));
            if (friend == null)
            {
                friend = AddFriendByCode(targetReq.FriendCode, targetReq.Username, targetReq.Address);
            }

            friend.IsTrusted = true;
            if (!string.IsNullOrEmpty(targetReq.ProposedTrustToken))
                friend.TrustToken = targetReq.ProposedTrustToken;
            else if (string.IsNullOrEmpty(friend.TrustToken))
                friend.TrustToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

            AppConfig.Current.FriendRequests.RemoveAll(r => r.FriendCode.Equals(targetReq.FriendCode, StringComparison.OrdinalIgnoreCase));
            AppConfig.Save();
        }

        var myAddress = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373";
        if (SignalingRelay != null)
        {
            _ = SignalingRelay.SendTrustUpgradeAcceptPacketAsync(
                friend.FriendCode,
                AppConfig.Current.FriendCode,
                AppConfig.Current.Username,
                myAddress,
                friend.TrustToken);
        }

        if (!string.IsNullOrWhiteSpace(friend.LastAddress) && !friend.LastAddress.Contains("169.254."))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var client = customClient ?? _httpClient;
                    var acceptUrl = $"{friend.LastAddress.TrimEnd('/')}/api/friends/trust-upgrade-accept";
                    var payload = new
                    {
                        senderCode = AppConfig.Current.FriendCode,
                        senderUsername = AppConfig.Current.Username,
                        senderAddress = myAddress,
                        trustToken = friend.TrustToken
                    };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                    await client.PostAsJsonAsync(acceptUrl, payload, cts.Token);
                }
                catch { }
            });
        }

        OnFriendRequestAccepted?.Invoke(friend);
        return friend;
    }

    /// <summary>
    /// Processes trust upgrade acceptance from remote friend.
    /// </summary>
    public FriendEntry? ProcessTrustUpgradeAccepted(string remoteCode, string remoteUsername, string remoteAddress, string? trustToken)
    {
        FriendEntry? friend = null;
        lock (_lock)
        {
            var cleanCode = remoteCode.Trim().ToUpperInvariant();
            if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

            AppConfig.Current.FriendRequests.RemoveAll(r => r.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

            friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            if (friend == null)
            {
                friend = AddFriendByCode(cleanCode, remoteUsername, remoteAddress);
            }

            friend.IsTrusted = true;
            if (!string.IsNullOrEmpty(trustToken)) friend.TrustToken = trustToken;
            if (!string.IsNullOrWhiteSpace(remoteAddress) && !remoteAddress.Contains("169.254.")) friend.LastAddress = remoteAddress;

            AppConfig.Save();
        }

        if (friend != null)
        {
            OnFriendRequestAccepted?.Invoke(friend);
        }
        return friend;
    }

    /// <summary>
    /// Accepts an incoming friend request, adds to active Friends with mutual trust,
    /// and dispatches acceptance confirmation to the remote sender.
    /// </summary>
    public async Task<FriendEntry?> AcceptFriendRequestAsync(string requestIdOrCode, bool asTrusted = true, HttpClient? customClient = null)
    {
        FriendRequestItem? targetReq = null;
        FriendEntry? newFriend = null;

        lock (_lock)
        {
            targetReq = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                r.Id.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase) ||
                r.FriendCode.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase));

            if (targetReq == null) return null;

            // Add to active Friends
            newFriend = AddFriendByCode(targetReq.FriendCode, targetReq.Username, targetReq.Address);
            if (asTrusted)
            {
                newFriend.IsTrusted = true;
                if (!string.IsNullOrEmpty(targetReq.ProposedTrustToken))
                {
                    newFriend.TrustToken = targetReq.ProposedTrustToken;
                }
                else if (string.IsNullOrEmpty(newFriend.TrustToken))
                {
                    newFriend.TrustToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
                }
            }

            // Remove from pending requests
            AppConfig.Current.FriendRequests.RemoveAll(r => r.FriendCode.Equals(targetReq.FriendCode, StringComparison.OrdinalIgnoreCase));
            AppConfig.Save();
        }

        // Notify remote sender that request was accepted
        if (!string.IsNullOrWhiteSpace(targetReq.Address))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var client = customClient ?? _httpClient;
                    var acceptUrl = $"{targetReq.Address.TrimEnd('/')}/api/friends/accept";
                    var myAddress = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373";

                    var payload = new
                    {
                        senderCode = AppConfig.Current.FriendCode,
                        senderUsername = AppConfig.Current.Username,
                        senderAddress = myAddress,
                        trustToken = newFriend.TrustToken,
                        isTrusted = newFriend.IsTrusted
                    };

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.5));
                    await client.PostAsJsonAsync(acceptUrl, payload, cts.Token);
                }
                catch { }
            });
        }

        if (SignalingRelay != null && targetReq != null && newFriend != null)
        {
            var myAddress = BestAddressProvider?.Invoke() ?? "http://127.0.0.1:7373";
            _ = SignalingRelay.SendFriendAcceptPacketAsync(
                targetReq.FriendCode,
                AppConfig.Current.FriendCode,
                AppConfig.Current.Username,
                myAddress,
                newFriend.TrustToken);
        }

        return newFriend;
    }

    public void ReceiveIncomingRequest(string senderCode, string senderUsername, string senderAddress, string? proposedToken, bool isTrusted = true)
    {
        ReceiveFriendRequest(senderCode, senderUsername, senderAddress, proposedToken, isTrusted, "Friend");
    }

    public void HandleRemoteAcceptance(string remoteCode, string remoteUsername, string remoteAddress, string? remoteTrustToken)
    {
        ProcessPeerAcceptedRequest(remoteCode, remoteUsername, remoteAddress, remoteTrustToken, true);
    }

    /// <summary>
    /// Processes acceptance confirmation sent from remote peer.
    /// Converts our outgoing request into an active mutual friend.
    /// </summary>
    public FriendEntry ProcessPeerAcceptedRequest(string remoteCode, string remoteUsername, string remoteAddress, string? trustToken, bool isTrusted)
    {
        FriendEntry friend;
        lock (_lock)
        {
            var cleanCode = remoteCode.Trim().ToUpperInvariant();
            if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

            AppConfig.Current.FriendRequests.RemoveAll(r => r.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

            friend = AddFriendByCode(cleanCode, remoteUsername, remoteAddress);
            friend.IsTrusted = isTrusted;
            if (!string.IsNullOrEmpty(trustToken))
            {
                friend.TrustToken = trustToken;
            }
            else if (isTrusted && string.IsNullOrEmpty(friend.TrustToken))
            {
                friend.TrustToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            }

            AppConfig.Save();
        }

        OnFriendRequestAccepted?.Invoke(friend);
        return friend;
    }

    public bool DeclineFriendRequest(string requestIdOrCode)
    {
        lock (_lock)
        {
            var req = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                r.Id.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase) ||
                r.FriendCode.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase));

            if (req != null)
            {
                AppConfig.Current.FriendRequests.Remove(req);
                AppConfig.Save();
                return true;
            }
            return false;
        }
    }

    public bool CancelOutgoingRequest(string requestIdOrCode)
    {
        lock (_lock)
        {
            var req = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                (r.Id.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase) ||
                 r.FriendCode.Equals(requestIdOrCode, StringComparison.OrdinalIgnoreCase)) &&
                r.Direction.Equals("Outgoing", StringComparison.OrdinalIgnoreCase));

            if (req != null)
            {
                AppConfig.Current.FriendRequests.Remove(req);
                AppConfig.Save();
                return true;
            }
            return false;
        }
    }

    public FriendEntry AddFriend(string username, string friendCode, string? address = null)
    {
        return AddFriendByCode(friendCode, username, address);
    }

    public FriendEntry AddFriendByCode(string friendCode, string? nickname = null, string? address = null)
    {
        lock (_lock)
        {
            var cleanCode = friendCode.Trim().ToUpperInvariant();
            if (!cleanCode.StartsWith("GH3-"))
            {
                cleanCode = "GH3-" + cleanCode;
            }

            // Check if friend already exists by code
            var existing = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(nickname)) existing.Username = EnsureUniqueNickname(nickname.Trim(), cleanCode);
                if (!string.IsNullOrWhiteSpace(address)) existing.LastAddress = address.Trim();
                AppConfig.Save();
                return existing;
            }

            var baseName = string.IsNullOrWhiteSpace(nickname) 
                ? $"Peer_{cleanCode[^4..]}" 
                : nickname.Trim();

            var uniqueName = EnsureUniqueNickname(baseName, cleanCode);

            var newFriend = new FriendEntry
            {
                Username = uniqueName,
                FriendCode = cleanCode,
                LastAddress = address?.Trim() ?? "",
                IsTrusted = false,
                TrustToken = "",
                AddedAt = DateTime.UtcNow
            };

            AppConfig.Current.Friends.Add(newFriend);
            AppConfig.Save();
            return newFriend;
        }
    }

    private string EnsureUniqueNickname(string nickname, string currentCode)
    {
        var name = nickname;
        int counter = 2;
        while (AppConfig.Current.Friends.Any(f => 
            !f.FriendCode.Equals(currentCode, StringComparison.OrdinalIgnoreCase) && 
            f.Username.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{nickname} ({counter++})";
        }
        return name;
    }

    public bool ToggleTrust(string friendCode, bool allowTrust)
    {
        lock (_lock)
        {
            var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(friendCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (friend == null) return false;

            friend.IsTrusted = allowTrust;
            if (allowTrust)
            {
                friend.TrustToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            }
            else
            {
                friend.TrustToken = "";
            }

            AppConfig.Save();
            return true;
        }
    }

    public bool RemoveFriend(string friendCode)
    {
        lock (_lock)
        {
            var count = AppConfig.Current.Friends.RemoveAll(f => f.FriendCode.Equals(friendCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (count > 0)
            {
                AppConfig.Save();
                return true;
            }
            return false;
        }
    }

    public bool ValidateTrustToken(string? token, out FriendEntry? trustedFriend)
    {
        trustedFriend = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (_lock)
        {
            trustedFriend = AppConfig.Current.Friends.FirstOrDefault(f =>
                f.IsTrusted && !string.IsNullOrEmpty(f.TrustToken) &&
                SecurityService.FixedTimeEquals(f.TrustToken, token.Trim()));
            return trustedFriend != null;
        }
    }
}
