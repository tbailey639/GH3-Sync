using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GH3_Sync;

public class SaveBackupInfo
{
    public string BackupId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public string Reason { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Path { get; set; } = "";
    public int FileCount { get; set; }
    public bool IncludesCustomsPak { get; set; }
}

public class SaveManager
{
    private readonly SyncLogger _logger;
    private readonly string? _gamePath;

    public static string SaveFolder
    {
        get
        {
            var userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(userDocs, "Aspyr", "Guitar Hero III", "Save");
        }
    }

    public static string BackupFolder
    {
        get
        {
            var appDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "SaveBackups");
            if (!Directory.Exists(appDir)) Directory.CreateDirectory(appDir);
            return appDir;
        }
    }

    public SaveManager(SyncLogger logger, string? gamePath = null)
    {
        _logger = logger;
        _gamePath = gamePath;
    }

    public bool HasSaves => Directory.Exists(SaveFolder) && Directory.GetFiles(SaveFolder, "*.*", SearchOption.AllDirectories).Length > 0;

    private string ResolveCustomsPakPath(string? overrideGamePath = null)
    {
        var gp = !string.IsNullOrWhiteSpace(overrideGamePath) ? overrideGamePath
               : !string.IsNullOrWhiteSpace(AppConfig.Current.GamePath) ? AppConfig.Current.GamePath
               : _gamePath ?? "";
        return string.IsNullOrWhiteSpace(gp) ? "" : Path.Combine(gp, "DATA", "customs.pak.xen");
    }

