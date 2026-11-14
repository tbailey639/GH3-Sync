using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.IO.Compression;
using System.Diagnostics;
using GH3_Sync;

var baseDir = AppDomain.CurrentDomain.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = baseDir,
    WebRootPath = Path.Combine(baseDir, "wwwroot")
});

// Configure port
var port = 7373;
foreach (var arg in args)
{
    if (arg.StartsWith("--port=") && int.TryParse(arg.Substring(7), out var p)) port = p;
}

builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, port);
    options.Limits.MaxRequestBodySize = 500 * 1024 * 1024; // 500 MB max for song uploads
});

// Services
var gh3Service = new Gh3Service();
var syncLogger = new SyncLogger(gh3Service.GamePath);
var securityService = new SecurityService();
var friendService = new FriendService(baseDir);
var creatorService = new SongCreatorService();
var saveManager = new SaveManager(syncLogger);

builder.Services.AddSingleton(gh3Service);
builder.Services.AddSingleton(syncLogger);
builder.Services.AddSingleton(securityService);
builder.Services.AddSingleton(friendService);
builder.Services.AddSingleton(creatorService);
builder.Services.AddSingleton(saveManager);
builder.Services.AddHttpClient();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

// Start In-Game Overlay
var overlay = new InGameOverlay(syncLogger);
overlay.Start();
syncLogger.LogInfo("In-game HUD overlay service started.");
syncLogger.LogInfo($"[SECURITY] Session Pairing PIN: {securityService.PairingPin}");
syncLogger.LogInfo($"[PROFILE] Active user: {friendService.Profile.Username} ({friendService.Profile.FriendCode})");

// Start LAN Peer Discovery Beacon for Trusted Friends
var peerDiscovery = new PeerDiscoveryService(port, syncLogger);
peerDiscovery.Start();
syncLogger.LogInfo("Peer LAN discovery beacon started.");

var songs = gh3Service.GetInstalledCustomSongs();
syncLogger.SetStatus(SyncState.Ready, "GH3-Sync Loaded", $"{songs.Count} Customs Active");

string globalBroadcastNotice = "";

string GetLocalIpAddress()
{
    try
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0);
        socket.Connect("8.8.8.8", 65530);
        if (socket.LocalEndPoint is IPEndPoint endPoint)
        {
            return endPoint.Address.ToString();
        }
    }
    catch { }
    return "127.0.0.1";
}

bool IsAuthorized(HttpContext context, SecurityService sec, FriendService friends, SyncLogger logger)
{
    var remoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    // 1. Local loopback is always allowed for web dashboard
    if (remoteIp == "127.0.0.1" || remoteIp == "::1") return true;

    // 2. Check for Trusted Friend Token (Skips PIN & IP restrictions!)
    string? token = null;
    if (context.Request.Headers.TryGetValue("X-GH3-Trust-Token", out var headerToken))
        token = headerToken.ToString();
    else if (context.Request.Query.TryGetValue("token", out var queryToken))
        token = queryToken.ToString();

    if (!string.IsNullOrEmpty(token) && friends.ValidateTrustToken(token, out var trustedFriend))
    {
        logger.LogInfo($"[SECURITY] Trusted Friend access granted to: {trustedFriend?.Username} from {remoteIp}");
        return true;
    }

    // 3. Fallback: Check Pairing PIN
    string? pin = null;
    if (context.Request.Headers.TryGetValue("X-GH3-PIN", out var headerPin))
        pin = headerPin.ToString();
    else if (context.Request.Query.TryGetValue("pin", out var queryPin))
        pin = queryPin.ToString();

    return sec.ValidatePin(pin, remoteIp, logger);
}

