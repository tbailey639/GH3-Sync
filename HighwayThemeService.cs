using System;
using System.Collections.Generic;
using WpfColor = System.Windows.Media.Color;

namespace GH3_Sync;

/// <summary>
/// Dynamic Venue & Highway Reactive Visuals: Matches songs to atmospheric venue and highway themes
/// (Inferno, Neon Grid, Desert Arena, Star Power Nebula) based on genre, tempo, and tags.
/// </summary>
public static class HighwayThemeService
{
    public record HighwayThemePreset(
        string Id,
        string Name,
        string VenueName,
        WpfColor NeckColor,
        WpfColor SideRailColor,
        WpfColor AmbientLightColor,
        WpfColor ParticleColor,
        string Description);

    public static readonly HighwayThemePreset Inferno = new(
        "inferno",
        "Inferno Pit",
        "Lou's Inferno",
        WpfColor.FromRgb(26, 10, 10),
        WpfColor.FromRgb(239, 68, 68),
        WpfColor.FromRgb(249, 115, 22),
        WpfColor.FromRgb(251, 146, 60),
        "Blazing lava streams, molten steel rails, and fiery embers for heavy metal and high BPM shredding.");

    public static readonly HighwayThemePreset NeonGrid = new(
        "neongrid",
        "Neon Underground",
        "Tokyo Underground",
        WpfColor.FromRgb(10, 15, 28),
        WpfColor.FromRgb(6, 182, 212),
        WpfColor.FromRgb(168, 85, 247),
        WpfColor.FromRgb(56, 189, 248),
        "Cyberpunk glowing neon grid, laser side-rails, and electric cyan particles for synthwave and electronic rock.");

    public static readonly HighwayThemePreset DesertArena = new(
        "desertarena",
        "Desert Rock Arena",
        "The Desert Rock Arena",
        WpfColor.FromRgb(28, 20, 15),
        WpfColor.FromRgb(217, 119, 6),
        WpfColor.FromRgb(245, 158, 11),
        WpfColor.FromRgb(252, 211, 77),
        "Sun-baked canyon stone, golden stadium spotlights, and warm dust flares for classic 70s/80s hard rock.");

    public static readonly HighwayThemePreset StarNebula = new(
        "starnebula",
        "Star Power Nebula",
        "Electric Colosseum",
        WpfColor.FromRgb(18, 12, 32),
        WpfColor.FromRgb(139, 92, 246),
        WpfColor.FromRgb(192, 132, 252),
        WpfColor.FromRgb(216, 180, 254),
        "Deep cosmic violet nebulae, starlight rails, and shimmering astral dust for epic prog rock and anthemic ballads.");

    public static List<HighwayThemePreset> AllPresets => new() { Inferno, NeonGrid, DesertArena, StarNebula };

    /// <summary>
    /// Auto-detects the optimal highway theme preset based on song metadata.
    /// </summary>
    public static HighwayThemePreset DetectTheme(string title, string artist, double bpm, List<string>? tags = null)
    {
        var text = $"{title} {artist} {string.Join(" ", tags ?? new())}".ToLowerInvariant();

        if (text.Contains("metal") || text.Contains("death") || text.Contains("slayer") || text.Contains("dragon") || bpm >= 165)
            return Inferno;

        if (text.Contains("synth") || text.Contains("cyber") || text.Contains("electro") || text.Contains("future") || text.Contains("wave"))
            return NeonGrid;

        if (text.Contains("star") || text.Contains("dream") || text.Contains("space") || text.Contains("ballad") || text.Contains("pink floyd"))
            return StarNebula;

        return DesertArena;
    }
}
