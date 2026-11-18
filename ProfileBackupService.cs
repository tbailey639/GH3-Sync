using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GH3_Sync;

/// <summary>
/// Passphrase-encrypted Profile Backup (.gh3backup) export & restore:
/// Protects config.json (Trusted Friend tokens, scores, lobby setlists, preferences),
/// custom Highway Skins, and Career Save Backups with PBKDF2-SHA256 (150k iterations) + AES-256-GCM.
/// </summary>
public static class ProfileBackupService
{
    public static string AppDataRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync");

    public static (bool Ok, string Message, long SizeBytes) ExportEncryptedBackup(
        string outputFilePath,
        string passphrase,
        SyncLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(passphrase) || passphrase.Length < 6)
            return (false, "Passphrase must be at least 6 characters long.", 0);

        try
        {
            AppConfig.Save();

            using var zipMs = new MemoryStream();
            int fileCount = 0;
            using (var archive = new ZipArchive(zipMs, ZipArchiveMode.Create, leaveOpen: true))
            {
                // 1. config.json
                var cfgPath = Path.Combine(AppDataRoot, "config.json");
                if (File.Exists(cfgPath))
                {
                    archive.CreateEntryFromFile(cfgPath, "config.json", CompressionLevel.Optimal);
                    fileCount++;
                }

                // 2. Custom Highway Skins
                var skinsDir = Path.Combine(AppDataRoot, "HighwaySkins");
                if (Directory.Exists(skinsDir))
                {
                    foreach (var f in Directory.GetFiles(skinsDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(AppDataRoot, f).Replace('\\', '/');
                        archive.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                        fileCount++;
                    }
                }

                // 3. Save Backups (cap individual file size safely)
                var savesDir = Path.Combine(AppDataRoot, "SaveBackups");
                if (Directory.Exists(savesDir))
                {
                    foreach (var f in Directory.GetFiles(savesDir, "*", SearchOption.AllDirectories))
                    {
                        if (new FileInfo(f).Length > 15 * 1024 * 1024) continue;
                        var rel = Path.GetRelativePath(AppDataRoot, f).Replace('\\', '/');
                        archive.CreateEntryFromFile(f, rel, CompressionLevel.Optimal);
                        fileCount++;
                    }
                }
            }

            var encrypted = SecurityService.EncryptWithPassphrase(zipMs.ToArray(), passphrase);
            var outDir = Path.GetDirectoryName(outputFilePath);
            if (!string.IsNullOrWhiteSpace(outDir)) Directory.CreateDirectory(outDir);
            File.WriteAllBytes(outputFilePath, encrypted);

            var msg = $"Encrypted backup saved ({fileCount} files, {encrypted.Length / 1024.0:F1} KB) using AES-256-GCM.";
            logger?.LogInfo($"[BACKUP] {msg} -> {outputFilePath}");
            return (true, msg, encrypted.Length);
        }
        catch (Exception ex)
        {
            logger?.LogWarn($"[BACKUP] Export failed: {ex.Message}");
            return (false, $"Backup export failed: {ex.Message}", 0);
        }
    }

    public static (bool Ok, string Message, int RestoredFiles) ImportEncryptedBackup(
        string backupFilePath,
        string passphrase,
        SyncLogger? logger = null)
    {
        if (!File.Exists(backupFilePath))
            return (false, "Backup file not found.", 0);
        if (string.IsNullOrWhiteSpace(passphrase))
            return (false, "Passphrase is required to decrypt backup.", 0);

        try
        {
            var raw = File.ReadAllBytes(backupFilePath);
            var zipBytes = SecurityService.DecryptWithPassphrase(raw, passphrase);

            Directory.CreateDirectory(AppDataRoot);
            var destRoot = Path.GetFullPath(AppDataRoot);
            if (!destRoot.EndsWith(Path.DirectorySeparatorChar)) destRoot += Path.DirectorySeparatorChar;

            long totalWritten = 0;
            int restored = 0;
            using var ms = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

            if (archive.Entries.Count > 500)
                return (false, "Backup contains too many entries (security limit exceeded).", 0);

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                var normalized = entry.FullName.Replace('\\', '/').TrimStart('/');
                if (!(normalized.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                      normalized.StartsWith("HighwaySkins/", StringComparison.OrdinalIgnoreCase) ||
                      normalized.StartsWith("SaveBackups/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue; // Only allow known safe profile folders
                }

                var targetPath = Path.GetFullPath(Path.Combine(AppDataRoot, normalized));
                if (!targetPath.StartsWith(destRoot, StringComparison.OrdinalIgnoreCase))
                    throw new System.Security.SecurityException("Blocked path traversal entry inside backup.");

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                SecurityService.CopyStreamWithLimit(entry, targetPath, 25 * 1024 * 1024, ref totalWritten, 150 * 1024 * 1024);
                restored++;
            }

            AppConfig.Load();
            var msg = $"Successfully decrypted & restored {restored} profile file(s)!";
            logger?.LogInfo($"[BACKUP] {msg}");
            return (true, msg, restored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return (false, "Incorrect passphrase or corrupted .gh3backup file (AES-GCM authentication failed).", 0);
        }
        catch (Exception ex)
        {
            logger?.LogWarn($"[BACKUP] Restore failed: {ex.Message}");
            return (false, $"Backup restore failed: {ex.Message}", 0);
        }
    }
}