// Background Heartbeat to Admin Hub (Safe Telemetry & Broadcasts)
_ = Task.Run(async () =>
{
    using var scope = app.Services.CreateScope();
    var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();
    var client = clientFactory.CreateClient();
    client.Timeout = TimeSpan.FromSeconds(4);

    while (true)
    {
        try
        {
            var compat = gh3Service.VerifyGamePath(gh3Service.GamePath);
            var payload = new
            {
                friendCode = AppConfig.Current.FriendCode,
                username = AppConfig.Current.Username,
                isBetterGh3 = compat.IsBetterGh3,
                appVersion = "1.2.0",
                customsCount = gh3Service.GetInstalledCustomSongs().Count,
                localPort = port,
                localIp = GetLocalIpAddress(),
                usage = AppConfig.Current.Usage,
                lastSeen = DateTime.UtcNow
            };

            var hubUrl = $"{AppConfig.Current.AdminHubUrl.TrimEnd('/')}/api/admin/heartbeat";
            var resp = await client.PostAsJsonAsync(hubUrl, payload);
            if (resp.IsSuccessStatusCode)
            {
                var reply = await resp.Content.ReadFromJsonAsync<JsonElement>();
                if (reply.TryGetProperty("broadcast", out var b))
                {
                    globalBroadcastNotice = b.GetString() ?? "";
                }
            }
        }
        catch { }
        await Task.Delay(15000);
    }
});

// ==================== SYSTEM & GAME STATUS ====================
app.MapGet("/api/status", (Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends, SaveManager saves) =>
{
    var localIp = GetLocalIpAddress();
    var customs = gh3.GetInstalledCustomSongs();
    var compat = gh3.VerifyGamePath(gh3.GamePath);
    return Results.Ok(new
    {
        isDetected = gh3.IsDetected,
        gamePath = gh3.GamePath,
        compatibility = compat,
        directInstall = AppConfig.Current.DirectInstallToGame,
        serverUrl = $"http://{localIp}:{port}?pin={sec.PairingPin}",
        rawAddress = $"http://{localIp}:{port}",
        pairingPin = sec.PairingPin,
        profile = friends.Profile,
        friendsCount = friends.Friends.Count,
        hasSaves = saves.HasSaves,
        usage = AppConfig.Current.Usage,
        globalBroadcast = globalBroadcastNotice,
        port,
        songs = customs,
        currentStatus = logger.CurrentStatus
    });
});

// ==================== GAME PATH & COMPATIBILITY APIs ====================
app.MapGet("/api/gamepath/verify", (string? path, Gh3Service gh3) =>
{
    var targetPath = string.IsNullOrWhiteSpace(path) ? gh3.GamePath : path.Trim();
    return Results.Ok(gh3.VerifyGamePath(targetPath));
});

app.MapPost("/api/gamepath/save", async (HttpContext context, Gh3Service gh3, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    if (req.TryGetProperty("gamePath", out var p))
    {
        var newPath = p.GetString()?.Trim() ?? "";
        var report = gh3.VerifyGamePath(newPath);
        if (report.IsValid)
        {
            AppConfig.UpdateGamePath(newPath);
            logger.LogInfo($"[SETTINGS] Game directory updated to: {newPath}");
            return Results.Ok(new { success = true, report });
        }
        return Results.BadRequest(new { error = report.StatusMessage });
    }
    return Results.BadRequest(new { error = "gamePath is required." });
});

app.MapPost("/api/settings/direct-install", async (HttpContext context, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    if (req.TryGetProperty("directInstall", out var d))
    {
        var direct = d.GetBoolean();
        AppConfig.SetDirectInstall(direct);
        logger.LogInfo($"[SETTINGS] Direct install to game set to: {direct}");
        return Results.Ok(new { success = true, directInstall = direct });
    }
    return Results.BadRequest(new { error = "directInstall boolean required." });
});

// ==================== CAREER SAVE PROTECTION APIs ====================
app.MapGet("/api/saves/status", (SaveManager saves) =>
{
    return Results.Ok(new
    {
        hasSaves = saves.HasSaves,
        saveFolder = SaveManager.SaveFolder,
        backups = saves.GetBackups()
    });
});

app.MapPost("/api/saves/backup", (SaveManager saves, SyncLogger logger) =>
{
    logger.LogInfo("[SAVES] Triggering manual save backup via UI");
    var backup = saves.BackupCurrentSaves("User manual backup from GH3-Sync dashboard");
    AppConfig.IncrementUsage("backup");
    return Results.Ok(backup);
});

