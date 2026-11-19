using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GH3_Sync;

/// <summary>
/// Tour Poster Studio: Generates high-res concert merchandise and gig flyers
/// in 4 authentic eras (70s Psychedelic, 80s Hair Metal, 90s Grunge, Modern Festival).
/// </summary>
public static class TourPosterStudioService
{
    public enum PosterStyle
    {
        Psychedelic70s,
        HairMetal80s,
        Grunge90s,
        ModernFestival
    }

    public static string GeneratePosterMarkdownSummary(string tourTitle, string venue, List<string> songTitles, PosterStyle style)
    {
        var eraName = style switch
        {
            PosterStyle.Psychedelic70s => "☮️ 1970s Psychedelic Acid Rock Flyer",
            PosterStyle.HairMetal80s => "⚡ 1980s Sunset Strip Hair Metal Gig Poster",
            PosterStyle.Grunge90s => "🎸 1990s Underground Grunge Halftone Flyer",
            PosterStyle.ModernFestival => "🎪 2026 World Tour Stadium Festival Banner",
            _ => "🎸 Concert Tour Poster"
        };

        var sb = new StringBuilder();
        sb.AppendLine($"# {eraName}");
        sb.AppendLine($"**HEADLINING TOUR:** {tourTitle.ToUpperInvariant()}");
        sb.AppendLine($"**VENUE:** {venue} • **DATE:** {DateTime.Now:MMMM dd, yyyy}");
        sb.AppendLine("---");
        sb.AppendLine("### 🎟️ OFFICIAL SETLIST BILLING:");
        for (int i = 0; i < songTitles.Count; i++)
        {
            sb.AppendLine($"{i + 1}. **{songTitles[i]}**");
        }
        sb.AppendLine("---");
        sb.AppendLine($"*Printed by GH3-Sync Tour Poster Studio • Designed for BetterGH3 Community & Discord*");
        return sb.ToString();
    }
}
