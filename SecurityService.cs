using System;
using System.IO;
using System.IO.Compression;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace GH3_Sync;

public class SecurityService
{
    public string PairingPin { get; private set; }
    public SemaphoreSlim DownloadConcurrencyGuard { get; } = new(2, 2);
    private readonly Dictionary<string, (int attempts, DateTime lastAttempt)> _failedAttempts = new();
    private readonly Dictionary<string, List<DateTime>> _ipRequestTimestamps = new();
    private readonly object _lock = new();

    public SecurityService()
    {
        PairingPin = GenerateSecurePin();
    }

    public static bool FixedTimeEquals(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        var ba = Encoding.UTF8.GetBytes(a);
        var bb = Encoding.UTF8.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    public bool CheckRequestRateLimit(string clientIp, SyncLogger logger, int maxPerMinute = 40)
    {
        if (clientIp == "127.0.0.1" || clientIp == "::1") return true;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (!_ipRequestTimestamps.TryGetValue(clientIp, out var list))
            {
                list = new List<DateTime>();
                _ipRequestTimestamps[clientIp] = list;
            }
            list.RemoveAll(t => (now - t).TotalSeconds > 60);
            if (list.Count >= maxPerMinute)
            {
                logger.LogWarn($"[SECURITY] Rate-limited IP {clientIp} ({list.Count} req/min).");
                return false;
            }
            list.Add(now);
            return true;
        }
    }

    public static string ComputeSha256Hex(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    public static bool VerifyFileSha256(string filePath, string? expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex) || !File.Exists(filePath)) return true;
        var cleanExpected = expectedHex.Trim().Replace("sha256:", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        using var sha = SHA256.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return FixedTimeEquals(actual, cleanExpected);
    }

    private static string GenerateSecurePin()
    {
        // Cryptographically secure 6-digit numeric PIN
        var bytes = RandomNumberGenerator.GetBytes(4);
        var val = BitConverter.ToUInt32(bytes, 0) % 900000 + 100000;
        return val.ToString();
    }

    public bool ValidatePin(string? clientPin, string clientIp, SyncLogger logger)
    {
        lock (_lock)
        {
            // Rate limiting: 5 failed attempts locks for 5 minutes
            if (_failedAttempts.TryGetValue(clientIp, out var record))
            {
                if (record.attempts >= 5)
                {
                    if (DateTime.UtcNow - record.lastAttempt < TimeSpan.FromMinutes(5))
                    {
                        logger.LogWarn($"[SECURITY ALERT] Blocked request from rate-limited IP: {clientIp}");
                        return false;
                    }
                    _failedAttempts.Remove(clientIp);
                }
            }

            if (FixedTimeEquals(clientPin?.Trim(), PairingPin))
            {
                if (_failedAttempts.ContainsKey(clientIp)) _failedAttempts.Remove(clientIp);
                logger.LogInfo($"[SECURITY] Successful peer authentication from: {clientIp}");
                return true;
            }

            // Failed attempt tracking
            var currentAttempts = _failedAttempts.TryGetValue(clientIp, out var existing) ? existing.attempts + 1 : 1;
            _failedAttempts[clientIp] = (currentAttempts, DateTime.UtcNow);
            logger.LogWarn($"[SECURITY ALERT] Invalid PIN attempt ({currentAttempts}/5) from: {clientIp}");
            return false;
        }
    }