app.MapPost("/api/saves/restore", (SaveManager saves, SyncLogger logger) =>
{
    logger.LogInfo("[SAVES] Restoring latest career save backup via UI");
    var success = saves.RestoreLatestBackup();
    AppConfig.IncrementUsage("restore");
    return Results.Ok(new { success });
});

// ==================== FRIENDS & PROFILE APIs ====================
app.MapGet("/api/profile", (FriendService friends) => Results.Ok(friends.Profile));

app.MapPost("/api/profile", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    if (req.TryGetProperty("username", out var u))
    {
        var newName = u.GetString()?.Trim() ?? "";
        if (!string.IsNullOrWhiteSpace(newName))
        {
            friends.SaveProfile(newName);
            logger.LogInfo($"[PROFILE] Username updated to '{newName}'");
        }
    }
    return Results.Ok(friends.Profile);
});

app.MapGet("/api/friends", (FriendService friends) => Results.Ok(friends.Friends));

app.MapPost("/api/friends/add", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    
    var code = req.GetProperty("friendCode").GetString() ?? "";
    var nickname = req.TryGetProperty("username", out var u) ? u.GetString() : null;
    var address = req.TryGetProperty("address", out var a) ? a.GetString() : null;

    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { error = "Friend Code (GH3-XXXX-XXXX) is required." });

    var friend = friends.AddFriendByCode(code, nickname, address);
    AppConfig.IncrementUsage("friend");
    logger.LogInfo($"[FRIENDS] Added/updated friend: {friend.Username} ({friend.FriendCode})");
    return Results.Ok(friend);
});

app.MapPost("/api/friends/update-address", async (HttpContext context, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var code = req.GetProperty("friendCode").GetString() ?? "";
    var address = req.GetProperty("address").GetString() ?? "";

    AppConfig.UpdateFriendAddress(code, address);
    logger.LogInfo($"[FRIENDS] Updated address for {code} to: {address}");
    return Results.Ok(new { success = true, friendCode = code, address });
});

app.MapGet("/api/friends/auto-resolve", async (string friendCode, IHttpClientFactory clientFactory, SyncLogger logger) =>
{
    var cleanCode = friendCode?.Trim() ?? "";
    var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));

    if (friend == null)
        return Results.NotFound(new { error = "Friend not found in your friends list." });

    // STRICT SECURITY POLICY: Automatic IP sharing is restricted to Trusted Friends only!
    if (!friend.IsTrusted)
    {
        return Results.BadRequest(new { 
            error = "Security Restriction: Automatic IP sharing is only permitted for Trusted Friends. Regular friends must exchange IP addresses manually for privacy and security." 
        });
    }

    // Special test loopback: If testing on same PC with local friend code
    if (cleanCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase))
    {
        var localSelf = $"http://127.0.0.1:{port}";
        friend.LastAddress = localSelf;
        AppConfig.Save();
        return Results.Ok(new { success = true, address = localSelf, username = friend.Username, source = "LoopbackTest", isOnline = true });
    }

    // 1. Query Central Admin Hub for latest live IP first
    try
    {
        var client = clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(2);
        var hubUrl = $"{AppConfig.Current.AdminHubUrl.TrimEnd('/')}/api/admin/resolve-friend?targetCode={Uri.EscapeDataString(cleanCode)}";
        var resp = await client.GetAsync(hubUrl);
        if (resp.IsSuccessStatusCode)
        {
            var data = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (data.TryGetProperty("found", out var found) && found.GetBoolean())
            {
                var addr = data.GetProperty("address").GetString() ?? "";
                if (!string.IsNullOrEmpty(addr))
                {
                    friend.LastAddress = addr;
                    AppConfig.Save();
                    logger.LogInfo($"[AUTO-RESOLVE] Automatically resolved live IP for Trusted Friend '{friend.Username}' via Hub -> {addr}");
                    return Results.Ok(new { success = true, address = addr, username = friend.Username, source = "AdminHub", isOnline = true });
                }
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogDebug($"[AUTO-RESOLVE] Hub lookup note: {ex.Message}");
    }

    // 2. If LAN discovery or prior session recorded a valid address, return it
    if (!string.IsNullOrEmpty(friend.LastAddress))
    {
        return Results.Ok(new { success = true, address = friend.LastAddress, username = friend.Username, source = "Saved/LAN", isOnline = true });
    }

    return Results.Ok(new { success = false, error = "Friend is currently offline or unreachable. When they open GH3-Sync, their address will automatically link here." });
});

