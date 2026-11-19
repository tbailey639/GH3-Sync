using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GH3_Sync;

public record BattlePowerUp(int Measure, double TimeSeconds, string Type, int FretIndex);

public class BossBattleCreatorService
{
    public static readonly string[] PowerUpTypes =
    {
        "Broken String",  // Forces player to hammer fret repeatedly to unfreeze
        "Double Notes",   // Duplicates notes into chord clusters
        "Whammy Attack",  // Forces intense whammy bar action to clear highway static
        "Lefty Flip",     // Flips highway controls horizontally
        "Amp Overload",   // Highway flickers and stutters with vintage vacuum tube glow
        "Difficulty Up",  // Temporarily elevates note density
        "Steal Power"     // Siphons rival player/boss power-up slot
    };

    public record BossBattleConfig(
        string BossName,
        string Difficulty,       // Apprentice, Shredder, Rock God
        int PowerUpDensity,      // 1 (Sparce) to 5 (Intense duel)
        double BossHealthDrain,  // Base health drain rate
        bool IncludeIntroSolo,
        string Venue
    );

    public (bool Success, string Message, int BattleGemsGenerated) ConvertChartToBossBattle(
        string chartPath,
        BossBattleConfig config)
    {
        if (!File.Exists(chartPath))
            return (false, $"Chart file not found: {chartPath}", 0);

        try
        {
            var lines = File.ReadAllLines(chartPath).ToList();
            int battleGemsCount = 0;
            var random = new Random();

            // Locate note tracks: [ExpertSingle], [HardSingle], etc.
            int trackIndex = lines.FindIndex(l => l.Contains("[ExpertSingle]", StringComparison.OrdinalIgnoreCase));
            if (trackIndex == -1)
                trackIndex = lines.FindIndex(l => l.Contains("[HardSingle]", StringComparison.OrdinalIgnoreCase));

            if (trackIndex == -1)
                return (false, "Could not find ExpertSingle or HardSingle track in chart.", 0);

            // In Guitar Hero III .chart format:
            // Special events and Star Power / Battle phrases are placed in [SyncTrack] or track sections
            // using "S 2 <length>" (Star Power) or "E battle_powerup" text events.
            var modifiedLines = new List<string>(lines.Count + 64);
            int notesSeenInMeasure = 0;
            int densityStep = Math.Max(12, 40 / Math.Max(1, config.PowerUpDensity));

            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                modifiedLines.Add(line);

                // Check for note lines: e.g. "  768 = N 0 0"
                if (line.TrimStart().Contains("= N "))
                {
                    notesSeenInMeasure++;
                    if (notesSeenInMeasure >= densityStep)
                    {
                        notesSeenInMeasure = 0;
                        var parts = line.Trim().Split('=');
                        if (parts.Length == 2 && long.TryParse(parts[0].Trim(), out long tick))
                        {
                            string chosenPowerUp = PowerUpTypes[random.Next(PowerUpTypes.Length)];
                            // Insert Battle Mode battle power-up event
                            modifiedLines.Add($"  {tick} = E \"battle_{chosenPowerUp.ToLowerInvariant().Replace(' ', '_')}\"");
                            // Add Star Power / Battle gem phrase marker (S 2 = battle phrase in GH3)
                            modifiedLines.Add($"  {tick} = S 2 768");
                            battleGemsCount++;
                        }
                    }
                }
            }

            // Write back to a new battle chart file
            string dir = Path.GetDirectoryName(chartPath)!;
            string battleChartPath = Path.Combine(dir, "notes_battle.chart");
            File.WriteAllLines(battleChartPath, modifiedLines, Encoding.UTF8);

            // Write Boss Battle metadata descriptor
            string metaPath = Path.Combine(dir, "boss_battle.ini");
            var sb = new StringBuilder();
            sb.AppendLine("[BossBattle]");
            sb.AppendLine($"BossName = {config.BossName}");
            sb.AppendLine($"Difficulty = {config.Difficulty}");
            sb.AppendLine($"PowerUpDensity = {config.PowerUpDensity}");
            sb.AppendLine($"BossHealthDrain = {config.BossHealthDrain:F2}");
            sb.AppendLine($"BattleGems = {battleGemsCount}");
            sb.AppendLine($"Venue = {config.Venue}");
            sb.AppendLine($"GeneratedDate = {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            File.WriteAllText(metaPath, sb.ToString(), Encoding.UTF8);

            return (true, $"Boss Battle Duel chart created with {battleGemsCount} battle power-up phrases!", battleGemsCount);
        }
        catch (Exception ex)
        {
            return (false, $"Error converting chart: {ex.Message}", 0);
        }
    }
}
