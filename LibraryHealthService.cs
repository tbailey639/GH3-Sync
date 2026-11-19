using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GH3_Sync;

public class HealthFinding
{
    public string Category { get; set; } = "";
    public string Severity { get; set; } = "Warning"; // OK, Warning, Error
    public string Message { get; set; } = "";
    public bool AutoFixable { get; set; }
}

public class LibraryHealthReport
{
    public List<HealthFinding> Findings { get; set; } = new();
    public List<string> OrphanedSongIds { get; set; } = new();
    public bool CustomsPakNeedsRepair { get; set; }
    public int IssueCount => Findings.Count(f => f.Severity != "OK");
    public string Summary => IssueCount == 0
        ? "Library is 100% healthy! All songs, soundbanks, customs.pak.xen, and BetterGH3 plugins verified."
        : $"Detected {IssueCount} issue(s) across your GH3 library. Click 'Auto-Heal All' to repair safely.";
}

/// <summary>
/// Scans Guitar Hero III customs.pak.xen, DATA\SONGS, DATA\MUSIC, and BetterGH3 PLUGINS\_log.txt
/// for broken/orphaned songs or header corruption, and heals them automatically in 1 click.
/// </summary>
public class LibraryHealthService
{
    private readonly Gh3Service _gh3;
    private readonly BetterGh3TweaksService _tweaks;
    private readonly SaveManager _saves;
    private readonly SyncLogger _logger;

    public LibraryHealthService(Gh3Service gh3, BetterGh3TweaksService tweaks, SaveManager saves, SyncLogger logger)
    {
        _gh3 = gh3;
        _tweaks = tweaks;
        _saves = saves;
        _logger = logger;
    }

    public LibraryHealthReport ScanLibrary()
    {
        var report = new LibraryHealthReport();
        if (!_gh3.IsDetected)
        {
            report.Findings.Add(new HealthFinding { Category = "Game Path", Severity = "Error", Message = "Guitar Hero III installation directory not detected.", AutoFixable = false });
            return report;
        }

        var songsDir = Path.Combine(_gh3.GamePath, "DATA", "SONGS");
        var musicDir = Path.Combine(_gh3.GamePath, "DATA", "MUSIC");

        // 1. Check customs.pak.xen header & orphaned entries
        if (File.Exists(_gh3.CustomsPakPath))
        {
            var header = new byte[4];
            using (var fs = File.OpenRead(_gh3.CustomsPakPath)) _ = fs.Read(header, 0, 4);
            bool magicOk = header[0] == 0xA7 && header[1] == 0xF5 && header[2] == 0x05 && header[3] == 0xC4;
            if (!magicOk)
            {
                report.CustomsPakNeedsRepair = true;
                report.Findings.Add(new HealthFinding { Category = "customs.pak.xen", Severity = "Error", Message = "customs.pak.xen header signature is invalid.", AutoFixable = true });
            }
            else
            {
                var map = CustomsPakService.ReadSongMetadataMap(_gh3.GamePath);
                report.Findings.Add(new HealthFinding { Category = "customs.pak.xen", Severity = "OK", Message = $"Header healthy ({map.Count} song entries readable).", AutoFixable = false });
            }
        }
        else
        {
            report.Findings.Add(new HealthFinding { Category = "customs.pak.xen", Severity = "OK", Message = "Not created yet (initializes on first custom song).", AutoFixable = false });
        }

        // 2. Check installed songs for missing audio (.fsb.xen) or missing chart (.pak.xen)
        var installed = _gh3.GetInstalledCustomSongs().Where(s => !s.IsProject).ToList();
        int missingAudio = 0;
        foreach (var s in installed)
        {
            var fsb = Path.Combine(musicDir, $"{s.SongId}.fsb.xen");
            var pak = Path.Combine(songsDir, $"{s.SongId}_song.pak.xen");
            if (File.Exists(pak) && !File.Exists(fsb))
            {
                missingAudio++;
                report.OrphanedSongIds.Add(s.SongId);
                report.Findings.Add(new HealthFinding
                {
                    Category = "Missing Audio",
                    Severity = "Warning",
                    Message = $"'{s.Title}' ({s.SongId}) has a chart PAK but is missing {s.SongId}.fsb.xen audio!",
                    AutoFixable = true
                });
            }
        }
        if (missingAudio == 0)
            report.Findings.Add(new HealthFinding { Category = "Song Soundbanks", Severity = "OK", Message = $"All {installed.Count} compiled song(s) have matching PAK + FSB files.", AutoFixable = false });

        // 3. Check BetterGH3 PLUGINS\_log.txt
        var plugins = _tweaks.GetPlugins();
        var enabledNonCore = plugins.Where(p => p.IsEnabled && !p.IsCoreProtected).ToList();
        var notInLog = enabledNonCore.Where(p => !p.VerifiedLoadedInLastLaunch).ToList();
        if (plugins.Count == 0)
        {
            report.Findings.Add(new HealthFinding { Category = "BetterGH3", Severity = "Warning", Message = "No BetterGH3 PLUGINS folder detected (vanilla GH3).", AutoFixable = false });
        }
        else if (notInLog.Count == 0)
        {
            report.Findings.Add(new HealthFinding { Category = "BetterGH3 Plugins", Severity = "OK", Message = $"All {enabledNonCore.Count} enabled plugin(s) verified loaded in PLUGINS\\_log.txt.", AutoFixable = false });
        }
        else
        {
            report.Findings.Add(new HealthFinding
            {
                Category = "BetterGH3 Plugins",
                Severity = "Warning",
                Message = $"{notInLog.Count} enabled plugin(s) were not in the last GH3 launch log (launch GH3 once to load: {string.Join(", ", notInLog.Select(p => p.FileName))}).",
                AutoFixable = false
            });
        }

        return report;
    }

    public (bool Ok, string Message) AutoHealLibrary()
    {
        try
        {
            var report = ScanLibrary();
            var actions = new List<string>();

            if (report.CustomsPakNeedsRepair)
            {
                var repaired = CustomsPakService.RepairCustomsPakIfCorrupted(_gh3.GamePath, _logger);
                if (repaired) actions.Add("Repaired customs.pak.xen header");
            }

            var aligned = CustomsPakService.AlignAndSortCustomsPak(_gh3.GamePath, _logger);
            if (aligned) actions.Add("Aligned setlist song slots for multiplayer");

            if (report.OrphanedSongIds.Count > 0)
            {
                _saves.BackupCurrentSaves("Before-AutoHeal-Library", "Before Auto-Heal", _gh3.GamePath);
                var installed = _gh3.GetInstalledCustomSongs();
                foreach (var id in report.OrphanedSongIds)
                {
                    var match = installed.FirstOrDefault(s => s.SongId.Equals(id, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                        _gh3.DeleteCustomSong(match, deleteProjectFolder: false, _saves, _logger);
                }
                actions.Add($"Cleaned {report.OrphanedSongIds.Count} broken song(s) missing audio");
            }

            var (cleaned, bytesFreed, _) = _gh3.CleanDuplicateAndTempFiles(_logger);
            if (cleaned > 0)
                actions.Add($"Removed {cleaned} temp/duplicate file(s) ({bytesFreed / (1024.0 * 1024.0):F1} MB freed)");

            var summary = actions.Count > 0
                ? "Auto-Heal complete: " + string.Join(" • ", actions)
                : "Library is already clean — no repairs needed!";
            _logger.LogInfo($"[AUTO-HEAL] {summary}");
            return (true, summary);
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[AUTO-HEAL] Error: {ex.Message}");
            return (false, $"Auto-Heal failed: {ex.Message}");
        }
    }
}
