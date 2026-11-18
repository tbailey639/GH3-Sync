using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class ComponentStatusItem
{
    public string Name { get; set; } = "";
    public string InstalledVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public bool IsInstalled { get; set; }
    public string Status { get; set; } = "";
    public string ReleaseUrl { get; set; } = "";
}

public class CompatibilityCheckItem
{
    public string Component { get; set; } = "";
    public bool Passed { get; set; }
    public string Details { get; set; } = "";
}

public class CompatibilityReport
{
    public bool AllPassed => Checks.All(c => c.Passed);
    public DateTime CheckedAt { get; set; } = DateTime.Now;
    public List<CompatibilityCheckItem> Checks { get; set; } = new();
}

/// <summary>
/// Checks updates across all integrated components (GH3-Sync, Honeycomb/GH-Toolkit-Core,
/// BetterGH3, Demucs/Basic Pitch portable tools, FFmpeg), runs a 5-point post-update
/// Compatibility Self-Test, and rolls back stem tool updates automatically if a check fails.
/// </summary>
public class ComponentUpdaterService
{
    private readonly Gh3Service _gh3;
    private readonly UpdateService _appUpdater;
    private readonly DemucsService _demucs;
    private readonly SyncLogger _logger;
    private readonly HttpClient _http;

    public ComponentUpdaterService(Gh3Service gh3, UpdateService appUpdater, DemucsService demucs, SyncLogger logger)
    {
        _gh3 = gh3;
        _appUpdater = appUpdater;
        _demucs = demucs;
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"GH3-Sync/{UpdateService.CurrentAppVersion}");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    public async Task<List<ComponentStatusItem>> CheckAllComponentsAsync(CancellationToken ct = default)
    {
        var items = new List<ComponentStatusItem>();

        // 1. GH3-Sync App
        var appRes = await _appUpdater.CheckForUpdatesAsync(ct);
        items.Add(new ComponentStatusItem
        {
            Name = "GH3-Sync",
            IsInstalled = true,
            InstalledVersion = UpdateService.CurrentAppVersion,
            LatestVersion = string.IsNullOrWhiteSpace(appRes.LatestVersion) ? UpdateService.CurrentAppVersion : appRes.LatestVersion,
            UpdateAvailable = appRes.UpdateAvailable,
            Status = appRes.Success
                ? (appRes.UpdateAvailable ? $"Update available (v{appRes.LatestVersion})" : "Up to date")
                : appRes.Message,
            ReleaseUrl = appRes.ReleaseUrl
        });

        // 2. Honeycomb / GH-Toolkit-Core Compiler
        var hDir = HoneycombCompilerService.FindHoneycombDirectory(AppConfig.Current.HoneycombPath, _gh3.GamePath);
        string localHoneycombVer = "Not Found";
        bool hasHoneycomb = !string.IsNullOrEmpty(hDir) && File.Exists(Path.Combine(hDir, "GH-Toolkit-Core.dll"));
        if (hasHoneycomb)
        {
            try
            {
                var fvi = FileVersionInfo.GetVersionInfo(Path.Combine(hDir, "Honeycomb-GUI.dll"));
                localHoneycombVer = !string.IsNullOrWhiteSpace(fvi.FileVersion) ? fvi.FileVersion : "Bundled";
            }
            catch { localHoneycombVer = "Bundled"; }
        }
        var (hcTag, hcUrl) = await GetLatestGitHubTagAsync("AddyMills", "Honeycomb-GUI", ct);
        items.Add(new ComponentStatusItem
        {
            Name = "Honeycomb Compiler (GH-Toolkit-Core)",
            IsInstalled = hasHoneycomb,
            InstalledVersion = localHoneycombVer,
            LatestVersion = string.IsNullOrEmpty(hcTag) ? localHoneycombVer : hcTag,
            UpdateAvailable = false, // Managed via GH3-Sync verified releases so reflection signatures stay compatible
            Status = hasHoneycomb ? $"Bundled & verified (Upstream: {(string.IsNullOrEmpty(hcTag) ? "n/a" : hcTag)})" : "Missing compiler files",
            ReleaseUrl = hcUrl
        });

        // 3. BetterGH3 Mod
        var compat = _gh3.VerifyGamePath(_gh3.GamePath);
        var (bghTag, bghUrl) = await GetLatestGitHubTagAsync("marchellodev", "BetterGH3", ct);
        items.Add(new ComponentStatusItem
        {
            Name = "BetterGH3 Mod",
            IsInstalled = compat.IsBetterGh3,
            InstalledVersion = compat.IsBetterGh3 ? "Installed" : "Not Detected",
            LatestVersion = string.IsNullOrEmpty(bghTag) ? "Latest" : bghTag,
            UpdateAvailable = !compat.IsBetterGh3,
            Status = compat.IsBetterGh3
                ? $"Active in GH3 folder (Latest upstream: {(string.IsNullOrEmpty(bghTag) ? "n/a" : bghTag)})"
                : "Not detected — install BetterGH3 for customs support",
            ReleaseUrl = string.IsNullOrEmpty(bghUrl) ? "https://github.com/marchellodev/BetterGH3/releases" : bghUrl
        });

        // 4. FFmpeg Audio Engine
        var ffmpegPath = !string.IsNullOrEmpty(hDir) ? HoneycombCompilerService.EnsureFfmpeg(hDir) : "";
        bool hasFfmpeg = !string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath);
        string ffVer = hasFfmpeg ? await GetFfmpegVersionAsync(ffmpegPath, ct) : "Missing";
        items.Add(new ComponentStatusItem
        {
            Name = "FFmpeg Audio Engine",
            IsInstalled = hasFfmpeg,
            InstalledVersion = ffVer,
            LatestVersion = ffVer,
            UpdateAvailable = !hasFfmpeg,
            Status = hasFfmpeg ? $"Ready ({ffVer})" : "Not found"
        });

