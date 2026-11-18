using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace GH3_Sync;

public record BlockedIpRecord(string IpAddress, int Attempts, DateTime LastAttemptUtc, bool IsManualBan, string Reason);

public class ExpiringInviteRecord
{
    public string Token { get; set; } = "";
    public string Label { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
}

/// <summary>
/// Hardened security layer for GH3-Sync:
/// - Challenge-Response HMAC-SHA256 P2P authentication (secrets never cross the wire in plaintext)
/// - Unified 5-attempt IP lockout & manual IP ban/unban for PINs, Trust Tokens & Session Invites
/// - Expiring Session Invite Tokens + 1-Click Rotate All Credentials
/// - Per-transfer random-salt PBKDF2-SHA256 + AES-256-GCM encryption ("GH3E" v2 format + v1 compat)
/// - Passphrase-encrypted Profile Backups (.gh3backup)
/// - Real streaming Zip-Bomb defense, trailing-slash Zip-Slip containment, & double-extension/ADS blocking
/// </summary>
public class SecurityService
{
    private static readonly byte[] V2Magic = Encoding.ASCII.GetBytes("GH3E");
    private static readonly byte[] BackupMagic = Encoding.ASCII.GetBytes("GH3B");

    public string PairingPin { get; private set; }
    public SemaphoreSlim DownloadConcurrencyGuard { get; } = new(2, 2);

    private readonly Dictionary<string, (int attempts, DateTime lastAttempt, string reason)> _failedAttempts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _manualBannedIps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<DateTime>> _ipRequestTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string ip, DateTime expiresUtc)> _activeChallenges = new(StringComparer.Ordinal);
    private readonly List<ExpiringInviteRecord> _expiringInvites = new();
    private readonly List<string> _securityAuditLog = new();
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

    // ==================== CHALLENGE-RESPONSE HMAC AUTH ====================
    public string IssueAuthChallenge(string clientIp)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            var expired = _activeChallenges.Where(kv => kv.Value.expiresUtc < now).Select(kv => kv.Key).ToList();
            foreach (var k in expired) _activeChallenges.Remove(k);

