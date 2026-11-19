using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GH3_Sync;

public record VaultManifest(
    string VaultVersion,
    string AppVersion,
    string CreatedBy,
    DateTime CreatedAtUtc,
    int SongCount,
    bool IncludesSaveProfiles,
    bool IncludesCustomsPak,
    string PakChecksumSha256,
    string Description
);

public class Gh3VaultBackupService
{
    public const string VaultExtension = ".gh3vault";

    public (bool Success, string Message, string? FilePath) ExportVault(
        string targetFilePath,
        string gamePath,
        string customSongsPath,
        string savesPath,
        Action<int, string>? progressCallback = null)
    {
        try
        {
            progressCallback?.Invoke(10, "Gathering songs, setlist pak, and paired career saves...");

            string tempDir = Path.Combine(Path.GetTempPath(), $"gh3vault_export_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                // 1. Copy Custom_Songs
                string tempSongsDir = Path.Combine(tempDir, "Custom_Songs");
                int songCount = 0;
                if (Directory.Exists(customSongsPath))
                {
                    progressCallback?.Invoke(25, "Packaging custom song folders and stems...");
                    CopyDirectory(customSongsPath, tempSongsDir);
                    songCount = Directory.GetDirectories(tempSongsDir).Length;
                }

                // 2. Copy customs.pak.xen
                string pakPath = Path.Combine(gamePath, "DATA", "PAK", "customs.pak.xen");
                string pakChecksum = "";
                bool hasPak = false;
                if (File.Exists(pakPath))
                {
                    progressCallback?.Invoke(50, "Calculating SHA-256 integrity hash of customs.pak.xen...");
                    hasPak = true;
                    string tempPakPath = Path.Combine(tempDir, "customs.pak.xen");
                    File.Copy(pakPath, tempPakPath, true);

                    using var sha = SHA256.Create();
                    using var stream = File.OpenRead(tempPakPath);
                    pakChecksum = Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
                }

                // 3. Copy Career Saves
                string tempSavesDir = Path.Combine(tempDir, "Career_Saves");
                bool hasSaves = false;
                if (Directory.Exists(savesPath))
                {
                    progressCallback?.Invoke(65, "Archiving paired career save profiles...");
                    CopyDirectory(savesPath, tempSavesDir);
                    hasSaves = true;
                }

                // 4. Write Vault Manifest
                var manifest = new VaultManifest(
                    VaultVersion: "1.4.2",
                    AppVersion: "1.4.2-XboxEdition",
                    CreatedBy: AppConfig.Current.Username ?? Environment.UserName,
                    CreatedAtUtc: DateTime.UtcNow,
                    SongCount: songCount,
                    IncludesSaveProfiles: hasSaves,
                    IncludesCustomsPak: hasPak,
                    PakChecksumSha256: pakChecksum,
                    Description: $"Full GH3-Sync Vault Backup ({songCount} songs)"
                );

                string manifestJson = JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(Path.Combine(tempDir, "vault_manifest.json"), manifestJson, Encoding.UTF8);

                // 5. Compress into target .gh3vault
                progressCallback?.Invoke(80, "Compressing into portable .gh3vault archive...");
                if (File.Exists(targetFilePath)) File.Delete(targetFilePath);

                ZipFile.CreateFromDirectory(tempDir, targetFilePath, CompressionLevel.Optimal, false);

                progressCallback?.Invoke(100, "Export complete!");
                return (true, $"Exported {songCount} songs and career saves to '{Path.GetFileName(targetFilePath)}'!", targetFilePath);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Vault export failed: {ex.Message}", null);
        }
    }

    public (bool Success, string Message, VaultManifest? Manifest) RestoreVault(
        string vaultFilePath,
        string gamePath,
        string customSongsPath,
        string savesPath,
        Action<int, string>? progressCallback = null)
    {
        if (!File.Exists(vaultFilePath))
            return (false, "Vault file does not exist.", null);

        try
        {
            progressCallback?.Invoke(15, "Opening and inspecting .gh3vault container...");
            string tempDir = Path.Combine(Path.GetTempPath(), $"gh3vault_import_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ZipFile.ExtractToDirectory(vaultFilePath, tempDir);

                string manifestPath = Path.Combine(tempDir, "vault_manifest.json");
                if (!File.Exists(manifestPath))
                    return (false, "Invalid .gh3vault file: missing vault_manifest.json.", null);

                var manifest = JsonSerializer.Deserialize<VaultManifest>(File.ReadAllText(manifestPath));
                if (manifest == null)
                    return (false, "Corrupt vault manifest.", null);

                // Restore customs.pak.xen
                string tempPak = Path.Combine(tempDir, "customs.pak.xen");
                if (File.Exists(tempPak))
                {
                    progressCallback?.Invoke(45, "Restoring matched customs.pak.xen setlist...");
                    string targetPak = Path.Combine(gamePath, "DATA", "PAK", "customs.pak.xen");
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPak)!);
                    File.Copy(tempPak, targetPak, true);
                }

                // Restore Custom_Songs
                string tempSongs = Path.Combine(tempDir, "Custom_Songs");
                if (Directory.Exists(tempSongs))
                {
                    progressCallback?.Invoke(70, "Restoring custom song stems and charts...");
                    CopyDirectory(tempSongs, customSongsPath);
                }

                // Restore Career Saves
                string tempSaves = Path.Combine(tempDir, "Career_Saves");
                if (Directory.Exists(tempSaves))
                {
                    progressCallback?.Invoke(85, "Restoring paired career save profiles...");
                    CopyDirectory(tempSaves, savesPath);
                }

                progressCallback?.Invoke(100, "Restore complete!");
                return (true, $"Restored vault cleanly! ({manifest.SongCount} songs, created by {manifest.CreatedBy})", manifest);
            }
            finally
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            return (false, $"Vault restore failed: {ex.Message}", null);
        }
    }

    private static void CopyDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, dest, true);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            string dest = Path.Combine(targetDir, Path.GetFileName(sub));
            CopyDirectory(sub, dest);
        }
    }
}
