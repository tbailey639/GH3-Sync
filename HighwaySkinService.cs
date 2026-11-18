using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace GH3_Sync;

public class HighwaySkinInfo
{
    public string Name { get; set; } = "";
    public string FilePath { get; set; } = "";
    public bool IsActive { get; set; }
}

/// <summary>
/// Creates, previews, installs, backs up, and restores custom Guitar Hero III highway fretboard textures.
/// Automatically formats any PNG/JPG into a clean 512x1024 seamless highway skin inside DATA\IMAGES\Highways
/// and keeps safe backups in %APPDATA%\GH3-Sync\HighwaySkins.
/// </summary>
public class HighwaySkinService
{
    private readonly Gh3Service _gh3;
    private readonly SyncLogger _logger;

    public static string SkinsVaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "HighwaySkins");

    public string GameHighwaysDir => Path.Combine(_gh3.GamePath ?? "", "DATA", "IMAGES", "Highways");
    public string ActiveCustomHighwayPath => Path.Combine(GameHighwaysDir, "custom_highway.png");

    public HighwaySkinService(Gh3Service gh3, SyncLogger logger)
    {
        _gh3 = gh3;
        _logger = logger;
        try { Directory.CreateDirectory(SkinsVaultDir); } catch { }
    }

    public List<HighwaySkinInfo> GetSavedSkins()
    {
        var list = new List<HighwaySkinInfo>();
        if (!Directory.Exists(SkinsVaultDir)) return list;
        var activeNameFile = Path.Combine(SkinsVaultDir, "_active.txt");
        var activeName = File.Exists(activeNameFile) ? File.ReadAllText(activeNameFile).Trim() : "";

        foreach (var f in Directory.GetFiles(SkinsVaultDir, "*.png").OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            list.Add(new HighwaySkinInfo
            {
                Name = name,
                FilePath = f,
                IsActive = name.Equals(activeName, StringComparison.OrdinalIgnoreCase) && File.Exists(ActiveCustomHighwayPath)
            });
        }
        return list;
    }

    public (bool Ok, string OutputPath, string Message) ImportAndApplyHighwayImage(string sourceImagePath, string skinName)
    {
        if (string.IsNullOrWhiteSpace(sourceImagePath) || !File.Exists(sourceImagePath))
            return (false, "", "Source image file not found.");

        try
        {
            Directory.CreateDirectory(SkinsVaultDir);
            Directory.CreateDirectory(GameHighwaysDir);

            var cleanName = string.Concat((string.IsNullOrWhiteSpace(skinName) ? Path.GetFileNameWithoutExtension(sourceImagePath) : skinName)
                .Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
            if (string.IsNullOrWhiteSpace(cleanName)) cleanName = "CustomHighway";

            var vaultPng = Path.Combine(SkinsVaultDir, $"{cleanName}.png");

            // Convert & format to clean 512x1024 vertical highway fretboard texture with subtle dark edge vignette
            using (var src = new Bitmap(sourceImagePath))
            using (var dest = new Bitmap(512, 1024, PixelFormat.Format32bppArgb))
            using (var g = Graphics.FromImage(dest))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.DrawImage(src, 0, 0, 512, 1024);

                // Subtle fretboard side rails & 4 lane guide lines for in-game readability
                using var railPen = new Pen(Color.FromArgb(140, 226, 232, 240), 4f);
                g.DrawLine(railPen, 3, 0, 3, 1024);
                g.DrawLine(railPen, 509, 0, 509, 1024);
                using var lanePen = new Pen(Color.FromArgb(55, 255, 255, 255), 1.5f);
                for (int i = 1; i <= 4; i++)
                {
                    float lx = i * (512f / 5f);
                    g.DrawLine(lanePen, lx, 0, lx, 1024);
                }

                dest.Save(vaultPng, ImageFormat.Png);
            }

            File.Copy(vaultPng, ActiveCustomHighwayPath, overwrite: true);
            File.WriteAllText(Path.Combine(SkinsVaultDir, "_active.txt"), cleanName);

            _logger.LogInfo($"[HIGHWAY SKIN] Formatted & installed custom highway '{cleanName}' -> {ActiveCustomHighwayPath}");
            return (true, vaultPng, $"Installed custom highway skin '{cleanName}' (512x1024) into DATA\\IMAGES\\Highways!");
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[HIGHWAY SKIN] Failed: {ex.Message}");
            return (false, "", $"Could not create highway skin: {ex.Message}");
        }
    }

    public (bool Ok, string Message) ApplySavedSkin(string skinName)
    {
        var vaultPng = Path.Combine(SkinsVaultDir, $"{skinName}.png");
        if (!File.Exists(vaultPng)) return (false, "Saved skin not found.");
        try
        {
            Directory.CreateDirectory(GameHighwaysDir);
            File.Copy(vaultPng, ActiveCustomHighwayPath, overwrite: true);
            File.WriteAllText(Path.Combine(SkinsVaultDir, "_active.txt"), skinName);
            return (true, $"Activated highway skin '{skinName}'!");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public (bool Ok, string Message) RestoreDefaultHighway()
    {
        try
        {
            if (File.Exists(ActiveCustomHighwayPath)) File.Delete(ActiveCustomHighwayPath);
            var activeFile = Path.Combine(SkinsVaultDir, "_active.txt");
            if (File.Exists(activeFile)) File.Delete(activeFile);
            _logger.LogInfo("[HIGHWAY SKIN] Restored default GH3 highway.");
            return (true, "Restored default Guitar Hero III highway texture.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