app.MapPost("/api/friends/toggle-trust", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var code = req.GetProperty("friendCode").GetString() ?? "";
    var allow = req.GetProperty("allowTrust").GetBoolean();

    friends.ToggleTrust(code, allow);
    logger.LogInfo($"[FRIENDS] Trust status for {code} toggled to: {allow}");
    return Results.Ok(new { success = true });
});

app.MapPost("/api/friends/remove", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var code = req.GetProperty("friendCode").GetString() ?? "";

    friends.RemoveFriend(code);
    logger.LogInfo($"[FRIENDS] Removed friend: {code}");
    return Results.Ok(new { success = true });
});

// ==================== FEEDBACK / "NOT WORKING" APIs ====================
app.MapPost("/api/feedback", async (HttpContext context, IHttpClientFactory clientFactory, Gh3Service gh3, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);

    var page = req.TryGetProperty("page", out var p) ? p.GetString() : "General";
    var category = req.TryGetProperty("category", out var c) ? c.GetString() : "Not Working";
    var message = req.TryGetProperty("message", out var m) ? m.GetString() : "";

    var compat = gh3.VerifyGamePath(gh3.GamePath);

    var item = new FeedbackItem
    {
        Page = page ?? "General",
        Category = category ?? "Not Working",
        Message = message ?? "",
        UserFriendCode = AppConfig.Current.FriendCode,
        Username = AppConfig.Current.Username,
        IsBetterGh3 = compat.IsBetterGh3,
        CreatedAt = DateTime.UtcNow,
        Status = "Open"
    };

    AppConfig.SaveFeedback(item);
    logger.LogInfo($"[FEEDBACK] Recorded user report on [{item.Page}]: {item.Category}");

    // Asynchronously forward to Admin Hub if available
    _ = Task.Run(async () =>
    {
        try
        {
            var client = clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(3);
            var hubUrl = $"{AppConfig.Current.AdminHubUrl.TrimEnd('/')}/api/admin/feedback/submit";
            await client.PostAsJsonAsync(hubUrl, item);
        }
        catch { }
    });

    return Results.Ok(new { success = true, feedbackId = item.Id });
});

app.MapGet("/api/feedback", () => Results.Ok(AppConfig.GetFeedback()));

// ==================== P2P SYNC & MANIFEST APIs ====================
app.MapGet("/api/manifest", (HttpContext context, Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends) =>
{
    if (!IsAuthorized(context, sec, friends, logger))
        return Results.Unauthorized();

    logger.LogDebug("Serving authenticated manifest request to peer.");
    return Results.Ok(gh3.GenerateManifest());
});

