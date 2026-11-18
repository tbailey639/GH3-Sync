using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;

namespace GH3_Sync;

public class FriendService
{
    private readonly object _lock = new();

    public UserProfile Profile => new()
    {
        Username = AppConfig.Current.Username,
        FriendCode = AppConfig.Current.FriendCode
    };

    public string Username => AppConfig.Current.Username;
    public string FriendCode => AppConfig.Current.FriendCode;
    public IReadOnlyList<FriendEntry> Friends => AppConfig.Current.Friends;

    public FriendService(string? baseDir = null)
    {
    }

    public void SaveProfile(string newUsername)
    {
        lock (_lock)
        {
            if (!string.IsNullOrWhiteSpace(newUsername))
            {
                AppConfig.UpdateUsername(newUsername.Trim());
            }
        }
    }

    public FriendEntry AddFriend(string username, string friendCode, string? address = null)
    {
        return AddFriendByCode(friendCode, username, address);
    }

    public FriendEntry AddFriendByCode(string friendCode, string? nickname = null, string? address = null)
    {
        lock (_lock)
        {
            var cleanCode = friendCode.Trim().ToUpperInvariant();
            if (!cleanCode.StartsWith("GH3-"))
            {
                cleanCode = "GH3-" + cleanCode;
            }

            // Check if friend already exists by code
            var existing = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(cleanCode, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!string.IsNullOrWhiteSpace(nickname)) existing.Username = EnsureUniqueNickname(nickname.Trim(), cleanCode);
                if (!string.IsNullOrWhiteSpace(address)) existing.LastAddress = address.Trim();
                AppConfig.Save();
                return existing;
            }

            var baseName = string.IsNullOrWhiteSpace(nickname) 
                ? $"Peer_{cleanCode.Substring(Math.Max(0, cleanCode.Length - 4))}" 
                : nickname.Trim();

            var uniqueName = EnsureUniqueNickname(baseName, cleanCode);

            var newFriend = new FriendEntry
            {
                Username = uniqueName,
                FriendCode = cleanCode,
                LastAddress = address?.Trim() ?? "",
                IsTrusted = false,
                TrustToken = "",
                AddedAt = DateTime.UtcNow
            };

            AppConfig.Current.Friends.Add(newFriend);
            AppConfig.Save();
            return newFriend;
        }
    }

    private string EnsureUniqueNickname(string nickname, string currentCode)
    {
        var name = nickname;
        int counter = 2;
        while (AppConfig.Current.Friends.Any(f => 
            !f.FriendCode.Equals(currentCode, StringComparison.OrdinalIgnoreCase) && 
            f.Username.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            name = $"{nickname} ({counter++})";
        }
        return name;
    }

    public bool ToggleTrust(string friendCode, bool allowTrust)
    {
        lock (_lock)
        {
            var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(friendCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (friend == null) return false;

            friend.IsTrusted = allowTrust;
            if (allowTrust)
            {
                // Generate 32-char cryptographically secure bearer token
                friend.TrustToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            }
            else
            {
                friend.TrustToken = "";
            }

            AppConfig.Save();
            return true;
        }
    }

    public bool RemoveFriend(string friendCode)
    {
        lock (_lock)
        {
            var count = AppConfig.Current.Friends.RemoveAll(f => f.FriendCode.Equals(friendCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (count > 0)
            {
                AppConfig.Save();
                return true;
            }
            return false;
        }
    }

    public bool ValidateTrustToken(string? token, out FriendEntry? trustedFriend)
    {
        trustedFriend = null;
        if (string.IsNullOrWhiteSpace(token)) return false;

        lock (_lock)
        {
            trustedFriend = AppConfig.Current.Friends.FirstOrDefault(f =>
                f.IsTrusted && !string.IsNullOrEmpty(f.TrustToken) &&
                SecurityService.FixedTimeEquals(f.TrustToken, token.Trim()));
            return trustedFriend != null;
        }
    }
}
