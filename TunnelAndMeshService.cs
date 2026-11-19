using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class MeshInterfaceInfo
{
    public string Provider { get; set; } = ""; // Tailscale, ZeroTier, LAN
    public string AdapterName { get; set; } = "";
    public string IpAddress { get; set; } = "";
    public string SyncUrl { get; set; } = "";
}

/// <summary>
/// Zero-Config CGNAT / Strict-Router P2P Service:
/// 1) Auto-detects Tailscale (100.64.0.0/10) and ZeroTier mesh VPN interfaces.
/// 2) Optional 1-Click HTTPS Quick Tunnel via official Cloudflare Tunnel (cloudflared)
///    so friends behind CGNAT / college / apartment routers can sync over HTTPS with zero port forwarding.
/// </summary>
public class TunnelAndMeshService : IDisposable
{
    private readonly int _localPort;
    private readonly SyncLogger _logger;
    private Process? _tunnelProcess;
    private readonly object _lock = new();
    private bool _disposed;

    public bool IsTunnelActive { get; private set; }
    public string? ActiveTunnelUrl { get; private set; }
    public string TunnelStatusMessage { get; private set; } = "Inactive (Use UPnP, Mesh VPN, or Start HTTPS Tunnel)";

    public event Action<string?>? TunnelStateChanged;

    public static string CloudflaredExePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GH3-Sync", "tools", "tunnel", "cloudflared.exe");

    private const string CloudflaredDownloadUrl =
        "https://github.com/cloudflare/cloudflared/releases/latest/download/cloudflared-windows-amd64.exe";

    public TunnelAndMeshService(int localPort, SyncLogger logger)
    {
        _localPort = localPort;
        _logger = logger;
    }