            if (_activeChallenges.Count > 500) _activeChallenges.Clear();
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            _activeChallenges[nonce] = (clientIp, now.AddSeconds(90));
            return nonce;
        }
    }

    public bool TryConsumeChallengeHmac(
        string? nonce,
        string? timestampStr,
        string? clientHmac,
        string methodAndPath,
        string clientIp,
        IEnumerable<(string Secret, string Mode, string Principal)> candidateSecrets,
        out string matchedSecret,
        out string matchedMode,
        out string matchedPrincipal)
    {
        matchedSecret = "";
        matchedMode = "";
        matchedPrincipal = "";

        if (string.IsNullOrWhiteSpace(nonce) || string.IsNullOrWhiteSpace(timestampStr) || string.IsNullOrWhiteSpace(clientHmac))
            return false;

        if (!long.TryParse(timestampStr, out var unixSec)) return false;
        var delta = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - unixSec);
        if (delta > 120) return false;

        lock (_lock)
        {
            if (!_activeChallenges.TryGetValue(nonce, out var entry)) return false;
            if (entry.expiresUtc < DateTime.UtcNow) { _activeChallenges.Remove(nonce); return false; }
            if (!string.Equals(entry.ip, clientIp, StringComparison.OrdinalIgnoreCase)) return false;
            _activeChallenges.Remove(nonce); // Single-use anti-replay
        }

        foreach (var (secret, mode, principal) in candidateSecrets)
        {
            if (string.IsNullOrWhiteSpace(secret)) continue;
            var expected = ComputeChallengeHmac(nonce, methodAndPath, timestampStr, secret);
            if (FixedTimeEquals(expected, clientHmac.Trim()))
            {
                matchedSecret = secret;
                matchedMode = mode;
                matchedPrincipal = principal;
                return true;
            }
        }
        return false;
    }

    public static string ComputeChallengeHmac(string nonce, string methodAndPath, string timestampStr, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var msg = Encoding.UTF8.GetBytes($"{nonce}|{methodAndPath.ToLowerInvariant()}|{timestampStr}");
        return Convert.ToHexString(hmac.ComputeHash(msg)).ToLowerInvariant();
    }

    // ==================== EXPIRING INVITE TOKENS & ROTATION ====================
    public ExpiringInviteRecord IssueExpiringInviteToken(string label, int validHours = 4)
    {
        lock (_lock)
        {
            PruneExpiredInvitesLocked();
            var rec = new ExpiringInviteRecord
            {
                Token = "INV-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                Label = string.IsNullOrWhiteSpace(label) ? "Session Lobby Invite" : label,
                ExpiresAtUtc = DateTime.UtcNow.AddHours(Math.Clamp(validHours, 1, 24))
            };
            _expiringInvites.Add(rec);
            RecordAuditLocked($"Issued {validHours}h expiring invite token '{rec.Label}'");
            return rec;
        }
    }

    public bool ValidateExpiringInviteToken(string? token, out ExpiringInviteRecord? matched)
    {
        matched = null;
        if (string.IsNullOrWhiteSpace(token)) return false;
        lock (_lock)
        {
            PruneExpiredInvitesLocked();
            foreach (var inv in _expiringInvites)
            {
                if (FixedTimeEquals(inv.Token, token.Trim()))
                {
                    matched = inv;
                    return true;
                }
            }
            return false;
        }
    }

    public List<ExpiringInviteRecord> GetActiveExpiringInvites()
    {
        lock (_lock)
        {
            PruneExpiredInvitesLocked();
            return _expiringInvites.ToList();
        }
    }

    public string RotateSessionCredentials(FriendService? friends = null)
    {
        lock (_lock)
        {
            PairingPin = GenerateSecurePin();
            _expiringInvites.Clear();
            _activeChallenges.Clear();
            RecordAuditLocked("Rotated Session PIN and revoked all temporary invite tokens.");
        }

        int rotatedFriends = 0;
        if (friends != null)
        {
            foreach (var f in friends.Friends.Where(x => x.IsTrusted).ToList())
            {
                f.TrustToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
                rotatedFriends++;
            }
            AppConfig.Save();
        }
        return $"Security credentials rotated! New Session PIN: {PairingPin}, revoked all expiring invites, and rotated {rotatedFriends} Trusted Friend token(s).";
    }

    private void PruneExpiredInvitesLocked()
    {
        var now = DateTime.UtcNow;
        _expiringInvites.RemoveAll(i => i.ExpiresAtUtc <= now);
    }

    // ==================== UNIFIED IP RATE LIMITING, LOCKOUT & AUDIT ====================
    public bool IsIpBlocked(string clientIp)
    {
        if (IsLoopbackIp(clientIp)) return false;
        lock (_lock)
        {
            if (_manualBannedIps.Contains(clientIp)) return true;
            if (_failedAttempts.TryGetValue(clientIp, out var rec) && rec.attempts >= 5)
            {
                if (DateTime.UtcNow - rec.lastAttempt < TimeSpan.FromMinutes(10))
                    return true;
                _failedAttempts.Remove(clientIp);
            }
            return false;
        }
    }

    public void RecordAuthFailure(string clientIp, string reason, SyncLogger logger)
    {
        if (IsLoopbackIp(clientIp)) return;
        lock (_lock)
        {
            var cur = _failedAttempts.TryGetValue(clientIp, out var ex) ? ex.attempts + 1 : 1;
            _failedAttempts[clientIp] = (cur, DateTime.UtcNow, reason);
            RecordAuditLocked($"Auth failure ({cur}/5) from {clientIp}: {reason}");
            logger.LogWarn($"[SECURITY ALERT] Auth failure ({cur}/5) from {clientIp}: {reason}");
        }
    }

    public void RecordAuthSuccess(string clientIp)
    {
        lock (_lock)
        {
            _failedAttempts.Remove(clientIp);
        }
    }

    public void BanIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || IsLoopbackIp(ip.Trim())) return;
        lock (_lock)
        {
            _manualBannedIps.Add(ip.Trim());
            RecordAuditLocked($"Manually banned IP: {ip.Trim()}");
        }
    }

    public void UnbanIp(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return;
        lock (_lock)
        {
            _manualBannedIps.Remove(ip.Trim());
            _failedAttempts.Remove(ip.Trim());
            RecordAuditLocked($"Unbanned IP: {ip.Trim()}");
        }
    }

    public List<BlockedIpRecord> GetBlockedIps()
    {
        lock (_lock)
        {
            var list = new List<BlockedIpRecord>();
            foreach (var ip in _manualBannedIps)
                list.Add(new BlockedIpRecord(ip, 5, DateTime.UtcNow, true, "Manually Banned"));
            foreach (var kv in _failedAttempts)
            {
                if (_manualBannedIps.Contains(kv.Key)) continue;
                if (kv.Value.attempts >= 5 && DateTime.UtcNow - kv.Value.lastAttempt < TimeSpan.FromMinutes(10))
                    list.Add(new BlockedIpRecord(kv.Key, kv.Value.attempts, kv.Value.lastAttempt, false, kv.Value.reason));
            }
            return list;
        }
    }

    public List<string> GetRecentAuditEntries()
    {
        lock (_lock) return _securityAuditLog.ToList();
    }

    private void RecordAuditLocked(string msg)
    {
        _securityAuditLog.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
        while (_securityAuditLog.Count > 60) _securityAuditLog.RemoveAt(0);
    }

    public static bool IsLoopbackIp(string? ip)
        => ip is "127.0.0.1" or "::1" or "::ffff:127.0.0.1" or "localhost";

    public bool CheckRequestRateLimit(string clientIp, SyncLogger logger, int maxPerMinute = 45)
    {
        if (IsLoopbackIp(clientIp)) return true;
        if (IsIpBlocked(clientIp)) return false;
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

    public static bool VerifyFileSha256(string filePath, string? expectedHex, bool requireChecksum = false)
    {
        if (!File.Exists(filePath)) return false;
        if (string.IsNullOrWhiteSpace(expectedHex)) return !requireChecksum;
        var cleanExpected = expectedHex.Trim().Replace("sha256:", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        using var sha = SHA256.Create();
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var actual = Convert.ToHexString(sha.ComputeHash(fs)).ToLowerInvariant();
        return FixedTimeEquals(actual, cleanExpected);
    }

    private static string GenerateSecurePin()
    {
        var bytes = RandomNumberGenerator.GetBytes(4);
        var val = BitConverter.ToUInt32(bytes, 0) % 900000 + 100000;
        return val.ToString();
    }

    public bool ValidatePin(string? clientPin, string clientIp, SyncLogger logger)
    {
        if (IsIpBlocked(clientIp))
        {
            logger.LogWarn($"[SECURITY ALERT] Blocked PIN attempt from locked-out IP: {clientIp}");
            return false;
        }

        if (FixedTimeEquals(clientPin?.Trim(), PairingPin))
        {
            RecordAuthSuccess(clientIp);
            logger.LogInfo($"[SECURITY] Successful peer PIN authentication from: {clientIp}");
            return true;
        }

        RecordAuthFailure(clientIp, "Invalid Pairing PIN", logger);
        return false;
    }

    // ==================== PER-TRANSFER SALTED AES-256-GCM ENCRYPTION ====================
    public byte[] EncryptPayload(byte[] plaintext, string? sharedSecret = null)
    {
        var secret = string.IsNullOrWhiteSpace(sharedSecret) ? PairingPin : sharedSecret;
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, 100_000, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var ciphertext = new byte[plaintext.Length];

        using (var aesGcm = new AesGcm(key, 16))
            aesGcm.Encrypt(nonce, plaintext, ciphertext, tag);

        // v2 Format: ["GH3E" 4B] + [Salt 16B] + [Nonce 12B] + [Tag 16B] + [Ciphertext]
        using var ms = new MemoryStream(48 + ciphertext.Length);
        ms.Write(V2Magic);
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(ciphertext);
        return ms.ToArray();
    }

    public byte[] DecryptPayload(byte[] encryptedData, string? sharedSecret = null)
    {
        var secret = string.IsNullOrWhiteSpace(sharedSecret) ? PairingPin : sharedSecret;

        // Check v2 Per-Transfer Random-Salt format ("GH3E")
        if (encryptedData.Length >= 48 &&
            encryptedData[0] == V2Magic[0] && encryptedData[1] == V2Magic[1] &&
            encryptedData[2] == V2Magic[2] && encryptedData[3] == V2Magic[3])
        {
            var salt = encryptedData.AsSpan(4, 16).ToArray();
            var nonce = encryptedData.AsSpan(20, 12).ToArray();
            var tag = encryptedData.AsSpan(32, 16).ToArray();
            var ciphertext = encryptedData.AsSpan(48).ToArray();
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(secret), salt, 100_000, HashAlgorithmName.SHA256, 32);
            var plaintext = new byte[ciphertext.Length];
            using var aesGcm = new AesGcm(key, 16);
            aesGcm.Decrypt(nonce, ciphertext, tag, plaintext);
            return plaintext;
        }

        // Backwards-compatible v1 decrypt for older beta peers
        if (encryptedData.Length < 28)
            throw new CryptographicException("Encrypted payload is corrupted or truncated.");

        var legacyKey = DeriveLegacyKey(secret);
        var legNonce = encryptedData.AsSpan(0, 12).ToArray();
        var legTag = encryptedData.AsSpan(12, 16).ToArray();
        var legCipher = encryptedData.AsSpan(28).ToArray();
        var legPlain = new byte[legCipher.Length];
        using (var aesGcm = new AesGcm(legacyKey, 16))
            aesGcm.Decrypt(legNonce, legCipher, legTag, legPlain);
        return legPlain;
    }

    public static byte[] EncryptWithPassphrase(byte[] plaintext, string passphrase)
    {
        if (string.IsNullOrEmpty(passphrase)) throw new ArgumentException("Passphrase is required.");
        var salt = RandomNumberGenerator.GetBytes(16);
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, 150_000, HashAlgorithmName.SHA256, 32);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plaintext.Length];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plaintext, cipher, tag);

        using var ms = new MemoryStream(48 + cipher.Length);
        ms.Write(BackupMagic);
        ms.Write(salt);
        ms.Write(nonce);
        ms.Write(tag);
        ms.Write(cipher);
        return ms.ToArray();
    }

    public static byte[] DecryptWithPassphrase(byte[] encryptedData, string passphrase)
    {
        if (encryptedData.Length < 48 ||
            encryptedData[0] != BackupMagic[0] || encryptedData[1] != BackupMagic[1] ||
            encryptedData[2] != BackupMagic[2] || encryptedData[3] != BackupMagic[3])
            throw new CryptographicException("Invalid or unrecognized .gh3backup file format.");

        var salt = encryptedData.AsSpan(4, 16).ToArray();
        var nonce = encryptedData.AsSpan(20, 12).ToArray();
        var tag = encryptedData.AsSpan(32, 16).ToArray();
        var cipher = encryptedData.AsSpan(48).ToArray();
        var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, 150_000, HashAlgorithmName.SHA256, 32);
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(nonce, cipher, tag, plain);
        return plain;
    }

    private static byte[] DeriveLegacyKey(string pin)
    {
        var salt = Encoding.UTF8.GetBytes("GH3-SYNC-P2P-SALT-v1");
        using var hmac = new HMACSHA256(salt);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(pin));
    }

    // ==================== STREAMING ZIP-BOMB & ZIP-SLIP DEFENSE ====================
    private static readonly HashSet<string> DangerousExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".dll", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".msi", ".msp", ".scr", ".com", ".pif", ".hta", ".reg", ".lnk"
    };

    private static void ValidateSafeFileName(string fullName, string fileName, SyncLogger logger)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Contains("..") || fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains(':') ||
            fullName.Contains(".."))
        {
            logger.LogWarn($"[SECURITY BLOCK] Blocked illegal filename or ADS path: {fullName}");
            throw new SecurityException($"Illegal file path in package: {fullName}");
        }

        // Block double-extensions hiding executable payloads (e.g. notes.chart.exe)
        var ext = Path.GetExtension(fileName);
        if (DangerousExtensions.Contains(ext))
        {
            logger.LogWarn($"[SECURITY BLOCK] Blocked executable payload: {fileName}");
            throw new SecurityException($"Executable file blocked: {fileName}");
        }
    }

    public static long CopyStreamWithLimit(ZipArchiveEntry entry, string destPath, long maxEntryBytes, ref long totalExtracted, long maxTotalBytes)
    {
        long written = 0;
        var buf = new byte[81920];
        using var src = entry.Open();
        using var dst = File.Create(destPath);
        int read;
        while ((read = src.Read(buf, 0, buf.Length)) > 0)
        {
            written += read;
            totalExtracted += read;
            if (written > maxEntryBytes || totalExtracted > maxTotalBytes)
            {
                dst.Close();
                try { File.Delete(destPath); } catch { }
                throw new SecurityException($"Zip-bomb protection triggered: '{entry.Name}' exceeded real decompressed byte limit.");
            }
            dst.Write(buf, 0, read);
        }
        return written;
    }

    public static void ValidateAndExtractZip(byte[] zipBytes, string songsDir, string musicDir, SyncLogger logger)
    {
        const long MaxEntrySize = 150L * 1024 * 1024;
        const long MaxTotalSize = 400L * 1024 * 1024;
        long totalExtracted = 0;

        var fullSongsDir = Path.GetFullPath(songsDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullMusicDir = Path.GetFullPath(musicDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var fileName = Path.GetFileName(entry.Name);
            ValidateSafeFileName(entry.FullName, fileName, logger);

            bool isSongPak = fileName.EndsWith("_song.pak.xen", StringComparison.OrdinalIgnoreCase);
            bool isAudioFsb = fileName.EndsWith(".fsb.xen", StringComparison.OrdinalIgnoreCase);
            bool isAudioDat = fileName.EndsWith(".dat.xen", StringComparison.OrdinalIgnoreCase);
            bool isMetadata = fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase);

            if (!isSongPak && !isAudioFsb && !isAudioDat && !isMetadata)
            {
                logger.LogWarn($"[SECURITY BLOCK] Rejected unallowed file type: {fileName}");
                throw new SecurityException($"Unauthorized file type in package: {fileName}");
            }

            if (isMetadata) continue;

            string targetPrefix = isSongPak ? fullSongsDir : fullMusicDir;
            var destinationPath = Path.GetFullPath(Path.Combine(targetPrefix, fileName));
            if (!destinationPath.StartsWith(targetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarn($"[SECURITY BLOCK] Path Traversal Attempt Detected: {destinationPath}");
                throw new SecurityException($"Path traversal blocked: {destinationPath}");
            }

            CopyStreamWithLimit(entry, destinationPath, MaxEntrySize, ref totalExtracted, MaxTotalSize);
            logger.LogDebug($"Extracted verified file: {fileName} -> {destinationPath}");
        }
    }

    public static void ExtractProjectZip(byte[] zipBytes, string targetDir, SyncLogger logger)
    {
        const long MaxEntrySize = 150L * 1024 * 1024;
        const long MaxTotalSize = 400L * 1024 * 1024;
        long totalExtracted = 0;

        var fullTargetDir = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(fullTargetDir);

        using var ms = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        foreach (var entry in archive.Entries)
        {
            var fileName = Path.GetFileName(entry.Name);
            ValidateSafeFileName(entry.FullName, fileName, logger);

            bool isGhproj = fileName.EndsWith(".ghproj", StringComparison.OrdinalIgnoreCase);
            bool isChart = fileName.EndsWith(".chart", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".mid", StringComparison.OrdinalIgnoreCase);
            bool isIni = fileName.Equals("song.ini", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ini", StringComparison.OrdinalIgnoreCase);
            bool isAudio = fileName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase);
            bool isArt = fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || fileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
            bool isMetadata = fileName.Equals("metadata.json", StringComparison.OrdinalIgnoreCase);

            if (!isGhproj && !isChart && !isIni && !isAudio && !isArt && !isMetadata)
            {
                logger.LogWarn($"[SECURITY BLOCK] Rejected unallowed project file type: {fileName}");
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(fullTargetDir, fileName));
            if (!destinationPath.StartsWith(fullTargetDir, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarn($"[SECURITY BLOCK] Path traversal blocked: {destinationPath}");
                throw new SecurityException($"Path traversal blocked: {destinationPath}");
            }

            CopyStreamWithLimit(entry, destinationPath, MaxEntrySize, ref totalExtracted, MaxTotalSize);
            logger.LogDebug($"Extracted project file: {fileName} -> {destinationPath}");
        }
    }

    public static void ExtractCloneHeroZipSafely(string zipPath, string targetDir, SyncLogger logger)
    {
        const long MaxEntryBytes = 200L * 1024 * 1024;
        const long MaxTotalBytes = 450L * 1024 * 1024;
        const int MaxFileCount = 80;
        string[] allowedExts = { ".chart", ".mid", ".midi", ".ini", ".ogg", ".mp3", ".wav", ".opus", ".flac", ".png", ".jpg", ".jpeg", ".json", ".txt" };

        var fullTarget = Path.GetFullPath(targetDir).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(fullTarget);

        long totalUncompressed = 0;
        int fileCount = 0;

        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;

            fileCount++;
            if (fileCount > MaxFileCount)
                throw new SecurityException($"Archive exceeds maximum file limit ({MaxFileCount}).");

            ValidateSafeFileName(entry.FullName, Path.GetFileName(entry.Name), logger);

            var ext = Path.GetExtension(entry.Name).ToLowerInvariant();
            if (!allowedExts.Contains(ext))
            {
                logger.LogWarn($"[SECURITY BLOCK] Skipped disallowed file in Clone Hero ZIP: {entry.FullName}");
                continue;
            }

            var destPath = Path.GetFullPath(Path.Combine(fullTarget, entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!destPath.StartsWith(fullTarget, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarn($"[SECURITY BLOCK] Zip-Slip path traversal blocked: {entry.FullName}");
                throw new SecurityException($"Path traversal blocked: {entry.FullName}");
            }

            var destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir)) Directory.CreateDirectory(destDir);
            CopyStreamWithLimit(entry, destPath, MaxEntryBytes, ref totalUncompressed, MaxTotalBytes);
        }
    }
}
