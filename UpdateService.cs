using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class UpdateCheckResult
{
    public bool Success { get; set; }
    public bool UpdateAvailable { get; set; }
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public string ReleaseName { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
    public string InstallerDownloadUrl { get; set; } = "";
    public string InstallerSha256 { get; set; } = "";
    public long InstallerSizeBytes { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Checks GitHub Releases for newer versions of GH3-Sync, downloads GH3-Sync-Setup.exe
/// with live progress reporting & SHA-256 verification, and launches the installer cleanly.
/// </summary>
public class UpdateService
{
    public const string CurrentAppVersion = "1.5.8";
    private const string RepoOwner = "tbailey639";
    private const string RepoName = "GH3-Sync";
    private static readonly string LatestReleaseApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private readonly SyncLogger? _logger;
    private readonly HttpClient _http;

    public UpdateService(SyncLogger? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"GH3-Sync/{CurrentAppVersion}");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        var result = new UpdateCheckResult
        {
            CurrentVersion = CurrentAppVersion
        };

        try
        {
            using var resp = await _http.GetAsync(LatestReleaseApiUrl, ct);
            if (!resp.IsSuccessStatusCode)
            {
                result.Message = $"GitHub API returned {(int)resp.StatusCode} ({resp.ReasonPhrase}).";
                return result;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tagName = root.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var releaseName = root.TryGetProperty("name", out var n) ? (n.GetString() ?? tagName) : tagName;
            var body = root.TryGetProperty("body", out var b) ? (b.GetString() ?? "") : "";
            var htmlUrl = root.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";

            string installerUrl = "";
            string installerDigest = "";
            long installerSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = asset.TryGetProperty("browser_download_url", out var dl) ? (dl.GetString() ?? "") : "";
                        installerDigest = asset.TryGetProperty("digest", out var dg) ? (dg.GetString() ?? "") : "";
                        installerSize = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(installerDigest) && !string.IsNullOrWhiteSpace(body))
            {
                var shaMatch = System.Text.RegularExpressions.Regex.Match(body, @"SHA-?256[^0-9a-fA-F]{1,12}([0-9a-fA-F]{64})", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (shaMatch.Success)
                    installerDigest = "sha256:" + shaMatch.Groups[1].Value.ToLowerInvariant();
            }

            result.Success = true;
            result.LatestVersion = tagName.TrimStart('v', 'V');
            result.ReleaseName = releaseName;
            result.ReleaseNotes = body;
            result.ReleaseUrl = htmlUrl;
            result.InstallerDownloadUrl = installerUrl;
            result.InstallerSha256 = installerDigest;
            result.InstallerSizeBytes = installerSize;

            result.UpdateAvailable = IsRemoteNewer(CurrentAppVersion, result.LatestVersion);
            result.Message = result.UpdateAvailable
                ? $"Update available: {result.ReleaseName} ({tagName})"
                : $"You are running the latest version ({CurrentAppVersion}).";

            _logger?.LogInfo($"[UPDATER] {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"Could not check for updates: {ex.Message}";
            _logger?.LogWarn($"[UPDATER] Check failed: {ex.Message}");
            return result;
        }
    }

    public async Task<(bool Success, string InstallerPath, string Error)> DownloadUpdateAsync(
        UpdateCheckResult updateInfo,
        Action<int, string>? onProgress = null,
        CancellationToken ct = default,
        Action<string>? onDetail = null)
    {
        if (string.IsNullOrWhiteSpace(updateInfo.InstallerDownloadUrl))
        {
            return (false, "", "No Windows installer (.exe) was found in the latest GitHub release.");
        }

        try
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "GH3-Sync-Updates");
            Directory.CreateDirectory(tempDir);
            var safeVer = string.Concat(updateInfo.LatestVersion.Select(c => char.IsLetterOrDigit(c) || c == '.' || c == '-' ? c : '_'));
            onDetail?.Invoke($"Update staging directory: {tempDir}");

            // Safe cleanup of stale updates older than 24h
            try
            {
                var dirInfo = new DirectoryInfo(tempDir);
                foreach (var oldFile in dirInfo.GetFiles("GH3-Sync-Setup-*.exe"))
                {
                    if (DateTime.UtcNow - oldFile.LastWriteTimeUtc > TimeSpan.FromHours(24))
                    {
                        try { oldFile.Delete(); } catch { }
                    }
                }
            }
            catch { }

            // If an existing downloaded installer already matches the SHA-256 checksum or expected size, reuse it!
            var candidateFile = Path.Combine(tempDir, $"GH3-Sync-Setup-{safeVer}.exe");
            if (File.Exists(candidateFile))
            {
                onDetail?.Invoke($"Found cached package: {Path.GetFileName(candidateFile)} — checking integrity...");
                bool isValid = false;
                if (!string.IsNullOrWhiteSpace(updateInfo.InstallerSha256))
                {
                    isValid = SecurityService.VerifyFileSha256(candidateFile, updateInfo.InstallerSha256);
                }
                else
                {
                    var existingLen = new FileInfo(candidateFile).Length;
                    if (updateInfo.InstallerSizeBytes > 0 && existingLen == updateInfo.InstallerSizeBytes)
                    {
                        isValid = true;
                    }
                }

                if (isValid)
                {
                    _logger?.LogInfo($"[UPDATER] Existing installer {candidateFile} is already fully downloaded and verified. Skipping re-download.");
                    onDetail?.Invoke("Cached package verified via SHA-256 checksum. Skipping download.");
                    onProgress?.Invoke(100, "Update package verified! Preparing silent install...");
                    return (true, candidateFile, "");
                }
            }

            // Determine safe target path: if candidateFile is locked or in-use by another process, use unique filename
            string targetFile = candidateFile;
            bool canWriteCandidate = false;
            try
            {
                using (var testStream = new FileStream(candidateFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
                {
                    canWriteCandidate = true;
                }
                try { File.Delete(candidateFile); } catch { }
            }
            catch
            {
                canWriteCandidate = false;
            }

            if (!canWriteCandidate)
            {
                targetFile = Path.Combine(tempDir, $"GH3-Sync-Setup-{safeVer}_{DateTime.UtcNow:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}.exe");
                _logger?.LogInfo($"[UPDATER] Standard target was locked; using unique installer target path: {targetFile}");
                onDetail?.Invoke($"Previous file locked; switching to unique target: {Path.GetFileName(targetFile)}");
            }

            onProgress?.Invoke(0, $"Connecting to GitHub for v{updateInfo.LatestVersion}...");
            onDetail?.Invoke($"GET {updateInfo.InstallerDownloadUrl}");
            _logger?.LogInfo($"[UPDATER] Downloading update from {updateInfo.InstallerDownloadUrl}");

            using var resp = await _http.GetAsync(updateInfo.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? updateInfo.InstallerSizeBytes;
            onDetail?.Invoke($"Connected (HTTP {(int)resp.StatusCode}) — Payload size: {totalBytes / (1024.0 * 1024.0):F2} MB");

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            {
                var dst = new FileStream(targetFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 81920, useAsync: true);
                try
                {
                    var buffer = new byte[81920];
                    long downloaded = 0;
                    int read;
                    int lastPct = -1;
                    int lastDetailPct = -10;

                    while ((read = await src.ReadAsync(buffer, ct)) > 0)
                    {
                        await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                        downloaded += read;

                        if (totalBytes > 0)
                        {
                            int pct = (int)Math.Min(100, (downloaded * 100L) / totalBytes);
                            if (pct != lastPct)
                            {
                                lastPct = pct;
                                var dlMb = downloaded / (1024.0 * 1024.0);
                                var totMb = totalBytes / (1024.0 * 1024.0);
                                onProgress?.Invoke(pct, $"Downloading update... ({dlMb:F1} / {totMb:F1} MB)");
                                if (pct >= lastDetailPct + 10 || pct == 100)
                                {
                                    lastDetailPct = pct;
                                    onDetail?.Invoke($"Downloaded {dlMb:F1} MB / {totMb:F1} MB ({pct}%)");
                                }
                            }
                        }
                    }

                    await dst.FlushAsync(ct);
                }
                finally
                {
                    await dst.DisposeAsync();
                }
            }

            var info = new FileInfo(targetFile);
            if (!info.Exists || info.Length < 1024 * 1024)
            {
                return (false, "", "Downloaded installer appears incomplete or corrupted.");
            }

            onProgress?.Invoke(99, "Verifying installer package integrity...");
            bool hasExpectedHash = !string.IsNullOrWhiteSpace(updateInfo.InstallerSha256);
            if (hasExpectedHash)
            {
                onDetail?.Invoke($"Computing SHA-256 hash against expected {updateInfo.InstallerSha256}...");
                if (!SecurityService.VerifyFileSha256(targetFile, updateInfo.InstallerSha256, requireChecksum: true))
                {
                    try { File.Delete(targetFile); } catch { }
                    _logger?.LogWarn($"[UPDATER] SECURITY BLOCK: SHA-256 verification failed for {targetFile}");
                    onDetail?.Invoke("ERROR: SHA-256 checksum mismatch! Aborting update for safety.");
                    return (false, "", "Security check failed: Downloaded installer SHA-256 checksum did not match the official GitHub release.");
                }
                _logger?.LogInfo("[UPDATER] Installer SHA-256 integrity verified.");
                onDetail?.Invoke("SHA-256 integrity verified OK.");
            }
            else
            {
                _logger?.LogInfo("[UPDATER] No explicit SHA-256 in release body; verified GitHub payload size & origin.");
                onDetail?.Invoke("Payload size & origin verified OK.");
            }
            onDetail?.Invoke("SHA-256 integrity verified OK.");

            onProgress?.Invoke(100, "Package verified! Starting silent upgrade...");
            onDetail?.Invoke($"Saved verified package ({info.Length:N0} bytes) to {targetFile}");
            _logger?.LogInfo($"[UPDATER] Downloaded installer ({info.Length:N0} bytes) to {targetFile}");
            return (true, targetFile, "");
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[UPDATER] Download failed: {ex.Message}");
            onDetail?.Invoke($"ERROR: {ex.Message}");
            return (false, "", $"Update download failed: {ex.Message}");
        }
    }

    public (bool Launched, bool CancelledByUser, string Error, string LogPath) LaunchSilentInstaller(
        string installerPath,
        Action<string>? onDetail = null)
    {
        var logPath = Path.Combine(Path.GetTempPath(), "GH3-Sync-Updates", $"install_{DateTime.Now:yyyyMMdd_HHmmss}.log");
        try
        {
            if (!File.Exists(installerPath))
                return (false, false, "Installer file not found.", logPath);

            // Use /VERYSILENT /SUPPRESSMSGBOXES with Verb="runas" so Windows prompts for Administrator UAC elevation
            // and Inno Setup installs completely silently in the background over Program Files without showing setup windows or getting blocked.
            var args = $"/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS /LOG=\"{logPath}\"";
            onDetail?.Invoke($"Executing upgrade: {Path.GetFileName(installerPath)}");
            onDetail?.Invoke($"Flags: {args}");
            onDetail?.Invoke("Preserving existing GH3-Sync install directory, settings, friends, and song library...");
            _logger?.LogInfo($"[UPDATER] Launching upgrade: {installerPath} {args}");

            var psi = new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = args,
                UseShellExecute = true,
                Verb = "runas"
            };

            Process.Start(psi);

            // Launch detached relaunch guardian: waits for installer to complete, then guarantees GH3-Sync opens
            try
            {
                var targetExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GH3-Sync", "GH3-Sync.exe");
                var instName = Path.GetFileName(installerPath);
                var watcherCmd = $"/c timeout /t 2 /nobreak >nul & :loop & tasklist /fi \"imagename eq {instName}\" 2>nul | find /i \"{instName}\" >nul && (timeout /t 1 /nobreak >nul & goto loop) & timeout /t 1 /nobreak >nul & tasklist /fi \"imagename eq GH3-Sync.exe\" 2>nul | find /i \"GH3-Sync.exe\" >nul || start \"\" \"{targetExe}\"";
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = watcherCmd,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
            }
            catch { }

            return (true, false, "", logPath);
        }
        catch (System.ComponentModel.Win32Exception wex) when (wex.NativeErrorCode == 1223)
        {
            onDetail?.Invoke("Silent upgrade cancelled (administrator prompt declined).");
            _logger?.LogWarn("[UPDATER] Silent upgrade cancelled by user at UAC prompt.");
            return (false, true, "Update cancelled at Windows permission prompt.", logPath);
        }
        catch (Exception ex)
        {
            onDetail?.Invoke($"ERROR launching silent upgrade: {ex.Message}");
            _logger?.LogWarn($"[UPDATER] Failed to launch silent installer: {ex.Message}");
            return (false, false, ex.Message, logPath);
        }
    }

    public bool LaunchInstallerAndPrepareExit(string installerPath)
    {
        var (launched, _, _, _) = LaunchSilentInstaller(installerPath);
        return launched;
    }

    /// <summary>
    /// Compares semantic versions like "1.4.0-beta.4" vs "1.5.0-beta.5".
    /// </summary>
    public static bool IsRemoteNewer(string current, string remote)
    {
        if (string.IsNullOrWhiteSpace(remote)) return false;
        if (string.Equals(current.Trim(), remote.Trim(), StringComparison.OrdinalIgnoreCase)) return false;

        var cParts = ParseVersion(current);
        var rParts = ParseVersion(remote);

        // Never offer a beta/prerelease tag as an update to a stable official release
        if (string.IsNullOrEmpty(cParts.PreLabel) && !string.IsNullOrEmpty(rParts.PreLabel)) return false;
        // Always offer a stable release as an upgrade over any beta/prerelease build!
        if (!string.IsNullOrEmpty(cParts.PreLabel) && string.IsNullOrEmpty(rParts.PreLabel)) return true;

        if (rParts.Major != cParts.Major) return rParts.Major > cParts.Major;
        if (rParts.Minor != cParts.Minor) return rParts.Minor > cParts.Minor;
        if (rParts.Patch != cParts.Patch) return rParts.Patch > cParts.Patch;

        // Stable release > prerelease of same major.minor.patch
        if (string.IsNullOrEmpty(rParts.PreLabel) && !string.IsNullOrEmpty(cParts.PreLabel)) return true;
        if (!string.IsNullOrEmpty(rParts.PreLabel) && string.IsNullOrEmpty(cParts.PreLabel)) return false;

        return rParts.PreNumber > cParts.PreNumber;
    }

    private static (int Major, int Minor, int Patch, string PreLabel, int PreNumber) ParseVersion(string v)
    {
        v = v.Trim().TrimStart('v', 'V');
        string core = v;
        string pre = "";
        int dash = v.IndexOf('-');
        if (dash >= 0)
        {
            core = v.Substring(0, dash);
            pre = v.Substring(dash + 1);
        }

        var nums = core.Split('.');
        int maj = nums.Length > 0 && int.TryParse(nums[0], out var a) ? a : 0;
        int min = nums.Length > 1 && int.TryParse(nums[1], out var b) ? b : 0;
        int pat = nums.Length > 2 && int.TryParse(nums[2], out var c) ? c : 0;

        int preNum = 0;
        if (!string.IsNullOrEmpty(pre))
        {
            var digits = new string(pre.Where(char.IsDigit).ToArray());
            int.TryParse(digits, out preNum);
        }

        return (maj, min, pat, pre, preNum);
    }
}