app.MapGet("/api/download/{songId}", (HttpContext context, string songId, Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends) =>
{
    if (!IsAuthorized(context, sec, friends, logger))
        return Results.Unauthorized();

    try
    {
        logger.LogInfo($"Serving encrypted song package for: {songId}");
        var packageBytes = gh3.PackageSong(songId);
        var encryptedBytes = sec.EncryptPayload(packageBytes);
        return Results.File(encryptedBytes, "application/octet-stream", $"{songId}.gh3enc");
    }
    catch (FileNotFoundException ex)
    {
        logger.LogWarn($"Song {songId} requested but not found: {ex.Message}");
        return Results.NotFound(new { error = $"Song {songId} not found." });
    }
    catch (Exception ex)
    {
        logger.LogError($"Error packaging song {songId}", ex);
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/download-customs-pak", (HttpContext context, Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends) =>
{
    if (!IsAuthorized(context, sec, friends, logger))
        return Results.Unauthorized();

    if (!File.Exists(gh3.CustomsPakPath))
        return Results.NotFound(new { error = "customs.pak.xen not found." });

    logger.LogInfo("Serving encrypted customs.pak.xen to peer.");
    var rawBytes = File.ReadAllBytes(gh3.CustomsPakPath);
    var encryptedBytes = sec.EncryptPayload(rawBytes);
    return Results.File(encryptedBytes, "application/octet-stream", "customs.pak.xen.gh3enc");
});

app.MapPost("/api/peer/compare", async (HttpContext context, IHttpClientFactory clientFactory, Gh3Service gh3, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);

    if (!req.TryGetProperty("remoteUrl", out var remoteUrlProp))
        return Results.BadRequest(new { error = "remoteUrl is required." });

    var inputUrl = remoteUrlProp.GetString()?.Trim();
    if (string.IsNullOrEmpty(inputUrl))
        return Results.BadRequest(new { error = "remoteUrl cannot be empty." });

    string? pin = null;
    string? token = null;
    var uri = new Uri(inputUrl);
    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    if (!string.IsNullOrEmpty(query["pin"])) pin = query["pin"];
    if (!string.IsNullOrEmpty(query["token"])) token = query["token"];

    var baseUrl = $"{uri.Scheme}://{uri.Authority}";

    try
    {
        logger.LogInfo($"Initiating library comparison with peer at: {baseUrl}");
        var client = clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(8);
        if (!string.IsNullOrEmpty(pin)) client.DefaultRequestHeaders.Add("X-GH3-PIN", pin);
        if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Add("X-GH3-Trust-Token", token);

        var manifestUrl = $"{baseUrl}/api/manifest" + 
            (!string.IsNullOrEmpty(token) ? $"?token={token}" : (!string.IsNullOrEmpty(pin) ? $"?pin={pin}" : ""));
        var remoteManifest = await client.GetFromJsonAsync<LibraryManifest>(manifestUrl);

        if (remoteManifest == null)
            return Results.BadRequest(new { error = "Remote peer returned empty manifest." });

        var myManifest = gh3.GenerateManifest();
        var diff = new LibraryDiff();

        var localSongs = myManifest.Songs;
        var localMap = localSongs.ToDictionary(s => s.SongId, StringComparer.OrdinalIgnoreCase);

        foreach (var remoteSong in remoteManifest.Songs)
        {
            // Fingerprint / Title+Artist duplicate checks
            var matchByFingerprint = !string.IsNullOrEmpty(remoteSong.OnogamiFingerprint)
                ? localSongs.FirstOrDefault(s => s.OnogamiFingerprint == remoteSong.OnogamiFingerprint)
                : null;
            var matchByTitleArtist = localSongs.FirstOrDefault(s =>
                s.Title.Equals(remoteSong.Title, StringComparison.OrdinalIgnoreCase) &&
                s.Artist.Equals(remoteSong.Artist, StringComparison.OrdinalIgnoreCase));

            if (localMap.TryGetValue(remoteSong.SongId, out var localSong))
            {
                if (localSong.PakHash == remoteSong.PakHash)
                {
                    diff.InSync.Add(localSong);
                }
                else
                {
                    remoteSong.IsDuplicate = true;
                    remoteSong.DuplicateReason = $"Duplicate ID detected: Already installed locally ({localSong.SongId})";
                    diff.MissingLocally.Add(remoteSong);
                }
            }
            else if (matchByFingerprint != null)
            {
                remoteSong.IsDuplicate = true;
                remoteSong.DuplicateReason = $"ONOGAMI Fingerprint match: Already owned as '{matchByFingerprint.Title}'";
                diff.MissingLocally.Add(remoteSong);
            }
            else if (matchByTitleArtist != null)
            {
                remoteSong.IsDuplicate = true;
                remoteSong.DuplicateReason = $"Duplicate song match: '{matchByTitleArtist.Title}' by '{matchByTitleArtist.Artist}' already installed";
                diff.MissingLocally.Add(remoteSong);
            }
            else
            {
                diff.MissingLocally.Add(remoteSong);
            }
        }

        foreach (var localSong in myManifest.Songs)
        {
            if (!remoteManifest.Songs.Any(s => s.SongId.Equals(localSong.SongId, StringComparison.OrdinalIgnoreCase)))
                diff.MissingOnRemote.Add(localSong);
        }

        diff.CustomsPakMatches = (myManifest.CustomsPakHash == remoteManifest.CustomsPakHash);
        logger.LogInfo($"Comparison completed: {diff.InSync.Count} in sync, {diff.MissingLocally.Count} missing locally ({diff.MissingLocally.Count(s => s.IsDuplicate)} duplicate/owned), {diff.MissingOnRemote.Count} missing on remote.");

        return Results.Ok(diff);
    }
    catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
    {
        logger.LogWarn("[SECURITY] Authentication failed with peer: Invalid PIN or Trust Token.");
        return Results.BadRequest(new { error = "Unauthorized: Invalid PIN or Revoked Trust Token." });
    }
    catch (Exception ex)
    {
        logger.LogError($"Comparison with {inputUrl} failed", ex);
        return Results.BadRequest(new { error = $"Connection failed: {ex.Message}" });
    }
});

// SELECTIVE or FULL SYNC
app.MapPost("/api/peer/sync-all", async (HttpContext context, IHttpClientFactory clientFactory, Gh3Service gh3, SyncLogger logger, SecurityService sec, SaveManager saves) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);

    if (!req.TryGetProperty("remoteUrl", out var remoteUrlProp))
        return Results.BadRequest(new { error = "remoteUrl is required." });

    var inputUrl = remoteUrlProp.GetString()?.Trim();
    if (string.IsNullOrEmpty(inputUrl))
        return Results.BadRequest(new { error = "remoteUrl cannot be empty." });

    // Optional list of specific song IDs chosen by user
    List<string>? selectedSongIds = null;
    if (req.TryGetProperty("selectedSongIds", out var idsProp) && idsProp.ValueKind == JsonValueKind.Array)
    {
        selectedSongIds = idsProp.EnumerateArray().Select(x => x.GetString()).Where(x => !string.IsNullOrEmpty(x)).ToList()!;
    }

    string? pin = null;
    string? token = null;
    var uri = new Uri(inputUrl);
    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    if (!string.IsNullOrEmpty(query["pin"])) pin = query["pin"];
    if (!string.IsNullOrEmpty(query["token"])) token = query["token"];
    var baseUrl = $"{uri.Scheme}://{uri.Authority}";

    try
    {
        // Automatically safeguard career saves before sync!
        try
        {
            saves.BackupCurrentSaves("Automated safety backup before P2P sync");
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Save backup warning: {ex.Message}");
        }

        logger.LogInfo($"Starting song synchronization from: {baseUrl}");
        var client = clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(10);
        if (!string.IsNullOrEmpty(pin)) client.DefaultRequestHeaders.Add("X-GH3-PIN", pin);
        if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Add("X-GH3-Trust-Token", token);

        var manifestUrl = $"{baseUrl}/api/manifest" + 
            (!string.IsNullOrEmpty(token) ? $"?token={token}" : (!string.IsNullOrEmpty(pin) ? $"?pin={pin}" : ""));
        var remoteManifest = await client.GetFromJsonAsync<LibraryManifest>(manifestUrl);
        if (remoteManifest == null) throw new InvalidOperationException("Could not fetch remote manifest.");

        var localManifest = gh3.GenerateManifest();
        var localSongIds = localManifest.Songs.Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Filter by user selection or all missing
        var targetSongs = remoteManifest.Songs.Where(s => !localSongIds.Contains(s.SongId));
        if (selectedSongIds != null && selectedSongIds.Count > 0)
        {
            var selectSet = new HashSet<string>(selectedSongIds, StringComparer.OrdinalIgnoreCase);
            targetSongs = targetSongs.Where(s => selectSet.Contains(s.SongId));
        }
        var missingSongs = targetSongs.ToList();

        int totalToSync = missingSongs.Count;
        int installedCount = 0;

        for (int i = 0; i < missingSongs.Count; i++)
        {
            var song = missingSongs[i];
            int percent = (int)(((i + 0.1f) / Math.Max(1, totalToSync)) * 90);

            logger.SetStatus(SyncState.Syncing, $"Syncing ({i + 1}/{totalToSync})", song.Title, percent);

            var downloadUrl = $"{baseUrl}/api/download/{Uri.EscapeDataString(song.SongId)}" + 
                (!string.IsNullOrEmpty(token) ? $"?token={token}" : (!string.IsNullOrEmpty(pin) ? $"?pin={pin}" : ""));
            var encryptedBytes = await client.GetByteArrayAsync(downloadUrl);
            var zipBytes = sec.DecryptPayload(encryptedBytes);

            gh3.InstallSongPackage(zipBytes, logger);
            installedCount++;
        }

        // Pull updated customs.pak.xen
        logger.SetStatus(SyncState.Syncing, "Finalizing Setlist", "customs.pak.xen", 95);
        try
        {
            var customsUrl = $"{baseUrl}/api/download-customs-pak" + 
                (!string.IsNullOrEmpty(token) ? $"?token={token}" : (!string.IsNullOrEmpty(pin) ? $"?pin={pin}" : ""));
            var encryptedCustomsBytes = await client.GetByteArrayAsync(customsUrl);
            var customsPakBytes = sec.DecryptPayload(encryptedCustomsBytes);

            gh3.BackupAndDeployCustomsPak(customsPakBytes);
            logger.LogInfo("customs.pak.xen verified and deployed successfully.");
        }
        catch (Exception ex)
        {
            logger.LogWarn($"Failed to sync customs.pak.xen: {ex.Message}");
        }

        AppConfig.IncrementUsage("sync");
        logger.SetStatus(SyncState.Success, "Sync Succeeded!", $"{installedCount} Songs Ready", 100);

        _ = Task.Run(async () =>
        {
            await Task.Delay(8000);
            var updated = gh3.GetInstalledCustomSongs();
            logger.SetStatus(SyncState.Ready, "GH3-Sync Active", $"{updated.Count} Customs In Sync");
        });

        return Results.Ok(new { success = true, installedCount });
    }
    catch (Exception ex)
    {
        logger.SetStatus(SyncState.Failed, "Sync Failed", ex.Message, 0);
        logger.LogError("Song synchronization failed", ex);
        return Results.BadRequest(new { error = $"Sync failed: {ex.Message}" });
    }
});

