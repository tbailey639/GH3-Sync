using System.IO;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using System.IO.Compression;
using System.Diagnostics;
using GH3_Sync;

var baseDir = AppDomain.CurrentDomain.BaseDirectory;
var appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync");
var writableWebRoot = Path.Combine(appDataDir, "p2p_root");
Directory.CreateDirectory(writableWebRoot);

bool isHeadlessArg = args.Contains("--headless") || args.Contains("--no-gui") || args.Contains("--server");
bool hasCustomPortArg = args.Any(a => a.StartsWith("--port="));
Mutex? singleInstanceMutex = null;

if (!isHeadlessArg && !hasCustomPortArg)
{
    // Use initiallyOwned: false so the named kernel Mutex tracks single-instance lifetime
    // without thread-affinity ownership (avoids ApplicationException on async shutdown threads).
    singleInstanceMutex = new Mutex(false, @"Global\GH3_Sync_SingleInstance_v1", out bool createdNew);
    if (!createdNew)
    {
        try
        {
            using var pipeClient = new NamedPipeClientStream(".", "GH3_Sync_RestorePipe", PipeDirection.Out);
            pipeClient.Connect(1200);
            using var sw = new StreamWriter(pipeClient);
            sw.WriteLine("RESTORE");
            sw.Flush();
        }
        catch { }
        return;
    }
}

AppDomain.CurrentDomain.UnhandledException += (_, exArgs) =>
{
    try
    {
        var err = exArgs.ExceptionObject?.ToString() ?? "Unknown fatal error";
        File.WriteAllText(Path.Combine(appDataDir, "crash.log"), $"[{DateTime.Now}]\n{err}\n");
        System.Windows.MessageBox.Show($"GH3-Sync encountered a startup error:\n\n{err}", "GH3-Sync Error",
            System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
    }
    catch { }
};

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = appDataDir,
    WebRootPath = writableWebRoot
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
var saveManager = new SaveManager(syncLogger, gh3Service.GamePath);
var compilerService = new HoneycombCompilerService(gh3Service, syncLogger, saveManager);
var cloneHeroService = new CloneHeroImportService(compilerService, syncLogger);
var audioAnalysisService = new AudioAnalysisService(syncLogger);
var demucsService = new DemucsService(syncLogger);
var creatorService = new SongCreatorService(compilerService, audioAnalysisService, demucsService);
var natService = new NatPortMappingService(port, syncLogger);
var updateService = new UpdateService(syncLogger);
var tempSessionService = new TemporarySessionService(gh3Service, saveManager, syncLogger);
var componentUpdater = new ComponentUpdaterService(gh3Service, updateService, demucsService, syncLogger);
var chartDoctor = new ChartDoctorService(compilerService, syncLogger);
var betterGh3Tweaks = new BetterGh3TweaksService(gh3Service, syncLogger);
var tunnelService = new TunnelAndMeshService(port, syncLogger);
var careerScores = new CareerSaveScoreService(gh3Service, saveManager, syncLogger);
var discordRpc = new DiscordRpcService(
    syncLogger,
    () => gh3Service.GetInstalledCustomSongs().Count,
    () => ((AppConfig.Current.LobbySetlistIds ?? new()).Count, Math.Clamp(AppConfig.Current.MaxLobbyAutoSyncSongs, 1, 25)));

builder.Services.AddSingleton(gh3Service);
builder.Services.AddSingleton(syncLogger);
builder.Services.AddSingleton(securityService);
builder.Services.AddSingleton(friendService);
builder.Services.AddSingleton(compilerService);
builder.Services.AddSingleton(cloneHeroService);
builder.Services.AddSingleton(audioAnalysisService);
builder.Services.AddSingleton(demucsService);
builder.Services.AddSingleton(creatorService);
builder.Services.AddSingleton(saveManager);
builder.Services.AddSingleton(natService);
builder.Services.AddSingleton(updateService);
builder.Services.AddSingleton(tempSessionService);
builder.Services.AddSingleton(componentUpdater);
builder.Services.AddSingleton(tunnelService);
builder.Services.AddSingleton(careerScores);
builder.Services.AddHttpClient();

var app = builder.Build();

static (string EffectiveIp, bool IsExternal) ResolveClientIdentity(HttpContext context)
{
    var rawIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    bool isDirectLoopback = SecurityService.IsLoopbackIp(rawIp);
    // Cloudflare Quick Tunnel / reverse proxy connections arrive from 127.0.0.1 with CF-Connecting-IP / X-Forwarded-For
    if (isDirectLoopback)
    {
        if (context.Request.Headers.TryGetValue("CF-Connecting-IP", out var cfIp) && !string.IsNullOrWhiteSpace(cfIp.ToString()))
            return (cfIp.ToString().Trim(), true);
        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var xff) && !string.IsNullOrWhiteSpace(xff.ToString()))
            return (xff.ToString().Split(',')[0].Trim(), true);
        return (rawIp, false);
    }
    return (rawIp, true);
}

