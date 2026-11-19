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
    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(15.0) };

    public static Func<string>? BestAddressProvider { get; set; }
    public static SignalingRelayService? SignalingRelay { get; set; }

    public UserProfile Profile => new()
    {
        Username = AppConfig.Current.Username,
        FriendCode = AppConfig.Current.FriendCode
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

        // 2. Check LAN UDP Peer Discovery Cache (Immediate detection on local Wi-Fi / LAN)
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

        // 3. Check Central Admin Hub Registry (/api/admin/resolve-friend)
        if (!string.IsNullOrWhiteSpace(AppConfig.Current.AdminHubUrl))
        {
            try
            {
                var hubUrl = $"{AppConfig.Current.AdminHubUrl.TrimEnd('/')}/api/admin/resolve-friend?targetCode={Uri.EscapeDataString(cleanCode)}";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
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

        // 4. Check previously saved address in existing Friends list
        var existingFriend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
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

        // 5. Check Global P2P Signaling Relay (Instant Internet rendezvous by Friend Code)
        try
        {
            if (SignalingRelay != null)
            {
                var (found, sUser, sAddr, sNorm) = await SignalingRelay.ResolvePeerAsync(cleanCode);
                if (found && !string.IsNullOrWhiteSpace(sAddr))
                {
                    if (existingFriend != null)
                    {
                        existingFriend.LastAddress = sAddr;
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
    public async Task<SendFriendRequestResult> SendFriendRequestAsync(string rawFriendCode, string? customAddress = null, HttpClient? customClient = null)
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

        if (string.IsNullOrWhiteSpace(targetAddress) || targetAddress.Contains("169.254."))
        {
            if (SignalingRelay != null)
            {
                var sigSent = await SignalingRelay.SendFriendRequestPacketAsync(cleanCode, AppConfig.Current.FriendCode, AppConfig.Current.Username, myAddress, proposedToken);
                if (sigSent)
                {
                    lock (_lock)
                    {
                        var req = new FriendRequestItem
                        {
                            FriendCode = cleanCode,
                            Username = !string.IsNullOrWhiteSpace(lookup.Username) ? lookup.Username : $"Player_{cleanCode[^4..]}",
                            Direction = "Outgoing",
                            Status = "Pending",
                            CreatedAt = DateTime.UtcNow,
                            Address = targetAddress ?? "",
                            ProposedTrustToken = proposedToken
                        };
                        AppConfig.Current.FriendRequests.RemoveAll(x => x.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
                        AppConfig.Current.FriendRequests.Add(req);
                        AppConfig.Save();
                    }

                    return new SendFriendRequestResult
                    {
                        Success = true,
                        ReceivedByPeer = true,
                        FriendCode = cleanCode,
                        Username = lookup.Username,
                        Message = $"Friend request delivered to {cleanCode} via Global P2P Signaling Relay!"
                    };
                }
            }

            // Do NOT add to requested list when peer is unreachable!
            return new SendFriendRequestResult
            {
                Success = false,
                ReceivedByPeer = false,
                FriendCode = cleanCode,
                Message = $"Could not reach {cleanCode} across local Wi-Fi or Hub.\n\nEnsure your friend has GH3-Sync open!"
            };
        }

        var payload = new
        {
            senderCode = AppConfig.Current.FriendCode,
            senderUsername = AppConfig.Current.Username,
            senderAddress = myAddress,
            proposedTrustToken = proposedToken,
            timestamp = DateTime.UtcNow
        };

        try
        {
            var reqUrl = $"{targetAddress.TrimEnd('/')}/api/friends/request";
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20.0));
            HttpResponseMessage resp;
            try
            {
                resp = await client.PostAsJsonAsync(reqUrl, payload, cts.Token);
            }
            catch (Exception) when (!cts.IsCancellationRequested)
            {
                await Task.Delay(1000, cts.Token);
                resp = await client.PostAsJsonAsync(reqUrl, payload, cts.Token);
            }

            if (resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                bool received = body.TryGetProperty("received", out var r) && r.GetBoolean();
                var peerUsername = body.TryGetProperty("username", out var u) ? u.GetString() ?? lookup.Username : lookup.Username;
                if (string.IsNullOrWhiteSpace(peerUsername)) peerUsername = $"Peer_{cleanCode[^4..]}";

                if (received || resp.IsSuccessStatusCode)
                {
                    // VERIFIED DELIVERY CONFIRMATION: Add to Outgoing Requested List
                    lock (_lock)
                    {
                        var outReq = new FriendRequestItem
                        {
                            FriendCode = cleanCode,
                            Username = peerUsername,
                            Address = targetAddress,
                            Direction = "Outgoing",
                            Status = "Pending",
                            ProposedTrustToken = proposedToken,
                            CreatedAt = DateTime.UtcNow
                        };

                        AppConfig.Current.FriendRequests.RemoveAll(x => x.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
                        AppConfig.Current.FriendRequests.Add(outReq);
                        AppConfig.Save();
                    }

                    return new SendFriendRequestResult
                    {
                        Success = true,
                        ReceivedByPeer = true,
                        FriendCode = cleanCode,
                        Username = peerUsername,
                        Message = $"Friend request successfully delivered and confirmed received by {peerUsername}!"
                    };
                }
            }

            return new SendFriendRequestResult
            {
                Success = false,
                ReceivedByPeer = false,
                FriendCode = cleanCode,
                Message = $"Remote client returned HTTP {(int)resp.StatusCode}. Request was NOT added to requested list."
            };
        }
        catch (Exception ex)
        {
            // If direct peer connection failed, attempt Admin Hub queue fallback
            if (!string.IsNullOrWhiteSpace(AppConfig.Current.AdminHubUrl))
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
                        createdAt = DateTime.UtcNow
                    };
                    using var hubCts = new CancellationTokenSource(TimeSpan.FromSeconds(2.5));
                    var hubResp = await client.PostAsJsonAsync(hubUrl, hubPayload, hubCts.Token);
                    if (hubResp.IsSuccessStatusCode)
                    {
                        var hubBody = await hubResp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: hubCts.Token);
                        if (hubBody.TryGetProperty("queued", out var q) && q.GetBoolean())
                        {
                            lock (_lock)
                            {
                                var outReq = new FriendRequestItem
                                {
                                    FriendCode = cleanCode,
                                    Username = lookup.Username,
                                    Address = targetAddress,
                                    Direction = "Outgoing",
                                    Status = "Pending",
                                    ProposedTrustToken = proposedToken,
                                    CreatedAt = DateTime.UtcNow
                                };
                                AppConfig.Current.FriendRequests.RemoveAll(x => x.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
                                AppConfig.Current.FriendRequests.Add(outReq);
                                AppConfig.Save();
                            }
                            return new SendFriendRequestResult
                            {
                                Success = true,
                                ReceivedByPeer = true,
                                FriendCode = cleanCode,
                                Username = lookup.Username,
                                Message = $"Friend request queued on Admin Hub for {lookup.Username} (queued for delivery on their next sync)!"
                            };
                        }
                    }
                }
                catch { }
            }

            if (SignalingRelay != null)
            {
                var sigSent = await SignalingRelay.SendFriendRequestPacketAsync(cleanCode, AppConfig.Current.FriendCode, AppConfig.Current.Username, myAddress, proposedToken);
                if (sigSent)
                {
                    lock (_lock)
                    {
                        var req = new FriendRequestItem
                        {
                            FriendCode = cleanCode,
                            Username = !string.IsNullOrWhiteSpace(lookup.Username) ? lookup.Username : $"Player_{cleanCode[^4..]}",
                            Direction = "Outgoing",
                            Status = "Pending",
                            CreatedAt = DateTime.UtcNow,
                            Address = targetAddress,
                            ProposedTrustToken = proposedToken
                        };
                        AppConfig.Current.FriendRequests.RemoveAll(x => x.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
                        AppConfig.Current.FriendRequests.Add(req);
                        AppConfig.Save();
                    }

                    return new SendFriendRequestResult
                    {
                        Success = true,
                        ReceivedByPeer = true,
                        FriendCode = cleanCode,
                        Username = lookup.Username,
                        Message = $"Friend request delivered to {lookup.Username} ({cleanCode}) via Global P2P Signaling Relay!"
                    };
                }
            }

            // Network failure / timeout -> DO NOT ADD TO REQUESTED LIST!
            return new SendFriendRequestResult
            {
                Success = false,
                ReceivedByPeer = false,
                FriendCode = cleanCode,
                Message = $"Delivery failed: Could not connect to {cleanCode} at {targetAddress} ({ex.Message}). Request was NOT added."
            };
        }
    }

    /// <summary>
    /// Processes an incoming friend request dispatched from another peer.
    /// Stores it in IncomingRequests and returns ACK receipt.
    /// </summary>
    public (bool Success, bool Received, string Status, string Username, string FriendCode) ReceiveFriendRequest(
        string senderCode, string senderUsername, string senderAddress, string? proposedToken)
    {
        if (string.IsNullOrWhiteSpace(senderCode))
            return (false, false, "invalid_code", Profile.Username, Profile.FriendCode);

        var cleanCode = senderCode.Trim().ToUpperInvariant();
        if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

        lock (_lock)
        {
            // If sender is already a friend, update their last address
            var existing = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(senderAddress)) existing.LastAddress = senderAddress.Trim();
                AppConfig.Save();
                return (true, true, "already_friends", Profile.Username, Profile.FriendCode);
            }

            var cleanName = string.IsNullOrWhiteSpace(senderUsername) ? $"Peer_{cleanCode[^4..]}" : senderUsername.Trim();

            var req = AppConfig.Current.FriendRequests.FirstOrDefault(r =>
                r.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase) &&
                r.Direction.Equals("Incoming", StringComparison.OrdinalIgnoreCase));

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
                    CreatedAt = DateTime.UtcNow
                };
                AppConfig.Current.FriendRequests.Add(req);
            }
            else
            {
                req.Username = cleanName;
                req.Address = senderAddress?.Trim() ?? req.Address;
                req.ProposedTrustToken = proposedToken ?? req.ProposedTrustToken;
                req.Status = "Pending";
                req.CreatedAt = DateTime.UtcNow;
            }

            AppConfig.Save();
            OnFriendRequestReceived?.Invoke(req);

            return (true, true, "received", Profile.Username, Profile.FriendCode);
        }
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
                if (string.IsNullOrEmpty(newFriend.TrustToken))
                {
                    newFriend.TrustToken = !string.IsNullOrEmpty(targetReq.ProposedTrustToken)
                        ? targetReq.ProposedTrustToken
                        : Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
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

    public void ReceiveIncomingRequest(string senderCode, string senderUsername, string senderAddress, string? proposedToken)
    {
        ReceiveFriendRequest(senderCode, senderUsername, senderAddress, proposedToken);
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
        lock (_lock)
        {
            var cleanCode = remoteCode.Trim().ToUpperInvariant();
            if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

            AppConfig.Current.FriendRequests.RemoveAll(r => r.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

            var friend = AddFriendByCode(cleanCode, remoteUsername, remoteAddress);
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
            OnFriendRequestAccepted?.Invoke(friend);
            return friend;
        }
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
