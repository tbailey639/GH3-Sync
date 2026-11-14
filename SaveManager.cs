using System;
using System.IO;
using System.Text.Json;

namespace GH3_Sync;

public class SaveBackupInfo
{
    public string BackupId { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public string Path { get; set; } = "";
    public int FileCount { get; set; }
}

public class SaveManager
{
    private readonly SyncLogger _logger;

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

    public SaveManager(SyncLogger logger)
    {
        _logger = logger;
    }

    public bool HasSaves => Directory.Exists(SaveFolder) && Directory.GetFiles(SaveFolder, "*.*", SearchOption.AllDirectories).Length > 0;

    public SaveBackupInfo BackupCurrentSaves(string reason = "Manual Backup")
    {
        if (!Directory.Exists(SaveFolder))
        {
            _logger.LogWarn("[SAVES] No existing save folder found to backup.");
            return new SaveBackupInfo();
        }

        var backupId = $"SaveBackup_{DateTime.Now:yyyyMMdd_HHmmss}";
        var targetDir = Path.Combine(BackupFolder, backupId);
        Directory.CreateDirectory(targetDir);

        int count = 0;
        foreach (var dirPath in Directory.GetDirectories(SaveFolder, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dirPath.Replace(SaveFolder, targetDir));
        }

        foreach (var newPath in Directory.GetFiles(SaveFolder, "*.*", SearchOption.AllDirectories))
        {
            File.Copy(newPath, newPath.Replace(SaveFolder, targetDir), true);
            count++;
        }

        _logger.LogInfo($"[SAVES] Successfully backed up {count} save files to: {backupId} ({reason})");
        return new SaveBackupInfo
        {
            BackupId = backupId,
            Timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Path = targetDir,
            FileCount = count
        };
    }

    public bool RestoreLatestBackup()
    {
        var dirs = Directory.GetDirectories(BackupFolder).OrderByDescending(d => d).ToList();
        if (dirs.Count == 0)
        {
            _logger.LogWarn("[SAVES] No save backups found to restore.");
            return false;
        }

        var latest = dirs[0];
        try
        {
            if (!Directory.Exists(SaveFolder)) Directory.CreateDirectory(SaveFolder);

            foreach (var dirPath in Directory.GetDirectories(latest, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(dirPath.Replace(latest, SaveFolder));
            }

            foreach (var newPath in Directory.GetFiles(latest, "*.*", SearchOption.AllDirectories))
            {
                File.Copy(newPath, newPath.Replace(latest, SaveFolder), true);
            }

            _logger.LogInfo($"[SAVES] Successfully restored save game from latest backup: {Path.GetFileName(latest)}");
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError("[SAVES] Failed to restore save backup", ex);
            return false;
        }
    }

    public List<SaveBackupInfo> GetBackups()
    {
        var list = new List<SaveBackupInfo>();
        if (!Directory.Exists(BackupFolder)) return list;

        foreach (var dir in Directory.GetDirectories(BackupFolder).OrderByDescending(d => d))
        {
            var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
            var name = Path.GetFileName(dir);
            list.Add(new SaveBackupInfo
            {
                BackupId = name,
                Timestamp = Directory.GetCreationTime(dir).ToString("yyyy-MM-dd HH:mm:ss"),
                Path = dir,
                FileCount = files.Length
            });
        }
        return list;
    }
}
