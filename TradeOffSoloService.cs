using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GH3_Sync;

/// <summary>
/// Trade-Off Solo Guitar Duels: Asymmetric Co-Op solos where Lead (P1) and Rhythm/Bass (P2)
/// alternate measures or phrases during guitar solos, trading licks back and forth.
/// </summary>
public static class TradeOffSoloService
{
    public record SoloPhrase(int MeasureStart, int MeasureEnd, int AssignedPlayer); // 1 = Lead, 2 = Rhythm/CoOp

    /// <summary>
    /// Processes a .chart file to generate a Trade-Off Solo duel.
    /// Alternates measures during [solo_on] ... [solo_off] sections between Single (Lead) and DoubleBass/Rhythm.
    /// </summary>
    public static (bool Success, string Message, string ModifiedChartContent) GenerateTradeOffSoloChart(string chartContent, int measuresPerTrade = 2)
    {
        if (string.IsNullOrWhiteSpace(chartContent))
            return (false, "Empty chart content", "");

        try
        {
            var lines = chartContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var sb = new StringBuilder();

            bool inSolo = false;
            int currentMeasure = 0;
            int activePlayer = 1; // 1 = Lead, 2 = Rhythm
            int measuresInCurrentTrade = 0;

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.Trim();

                // Detect solo markers
                if (trimmed.Contains("E \"solo_on\"") || trimmed.Contains("E \"solo\"") || trimmed.Contains("E solo_on"))
                {
                    inSolo = true;
                    measuresInCurrentTrade = 0;
                    activePlayer = 1;
                    sb.AppendLine(line);
                    sb.AppendLine($"  {GetResolutionTick(trimmed)} = E \"section Trade-Off Solo (P1 Lead)\"");
                    continue;
                }
                else if (trimmed.Contains("E \"solo_off\"") || trimmed.Contains("E solo_off"))
                {
                    inSolo = false;
                    sb.AppendLine(line);
                    continue;
                }

                // Detect measure boundaries (e.g. TS / time signature or beat markers)
                if (inSolo && trimmed.Contains(" = TS "))
                {
                    currentMeasure++;
                    measuresInCurrentTrade++;
                    if (measuresInCurrentTrade >= measuresPerTrade)
                    {
                        measuresInCurrentTrade = 0;
                        activePlayer = (activePlayer == 1) ? 2 : 1;
                        sb.AppendLine($"  {GetResolutionTick(trimmed)} = E \"section Trade-Off (P{activePlayer} Turn!)\"");
                    }
                }

                sb.AppendLine(line);
            }

            return (true, $"Trade-off solo duels successfully generated (alternating every {measuresPerTrade} measures)!", sb.ToString());
        }
        catch (Exception ex)
        {
            return (false, $"Error generating trade-off solo: {ex.Message}", chartContent);
        }
    }

    private static string GetResolutionTick(string line)
    {
        var parts = line.Split('=', StringSplitOptions.TrimEntries);
        if (parts.Length > 0 && long.TryParse(parts[0], out var tick))
        {
            return tick.ToString();
        }
        return "0";
    }
}