        // 5. Stem Separator & Pitch Engine (Demucs + Basic Pitch)
        var stemStatus = _demucs.GetStatus();
        var py = stemStatus.ExecutablePath;
        bool hasStems = stemStatus.IsInstalled && !string.IsNullOrEmpty(py);
        string stemVer = hasStems ? await GetPythonPackagesSummaryAsync(py, ct) : "Not Installed (Optional)";
        bool stemOutdated = hasStems && await CheckIfDemucsOutdatedAsync(py, ct);
        items.Add(new ComponentStatusItem
        {
            Name = "Stem Separator & Pitch Engine (Demucs + Basic Pitch)",
            IsInstalled = hasStems,
            InstalledVersion = stemVer,
            LatestVersion = stemOutdated ? "Update available" : stemVer,
            UpdateAvailable = stemOutdated,
            Status = !hasStems
                ? "Optional component (install from Song Creator tab)"
                : (stemOutdated ? "Package update available (safe rollback enabled)" : $"Ready ({stemVer})")
        });

        return items;
    }

    /// <summary>
    /// Runs a 5-point post-update compatibility self-test across all core features so
    /// updates never silently break compilation, audio encoding, customs.pak.xen, or stem separation.
    /// </summary>
    public async Task<CompatibilityReport> RunCompatibilitySelfTestAsync(CancellationToken ct = default)
    {
        var report = new CompatibilityReport();
        _logger.LogInfo("[COMPATIBILITY] Starting 5-point compatibility self-test...");

        // Check 1: Honeycomb & GH-Toolkit-Core reflection entry points
        try
        {
            var hDir = HoneycombCompilerService.FindHoneycombDirectory(AppConfig.Current.HoneycombPath, _gh3.GamePath);
            if (string.IsNullOrEmpty(hDir))
            {
                report.Checks.Add(new CompatibilityCheckItem { Component = "Honeycomb Compiler DLLs", Passed = false, Details = "Compiler directory not found." });
            }
            else
            {
                var coreDll = Path.Combine(hDir, "GH-Toolkit-Core.dll");
                var guiDll = Path.Combine(hDir, "Honeycomb-GUI.dll");
                bool filesOk = File.Exists(coreDll) && File.Exists(guiDll);
                if (!filesOk)
                {
                    report.Checks.Add(new CompatibilityCheckItem { Component = "Honeycomb Compiler DLLs", Passed = false, Details = "Missing GH-Toolkit-Core.dll or Honeycomb-GUI.dll." });
                }
                else
                {
                    var coreAsm = Assembly.LoadFrom(coreDll);
                    var hasPak = coreAsm.GetType("GH_Toolkit_Core.PAK.PAK") != null;
                    var hasQb = coreAsm.GetType("GH_Toolkit_Core.QB.QB") != null;
                    var hasFsb = coreAsm.GetType("GH_Toolkit_Core.Audio.FSB") != null;
                    var hasChart = coreAsm.GetType("GH_Toolkit_Core.MIDI.Chart") != null;
                    bool ok = hasPak && hasQb && hasFsb && hasChart;
                    report.Checks.Add(new CompatibilityCheckItem
                    {
                        Component = "Honeycomb Compiler APIs",
                        Passed = ok,
                        Details = ok ? "PAK, QB, FSB, and MIDI Chart APIs verified." : "One or more required GH-Toolkit-Core types missing."
                    });
                }
            }
        }
        catch (Exception ex)
        {
            report.Checks.Add(new CompatibilityCheckItem { Component = "Honeycomb Compiler APIs", Passed = false, Details = ex.Message });
        }

        // Check 2: FFmpeg execution
        try
        {
            var hDir = HoneycombCompilerService.FindHoneycombDirectory(AppConfig.Current.HoneycombPath, _gh3.GamePath);
            var ff = !string.IsNullOrEmpty(hDir) ? HoneycombCompilerService.EnsureFfmpeg(hDir) : "";
            var ver = !string.IsNullOrEmpty(ff) ? await GetFfmpegVersionAsync(ff, ct) : "";
            bool ok = !string.IsNullOrEmpty(ver) && ver != "Error";
            report.Checks.Add(new CompatibilityCheckItem
            {
                Component = "FFmpeg Audio Encoder",
                Passed = ok,
                Details = ok ? $"Executed cleanly ({ver})." : "ffmpeg.exe could not be executed."
            });
        }
        catch (Exception ex)
        {
            report.Checks.Add(new CompatibilityCheckItem { Component = "FFmpeg Audio Encoder", Passed = false, Details = ex.Message });
        }

        // Check 3: GH3 Game Path, BetterGH3, & customs.pak.xen integrity
        try
        {
            var v = _gh3.VerifyGamePath(_gh3.GamePath);
            if (!v.IsValid)
            {
                report.Checks.Add(new CompatibilityCheckItem { Component = "GH3 & customs.pak.xen", Passed = false, Details = v.StatusMessage });
            }
            else if (File.Exists(_gh3.CustomsPakPath))
            {
                var header = new byte[4];
                using (var fs = File.OpenRead(_gh3.CustomsPakPath)) _ = fs.Read(header, 0, 4);
                bool magicOk = header[0] == 0xA7 && header[1] == 0xF5 && header[2] == 0x05 && header[3] == 0xC4;
                var map = CustomsPakService.ReadSongMetadataMap(_gh3.GamePath);
                report.Checks.Add(new CompatibilityCheckItem
                {
                    Component = "GH3 & customs.pak.xen",
                    Passed = magicOk,
                    Details = magicOk
                        ? $"BetterGH3={(v.IsBetterGh3 ? "Active" : "Vanilla")}, customs.pak.xen valid ({map.Count} song entries readable)."
                        : "customs.pak.xen header check failed."
                });
            }
            else
            {
                report.Checks.Add(new CompatibilityCheckItem
                {
                    Component = "GH3 & customs.pak.xen",
                    Passed = true,
                    Details = $"GH3 detected (BetterGH3={(v.IsBetterGh3 ? "Active" : "Vanilla")}). customs.pak.xen will be created on first song."
                });
            }
        }
        catch (Exception ex)
        {
            report.Checks.Add(new CompatibilityCheckItem { Component = "GH3 & customs.pak.xen", Passed = false, Details = ex.Message });
        }

        // Check 4: Portable Stem Separator / Pitch Engine (if installed)
        try
        {
            var st = _demucs.GetStatus();
            var py = st.ExecutablePath;
            if (!st.IsInstalled || string.IsNullOrEmpty(py))
            {
                report.Checks.Add(new CompatibilityCheckItem
                {
                    Component = "Stem Separator & Pitch Engine",
                    Passed = true,
                    Details = "Not installed yet (optional — built-in DSP note engine active)."
                });
            }
            else
            {
                var (exitCode, output) = await RunProcessOutputAsync(py, "-c \"import torch, demucs, onnxruntime; print('OK:' + torch.__version__)\"", ct);
                bool ok = exitCode == 0 && output.Contains("OK:");
                report.Checks.Add(new CompatibilityCheckItem
                {
                    Component = "Stem Separator & Pitch Engine",
                    Passed = ok,
                    Details = ok ? $"Python stem runtime healthy ({output.Trim()})." : $"Import check failed: {output.Trim()}"
                });
            }
        }
        catch (Exception ex)
        {
            report.Checks.Add(new CompatibilityCheckItem { Component = "Stem Separator & Pitch Engine", Passed = false, Details = ex.Message });
        }

        // Check 5: AppData Storage & Model Cache Writability
        try
        {
            var appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync");
            Directory.CreateDirectory(appData);
            var probe = Path.Combine(appData, ".compat_probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            report.Checks.Add(new CompatibilityCheckItem
            {
                Component = "AppData & Config Storage",
                Passed = true,
                Details = $"Writable ({appData})."
            });
        }
        catch (Exception ex)
        {
            report.Checks.Add(new CompatibilityCheckItem { Component = "AppData & Config Storage", Passed = false, Details = ex.Message });
        }

        _logger.LogInfo($"[COMPATIBILITY] Self-test completed: {report.Checks.Count(c => c.Passed)}/{report.Checks.Count} passed.");
        return report;
    }

    /// <summary>
    /// Safely updates portable Demucs & Basic Pitch packages with a pre-update pip freeze snapshot
    /// and automatic rollback if the post-update compatibility self-test fails.
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateStemToolsWithRollbackAsync(
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var st = _demucs.GetStatus();
        var py = st.ExecutablePath;
        if (!st.IsInstalled || string.IsNullOrEmpty(py))
            return (false, "Portable Stem Engine is not installed yet. Install it from the Song Creator tab first.");

        var snapshotFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GH3-Sync", "tools", "ai", "last_good_requirements.txt");

        try
        {
            onStatus?.Invoke("Capturing rollback snapshot of current Python packages...");
            var (freezeCode, freezeOut) = await RunProcessOutputAsync(py, "-m pip freeze", ct);
            if (freezeCode == 0 && !string.IsNullOrWhiteSpace(freezeOut))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(snapshotFile)!);
                File.WriteAllText(snapshotFile, freezeOut);
            }

            onStatus?.Invoke("Updating Demucs & Basic Pitch packages...");
            var (upCode, upOut) = await RunProcessOutputAsync(py, "-m pip install --upgrade demucs basic-pitch onnxruntime --quiet", ct);

            onStatus?.Invoke("Running post-update compatibility self-test...");
            var report = await RunCompatibilitySelfTestAsync(ct);
            var stemCheck = report.Checks.FirstOrDefault(c => c.Component.Contains("Stem"));

            if (upCode == 0 && (stemCheck == null || stemCheck.Passed))
            {
                return (true, "Stem tools updated and verified compatible!");
            }

            if (AppConfig.Current.AutoRollbackOnFailedUpdate && File.Exists(snapshotFile))
            {
                onStatus?.Invoke("Compatibility check failed — rolling back to previous working versions...");
                _logger.LogWarn("[UPDATER] Post-update stem check failed; rolling back automatically.");
                await RunProcessOutputAsync(py, $"-m pip install -r \"{snapshotFile}\" --quiet", ct);
                return (false, $"Update failed compatibility test and was automatically rolled back. ({stemCheck?.Details ?? upOut})");
            }

            return (false, $"Update finished with compatibility issues: {stemCheck?.Details ?? upOut}");
        }
        catch (Exception ex)
        {
            return (false, $"Stem tool update failed: {ex.Message}");
        }
    }

    private async Task<(string Tag, string Url)> GetLatestGitHubTagAsync(string owner, string repo, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://api.github.com/repos/{owner}/{repo}/releases/latest", ct);
            if (!resp.IsSuccessStatusCode) return ("", "");
            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? (t.GetString() ?? "") : "";
            var url = doc.RootElement.TryGetProperty("html_url", out var h) ? (h.GetString() ?? "") : "";
            return (tag, url);
        }
        catch
        {
            return ("", "");
        }
    }

    private static async Task<string> GetFfmpegVersionAsync(string ffmpegPath, CancellationToken ct)
    {
        var (code, output) = await RunProcessOutputAsync(ffmpegPath, "-version", ct);
        if (code != 0) return "Error";
        var firstLine = output.Split('\n').FirstOrDefault()?.Trim() ?? "";
        var m = Regex.Match(firstLine, @"ffmpeg version\s+([^\s]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "Installed";
    }

    private static async Task<string> GetPythonPackagesSummaryAsync(string pythonExe, CancellationToken ct)
    {
        var (code, output) = await RunProcessOutputAsync(
            pythonExe,
            "-c \"import importlib.metadata as m; print('demucs ' + m.version('demucs') + ', basic-pitch ' + (m.version('basic-pitch') if 'basic-pitch' in [d.metadata['Name'].lower() for d in m.distributions()] else 'n/a'))\"",
            ct);
        return code == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : "Installed";
    }

    private static async Task<bool> CheckIfDemucsOutdatedAsync(string pythonExe, CancellationToken ct)
    {
        var (code, output) = await RunProcessOutputAsync(pythonExe, "-m pip list --outdated --format=json", ct);
        if (code != 0 || string.IsNullOrWhiteSpace(output)) return false;
        return output.Contains("\"demucs\"", StringComparison.OrdinalIgnoreCase) ||
               output.Contains("\"basic-pitch\"", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int ExitCode, string Output)> RunProcessOutputAsync(string exe, string args, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null) return (-1, "Failed to start process");
            var stdout = await proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = await proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            return (proc.ExitCode, string.IsNullOrWhiteSpace(stdout) ? stderr : stdout);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }
}
