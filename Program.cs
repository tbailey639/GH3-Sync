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
var saveManager = new SaveManager(syncLogger);
var compilerService = new HoneycombCompilerService(gh3Service, syncLogger, saveManager);
var creatorService = new SongCreatorService(compilerService);
var natService = new NatPortMappingService(port, syncLogger);

builder.Services.AddSingleton(gh3Service);
builder.Services.AddSingleton(syncLogger);
builder.Services.AddSingleton(securityService);
builder.Services.AddSingleton(friendService);
builder.Services.AddSingleton(compilerService);
builder.Services.AddSingleton(creatorService);
builder.Services.AddSingleton(saveManager);
builder.Services.AddSingleton(natService);
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

// Start UPnP / NAT-PMP & Public IP resolution in background
if (AppConfig.Current.AutoPortMap)
{
    _ = Task.Run(async () =>
    {
        await natService.InitializeAsync();
    });
}

var songs = gh3Service.GetInstalledCustomSongs();
syncLogger.SetStatus(SyncState.Ready, "GH3-Sync Loaded", $"{songs.Count} Customs Active");

string globalBroadcastNotice = "";

string GetBestPublicOrLanAddress()
{
    if (!string.IsNullOrWhiteSpace(AppConfig.Current.CustomPublicAddress))
    {
        var custom = AppConfig.Current.CustomPublicAddress.Trim();
        if (!custom.StartsWith("http://") && !custom.StartsWith("https://")) custom = "http://" + custom;
        return custom;
    }

    if (!string.IsNullOrEmpty(natService.PublicIp))
    {
        return $"http://{natService.PublicIp}:{natService.ExternalPort ?? port}";
    }

    return $"http://{GetLocalIpAddress()}:{port}";
}

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
                appVersion = AppConfig.Current.AppVersion,
                customsCount = gh3Service.GetInstalledCustomSongs().Count,
                localPort = port,
                localIp = GetLocalIpAddress(),
                publicIp = natService.PublicIp,
                externalPort = natService.ExternalPort ?? port,
                publicAddress = GetBestPublicOrLanAddress(),
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
app.MapGet("/api/status", (Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends, SaveManager saves, NatPortMappingService nat, HoneycombCompilerService compiler) =>
{
    var localIp = GetLocalIpAddress();
    var customs = gh3.GetInstalledCustomSongs();
    var compat = gh3.VerifyGamePath(gh3.GamePath);
    var bestAddr = GetBestPublicOrLanAddress();
    var compilerStatus = compiler.GetStatusReport();
    return Results.Ok(new
    {
        isDetected = gh3.IsDetected,
        gamePath = gh3.GamePath,
        customSongsPath = SongCreatorService.ResolveSongsDirectory(),
        compatibility = compat,
        directInstall = AppConfig.Current.DirectInstallToGame,
        honeycomb = compilerStatus,
        autoCompile = AppConfig.Current.AutoCompileCreatedSongs,
        serverUrl = $"{bestAddr}?pin={sec.PairingPin}",
        rawAddress = bestAddr,
        localAddress = $"http://{localIp}:{port}",
        publicAddress = nat.PublicIp != null ? $"http://{nat.PublicIp}:{nat.ExternalPort ?? port}" : null,
        customPublicAddress = AppConfig.Current.CustomPublicAddress,
        autoPortMap = AppConfig.Current.AutoPortMap,
        natStatus = new
        {
            publicIp = nat.PublicIp,
            externalPort = nat.ExternalPort ?? port,
            isMapped = nat.IsMapped,
            method = nat.MappingMethod
        },
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

app.MapPost("/api/settings/songs-path", async (HttpContext context, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    if (req.TryGetProperty("songsPath", out var p))
    {
        var newPath = p.GetString()?.Trim() ?? "";
        if (!string.IsNullOrEmpty(newPath))
        {
            if (!Directory.Exists(newPath))
            {
                try { Directory.CreateDirectory(newPath); }
                catch (Exception ex) { return Results.BadRequest(new { error = $"Could not create directory: {ex.Message}" }); }
            }
            AppConfig.UpdateCustomSongsPath(newPath);
            logger.LogInfo($"[SETTINGS] Custom Songs directory updated to: {newPath}");
            return Results.Ok(new { success = true, songsPath = newPath });
        }
    }
    return Results.BadRequest(new { error = "songsPath is required." });
});

app.MapPost("/api/settings/honeycomb-path", async (HttpContext context, HoneycombCompilerService compiler, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    if (req.TryGetProperty("honeycombPath", out var p) || req.TryGetProperty("path", out p))
    {
        var newPath = p.GetString()?.Trim() ?? "";
        AppConfig.UpdateHoneycombPath(newPath);
        var report = compiler.GetStatusReport();
        logger.LogInfo($"[SETTINGS] Honeycomb path updated to: '{newPath}' (Compiler Status: {report.StatusMessage})");
        return Results.Ok(new { success = true, report, honeycombPath = AppConfig.Current.HoneycombPath });
    }
    return Results.BadRequest(new { error = "honeycombPath is required." });
});

app.MapPost("/api/settings/auto-compile", async (HttpContext context, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    if (req.TryGetProperty("autoCompile", out var ac))
    {
        var val = ac.GetBoolean();
        AppConfig.SetAutoCompileCreatedSongs(val);
        logger.LogInfo($"[SETTINGS] Auto-compile created songs set to: {val}");
        return Results.Ok(new { success = true, autoCompile = val });
    }
    return Results.BadRequest(new { error = "autoCompile boolean required." });
});

app.MapGet("/api/open-folder", (string? path, SyncLogger logger) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Path is required" });

    try
    {
        if (Directory.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{path}\"",
                UseShellExecute = true
            });
            logger.LogInfo($"[EXPLORER] Opened folder: {path}");
            return Results.Ok(new { success = true });
        }
        else if (File.Exists(path))
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            });
            return Results.Ok(new { success = true });
        }
        return Results.NotFound(new { error = "Directory not found: " + path });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapGet("/api/open-ghproj", (string? path, SyncLogger logger) =>
{
    if (string.IsNullOrWhiteSpace(path))
        return Results.BadRequest(new { error = "Path is required" });

    try
    {
        string? ghprojFile = null;
        if (File.Exists(path) && path.EndsWith(".ghproj", StringComparison.OrdinalIgnoreCase))
        {
            ghprojFile = path;
        }
        else if (Directory.Exists(path))
        {
            var files = Directory.GetFiles(path, "*.ghproj");
            if (files.Length > 0) ghprojFile = files[0];
        }

        if (ghprojFile != null && File.Exists(ghprojFile))
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = ghprojFile,
                    UseShellExecute = true
                });
                logger.LogInfo($"[HONEYCOMB] Launched project: {ghprojFile}");
                return Results.Ok(new { success = true, opened = ghprojFile });
            }
            catch
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{ghprojFile}\"",
                    UseShellExecute = true
                });
                return Results.Ok(new { success = true, opened = "explorer" });
            }
        }

        return Results.NotFound(new { error = "No .ghproj file found at: " + path });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