static bool IsExternalP2pRoute(string path)
{
    if (path.Equals("/api/auth/challenge", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/manifest", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/download-customs-pak", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.StartsWith("/api/download/", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/profile", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/friends/request", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/friends/accept", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/friends/decline", StringComparison.OrdinalIgnoreCase)) return true;
    if (path.Equals("/api/friends/check-status", StringComparison.OrdinalIgnoreCase)) return true;
    return false;
}

// Strict External-IP Lockdown, Localhost DNS-Rebinding/CSRF Shield, IP Ban, Online/Offline P2P Mode & Rate Limit Middleware
app.Use(async (context, next) =>
{
    var (effectiveIp, isExternal) = ResolveClientIdentity(context);
    var reqPath = context.Request.Path.Value ?? "/";

    if (!isExternal)
    {
        // Localhost DNS-Rebinding Guard: reject spoofed Host headers (e.g. evil.com pointing to 127.0.0.1)
        var hostName = context.Request.Host.Host ?? "";
        bool validLocalHost = hostName.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                           || hostName == "127.0.0.1"
                           || hostName == "::1"
                           || hostName == "[::1]";
        if (!string.IsNullOrEmpty(hostName) && !validLocalHost)
        {
            syncLogger.LogWarn($"[SECURITY] Blocked DNS-rebinding Host header '{hostName}' on {reqPath}");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden: invalid local Host header." });
            return;
        }

        // Localhost Browser CSRF Shield: reject cross-origin fetches from external websites
        if (context.Request.Headers.TryGetValue("Origin", out var originHdr) && !string.IsNullOrWhiteSpace(originHdr.ToString()))
        {
            var origin = originHdr.ToString().Trim();
            bool safeOrigin = origin.StartsWith("http://127.0.0.1", StringComparison.OrdinalIgnoreCase)
                           || origin.StartsWith("http://localhost", StringComparison.OrdinalIgnoreCase)
                           || origin.StartsWith("http://[::1]", StringComparison.OrdinalIgnoreCase);
            if (!safeOrigin)
            {
                syncLogger.LogWarn($"[SECURITY] Blocked cross-origin browser CSRF request from '{origin}' to {reqPath}");
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsJsonAsync(new { error = "Forbidden: cross-origin requests to local API are blocked." });
                return;
            }
        }
    }

    if (isExternal)
    {
        if (securityService.IsIpBlocked(effectiveIp))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Access denied: IP is temporarily locked or banned." });
            return;
        }

        if (!IsExternalP2pRoute(reqPath))
        {
            syncLogger.LogWarn($"[SECURITY] Blocked external IP {effectiveIp} from local-only endpoint: {reqPath}");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { error = "Forbidden: endpoint restricted to local desktop access only." });
            return;
        }

        if (!AppConfig.Current.P2pOnlineEnabled)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new { error = "Peer P2P sharing is currently in Offline Mode." });
            return;
        }
    }

    if (!securityService.CheckRequestRateLimit(effectiveIp, syncLogger))
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await context.Response.WriteAsJsonAsync(new { error = "Rate limit exceeded. Please wait a moment and try again." });
        return;
    }

    await next();
});

// Start In-Game Overlay & Discord Rich Presence
var overlay = new InGameOverlay(syncLogger);
overlay.Start();
discordRpc.Start();
syncLogger.LogInfo("In-game HUD overlay & Discord Rich Presence services started.");
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
    if (tunnelService.IsTunnelActive && !string.IsNullOrWhiteSpace(tunnelService.ActiveTunnelUrl))
        return tunnelService.ActiveTunnelUrl!;

    if (!string.IsNullOrWhiteSpace(AppConfig.Current.CustomPublicAddress))
    {
        var custom = AppConfig.Current.CustomPublicAddress.Trim();
        if (!custom.StartsWith("http://") && !custom.StartsWith("https://")) custom = "http://" + custom;
        return custom;
    }

    var meshUrl = tunnelService.GetPreferredMeshUrl();
    if (!string.IsNullOrWhiteSpace(meshUrl))
        return meshUrl;

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