// ==================== ONOGAMI SONG CREATOR ====================
app.MapPost("/api/creator/create-song", async (HttpRequest request, SongCreatorService creator, SyncLogger logger) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Form-data required with audio and metadata." });

    var form = await request.ReadFormAsync();
    var autoGen = form["autoGenerateChart"].ToString() == "true";
    var meta = new SongCreationRequest
    {
        Title = form["title"].ToString(),
        Artist = form["artist"].ToString(),
        Album = form["album"].ToString(),
        Year = int.TryParse(form["year"], out var y) ? y : DateTime.Now.Year,
        Genre = form["genre"].ToString(),
        Charter = form["charter"].ToString(),
        DiffGuitar = int.TryParse(form["diffGuitar"], out var dg) ? dg : 4,
        DiffRhythm = int.TryParse(form["diffRhythm"], out var dr) ? dr : 3,
        PreviewStartMs = int.TryParse(form["previewStartMs"], out var ps) ? ps : 5000,
        Bpm = int.TryParse(form["bpm"], out var bpmVal) ? bpmVal : 130,
        AutoGenerateChart = autoGen
    };

    var audioFile = form.Files.GetFile("audioFile");
    var chartFile = form.Files.GetFile("chartFile");

    if (audioFile == null || audioFile.Length == 0)
        return Results.BadRequest(new { error = "Audio file (.mp3/.wav/.ogg) is required." });

    if (!autoGen && (chartFile == null || chartFile.Length == 0))
        return Results.BadRequest(new { error = "Chart/MIDI file (.chart/.mid) is required, or choose Auto-Generate Chart." });

    using var audioMs = new MemoryStream();
    await audioFile.CopyToAsync(audioMs);

    byte[]? chartBytes = null;
    string? chartFileName = null;
    if (chartFile != null && chartFile.Length > 0)
    {
        using var chartMs = new MemoryStream();
        await chartFile.CopyToAsync(chartMs);
        chartBytes = chartMs.ToArray();
        chartFileName = chartFile.FileName;
    }

    logger.LogInfo($"[CREATOR] Creating custom song '{meta.Title}' by '{meta.Artist}' using ONOGAMI Method (AutoGenChart: {autoGen})...");
    var result = creator.CreateSongFromOnogamiMethod(
        meta,
        audioMs.ToArray(),
        audioFile.FileName,
        chartBytes,
        chartFileName);

    if (result.Success)
    {
        AppConfig.IncrementUsage("creator");
        logger.LogInfo($"[CREATOR] {result.Message} Generated at: {result.ProjectDirectory}");
        return Results.Ok(result);
    }
    return Results.BadRequest(new { error = result.Message });
});

