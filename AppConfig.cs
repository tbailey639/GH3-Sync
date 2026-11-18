using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace GH3_Sync;

public class UserProfile
{
    public string Username { get; set; } = "Player";
    public string FriendCode { get; set; } = "";
}

public class FriendEntry
{
    public string Username { get; set; } = "";
    public string FriendCode { get; set; } = "";
    public string LastAddress { get; set; } = "";
    public bool IsTrusted { get; set; }
    public string TrustToken { get; set; } = "";
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
}

public class FeatureUsageStats
{
    public int P2pSyncCount { get; set; } = 0;
    public int SongCreatorCount { get; set; } = 0;
    public int SaveBackupCount { get; set; } = 0;
    public int SaveRestoreCount { get; set; } = 0;
    public int ImportArchiveCount { get; set; } = 0;
    public int ExportPackCount { get; set; } = 0;
    public int FriendsAddedCount { get; set; } = 0;
}

public class FeedbackItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Page { get; set; } = "";
    public string Category { get; set; } = "";
    public string Message { get; set; } = "";
    public string UserFriendCode { get; set; } = "";
    public string Username { get; set; } = "";
    public string AppVersion { get; set; } = "1.4.0-beta.4";
    public bool IsBetterGh3 { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Open"; // Open, Investigating, Resolved
}

public class AppConfigData
{
    public string AppVersion { get; set; } = UpdateService.CurrentAppVersion;
    public string Username { get; set; } = "Player_" + Random.Shared.Next(1000, 9999);
    public string FriendCode { get; set; } = "";
    public string GamePath { get; set; } = "";
    public bool DirectInstallToGame { get; set; } = true;
    public string AdminHubUrl { get; set; } = "http://localhost:7575";
    public string CustomPublicAddress { get; set; } = ""; // DDNS or manually configured Public IP/Port
    public bool AutoPortMap { get; set; } = true;         // UPnP / NAT-PMP enabled
    public FeatureUsageStats Usage { get; set; } = new();
    public List<FriendEntry> Friends { get; set; } = new();
    public List<string> CreatedSongIds { get; set; } = new() { "beatit_6ce4b6e5", "gentlyweeps_26711a91", "onogami_fa5fdfea" };
    public string CustomSongsPath { get; set; } = "";
    public List<string> CreatedSongDirectories { get; set; } = new();
    public List<string> FavoriteSongIds { get; set; } = new();
    public List<string> LobbySetlistIds { get; set; } = new();
    public int MaxLobbyAutoSyncSongs { get; set; } = 10;
    public bool MinimizeOnGameLaunch { get; set; } = true;
    public string HoneycombPath { get; set; } = "";
    public bool AutoCompileCreatedSongs { get; set; } = true;
}

public static class AppConfig
{
    private static readonly string ConfigDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync");
    private static readonly string ConfigPath = Path.Combine(ConfigDir, "config.json");
    private static readonly string FeedbackPath = Path.Combine(ConfigDir, "feedback.json");
    private static readonly object _lock = new();

    public static AppConfigData Current { get; private set; } = new();