    // AES-256-GCM Package Encryption & Decryption (supports shared PIN or Trusted Token across peers!)
    public byte[] EncryptPayload(byte[] plaintext, string? sharedSecret = null)
    {
        var key = DeriveKey(string.IsNullOrWhiteSpace(sharedSecret) ? PairingPin : sharedSecret);
        var nonce = RandomNumberGenerator.GetBytes(12); // 96-bit nonce
        var tag = new byte[16]; // 128-bit authentication tag
        var ciphertext = new byte[plaintext.Length];

        using (var aesGcm = new AesGcm(key, 16))
        {
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        // Pack: [12 bytes Nonce] + [16 bytes Tag] + [Ciphertext]
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(nonce);
        bw.Write(tag);
        bw.Write(ciphertext);
        return ms.ToArray();
    }

    public byte[] DecryptPayload(byte[] encryptedData, string? sharedSecret = null)
    {
        if (encryptedData.Length < 28)
            throw new CryptographicException("Encrypted payload is corrupted or truncated.");

        var key = DeriveKey(string.IsNullOrWhiteSpace(sharedSecret) ? PairingPin : sharedSecret);
        using var ms = new MemoryStream(encryptedData);
        using var br = new BinaryReader(ms);

        var nonce = br.ReadBytes(12);
        var tag = br.ReadBytes(16);
        var ciphertext = br.ReadBytes((int)(ms.Length - 28));
        var plaintext = new byte[ciphertext.Length];

        using (var aesGcm = new AesGcm(key, 16))
        {
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
        }

        return plaintext;
    }

    private static byte[] DeriveKey(string pin)
    {
        // Derive 256-bit key using SHA-256 + fixed salt
        var salt = Encoding.UTF8.GetBytes("GH3-SYNC-P2P-SALT-v1");
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(pin));
    }

    // Zip Slip, Path Traversal, and Extension Sanitizer
    public static void ValidateAndExtractZip(byte[] zipBytes, string songsDir, string musicDir, SyncLogger logger)
    {
        const long MaxEntrySize = 150 * 1024 * 1024; // 150 MB max per file
        const long MaxTotalSize = 400 * 1024 * 1024; // 400 MB max total bundle
        long totalExtracted = 0;

        var fullSongsDir = Path.GetFullPath(songsDir);
        var fullMusicDir = Path.GetFullPath(musicDir);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            // 1. Strict filename sanitization (Reject any path separators or traversal strings)
            var fileName = Path.GetFileName(entry.Name);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            {
                logger.LogWarn($"[SECURITY BLOCK] Blocked illegal filename: {entry.FullName}");
                throw new SecurityException($"Illegal file path in package: {entry.FullName}");
            }

            // 2. Strict whitelist of allowed extensions
            bool isSongPak = fileName.EndsWith("_song.pak.xen", StringComparison.OrdinalIgnoreCase);
            bool isAudioFsb = fileName.EndsWith(".fsb.xen", StringComparison.OrdinalIgnoreCase);
            bool isAudioDat = fileName.EndsWith(".dat.xen", StringComparison.OrdinalIgnoreCase);
            bool isMetadata = fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase);

            if (!isSongPak && !isAudioFsb && !isAudioDat && !isMetadata)
            {
                logger.LogWarn($"[SECURITY BLOCK] Rejected unallowed file type: {fileName}");
                throw new SecurityException($"Unauthorized file type in package: {fileName}");
            }

            // 3. Zip bomb & size guard
            if (entry.Length > MaxEntrySize)
            {
                logger.LogWarn($"[SECURITY BLOCK] File {fileName} exceeds maximum size limit ({entry.Length} bytes).");
                throw new SecurityException($"File exceeds safety limits: {fileName}");
            }
            totalExtracted += entry.Length;
            if (totalExtracted > MaxTotalSize)
            {
                logger.LogWarn("[SECURITY BLOCK] Package exceeded maximum total uncompressed size limit.");
                throw new SecurityException("Package exceeds total safety size limits.");
            }

            // 4. Target folder containment validation
            string targetDir = isSongPak ? fullSongsDir : fullMusicDir;
            if (isMetadata) continue; // metadata is read, not extracted to game folder

