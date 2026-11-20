using System;
using System.Collections.Generic;
using System.Linq;

namespace GH3_Sync;

/// <summary>
/// In-App Rock Hall of Fame & Achievements Showcase:
/// Tracks unlockable milestones, trophies, and stats celebrating custom charting and multiplayer achievements.
/// </summary>
public static class RockHallOfFameService
{
    public record RockAchievement(
        string Id,
        string Title,
        string Description,
        string Icon,
        string Tier, // "Gold", "Platinum", "Diamond"
        bool IsUnlocked);

    public static readonly List<(string Id, string Title, string Description, string Icon, string Tier)> Definitions = new()
    {
        ("FC_MASTER", "Full Combo Master", "Hit a 100% streak on an Expert custom chart", "👑", "Diamond"),
        ("ONE_MILLION_CLUB", "The 1 Million Club", "Score over 1,000,000 points on a single song", "💰", "Diamond"),
        ("MASTER_CHARTER", "Master Charter", "Create and compile 5 or more custom songs with verified signatures", "✍️", "Gold"),
        ("ROAD_WARRIOR", "Road Warrior", "Generate and complete a Rock Tour Tournament bracket", "🚌", "Gold"),
        ("BOSS_SLAYER", "Boss Slayer", "Generate a Boss Battle Duel chart with authentic battle gems", "⚡", "Gold"),
        ("GLOBAL_ROCKER", "Global Rocker", "Connect and sync custom setlists with 3 or more P2P friends", "🌐", "Platinum"),
        ("MEDLEY_MARATHON", "Marathon Legend", "Assemble an Endless Rock Concert Medley of 3+ tracks", "🎸", "Platinum"),
        ("STAGE_VIRTUOSO", "Stage Virtuoso", "Calibrate controller latency down to under 5ms jitter at 1000Hz", "⏱️", "Gold"),
        ("TRUSTED_DUO", "Brothers in Shred", "Form a mutual Trusted Friend bond for permanent custom song sharing", "🤝", "Gold"),
        ("SETLIST_SHREDDER", "Setlist Virtuoso", "Mirror and rock out to a full 10-song custom lobby setlist", "📑", "Platinum"),
        ("HYPE_MASTER", "Crowd Pleaser", "Blast an Airhorn or Hype SFX during a live multiplayer lobby session", "📢", "Gold"),
        ("FIVE_STAR_STREAK", "5-Star Phenomenon", "Achieve 5 Stars on 10 or more custom tracks in career saves", "⭐", "Diamond"),
        ("ROCK_OUT_DUO", "Rock Out Partner", "Send or accept an Ask to Play Together multiplayer invite", "🎸", "Platinum"),
        ("AI_STEM_WIZARD", "Stem Wizard", "Separate guitar, bass, vocals, and crowd stems with Demucs AI", "🧪", "Gold")
    };

    public static List<RockAchievement> GetAllAchievements()
    {
        var unlocked = new HashSet<string>(AppConfig.Current.UnlockedAchievements ?? new(), StringComparer.OrdinalIgnoreCase);

        // Auto-check dynamic conditions
        if (AppConfig.Current.CreatedSongDirectories?.Count >= 5)
            unlocked.Add("MASTER_CHARTER");

        if (AppConfig.Current.Friends?.Count >= 3)
            unlocked.Add("GLOBAL_ROCKER");

        if (AppConfig.Current.Friends?.Any(f => f.IsTrusted) == true || (AppConfig.Current.KeptFriendSongIds?.Count ?? 0) > 0)
            unlocked.Add("TRUSTED_DUO");

        return Definitions.Select(d => new RockAchievement(
            d.Id,
            d.Title,
            d.Description,
            d.Icon,
            d.Tier,
            unlocked.Contains(d.Id)
        )).ToList();
    }

    public static bool UnlockAchievement(string id)
    {
        if (AppConfig.Current.UnlockedAchievements == null)
            AppConfig.Current.UnlockedAchievements = new();

        if (!AppConfig.Current.UnlockedAchievements.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            AppConfig.Current.UnlockedAchievements.Add(id);
            AppConfig.Save();
            return true;
        }
        return false;
    }

    public static string GenerateHallOfFameSummary()
    {
        var achievements = GetAllAchievements();
        int unlockedCount = achievements.Count(a => a.IsUnlocked);
        int total = achievements.Count;
        int pct = total > 0 ? (int)((unlockedCount / (double)total) * 100) : 0;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"🏆 ROCK HALL OF FAME TROPHY SHOWCASE ({unlockedCount}/{total} Unlocked - {pct}%)");
        sb.AppendLine("═══════════════════════════════════════════════════════");
        foreach (var a in achievements)
        {
            string status = a.IsUnlocked ? "✓ UNLOCKED" : "🔒 LOCKED";
            sb.AppendLine($"{a.Icon} [{a.Tier.ToUpperInvariant()}] {a.Title} — {status}");
            sb.AppendLine($"   {a.Description}");
        }
        return sb.ToString();
    }
}
