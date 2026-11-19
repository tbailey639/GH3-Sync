using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GH3_Sync;

public record CharterSignature(
    string CharterName,
    string CharterFriendCode,
    string ChartSha256,
    string SignatureHex,
    string PublicKeyXml,
    DateTime SignedAtUtc
);

public class CharterSignatureService
{
    public const string SigExtension = ".gh3sig";

    public (bool Success, string Message, CharterSignature? Sig) SignChart(
        string chartFilePath,
        string charterName,
        string charterFriendCode,
        string? privateKeyXml = null)
    {
        if (!File.Exists(chartFilePath))
            return (false, "Chart file does not exist.", null);

        try
        {
            byte[] chartBytes = File.ReadAllBytes(chartFilePath);
            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(chartBytes);
            string hashHex = Convert.ToHexString(hash).ToLowerInvariant();

            using var rsa = RSA.Create(2048);
            if (!string.IsNullOrEmpty(privateKeyXml))
            {
                rsa.FromXmlString(privateKeyXml);
            }

            byte[] signature = rsa.SignHash(hash, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            string sigHex = Convert.ToHexString(signature);
            string pubKeyXml = rsa.ToXmlString(false);

            var charterSig = new CharterSignature(
                CharterName: charterName,
                CharterFriendCode: charterFriendCode,
                ChartSha256: hashHex,
                SignatureHex: sigHex,
                PublicKeyXml: pubKeyXml,
                SignedAtUtc: DateTime.UtcNow
            );

            string sigPath = Path.ChangeExtension(chartFilePath, SigExtension);
            string json = JsonSerializer.Serialize(charterSig, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(sigPath, json, Encoding.UTF8);

            return (true, $"Chart successfully signed by {charterName}!", charterSig);
        }
        catch (Exception ex)
        {
            return (false, $"Signing failed: {ex.Message}", null);
        }
    }

    public (bool IsValid, string Message, string? CharterName) VerifyChart(string chartFilePath)
    {
        if (!File.Exists(chartFilePath))
            return (false, "Chart file missing.", null);

        string sigPath = Path.ChangeExtension(chartFilePath, SigExtension);
        if (!File.Exists(sigPath))
            return (false, "Unsigned chart (no .gh3sig signature file present).", null);

        try
        {
            var json = File.ReadAllText(sigPath);
            var sig = JsonSerializer.Deserialize<CharterSignature>(json);
            if (sig == null) return (false, "Corrupt signature file.", null);

            byte[] chartBytes = File.ReadAllBytes(chartFilePath);
            using var sha = SHA256.Create();
            byte[] currentHash = sha.ComputeHash(chartBytes);
            string currentHashHex = Convert.ToHexString(currentHash).ToLowerInvariant();

            if (!string.Equals(currentHashHex, sig.ChartSha256, StringComparison.OrdinalIgnoreCase))
                return (false, "Tampered chart: checksum does not match cryptographic signature!", sig.CharterName);

            using var rsa = RSA.Create();
            rsa.FromXmlString(sig.PublicKeyXml);

            byte[] sigBytes = Convert.FromHexString(sig.SignatureHex);
            bool verified = rsa.VerifyHash(currentHash, sigBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

            if (verified)
                return (true, $"✓ Cryptographically Verified by {sig.CharterName} ({sig.CharterFriendCode})", sig.CharterName);
            else
                return (false, "Cryptographic signature verification failed.", sig.CharterName);
        }
        catch (Exception ex)
        {
            return (false, $"Verification error: {ex.Message}", null);
        }
    }
}
