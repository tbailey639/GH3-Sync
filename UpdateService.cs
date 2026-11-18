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
    public long InstallerSizeBytes { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Checks GitHub Releases for newer versions of GH3-Sync, downloads GH3-Sync-Setup.exe
/// with live progress reporting, and launches the installer cleanly.
/// </summary>
public class UpdateService
{
    public const string CurrentAppVersion = "1.5.0-beta.5";
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
            long installerSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var an) ? (an.GetString() ?? "") : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerUrl = asset.TryGetProperty("browser_download_url", out var dl) ? (dl.GetString() ?? "") : "";
                        installerSize = asset.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0;
                        break;
                    }
                }
            }

            result.Success = true;
            result.LatestVersion = tagName.TrimStart('v', 'V');
            result.ReleaseName = releaseName;
            result.ReleaseNotes = body;
            result.ReleaseUrl = htmlUrl;
            result.InstallerDownloadUrl = installerUrl;
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
        CancellationToken ct = default)
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
            var targetFile = Path.Combine(tempDir, $"GH3-Sync-Setup-{safeVer}.exe");

            onProgress?.Invoke(0, $"Connecting to GitHub to download v{updateInfo.LatestVersion}...");
            _logger?.LogInfo($"[UPDATER] Downloading update from {updateInfo.InstallerDownloadUrl}");

            using var resp = await _http.GetAsync(updateInfo.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? updateInfo.InstallerSizeBytes;
            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(targetFile);

            var buffer = new byte[81920];
            long downloaded = 0;
            int read;
            int lastPct = -1;

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
                        onProgress?.Invoke(pct, $"Downloading update... {pct}% ({dlMb:F1} / {totMb:F1} MB)");
                    }
                }
            }

            await dst.FlushAsync(ct);

            var info = new FileInfo(targetFile);
            if (!info.Exists || info.Length < 1024 * 1024)
            {
                return (false, "", "Downloaded installer appears incomplete or corrupted.");
            }

            onProgress?.Invoke(100, "Download complete! Launching installer...");
            _logger?.LogInfo($"[UPDATER] Downloaded installer ({info.Length:N0} bytes) to {targetFile}");
            return (true, targetFile, "");
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[UPDATER] Download failed: {ex.Message}");
            return (false, "", $"Update download failed: {ex.Message}");
        }
    }

    public bool LaunchInstallerAndPrepareExit(string installerPath)
    {
        try
        {
            if (!File.Exists(installerPath)) return false;

            Process.Start(new ProcessStartInfo
            {
                FileName = installerPath,
                Arguments = "/CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[UPDATER] Failed to launch installer: {ex.Message}");
            return false;
        }
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