    static AppConfig()
    {
        Load();
    }

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);

                if (File.Exists(ConfigPath))
                {
                    var json = File.ReadAllText(ConfigPath);
                    var data = JsonSerializer.Deserialize<AppConfigData>(json);
                    if (data != null)
                    {
                        Current = data;
                        if (string.IsNullOrEmpty(Current.FriendCode))
                        {
                            Current.FriendCode = GenerateFriendCode();
                            Save();
                        }
                        if (Current.Usage == null) Current.Usage = new FeatureUsageStats();
                        EnsureSeedCreatedSongs();
                        return;
                    }
                }
            }
            catch { }

            // Fresh initial configuration
            Current = new AppConfigData
            {
                FriendCode = GenerateFriendCode()
            };
            EnsureSeedCreatedSongs();
            Save();
        }
    }

    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(ConfigDir)) Directory.CreateDirectory(ConfigDir);
                var json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(ConfigPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CONFIG ERROR] Could not save config: {ex.Message}");
            }
        }
    }

    public static void UpdateUsername(string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        Current.Username = newName.Trim();
        Save();
    }

    public static void UpdateGamePath(string path)
    {
        Current.GamePath = path.Trim();
        Save();
    }

    public static void SetDirectInstall(bool direct)
    {
        Current.DirectInstallToGame = direct;
        Save();
    }

    public static void UpdateHoneycombPath(string path)
    {
        lock (_lock)
        {
            Current.HoneycombPath = path?.Trim() ?? "";
            Save();
        }
    }

    public static void SetAutoCompileCreatedSongs(bool autoCompile)
    {
        lock (_lock)
        {
            Current.AutoCompileCreatedSongs = autoCompile;
            Save();
        }
    }

    public static void UpdateNetworkSettings(string customAddress, bool autoPortMap)
    {
        lock (_lock)
        {
            Current.CustomPublicAddress = customAddress?.Trim() ?? "";
            Current.AutoPortMap = autoPortMap;
            Save();
        }
    }

    public static void UpdateFriendAddress(string friendCode, string address)
    {
        lock (_lock)
        {
            var friend = Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(friendCode.Trim(), StringComparison.OrdinalIgnoreCase));
            if (friend != null)
            {
                friend.LastAddress = address.Trim();
                Save();
            }
        }
    }

    public static void IncrementUsage(string feature)
    {
        lock (_lock)
        {
            if (Current.Usage == null) Current.Usage = new FeatureUsageStats();

            switch (feature.ToLowerInvariant())
            {
                case "sync":
                case "p2psync":
                    Current.Usage.P2pSyncCount++;
                    break;
                case "creator":
                case "songcreator":
                    Current.Usage.SongCreatorCount++;
                    break;
                case "backup":
                case "savebackup":
                    Current.Usage.SaveBackupCount++;
                    break;
                case "restore":
                case "saverestore":
                    Current.Usage.SaveRestoreCount++;
                    break;
                case "import":
                case "fileimport":
                    Current.Usage.ImportArchiveCount++;
                    break;
                case "export":
                case "packexport":
                    Current.Usage.ExportPackCount++;
                    break;
                case "friend":
                case "addfriend":
                    Current.Usage.FriendsAddedCount++;
                    break;
            }
            Save();
        }
    }

    public static void SaveFeedback(FeedbackItem item)
    {
        lock (_lock)
        {
            try
            {
                var list = GetFeedback();
                list.Insert(0, item);
                var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(FeedbackPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FEEDBACK ERROR] Could not save feedback: {ex.Message}");
            }
        }
    }

    public static List<FeedbackItem> GetFeedback()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FeedbackPath))
                {
                    var json = File.ReadAllText(FeedbackPath);
                    return JsonSerializer.Deserialize<List<FeedbackItem>>(json) ?? new List<FeedbackItem>();
                }
            }
            catch { }
            return new List<FeedbackItem>();
        }
    }

    public static void UpdateCustomSongsPath(string path)
    {
        lock (_lock)
        {
            Current.CustomSongsPath = path?.Trim() ?? "";
            Save();
        }
    }

    public static void RegisterCreatedSong(string songId, string? targetDir = null)
    {
        lock (_lock)
        {
            bool changed = false;
            if (!string.IsNullOrWhiteSpace(songId) && !Current.CreatedSongIds.Contains(songId, StringComparer.OrdinalIgnoreCase))
            {
                Current.CreatedSongIds.Add(songId);
                changed = true;
            }

            if (!string.IsNullOrWhiteSpace(targetDir) && Directory.Exists(targetDir))
            {
                if (Current.CreatedSongDirectories == null) Current.CreatedSongDirectories = new();
                if (!Current.CreatedSongDirectories.Contains(targetDir, StringComparer.OrdinalIgnoreCase))
                {
                    Current.CreatedSongDirectories.Add(targetDir);
                    changed = true;
                }
            }

            if (changed) Save();
        }
    }

    public static void UnregisterCreatedSong(string songId, string? targetDir = null)
    {
        lock (_lock)
        {
            bool changed = false;
            if (!string.IsNullOrWhiteSpace(songId))
            {
                int removed = Current.CreatedSongIds.RemoveAll(s => s.Equals(songId, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) changed = true;
            }
            if (!string.IsNullOrWhiteSpace(targetDir) && Current.CreatedSongDirectories != null)
            {
                int removed = Current.CreatedSongDirectories.RemoveAll(d => d.Equals(targetDir, StringComparison.OrdinalIgnoreCase));
                if (removed > 0) changed = true;
            }
            if (changed) Save();
        }
    }

    public static bool ToggleFavorite(string songId)
    {
        lock (_lock)
        {
            if (Current.FavoriteSongIds == null) Current.FavoriteSongIds = new();
            bool exists = Current.FavoriteSongIds.Any(s => s.Equals(songId, StringComparison.OrdinalIgnoreCase));
            if (exists)
                Current.FavoriteSongIds.RemoveAll(s => s.Equals(songId, StringComparison.OrdinalIgnoreCase));
            else
                Current.FavoriteSongIds.Add(songId);
            Save();
            return !exists;
        }
    }

    public static (bool Added, string Message) ToggleLobbySetlist(string songId)
    {
        lock (_lock)
        {
            if (Current.LobbySetlistIds == null) Current.LobbySetlistIds = new();
            int max = Math.Clamp(Current.MaxLobbyAutoSyncSongs, 1, 25);
            bool exists = Current.LobbySetlistIds.Any(s => s.Equals(songId, StringComparison.OrdinalIgnoreCase));
            if (exists)
            {
                Current.LobbySetlistIds.RemoveAll(s => s.Equals(songId, StringComparison.OrdinalIgnoreCase));
                Save();
                return (false, $"Removed from Lobby Auto-Sync ({Current.LobbySetlistIds.Count}/{max}).");
            }
            if (Current.LobbySetlistIds.Count >= max)
            {
                return (false, $"Lobby Auto-Sync limit reached ({max}/{max}). Uncheck another song or raise the limit in P2P Sync.");
            }
            Current.LobbySetlistIds.Add(songId);
            Save();
            return (true, $"Added to Lobby Auto-Sync ({Current.LobbySetlistIds.Count}/{max}).");
        }
    }

    private static void EnsureSeedCreatedSongs()
    {
        string[] seeds = new[] { "beatit_6ce4b6e5", "gentlyweeps_26711a91", "onogami_fa5fdfea", "beatit", "gentlyweeps", "onogami" };
        foreach (var s in seeds)
        {
            if (!Current.CreatedSongIds.Contains(s, StringComparer.OrdinalIgnoreCase))
                Current.CreatedSongIds.Add(s);
        }
    }

    public static string GenerateFriendCode()
    {
        var raw = Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToUpperInvariant();
        return $"GH3-{raw.Substring(0, 4)}-{raw.Substring(4, 4)}";
    }
}