FriendService.BestAddressProvider = GetBestPublicOrLanAddress;

bool TryAuthorize(HttpContext context, SecurityService sec, FriendService friends, SyncLogger logger, out string sharedSecret, out bool isTrustedFriend)
{
    sharedSecret = sec.PairingPin;
    isTrustedFriend = false;
    var (remoteIp, isExternal) = ResolveClientIdentity(context);

    if (isExternal && sec.IsIpBlocked(remoteIp))
        return false;

    // 1. Challenge-Response HMAC-SHA256 Auth (Preferred — secret never crosses the wire in plaintext!)
    if (context.Request.Headers.TryGetValue("X-GH3-Auth-Nonce", out var hdrNonce) &&
        context.Request.Headers.TryGetValue("X-GH3-Auth-Timestamp", out var hdrTs) &&
        context.Request.Headers.TryGetValue("X-GH3-Auth-Hmac", out var hdrHmac))
    {
        var candidates = new List<(string Secret, string Mode, string Principal)>();
        foreach (var f in friends.Friends.Where(x => x.IsTrusted && !string.IsNullOrWhiteSpace(x.TrustToken)))
            candidates.Add((f.TrustToken, "trusted", f.Username));
        foreach (var inv in sec.GetActiveExpiringInvites())
            candidates.Add((inv.Token, "invite", inv.Label));
        candidates.Add((sec.PairingPin, "pin", "Session PIN"));

        var methodPath = $"{context.Request.Method} {context.Request.Path.Value}";
        if (sec.TryConsumeChallengeHmac(hdrNonce.ToString(), hdrTs.ToString(), hdrHmac.ToString(), methodPath, remoteIp, candidates, out var matchedSecret, out var mode, out var principal))
        {
            sharedSecret = matchedSecret;
            isTrustedFriend = mode == "trusted" || mode == "invite";
            sec.RecordAuthSuccess(remoteIp);
            logger.LogInfo($"[SECURITY] Challenge-HMAC auth granted ({mode}: {principal}) from {remoteIp}");
            return true;
        }
        sec.RecordAuthFailure(remoteIp, "Invalid or replayed Challenge-Response HMAC", logger);
        return false;
    }

    // 2. Backwards-compatible HTTP Headers ONLY (never query strings, which leak in logs/history)
    string? token = null;
    if (context.Request.Headers.TryGetValue("X-GH3-Trust-Token", out var headerToken))
        token = headerToken.ToString();

    string? pin = null;
    if (context.Request.Headers.TryGetValue("X-GH3-PIN", out var headerPin))
        pin = headerPin.ToString();

    if (!string.IsNullOrEmpty(token))
    {
        if (friends.ValidateTrustToken(token, out var trustedFriend))
        {
            sharedSecret = token;
            isTrustedFriend = true;
            sec.RecordAuthSuccess(remoteIp);
            logger.LogInfo($"[SECURITY] Trusted Friend access granted to: {trustedFriend?.Username} from {remoteIp}");
            return true;
        }
        if (sec.ValidateExpiringInviteToken(token, out var invite))
        {
            sharedSecret = token;
            isTrustedFriend = true;
            sec.RecordAuthSuccess(remoteIp);
            logger.LogInfo($"[SECURITY] Expiring Invite access granted ({invite?.Label}) from {remoteIp}");
            return true;
        }
        sec.RecordAuthFailure(remoteIp, "Invalid/revoked Trust or Invite Token", logger);
        return false;
    }

    if (!string.IsNullOrEmpty(pin))
    {
        if (sec.ValidatePin(pin, remoteIp, logger))
        {
            sharedSecret = pin;
            isTrustedFriend = false;
            return true;
        }
        return false;
    }

    // 3. Unauthenticated local loopback (owner self-diagnostics only — NEVER Cloudflare-proxied external)
    if (!isExternal)
    {
        isTrustedFriend = true;
        return true;
    }

    return false;
}