// ==================== DIRECT FILE IMPORT ====================
app.MapPost("/api/import-archive", async (HttpRequest request, Gh3Service gh3, SyncLogger logger, SaveManager saves) =>
{
    if (!request.HasFormContentType || request.Form.Files.Count == 0)
        return Results.BadRequest(new { error = "Upload file (.gh3pack, .gh3song, .sgh, or .zip) required." });

    var file = request.Form.Files[0];
    using var ms = new MemoryStream();
    await file.CopyToAsync(ms);

    logger.LogInfo($"[IMPORT] Importing archive '{file.FileName}' ({file.Length:N0} bytes)...");
    try
    {
        // Automated save backup before import
        try { saves.BackupCurrentSaves($"Safety backup before importing {file.FileName}"); } catch { }

        int imported = gh3.ImportArchiveFile(ms.ToArray(), file.FileName, logger);
        AppConfig.IncrementUsage("import");
        logger.LogInfo($"[IMPORT] Successfully imported {imported} songs from '{file.FileName}'.");
        return Results.Ok(new { success = true, importedCount = imported });
    }
    catch (Exception ex)
    {
        logger.LogError($"[IMPORT] Import failed for '{file.FileName}'", ex);
        return Results.BadRequest(new { error = $"Import failed: {ex.Message}" });
    }
});

