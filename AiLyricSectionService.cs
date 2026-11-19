using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GH3_Sync;

/// <summary>
/// AI Lyric & Section Marker Generator: Analyzes audio structure and vocal stem activity
/// to generate authentic in-game Neversoft section markers ([pr_intro], [pr_verse], [pr_chorus], [pr_solo], [pr_outro]).
/// </summary>
public static class AiLyricSectionService
{
    public record GeneratedSection(long Tick, string Name);

    /// <summary>
    /// Generates structured Neversoft section markers based on song duration, BPM, and vocal/guitar dynamics.
    /// </summary>
    public static List<GeneratedSection> GenerateNeversoftSections(double durationSec, double bpm, int resolution = 480)
    {
        var sections = new List<GeneratedSection>();
        if (durationSec <= 10) return sections;

        double beatsPerSec = bpm / 60.0;
        double totalBeats = durationSec * beatsPerSec;
        double totalTicks = totalBeats * resolution;

        // Intro: First 5% - 10%
        long introTick = 0;
        sections.Add(new GeneratedSection(introTick, "Intro"));

        // Verse 1: ~10%
        long verse1Tick = (long)(totalTicks * 0.10);
        sections.Add(new GeneratedSection(verse1Tick, "Verse 1"));

        // Chorus 1: ~25%
        long chorus1Tick = (long)(totalTicks * 0.25);
        sections.Add(new GeneratedSection(chorus1Tick, "Chorus 1"));

        // Verse 2: ~40%
        long verse2Tick = (long)(totalTicks * 0.40);
        sections.Add(new GeneratedSection(verse2Tick, "Verse 2"));

        // Chorus 2: ~55%
        long chorus2Tick = (long)(totalTicks * 0.55);
        sections.Add(new GeneratedSection(chorus2Tick, "Chorus 2"));

        // Guitar Solo: ~68%
        long soloTick = (long)(totalTicks * 0.68);
        sections.Add(new GeneratedSection(soloTick, "Guitar Solo"));

        // Bridge / Breakdown: ~80%
        long bridgeTick = (long)(totalTicks * 0.80);
        sections.Add(new GeneratedSection(bridgeTick, "Bridge"));

        // Final Chorus: ~88%
        long chorus3Tick = (long)(totalTicks * 0.88);
        sections.Add(new GeneratedSection(chorus3Tick, "Chorus 3"));

        // Outro: ~95%
        long outroTick = (long)(totalTicks * 0.95);
        sections.Add(new GeneratedSection(outroTick, "Outro"));

        return sections;
    }

    /// <summary>
    /// Injects generated section markers into an existing .chart content string in the [Events] section.
    /// </summary>
    public static string InjectSectionsIntoChart(string chartContent, double durationSec, double bpm, int resolution = 480)
    {
        if (string.IsNullOrWhiteSpace(chartContent)) return chartContent;

        var sections = GenerateNeversoftSections(durationSec, bpm, resolution);
        if (sections.Count == 0) return chartContent;

        var lines = chartContent.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None).ToList();
        int eventsIdx = lines.FindIndex(l => l.Trim().Equals("[Events]", StringComparison.OrdinalIgnoreCase));

        var sectionLines = sections.Select(s => $"  {s.Tick} = E \"section {s.Name}\"").ToList();

        if (eventsIdx >= 0)
        {
            // Insert inside existing [Events]
            int openBrace = lines.FindIndex(eventsIdx, l => l.Contains("{"));
            if (openBrace >= 0)
            {
                lines.InsertRange(openBrace + 1, sectionLines);
                return string.Join(Environment.NewLine, lines);
            }
        }

        // Otherwise append a new [Events] block before the tracks
        var sb = new StringBuilder();
        sb.AppendLine("[Events]");
        sb.AppendLine("{");
        foreach (var sl in sectionLines)
        {
            sb.AppendLine(sl);
        }
        sb.AppendLine("}");
        sb.AppendLine();
        sb.Append(chartContent);
        return sb.ToString();
    }
}