    /// <summary>
    /// Creates a paired snapshot of BOTH Guitar Hero III career saves (s000.d, toc.dat)
    /// AND the matching DATA\customs.pak.xen so restoring never causes corrupt-save mismatches.
    /// </summary>
    public SaveBackupInfo BackupCurrentSaves(string reason = "Manual Snapshot", string? profileName = null, string? gamePath = null)
    {
        var info = new SaveBackupInfo
        {
            Reason = reason,
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
        };

        var safeName = string.IsNullOrWhiteSpace(profileName)
            ? $"SaveBackup_{DateTime.Now:yyyyMMdd_HHmmss}"
            : $"Profile_{Sanitize(profileName)}_{DateTime.Now:yyyyMMdd_HHmmss}";

        info.BackupId = safeName;
        info.ProfileName = string.IsNullOrWhiteSpace(profileName) ? reason : profileName.Trim();

        var targetDir = Path.Combine(BackupFolder, safeName);
        var saveSubDir = Path.Combine(targetDir, "SaveFiles");
        Directory.CreateDirectory(saveSubDir);
        info.Path = targetDir;

        int count = 0;

        // 1. Copy GH3 career save files (s000.d, toc.dat, etc.)
        if (Directory.Exists(SaveFolder))
        {
            foreach (var dirPath in Directory.GetDirectories(SaveFolder, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(SaveFolder, saveSubDir));
            }
            foreach (var filePath in Directory.GetFiles(SaveFolder, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(filePath, filePath.Replace(SaveFolder, saveSubDir), true);
                count++;
            }
        }
        else
        {
            _logger.LogWarn("[SAVES] No existing GH3 save folder found to back up.");
        }

        // 2. Pair matching DATA\customs.pak.xen so Restore stays 100% compatible with GH3!
        var customsPak = ResolveCustomsPakPath(gamePath);
        if (!string.IsNullOrEmpty(customsPak) && File.Exists(customsPak))
        {
            try
            {
                File.Copy(customsPak, Path.Combine(targetDir, "customs.pak.xen"), true);
                info.IncludesCustomsPak = true;
                count++;
            }
            catch (Exception ex)
            {
                _logger.LogWarn($"[SAVES] Could not pair customs.pak.xen in snapshot: {ex.Message}");
            }
        }

        info.FileCount = count;

        try
        {
            File.WriteAllText(
                Path.Combine(targetDir, "snapshot.json"),
                JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }

        _logger.LogInfo($"[SAVES] Snapshot '{info.ProfileName}' saved ({count} files, customs.pak paired: {info.IncludesCustomsPak}).");
        return info;
    }

    public bool RestoreLatestBackup(string? gamePath = null)
    {
        var list = GetBackups();
        if (list.Count == 0) return false;
        return RestoreBackup(list[0].BackupId, gamePath);
    }

    /// <summary>
    /// Restores a specific paired snapshot (career saves + matching customs.pak.xen).
    /// </summary>
    public bool RestoreBackup(string backupId, string? gamePath = null)
    {
        var snapshotDir = Path.Combine(BackupFolder, backupId);
        if (!Directory.Exists(snapshotDir))
        {
            _logger.LogWarn($"[SAVES] Snapshot not found: {backupId}");
            return false;
        }

        try
        {
            // Support both new paired layout (SaveFiles/) and legacy flat backups
            var saveSrcDir = Directory.Exists(Path.Combine(snapshotDir, "SaveFiles"))
                ? Path.Combine(snapshotDir, "SaveFiles")
                : snapshotDir;

            if (!Directory.Exists(SaveFolder)) Directory.CreateDirectory(SaveFolder);

            foreach (var dirPath in Directory.GetDirectories(saveSrcDir, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(saveSrcDir, SaveFolder));
            }

            foreach (var filePath in Directory.GetFiles(saveSrcDir, "*.*", SearchOption.AllDirectories))
            {
                var fname = Path.GetFileName(filePath);
                if (fname.Equals("snapshot.json", StringComparison.OrdinalIgnoreCase) ||
                    fname.Equals("customs.pak.xen", StringComparison.OrdinalIgnoreCase))
                    continue;

                File.Copy(filePath, filePath.Replace(saveSrcDir, SaveFolder), true);
            }

            // Also restore paired customs.pak.xen if included in snapshot!
            var pairedPak = Path.Combine(snapshotDir, "customs.pak.xen");
            var targetPak = ResolveCustomsPakPath(gamePath);
            if (File.Exists(pairedPak) && !string.IsNullOrEmpty(targetPak))
            {
                var dataDir = Path.GetDirectoryName(targetPak);
                if (!string.IsNullOrEmpty(dataDir) && Directory.Exists(dataDir))
                {
                    File.Copy(pairedPak, targetPak, true);
                    _logger.LogInfo($"[SAVES] Restored paired customs.pak.xen alongside career save.");
                }
            }

            _logger.LogInfo($"[SAVES] Successfully restored snapshot: {backupId}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError($"[SAVES] Failed to restore snapshot '{backupId}'", ex);
            return false;
        }
    }

    public bool DeleteBackup(string backupId)
    {
        var snapshotDir = Path.Combine(BackupFolder, backupId);
        if (!Directory.Exists(snapshotDir)) return false;
        try
        {
            Directory.Delete(snapshotDir, true);
            return true;
        }
        catch { return false; }
    }

    public List<SaveBackupInfo> GetBackups()
    {
        var list = new List<SaveBackupInfo>();
        if (!Directory.Exists(BackupFolder)) return list;

        foreach (var dir in Directory.GetDirectories(BackupFolder).OrderByDescending(d => Directory.GetCreationTime(d)))
        {
            var metaPath = Path.Combine(dir, "snapshot.json");
            if (File.Exists(metaPath))
            {
                try
                {
                    var loaded = JsonSerializer.Deserialize<SaveBackupInfo>(File.ReadAllText(metaPath));
                    if (loaded != null)
                    {
                        loaded.Path = dir;
                        loaded.BackupId = Path.GetFileName(dir);
                        list.Add(loaded);
                        continue;
                    }
                }
                catch { }
            }

            var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
            var name = Path.GetFileName(dir);
            list.Add(new SaveBackupInfo
            {
                BackupId = name,
                ProfileName = name,
                Reason = "Snapshot",
                Timestamp = Directory.GetCreationTime(dir).ToString("yyyy-MM-dd HH:mm:ss"),
                Path = dir,
                FileCount = files.Length,
                IncludesCustomsPak = File.Exists(Path.Combine(dir, "customs.pak.xen"))
            });
        }
        return list;
    }

    public int PruneOldBackups(int keepCount = 20)
    {
        if (!Directory.Exists(BackupFolder)) return 0;
        int pruned = 0;
        try
        {
            var dirs = Directory.GetDirectories(BackupFolder)
                .OrderByDescending(d => Directory.GetCreationTime(d))
                .ToList();

            if (dirs.Count > keepCount)
            {
                var toDelete = dirs.Skip(keepCount);
                foreach (var d in toDelete)
                {
                    try
                    {
                        Directory.Delete(d, recursive: true);
                        pruned++;
                    }
                    catch { }
                }
            }
        }
        catch { }
        return pruned;
    }

    private static string Sanitize(string s)
    {
        var chars = s.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray();
        var clean = new string(chars);
        return string.IsNullOrEmpty(clean) ? "Profile" : (clean.Length > 24 ? clean[..24] : clean);
    }
}