// ==================== LOGS & EXPORT ====================
app.MapGet("/api/logs", (SyncLogger logger) => Results.Content(logger.ReadRecentLogs(200), "text/plain"));

app.MapGet("/api/export-pack", (Gh3Service gh3, SyncLogger logger) =>
{
    logger.LogInfo("Generating full .gh3pack export...");
    var customs = gh3.GetInstalledCustomSongs();
    using var ms = new MemoryStream();
    using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
    {
        foreach (var song in customs)
        {
            var songZip = gh3.PackageSong(song.SongId);
            var entry = archive.CreateEntry($"{song.SongId}.gh3song");
            using var entryStream = entry.Open();
            entryStream.Write(songZip);
        }

        if (File.Exists(gh3.CustomsPakPath))
        {
            archive.CreateEntryFromFile(gh3.CustomsPakPath, "customs.pak.xen");
        }

        var manifestJson = JsonSerializer.Serialize(gh3.GenerateManifest(), new JsonSerializerOptions { WriteIndented = true });
        var manifestEntry = archive.CreateEntry("manifest.json");
        using var writer = new StreamWriter(manifestEntry.Open());
        writer.Write(manifestJson);
    }

    AppConfig.IncrementUsage("export");
    logger.LogInfo($"Full pack generated successfully ({ms.Length:N0} bytes).");
    return Results.File(ms.ToArray(), "application/zip", "GH3_Full_Customs_Pack.gh3pack");
});

// Auto launch browser
if (!args.Contains("--no-browser") && !args.Contains("--headless"))
{
    _ = Task.Run(async () =>
    {
        await Task.Delay(1200);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = $"http://localhost:{port}",
                UseShellExecute = true
            });
        }
        catch { }
    });
}

Console.WriteLine("=================================================");
Console.WriteLine("  🎸 GH3-Sync | Complete P2P & Song Creator Engine");
Console.WriteLine($"  Local Web Dashboard: http://localhost:{port}");
Console.WriteLine($"  LAN Address:         http://{GetLocalIpAddress()}:{port}");
Console.WriteLine($"  Active Profile:      {friendService.Profile.Username} [{friendService.Profile.FriendCode}]");
Console.WriteLine($"  Session PIN:         {securityService.PairingPin}");
Console.WriteLine("=================================================");

app.Run();