bool IsAuthorized(HttpContext context, SecurityService sec, FriendService friends, SyncLogger logger)
    => TryAuthorize(context, sec, friends, logger, out _, out _);

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
                if (reply.TryGetProperty("pendingRequests", out var pr) && pr.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in pr.EnumerateArray())
                    {
                        var sCode = item.TryGetProperty("senderCode", out var sc) ? sc.GetString() ?? "" : "";
                        var sUser = item.TryGetProperty("senderUsername", out var su) ? su.GetString() ?? "" : "";
                        var sAddr = item.TryGetProperty("senderAddress", out var sa) ? sa.GetString() ?? "" : "";
                        var sTok = item.TryGetProperty("proposedTrustToken", out var pt) ? pt.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(sCode))
                        {
                            friendService.ReceiveFriendRequest(sCode, sUser, sAddr, sTok);
                        }
                    }
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
            var honeycombDir = HoneycombCompilerService.FindHoneycombDirectory();
            var honeycombExe = !string.IsNullOrEmpty(honeycombDir) ? Path.Combine(honeycombDir, "Honeycomb-GUI.exe") : null;

            if (honeycombExe != null && File.Exists(honeycombExe))
            {
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = honeycombExe,
                        Arguments = $"\"{ghprojFile}\"",
                        WorkingDirectory = honeycombDir,
                        UseShellExecute = true
                    });
                    logger.LogInfo($"[HONEYCOMB] Launched Honeycomb-GUI with project: {ghprojFile}");
                    return Results.Ok(new { success = true, opened = ghprojFile, method = "Honeycomb-GUI.exe" });
                }
                catch (Exception ex)
                {
                    logger.LogWarn($"[HONEYCOMB] Direct exe launch failed ({ex.Message}), falling back to shell...");
                }
            }

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

app.MapGet("/api/friends/requests", (FriendService friends) => Results.Ok(new
{
    incoming = friends.IncomingRequests,
    outgoing = friends.OutgoingRequests
}));

app.MapPost("/api/friends/lookup", async (HttpContext context, FriendService friends, IHttpClientFactory clientFactory) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var code = req.TryGetProperty("friendCode", out var c) ? c.GetString() ?? "" : "";
    var client = clientFactory.CreateClient();
    var result = await friends.LookupFriendByCodeAsync(code, client);
    return Results.Ok(result);
});

app.MapPost("/api/friends/send-request", async (HttpContext context, FriendService friends, IHttpClientFactory clientFactory, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var code = req.TryGetProperty("friendCode", out var c) ? c.GetString() ?? "" : "";
    var address = req.TryGetProperty("address", out var a) ? a.GetString() : null;

    if (string.IsNullOrWhiteSpace(code))
        return Results.BadRequest(new { success = false, message = "Friend Code is required." });

    var client = clientFactory.CreateClient();
    var result = await friends.SendFriendRequestAsync(code, address, client);
    if (result.Success && result.ReceivedByPeer)
    {
        AppConfig.IncrementUsage("friend");
        logger.LogInfo($"[FRIENDS] Friend request delivered and confirmed received by {result.Username} ({result.FriendCode})");
    }
    else
    {
        logger.LogWarn($"[FRIENDS] Could not deliver friend request to {code}: {result.Message}");
    }

    return Results.Ok(result);
});

// P2P Handshake: Remote peer calls this endpoint to deliver an incoming friend request
app.MapPost("/api/friends/request", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);

    var senderCode = req.TryGetProperty("senderCode", out var sc) ? sc.GetString() ?? "" : "";
    var senderUsername = req.TryGetProperty("senderUsername", out var su) ? su.GetString() ?? "" : "";
    var senderAddress = req.TryGetProperty("senderAddress", out var sa) ? sa.GetString() ?? "" : "";
    var proposedToken = req.TryGetProperty("proposedTrustToken", out var pt) ? pt.GetString() : null;

    if (string.IsNullOrWhiteSpace(senderCode))
        return Results.BadRequest(new { error = "senderCode is required." });

    var res = friends.ReceiveFriendRequest(senderCode, senderUsername, senderAddress, proposedToken);
    logger.LogInfo($"[FRIENDS] Received friend request from {senderUsername} ({senderCode}) at {senderAddress} [Status: {res.Status}]");

    return Results.Ok(new
    {
        success = res.Success,
        received = res.Received,
        status = res.Status,
        username = res.Username,
        friendCode = res.FriendCode,
        message = "Friend request successfully received and waiting for approval."
    });
});

