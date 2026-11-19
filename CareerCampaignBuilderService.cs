using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GH3_Sync;

public record CampaignSong(string SongId, string Title, string Artist, int TierIndex, bool IsEncore, bool IsBossDuel);

public record CareerTier(
    int TierNumber,
    string TierName,
    string VenueName,
    int SongsRequiredForEncore,
    List<CampaignSong> Songs,
    CampaignSong? EncoreSong,
    CampaignSong? BossDuel
);

public record CustomCareerCampaign(
    string CampaignId,
    string CampaignTitle,
    string Creator,
    DateTime CreatedAt,
    List<CareerTier> Tiers
);

public class CareerCampaignBuilderService
{
    public static readonly string[] DefaultVenues =
    {
        "Suburban Garage",
        "Mitch's Moose Lounge",
        "The Video Shoot",
        "European Metal Fest",
        "The Desert Rock Arena",
        "Tokyo Mega Dome",
        "Underworld Prison",
        "Battle for Lou's Soul (Hell)"
    };

    public static readonly string[] DefaultTierNames =
    {
        "Tier 1: Opening Act",
        "Tier 2: Dive Bar Heroes",
        "Tier 3: Club Circuit",
        "Tier 4: Festival Shredders",
        "Tier 5: Stadium Giants",
        "Tier 6: International Tour",
        "Tier 7: Netherworld Ascension",
        "Tier 8: Battle of the Rock Gods"
    };

    public CustomCareerCampaign CreateDefaultCampaign(string title, List<CustomSongInfo> songs)
    {
        var tiers = new List<CareerTier>();
        int songIndex = 0;

        for (int t = 0; t < 8; t++)
        {
            var tierSongs = new List<CampaignSong>();
            int songsInThisTier = 4;

            for (int s = 0; s < songsInThisTier && songIndex < songs.Count; s++)
            {
                var src = songs[songIndex++];
                tierSongs.Add(new CampaignSong(src.SongId, src.Title, src.Artist, t + 1, false, false));
            }

            CampaignSong? encore = null;
            if (songIndex < songs.Count)
            {
                var encSrc = songs[songIndex++];
                encore = new CampaignSong(encSrc.SongId, encSrc.Title, encSrc.Artist, t + 1, true, false);
            }

            CampaignSong? boss = null;
            if (t == 7 && songIndex < songs.Count)
            {
                var bossSrc = songs[songIndex++];
                boss = new CampaignSong(bossSrc.SongId, bossSrc.Title, bossSrc.Artist, t + 1, false, true);
            }

            tiers.Add(new CareerTier(
                TierNumber: t + 1,
                TierName: DefaultTierNames[t],
                VenueName: DefaultVenues[t],
                SongsRequiredForEncore: Math.Min(3, tierSongs.Count),
                Songs: tierSongs,
                EncoreSong: encore,
                BossDuel: boss
            ));
        }

        return new CustomCareerCampaign(
            CampaignId: Guid.NewGuid().ToString("N")[..8],
            CampaignTitle: title,
            Creator: AppConfig.Current.Username ?? "Rock Legend",
            CreatedAt: DateTime.UtcNow,
            Tiers: tiers
        );
    }

    public void SaveCampaign(CustomCareerCampaign campaign, string saveDirectory)
    {
        Directory.CreateDirectory(saveDirectory);
        string path = Path.Combine(saveDirectory, $"campaign_{campaign.CampaignId}.json");
        var json = JsonSerializer.Serialize(campaign, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    public List<CustomCareerCampaign> LoadCampaigns(string saveDirectory)
    {
        var list = new List<CustomCareerCampaign>();
        if (!Directory.Exists(saveDirectory)) return list;

        foreach (var file in Directory.GetFiles(saveDirectory, "campaign_*.json"))
        {
            try
            {
                var json = File.ReadAllText(file);
                var c = JsonSerializer.Deserialize<CustomCareerCampaign>(json);
                if (c != null) list.Add(c);
            }
            catch { }
        }

        return list;
    }
}