    /// <summary>
    /// Scans active IPv4 network adapters for Tailscale (100.64.0.0/10) and ZeroTier virtual mesh adapters.
    /// </summary>
    public List<MeshInterfaceInfo> DetectMeshInterfaces()
    {
        var results = new List<MeshInterfaceInfo>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;

                var nameAndDesc = $"{nic.Name} {nic.Description}";
                var props = nic.GetIPProperties();
                foreach (var uni in props.UnicastAddresses)
                {
                    if (uni.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var bytes = uni.Address.GetAddressBytes();
                    if (bytes.Length != 4) continue;
                    // Ignore dead unconfigured APIPA link-local fallback addresses (169.254.x.x)
                    if (bytes[0] == 169 && bytes[1] == 254) continue;
                    var ip = uni.Address.ToString();

                    bool isTailscaleRange = bytes[0] == 100 && bytes[1] >= 64 && bytes[1] <= 127;
                    if ((nameAndDesc.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) && isTailscaleRange) || isTailscaleRange)
                    {
                        results.Add(new MeshInterfaceInfo
                        {
                            Provider = "Tailscale",
                            AdapterName = nic.Name,
                            IpAddress = ip,
                            SyncUrl = $"http://{ip}:{_localPort}"
                        });
                    }
                    else if (nameAndDesc.Contains("ZeroTier", StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(new MeshInterfaceInfo
                        {
                            Provider = "ZeroTier",
                            AdapterName = nic.Name,
                            IpAddress = ip,
                            SyncUrl = $"http://{ip}:{_localPort}"
                        });
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[MESH] Mesh interface scan warning: {ex.Message}");
        }
        return results;
    }

    public string? GetPreferredMeshUrl()
    {
        var meshes = DetectMeshInterfaces();
        return meshes.FirstOrDefault(m => m.Provider == "Tailscale")?.SyncUrl
            ?? meshes.FirstOrDefault(m => m.Provider == "ZeroTier")?.SyncUrl;
    }

    public async Task<(bool Ok, string UrlOrError)> StartQuickTunnelAsync(
        Action<string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (IsTunnelActive && !string.IsNullOrWhiteSpace(ActiveTunnelUrl))
            return (true, ActiveTunnelUrl);

        try
        {
            if (!File.Exists(CloudflaredExePath))
            {
                onProgress?.Invoke("Downloading official cloudflared binary (~35 MB)...");
                _logger.LogInfo("[TUNNEL] Downloading official cloudflared-windows-amd64.exe...");
                Directory.CreateDirectory(Path.GetDirectoryName(CloudflaredExePath)!);

                using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
                http.DefaultRequestHeaders.Add("User-Agent", $"GH3-Sync/{UpdateService.CurrentAppVersion}");
                var tmpPath = CloudflaredExePath + ".download";
                using (var resp = await http.GetAsync(CloudflaredDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                    await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    await netStream.CopyToAsync(fs, ct);
                }

                if (new FileInfo(tmpPath).Length < 500_000)
                {
                    try { File.Delete(tmpPath); } catch { }
                    return (false, "Downloaded cloudflared binary appears invalid or truncated.");
                }

                // Verify official Cloudflare Authenticode signature & GitHub release digest before trusting binary
                onProgress?.Invoke("Verifying Cloudflare Authenticode digital signature...");
                if (!VerifyCloudflaredSignature(tmpPath, out var sigReason))
                {
                    try { File.Delete(tmpPath); } catch { }
                    _logger.LogWarn($"[TUNNEL] SECURITY BLOCK: {sigReason}");
                    return (false, $"Security check failed: {sigReason}");
                }

                File.Move(tmpPath, CloudflaredExePath, overwrite: true);
                _logger.LogInfo($"[TUNNEL] Verified cloudflared binary saved to {CloudflaredExePath} ({sigReason})");
            }
            else if (!VerifyCloudflaredSignature(CloudflaredExePath, out var existingSigErr))
            {
                _logger.LogWarn($"[TUNNEL] SECURITY BLOCK on existing cloudflared.exe: {existingSigErr}");
                return (false, $"Existing cloudflared.exe failed signature check ({existingSigErr}). Delete it and retry.");
            }

            StopTunnel();
            onProgress?.Invoke("Starting encrypted Cloudflare HTTPS Quick Tunnel...");

            var psi = new ProcessStartInfo
            {
                FileName = CloudflaredExePath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("tunnel");
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add($"http://127.0.0.1:{_localPort}");
            psi.ArgumentList.Add("--no-autoupdate");

            var urlTcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var urlRegex = new Regex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase);

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            void InspectLine(string? line)
            {
                if (string.IsNullOrWhiteSpace(line)) return;
                var m = urlRegex.Match(line);
                if (m.Success)
                {
                    urlTcs.TrySetResult(m.Value.Trim());
                }
            }

            proc.OutputDataReceived += (_, e) => InspectLine(e.Data);
            proc.ErrorDataReceived += (_, e) => InspectLine(e.Data);
            proc.Exited += (_, _) =>
            {
                urlTcs.TrySetResult(null);
                lock (_lock)
                {
                    if (_tunnelProcess == proc)
                    {
                        IsTunnelActive = false;
                        ActiveTunnelUrl = null;
                        TunnelStatusMessage = "Tunnel disconnected.";
                        TunnelStateChanged?.Invoke(null);
                    }
                }
            };

            if (!proc.Start())
                return (false, "Failed to start cloudflared process.");

            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            lock (_lock)
            {
                _tunnelProcess = proc;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(25));
            using var reg = timeoutCts.Token.Register(() => urlTcs.TrySetResult(null));

            var discoveredUrl = await urlTcs.Task;
            if (string.IsNullOrWhiteSpace(discoveredUrl))
            {
                StopTunnel();
                return (false, "Timed out waiting for Cloudflare Quick Tunnel URL. Check internet connection or firewall.");
            }

            lock (_lock)
            {
                IsTunnelActive = true;
                ActiveTunnelUrl = discoveredUrl;
                TunnelStatusMessage = $"HTTPS Tunnel Active: {discoveredUrl}";
            }

            _logger.LogInfo($"[TUNNEL] Encrypted HTTPS Quick Tunnel ready: {discoveredUrl}");
            TunnelStateChanged?.Invoke(discoveredUrl);
            return (true, discoveredUrl);
        }
        catch (Exception ex)
        {
            StopTunnel();
            _logger.LogWarn($"[TUNNEL] Failed to start Quick Tunnel: {ex.Message}");
            return (false, $"Quick Tunnel error: {ex.Message}");
        }
    }

    public static bool VerifyCloudflaredSignature(string exePath, out string detail)
    {
        detail = "";
        try
        {
            if (!File.Exists(exePath)) { detail = "Binary missing."; return false; }
#pragma warning disable SYSLIB0057 // Authenticode PE signature extraction API on Windows
            using var baseCert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(exePath);
            using var cert = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(baseCert.GetRawCertData());
#pragma warning restore SYSLIB0057
            var subject = cert.Subject ?? "";
            if (!subject.Contains("Cloudflare", StringComparison.OrdinalIgnoreCase))
            {
                detail = $"Unexpected signer '{subject}' (expected Cloudflare, Inc.).";
                return false;
            }
            using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
            chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.Online;
            chain.ChainPolicy.VerificationFlags = System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreCertificateAuthorityRevocationUnknown
                                                | System.Security.Cryptography.X509Certificates.X509VerificationFlags.IgnoreEndRevocationUnknown;
            bool chainOk = chain.Build(cert);
            if (!chainOk)
            {
                detail = "Authenticode certificate chain validation failed.";
                return false;
            }
            detail = $"Authenticode verified ({subject})";
            return true;
        }
        catch (Exception ex)
        {
            detail = $"Authenticode signature check failed: {ex.Message}";
            return false;
        }
    }

    public static void KillAllOrphanCloudflaredProcesses()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("cloudflared"))
            {
                try { p.Kill(true); } catch { }
            }
        }
        catch { }
    }

    public void StopTunnel()
    {
        Process? toKill;
        lock (_lock)
        {
            toKill = _tunnelProcess;
            _tunnelProcess = null;
            IsTunnelActive = false;
            ActiveTunnelUrl = null;
            TunnelStatusMessage = "Inactive";
        }

        if (toKill != null)
        {
            try
            {
                if (!toKill.HasExited)
                    toKill.Kill(entireProcessTree: true);
            }
            catch { }
            try { toKill.Dispose(); } catch { }
            _logger.LogInfo("[TUNNEL] Stopped HTTPS Quick Tunnel.");
            TunnelStateChanged?.Invoke(null);
        }

        KillAllOrphanCloudflaredProcesses();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopTunnel();
    }
}