// P2P Handshake: Remote peer calls this endpoint when they accept our friend request
app.MapPost("/api/friends/accept", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);

    var senderCode = req.TryGetProperty("senderCode", out var sc) ? sc.GetString() ?? "" : "";
    var senderUsername = req.TryGetProperty("senderUsername", out var su) ? su.GetString() ?? "" : "";
    var senderAddress = req.TryGetProperty("senderAddress", out var sa) ? sa.GetString() ?? "" : "";
    var trustToken = req.TryGetProperty("trustToken", out var tt) ? tt.GetString() : null;
    var isTrusted = !req.TryGetProperty("isTrusted", out var it) || it.GetBoolean();

    if (!string.IsNullOrWhiteSpace(senderCode))
    {
        var friend = friends.ProcessPeerAcceptedRequest(senderCode, senderUsername, senderAddress, trustToken, isTrusted);
        logger.LogInfo($"[FRIENDS] Remote peer {friend.Username} ({friend.FriendCode}) accepted friend request! Mutual trust established.");
    }

    return Results.Ok(new { success = true });
});

app.MapPost("/api/friends/accept-request", async (HttpContext context, FriendService friends, IHttpClientFactory clientFactory, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var idOrCode = req.TryGetProperty("id", out var i) ? i.GetString() ?? "" : (req.TryGetProperty("friendCode", out var c) ? c.GetString() ?? "" : "");
    var asTrusted = !req.TryGetProperty("asTrusted", out var at) || at.GetBoolean();

    var client = clientFactory.CreateClient();
    var added = await friends.AcceptFriendRequestAsync(idOrCode, asTrusted, client);
    if (added != null)
    {
        AppConfig.IncrementUsage("friend");
        logger.LogInfo($"[FRIENDS] Accepted friend request for {added.Username} ({added.FriendCode}) [Trusted: {added.IsTrusted}]");
        return Results.Ok(new { success = true, friend = added });
    }
    return Results.NotFound(new { error = "Friend request not found." });
});

app.MapPost("/api/friends/decline-request", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var idOrCode = req.TryGetProperty("id", out var i) ? i.GetString() ?? "" : (req.TryGetProperty("friendCode", out var c) ? c.GetString() ?? "" : "");

    var ok = friends.DeclineFriendRequest(idOrCode);
    logger.LogInfo($"[FRIENDS] Declined friend request for {idOrCode}");
    return Results.Ok(new { success = ok });
});

app.MapPost("/api/friends/cancel-request", async (HttpContext context, FriendService friends, SyncLogger logger) =>
{
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var req = JsonSerializer.Deserialize<JsonElement>(body);
    var idOrCode = req.TryGetProperty("id", out var i) ? i.GetString() ?? "" : (req.TryGetProperty("friendCode", out var c) ? c.GetString() ?? "" : "");

    var ok = friends.CancelOutgoingRequest(idOrCode);
    logger.LogInfo($"[FRIENDS] Canceled outgoing friend request for {idOrCode}");
    return Results.Ok(new { success = ok });
});

app.MapGet("/api/friends/check-status", (string? code, FriendService friends) =>
{
    var cleanCode = code?.Trim().ToUpperInvariant() ?? "";
    if (!cleanCode.StartsWith("GH3-")) cleanCode = "GH3-" + cleanCode;

    var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
    if (friend != null)
    {
        return Results.Ok(new
        {
            isFriend = true,
            isTrusted = friend.IsTrusted,
            trustToken = friend.IsTrusted ? friend.TrustToken : "",
            username = friends.Profile.Username
        });
    }

    return Results.Ok(new
    {
        isFriend = false,
        isTrusted = false,
        trustToken = "",
        username = friends.Profile.Username
    });
});

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

// ==================== P2P CHALLENGE-RESPONSE & SYNC APIs ====================
app.MapGet("/api/auth/challenge", (HttpContext context, SecurityService sec) =>
{
    var (remoteIp, _) = ResolveClientIdentity(context);
    var nonce = sec.IssueAuthChallenge(remoteIp);
    return Results.Ok(new { nonce, expiresInSeconds = 90 });
});

app.MapGet("/api/manifest", (HttpContext context, Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends) =>
{
    if (!IsAuthorized(context, sec, friends, logger))
        return Results.Unauthorized();

    logger.LogDebug("Serving authenticated & ECDSA-signed manifest request to peer.");
    logger.PulsePeerActivity("Peer connected & checking library");
    var manifestJson = JsonSerializer.SerializeToUtf8Bytes(gh3.GenerateManifest());
    context.Response.Headers["X-GH3-Host-PubKey"] = sec.HostPublicKeyBase64;
    context.Response.Headers["X-GH3-Host-Fingerprint"] = sec.HostPublicKeyFingerprint;
    context.Response.Headers["X-GH3-Host-Signature"] = sec.SignManifestBytes(manifestJson);
    return Results.Bytes(manifestJson, "application/json; charset=utf-8");
});

