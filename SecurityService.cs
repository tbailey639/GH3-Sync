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
    private readonly Dictionary<string, (int attempts, DateTime lastAttempt)> _failedAttempts = new();
    private readonly object _lock = new();

    public SecurityService()
    {
        PairingPin = GenerateSecurePin();
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

            if (string.Equals(clientPin?.Trim(), PairingPin, StringComparison.Ordinal))
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

    // AES-256-GCM Package Encryption & Decryption
    public byte[] EncryptPayload(byte[] plaintext)
    {
        var key = DeriveKey(PairingPin);
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

    public byte[] DecryptPayload(byte[] encryptedData)
    {
        if (encryptedData.Length < 28)
            throw new CryptographicException("Encrypted payload is corrupted or truncated.");

        var key = DeriveKey(PairingPin);
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
        }
    }
}