app.MapPost("/api/settings/network", async (HttpContext context, NatPortMappingService nat, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);

    var customAddr = req.TryGetProperty("customPublicAddress", out var a) ? a.GetString() ?? "" : "";
    var autoPort = req.TryGetProperty("autoPortMap", out var m) ? m.GetBoolean() : true;

    AppConfig.UpdateNetworkSettings(customAddr, autoPort);
    logger.LogInfo($"[SETTINGS] Network updated - Custom Public/DDNS: '{customAddr}', Auto UPnP/NAT-PMP: {autoPort}");

    if (autoPort && !nat.IsMapped)
    {
        _ = Task.Run(async () => await nat.InitializeAsync());
    }

    return Results.Ok(new
    {
        success = true,
        customPublicAddress = AppConfig.Current.CustomPublicAddress,
        autoPortMap = AppConfig.Current.AutoPortMap,
        bestAddress = GetBestPublicOrLanAddress()
    });
});

app.MapPost("/api/nat/refresh", async (NatPortMappingService nat) =>
{
    await nat.InitializeAsync();
    return Results.Ok(new
    {
        success = true,
        publicIp = nat.PublicIp,
        externalPort = nat.ExternalPort ?? port,
        isMapped = nat.IsMapped,
        method = nat.MappingMethod,
        bestAddress = GetBestPublicOrLanAddress()
    });
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

// DIRECT SONG DOWNLOAD FROM CONNECTED USER
app.MapGet("/api/peer/download-file", async (string remoteUrl, string songId, IHttpClientFactory clientFactory, SyncLogger logger, SecurityService sec) =>
{
    var cleanUrl = remoteUrl?.Trim() ?? "";
    var cleanSongId = songId?.Trim() ?? "";

    if (string.IsNullOrEmpty(cleanUrl) || string.IsNullOrEmpty(cleanSongId))
        return Results.BadRequest(new { error = "remoteUrl and songId are required." });

    string? pin = null;
    string? token = null;
    var uri = new Uri(cleanUrl);
    var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
    if (!string.IsNullOrEmpty(query["pin"])) pin = query["pin"];
    if (!string.IsNullOrEmpty(query["token"])) token = query["token"];

    var baseUrl = $"{uri.Scheme}://{uri.Authority}";

    try
    {
        logger.LogInfo($"Downloading song package '{cleanSongId}' from connected user at: {baseUrl}");
        var client = clientFactory.CreateClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        if (!string.IsNullOrEmpty(pin)) client.DefaultRequestHeaders.Add("X-GH3-PIN", pin);
        if (!string.IsNullOrEmpty(token)) client.DefaultRequestHeaders.Add("X-GH3-Trust-Token", token);

        var downloadUrl = $"{baseUrl}/api/download/{Uri.EscapeDataString(cleanSongId)}" +
            (!string.IsNullOrEmpty(token) ? $"?token={token}" : (!string.IsNullOrEmpty(pin) ? $"?pin={pin}" : ""));

        var encryptedBytes = await client.GetByteArrayAsync(downloadUrl);
        var zipBytes = sec.DecryptPayload(encryptedBytes);

        logger.LogInfo($"Successfully downloaded '{cleanSongId}' ({zipBytes.Length:N0} bytes) from connected user.");
        return Results.File(zipBytes, "application/zip", $"{cleanSongId}.gh3song");
    }
    catch (Exception ex)
    {
        logger.LogError($"Error downloading song '{cleanSongId}' from connected user", ex);
        return Results.BadRequest(new { error = $"Could not download song from connected user: {ex.Message}" });
    }
});

// ==================== ONOGAMI SONG CREATOR ====================
app.MapPost("/api/creator/create-song", async (HttpRequest request, SongCreatorService creator, SyncLogger logger) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Form-data required with audio and metadata." });

    var form = await request.ReadFormAsync();
    var autoGen = form["autoGenerateChart"].ToString() == "true";
    var autoCompile = form["autoCompile"].ToString() == "true" || (form.ContainsKey("autoCompile") ? false : AppConfig.Current.AutoCompileCreatedSongs);
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
        AutoGenerateChart = autoGen,
        AutoCompile = autoCompile
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

    logger.LogInfo($"[CREATOR] Creating custom song '{meta.Title}' by '{meta.Artist}' using ONOGAMI Method (AutoGenChart: {autoGen}, AutoCompile: {autoCompile})...");
    var result = await creator.CreateSongFromOnogamiMethodAsync(
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

// ==================== HONEYCOMB COMPILER ====================
app.MapGet("/api/compiler/status", (HoneycombCompilerService compiler) =>
{
    return Results.Ok(compiler.GetStatusReport());
});

app.MapPost("/api/compiler/compile", async (HttpRequest request, HoneycombCompilerService compiler, Gh3Service gh3, SyncLogger logger) =>
{
    using var reader = new StreamReader(request.Body);
    var bodyStr = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(bodyStr);

    string ghprojPath = "";
    if (req.TryGetProperty("ghprojPath", out var p)) ghprojPath = p.GetString() ?? "";
    if (string.IsNullOrEmpty(ghprojPath) && req.TryGetProperty("songId", out var sId))
    {
        var songs = gh3.GetInstalledCustomSongs();
        var match = songs.FirstOrDefault(s => s.SongId.Equals(sId.GetString(), StringComparison.OrdinalIgnoreCase));
        if (match != null && !string.IsNullOrEmpty(match.GhprojPath))
        {
            ghprojPath = match.GhprojPath;
        }
        else if (match != null && !string.IsNullOrEmpty(match.ProjectDirectory))
        {
            var proj = Directory.GetFiles(match.ProjectDirectory, "*.ghproj").FirstOrDefault();
            if (!string.IsNullOrEmpty(proj)) ghprojPath = proj;
        }
        else
        {
            var projDir = gh3.FindProjectDirectory(sId.GetString() ?? "");
            if (!string.IsNullOrEmpty(projDir))
            {
                var proj = Directory.GetFiles(projDir, "*.ghproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(proj)) ghprojPath = proj;
            }
        }
    }

    if (string.IsNullOrEmpty(ghprojPath) || !File.Exists(ghprojPath))
        return Results.BadRequest(new { error = "Valid .ghproj file path or registered project songId required." });

    logger.LogInfo($"[COMPILER] Starting Honeycomb compilation for: {Path.GetFileName(ghprojPath)}");
    var result = await compiler.CompileSongProjectAsync(ghprojPath);
    if (result.Success)
    {
        return Results.Ok(result);
    }
    return Results.BadRequest(new { error = result.Message, logs = result.Logs });
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
Console.WriteLine($"  🎸 GH3-Sync Beta 4 (v{AppConfig.Current.AppVersion}) | P2P Synchronizer, Friends & ONOGAMI");
Console.WriteLine($"  Local Web Dashboard: http://localhost:{port}");
Console.WriteLine($"  LAN Address:         http://{GetLocalIpAddress()}:{port}");
Console.WriteLine($"  Public / NAT Address: {GetBestPublicOrLanAddress()}");
Console.WriteLine($"  Active Profile:      {friendService.Profile.Username} [{friendService.Profile.FriendCode}]");
Console.WriteLine($"  Session PIN:         {securityService.PairingPin}");
Console.WriteLine("=================================================");

app.Run();