app.MapGet("/api/download/{songId}", async (HttpContext context, string songId, Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends) =>
{
    if (!TryAuthorize(context, sec, friends, logger, out var sharedSecret, out var isTrustedFriend))
        return Results.Unauthorized();

    if (!isTrustedFriend)
    {
        logger.LogWarn($"[SECURITY] Blocked untrusted PIN download attempt for '{songId}' (Trusted Friend Token required).");
        return Results.Json(
            new { error = "Trusted Friend Token required to download songs. Ask the host to mark you as a Trusted Friend." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    var (peerIp, isExternalPeer) = ResolveClientIdentity(context);
    if (isExternalPeer && AppConfig.Current.RequireHostDownloadApproval)
    {
        var matchSong = gh3.GetInstalledCustomSongs().FirstOrDefault(s => s.SongId.Equals(songId, StringComparison.OrdinalIgnoreCase));
        bool approved = await sec.RequestDownloadApprovalAsync(peerIp, songId, matchSong?.Title ?? songId, matchSong?.TotalSizeBytes ?? 0, logger);
        if (!approved)
            return Results.Json(new { error = "Download request was declined by the host." }, statusCode: StatusCodes.Status403Forbidden);
    }

    bool acquired = await sec.DownloadConcurrencyGuard.WaitAsync(TimeSpan.FromSeconds(30));
    if (!acquired)
        return Results.Json(new { error = "Server busy handling concurrent song transfers. Please retry shortly." }, statusCode: StatusCodes.Status429TooManyRequests);

    try
    {
        logger.LogInfo($"Serving encrypted song package for: {songId}");
        logger.PulsePeerActivity($"Sharing {songId}");
        var packageBytes = gh3.PackageSong(songId);
        var encryptedBytes = sec.EncryptPayload(packageBytes, sharedSecret);
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
    finally
    {
        sec.DownloadConcurrencyGuard.Release();
    }
});

app.MapGet("/api/download-customs-pak", (HttpContext context, Gh3Service gh3, SyncLogger logger, SecurityService sec, FriendService friends) =>
{
    if (!TryAuthorize(context, sec, friends, logger, out var sharedSecret, out var isTrustedFriend))
        return Results.Unauthorized();

    if (!isTrustedFriend)
    {
        logger.LogWarn("[SECURITY] Blocked untrusted PIN request for customs.pak.xen (Trusted Friend Token required).");
        return Results.Json(
            new { error = "Trusted Friend Token required to download songlist data." },
            statusCode: StatusCodes.Status403Forbidden);
    }

    if (!File.Exists(gh3.CustomsPakPath))
        return Results.NotFound(new { error = "customs.pak.xen not found." });

    logger.LogInfo("Serving encrypted customs.pak.xen to trusted peer.");
    var rawBytes = File.ReadAllBytes(gh3.CustomsPakPath);
    var encryptedBytes = sec.EncryptPayload(rawBytes, sharedSecret);
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

        var manifestUrl = $"{baseUrl}/api/manifest";
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

        var manifestUrl = $"{baseUrl}/api/manifest";
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

        var sharedSecret = !string.IsNullOrEmpty(token) ? token : (!string.IsNullOrEmpty(pin) ? pin : sec.PairingPin);
        for (int i = 0; i < missingSongs.Count; i++)
        {
            var song = missingSongs[i];
            int percent = (int)(((i + 0.1f) / Math.Max(1, totalToSync)) * 90);

            logger.SetStatus(SyncState.Syncing, $"Syncing ({i + 1}/{totalToSync})", song.Title, percent);

            var downloadUrl = $"{baseUrl}/api/download/{Uri.EscapeDataString(song.SongId)}";
            var encryptedBytes = await client.GetByteArrayAsync(downloadUrl);
            var zipBytes = sec.DecryptPayload(encryptedBytes, sharedSecret);

            gh3.InstallSongPackage(zipBytes, logger, song.PakHash, song.FsbHash, mergeIntoCustomsPak: true);
            installedCount++;
        }

        // Only pull full customs.pak.xen if recipient does not have one yet; otherwise individual songs were merged cleanly!
        if (!File.Exists(gh3.CustomsPakPath))
        {
            logger.SetStatus(SyncState.Syncing, "Finalizing Setlist", "customs.pak.xen", 95);
            try
            {
                var customsUrl = $"{baseUrl}/api/download-customs-pak";
                var encryptedCustomsBytes = await client.GetByteArrayAsync(customsUrl);
                var customsPakBytes = sec.DecryptPayload(encryptedCustomsBytes, sharedSecret);

                gh3.BackupAndDeployCustomsPak(customsPakBytes);
                logger.LogInfo("customs.pak.xen verified and deployed successfully.");
            }
            catch (Exception ex)
            {
                logger.LogWarn($"Failed to sync customs.pak.xen: {ex.Message}");
            }
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

        var sharedSecret = !string.IsNullOrEmpty(token) ? token : (!string.IsNullOrEmpty(pin) ? pin : sec.PairingPin);
        var downloadUrl = $"{baseUrl}/api/download/{Uri.EscapeDataString(cleanSongId)}";

        var encryptedBytes = await client.GetByteArrayAsync(downloadUrl);
        var zipBytes = sec.DecryptPayload(encryptedBytes, sharedSecret);

        logger.LogInfo($"Successfully downloaded '{cleanSongId}' ({zipBytes.Length:N0} bytes) from connected user.");
        return Results.File(zipBytes, "application/zip", $"{cleanSongId}.gh3song");
    }
    catch (Exception ex)
    {
        logger.LogError($"Error downloading song '{cleanSongId}' from connected user", ex);
        return Results.BadRequest(new { error = $"Could not download song from connected user: {ex.Message}" });
    }
});

// ==================== ONOGAMI SONG CREATOR & AI AUDIO ====================
app.MapGet("/api/demucs/status", (DemucsService demucs) =>
{
    return Results.Ok(demucs.GetStatus());
});

app.MapPost("/api/audio/detect-bpm", async (HttpRequest request, AudioAnalysisService analyzer) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Form-data required with audio file." });

    var form = await request.ReadFormAsync();
    var audio = form.Files.GetFile("audioFile");
    if (audio == null || audio.Length == 0)
        return Results.BadRequest(new { error = "Audio file required." });

    var tempPath = Path.Combine(Path.GetTempPath(), $"bpm_check_{Guid.NewGuid():N}.mp3");
    try
    {
        using (var fs = File.Create(tempPath))
        {
            await audio.CopyToAsync(fs);
        }
        var analysis = analyzer.AnalyzeAudio(tempPath);
        return Results.Ok(new
        {
            success = analysis.Success,
            bpm = analysis.Bpm,
            downbeatMs = analysis.DownbeatOffsetMs,
            message = analysis.Message,
            onsetsCount = analysis.OnsetTimestampsSeconds.Count
        });
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
});

app.MapPost("/api/creator/create-song", async (HttpRequest request, SongCreatorService creator, SyncLogger logger) =>
{
    if (!request.HasFormContentType)
        return Results.BadRequest(new { error = "Form-data required with audio and metadata." });

    var form = await request.ReadFormAsync();
    var autoGen = form["autoGenerateChart"].ToString() == "true";
    var autoCompile = form["autoCompile"].ToString() == "true" || (form.ContainsKey("autoCompile") ? false : AppConfig.Current.AutoCompileCreatedSongs);
    var autoDetectBpm = form["autoDetectBpm"].ToString() == "true" || !form.ContainsKey("bpm") || string.IsNullOrWhiteSpace(form["bpm"]);
    var separateStems = form["separateStems"].ToString() == "true";

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
        AutoDetectBpm = autoDetectBpm,
        SeparateStemsWithDemucs = separateStems,
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

// ==================== OBS STUDIO LOOPBACK-ONLY OVERLAY ====================
app.MapGet("/obs-overlay", () => Results.Content(ObsOverlayService.BuildOverlayHtml(), "text/html; charset=utf-8"));
app.MapGet("/api/obs-now-playing", () => Results.Ok(ObsOverlayService.GetSnapshot()));
app.MapGet("/api/obs-now-playing/art", () =>
{
    var art = ObsOverlayService.GetCoverArtPath();
    if (string.IsNullOrEmpty(art) || !File.Exists(art)) return Results.NotFound();
    var ct = art.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg";
    return Results.File(File.ReadAllBytes(art), ct);
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

Console.WriteLine("=================================================");
Console.WriteLine($"  GH3-Sync v{UpdateService.CurrentAppVersion}");
Console.WriteLine($"  P2P Sync Listener:   http://localhost:{port}");
Console.WriteLine($"  LAN Address:         http://{GetLocalIpAddress()}:{port}");
Console.WriteLine($"  Public / NAT Address: {GetBestPublicOrLanAddress()}");
Console.WriteLine($"  Active Profile:      {friendService.Profile.Username} [{friendService.Profile.FriendCode}]");
Console.WriteLine($"  Session PIN:         {securityService.PairingPin}");
Console.WriteLine("=================================================");

bool isHeadless = isHeadlessArg;

if (isHeadless)
{
    app.Run();
}
else
{
    var uiDone = new TaskCompletionSource<bool>();
    using var pipeCts = new CancellationTokenSource();

    var uiThread = new Thread(() =>
    {
        try
        {
            var wpfApp = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };

            wpfApp.DispatcherUnhandledException += (_, exArgs) =>
            {
                syncLogger.LogError("WPF UI Error", exArgs.Exception);
                try
                {
                    File.WriteAllText(Path.Combine(appDataDir, "crash.log"), $"[{DateTime.Now}]\n{exArgs.Exception}\n");
                    System.Windows.MessageBox.Show($"UI Error: {exArgs.Exception.Message}", "GH3-Sync",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
                catch { }
                exArgs.Handled = true;
            };

            wpfApp.Startup += async (_, _) =>
            {
                var splash = new GH3_Sync.Views.SplashWindow();
                splash.Show();
                splash.UpdateProgress(20, "Starting background sync service...");

                try
                {
                    await app.StartAsync();
                }
                catch (Exception srvEx)
                {
                    syncLogger.LogWarn($"[P2P] Background listener port {port} busy or unavailable: {srvEx.Message}");
                }
                splash.UpdateProgress(55, "Loading Guitar Hero III song library...");
                if (!string.IsNullOrWhiteSpace(gh3Service.GamePath))
                {
                    await Task.Run(() => CustomsPakService.RepairCustomsPakIfCorrupted(gh3Service.GamePath, syncLogger));
                }
                await Task.Delay(300);

                splash.UpdateProgress(85, "Preparing workspace...");
                var mainWin = new GH3_Sync.Views.MainWindow(
                    gh3Service,
                    syncLogger,
                    securityService,
                    friendService,
                    saveManager,
                    compilerService,
                    cloneHeroService,
                    audioAnalysisService,
                    demucsService,
                    creatorService,
                    natService,
                    updateService,
                    tempSessionService,
                    componentUpdater,
                    port,
                    chartDoctor,
                    betterGh3Tweaks,
                    discordRpc,
                    tunnelService,
                    careerScores);

                // Single-instance restore listener (brings minimized/tray window to front if user launches GH3-Sync again)
                if (!hasCustomPortArg)
                {
                    _ = Task.Run(async () =>
                    {
                        while (!pipeCts.IsCancellationRequested)
                        {
                            try
                            {
                                using var server = new NamedPipeServerStream("GH3_Sync_RestorePipe", PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                                await server.WaitForConnectionAsync(pipeCts.Token);
                                using var sr = new StreamReader(server);
                                var msg = await sr.ReadLineAsync();
                                if (msg == "RESTORE")
                                {
                                    mainWin.Dispatcher.Invoke(() => mainWin.RestoreFromTrayOrActivate());
                                }
                            }
                            catch (OperationCanceledException) { break; }
                            catch { await Task.Delay(500); }
                        }
                    });
                }

                await Task.Delay(350);
                splash.UpdateProgress(100, "Ready to Rock!");
                await Task.Delay(200);

                wpfApp.MainWindow = mainWin;
                mainWin.Closed += (_, _) => wpfApp.Shutdown();
                mainWin.Show();
                await splash.FadeOutAndCloseAsync();
            };

            wpfApp.Run();
        }
        catch (Exception ex)
        {
            syncLogger.LogError("WPF startup failed", ex);
        }
        finally
        {
            uiDone.TrySetResult(true);
        }
    });

    uiThread.SetApartmentState(ApartmentState.STA);
    uiThread.IsBackground = false;
    uiThread.Start();

    await uiDone.Task;
    pipeCts.Cancel();
    tunnelService.Dispose();
    careerScores.Dispose();
    discordRpc.Dispose();
    tempSessionService.Stop();
    await natService.RemoveMappingAsync();
    await app.StopAsync();
    try
    {
        GC.KeepAlive(singleInstanceMutex);
        singleInstanceMutex?.Dispose();
    }
    catch { }
}
