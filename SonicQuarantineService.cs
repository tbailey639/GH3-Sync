using System;
using System.IO;
using System.IO.Compression;
using System.Linq;

namespace GH3_Sync;

public record QuarantineScanResult(bool IsSafe, string Reason, int ScannedFilesCount, long TotalUncompressedBytes);

public static class SonicQuarantineService
{
    private static readonly string[] DangerousExtensions =
    {
        ".exe", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".wsh",
        ".dll", ".scr", ".pif", ".com", ".cpl", ".hta", ".msc", ".jar", ".lnk"
    };

    private const long MaxAllowedUncompressedBytes = 1024L * 1024L * 1024L; // 1 GB cap
    private const double MaxAllowedCompressionRatio = 80.0; // Zip bomb threshold

    public static QuarantineScanResult ScanZipArchive(string zipFilePath)
    {
        if (!File.Exists(zipFilePath))
            return new QuarantineScanResult(false, "File not found.", 0, 0);

        try
        {
            var fileInfo = new FileInfo(zipFilePath);
            long archiveSize = Math.Max(1L, fileInfo.Length);
            long totalUncompressed = 0;
            int fileCount = 0;

            using var archive = ZipFile.OpenRead(zipFilePath);
            foreach (var entry in archive.Entries)
            {
                fileCount++;

                // 1. Path traversal security checks (Prevent Zip Slip)
                string entryName = entry.FullName.Replace('\\', '/');
                if (entryName.Contains("../") || entryName.Contains("/..") || entryName.StartsWith("/"))
                {
                    return new QuarantineScanResult(false, $"Path traversal detected: '{entry.FullName}'", fileCount, totalUncompressed);
                }

                if (entryName.Contains(":") && !entryName.Contains("://"))
                {
                    return new QuarantineScanResult(false, $"NTFS alternate data stream detected: '{entry.FullName}'", fileCount, totalUncompressed);
                }

                // 2. Dangerous executable extension check
                string ext = Path.GetExtension(entryName).ToLowerInvariant();
                if (DangerousExtensions.Contains(ext))
                {
                    return new QuarantineScanResult(false, $"Disallowed executable or script file detected: '{entry.FullName}' ({ext})", fileCount, totalUncompressed);
                }

                // 3. Zip bomb explosion ratio check
                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxAllowedUncompressedBytes)
                {
                    return new QuarantineScanResult(false, $"Archive exceeds maximum allowable size (1 GB). Possible zip bomb.", fileCount, totalUncompressed);
                }
            }

            double ratio = (double)totalUncompressed / archiveSize;
            if (ratio > MaxAllowedCompressionRatio && totalUncompressed > 20 * 1024 * 1024)
            {
                return new QuarantineScanResult(false, $"Suspicious compression ratio ({ratio:F1}:1). Possible zip bomb attack.", fileCount, totalUncompressed);
            }

            return new QuarantineScanResult(true, "Clean archive verified.", fileCount, totalUncompressed);
        }
        catch (Exception ex)
        {
            return new QuarantineScanResult(false, $"Archive scan failure: {ex.Message}", 0, 0);
        }
    }

    public static QuarantineScanResult ScanSongFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath))
            return new QuarantineScanResult(false, "Folder not found.", 0, 0);

        try
        {
            int fileCount = 0;
            long totalBytes = 0;

            foreach (var filePath in Directory.EnumerateFiles(folderPath, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                var fi = new FileInfo(filePath);
                totalBytes += fi.Length;

                string ext = fi.Extension.ToLowerInvariant();
                if (DangerousExtensions.Contains(ext))
                {
                    return new QuarantineScanResult(false, $"Disallowed file in song directory: '{fi.Name}'", fileCount, totalBytes);
                }
            }

            return new QuarantineScanResult(true, "Song folder clean.", fileCount, totalBytes);
        }
        catch (Exception ex)
        {
            return new QuarantineScanResult(false, $"Folder scan failure: {ex.Message}", 0, 0);
        }
    }
}