            var destinationPath = Path.GetFullPath(Path.Combine(targetDir, fileName));
            if (!destinationPath.StartsWith(targetDir, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarn($"[SECURITY BLOCK] Path Traversal Attempt Detected: {destinationPath}");
                throw new SecurityException($"Path traversal blocked: {destinationPath}");
            }

            // Extract file safely
            entry.ExtractToFile(destinationPath, overwrite: true);
            logger.LogDebug($"Extracted verified file: {fileName} -> {destinationPath}");
        }
    }

    public static void ExtractProjectZip(byte[] zipBytes, string targetDir, SyncLogger logger)
    {
        const long MaxEntrySize = 150 * 1024 * 1024;
        const long MaxTotalSize = 400 * 1024 * 1024;
        long totalExtracted = 0;

        var fullTargetDir = Path.GetFullPath(targetDir);
        if (!Directory.Exists(fullTargetDir)) Directory.CreateDirectory(fullTargetDir);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var fileName = Path.GetFileName(entry.Name);
            if (string.IsNullOrWhiteSpace(fileName) || fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\'))
            {
                logger.LogWarn($"[SECURITY BLOCK] Blocked illegal filename: {entry.FullName}");
                throw new SecurityException($"Illegal file path in package: {entry.FullName}");
            }

            bool isGhproj = fileName.EndsWith(".ghproj", StringComparison.OrdinalIgnoreCase);
            bool isChart = fileName.EndsWith(".chart", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase);
            bool isIni = fileName.Equals("song.ini", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
            bool isAudio = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
            bool isMetadata = fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase);

            if (!isGhproj && !isChart && !isIni && !isAudio && !isMetadata)
            {
                logger.LogWarn($"[SECURITY BLOCK] Rejected unallowed project file type: {fileName}");
                continue;
            }

            if (entry.Length > MaxEntrySize)
                throw new SecurityException($"File exceeds safety limits: {fileName}");

            totalExtracted += entry.Length;
            if (totalExtracted > MaxTotalSize)
                throw new SecurityException("Package exceeds total safety size limits.");

            var destinationPath = Path.GetFullPath(Path.Combine(fullTargetDir, fileName));
            if (!destinationPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarn($"[SECURITY BLOCK] Path traversal blocked: {destinationPath}");
                throw new SecurityException($"Path traversal blocked: {destinationPath}");
            }

            entry.ExtractToFile(destinationPath, overwrite: true);
            logger.LogDebug($"Extracted project file: {fileName} -> {destinationPath}");
        }
    }

    /// <summary>
    /// Safely extracts a Clone Hero / YARG .zip archive with strict Zip-Bomb limits,
    /// max file count cap, path-traversal defense, and strict media/chart extension allowlist.
    /// Hard-blocks .exe, .dll, .bat, .ps1, .cmd, .vbs, .msi, .scr, etc.
    /// </summary>
    public static void ExtractCloneHeroZipSafely(string zipPath, string targetDir, SyncLogger logger)
    {
        const long MaxEntryBytes = 200L * 1024 * 1024;
        const long MaxTotalBytes = 450L * 1024 * 1024;
        const int MaxFileCount = 80;
        string[] allowedExts = new[] { ".chart", ".mid", ".midi", ".ini", ".ogg", ".mp3", ".wav", ".opus", ".flac", ".png", ".jpg", ".jpeg", ".json", ".txt" };

        var fullTarget = Path.GetFullPath(targetDir);
        Directory.CreateDirectory(fullTarget);

        long totalUncompressed = 0;
        int fileCount = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // Directory entry

            fileCount++;
            if (fileCount > MaxFileCount)
                throw new SecurityException($"Archive exceeds maximum file limit ({MaxFileCount}).");

            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (!allowedExts.Contains(ext))
            {
                logger.LogWarn($"[SECURITY BLOCK] Skipped disallowed file in Clone Hero ZIP: {entry.FullName}");
                continue;
            }

            if (entry.Length > MaxEntryBytes)
                throw new SecurityException($"File '{entry.Name}' exceeds safety size cap.");

            totalUncompressed += entry.Length;
            if (totalUncompressed > MaxTotalBytes)
                throw new SecurityException("Archive exceeds total uncompressed size safety limit (possible zip bomb).");

            var destPath = Path.GetFullPath(Path.Combine(fullTarget, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destPath.StartsWith(fullTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarn($"[SECURITY BLOCK] Zip-Slip path traversal blocked: {entry.FullName}");
                throw new SecurityException($"Path traversal blocked: {entry.FullName}");
            }

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }
}
