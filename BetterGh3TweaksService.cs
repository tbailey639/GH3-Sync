using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace GH3_Sync;

public class BetterGh3PluginInfo
{
    public string FileName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Description { get; set; } = "";
    public bool IsEnabled { get; set; }
    public bool IsCoreProtected { get; set; }
    public string FullPath { get; set; } = "";
}

/// <summary>
/// Safely inspects and toggles BetterGH3 PLUGINS (*.dll) between PLUGINS\ and PLUGINS\Disabled\,
/// while protecting critical core.dll from accidental removal.
/// </summary>
public class BetterGh3TweaksService
{
    private readonly Gh3Service _gh3;
    private readonly SyncLogger _logger;

    private static readonly Dictionary<string, (string Title, string Desc, bool Core)> KnownPlugins =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["core.dll"] = ("BetterGH3 Core Engine", "Required BetterGH3 core loader & custom song support (protected).", true),
            ["BorderlessWindow.dll"] = ("Borderless Fullscreen Window", "Runs GH3 in borderless fullscreen for instant Alt-Tab and smooth HUD overlay rendering.", false),
            ["FastGH3.dll"] = ("FastGH3 Speed & Startup Patch", "Skips intro splash movies and speeds up menu & song loading.", false),
            ["NoteLimitFix.dll"] = ("Note Limit Fix", "Removes GH3's vanilla note buffer ceiling so dense custom solos never drop notes.", false),
            ["SongLimitFix.dll"] = ("Custom Song Limit Fix", "Expands the custom song setlist ceiling beyond vanilla GH3 limits.", false),
            ["TapHopoChord.dll"] = ("Clone Hero / Modern HOPO & Tap Support", "Enables modern tap notes, extended sustains, and open-note purple bars.", false),
            ["UncappedFPS.dll"] = ("Uncapped Frame Rate", "Unlocks frame rate above 60 FPS for ultra-smooth highway scrolling on high-Hz monitors.", false),
            ["WhammyFix.dll"] = ("Whammy Bar Input Fix", "Fixes whammy axis deadzones and Star Power whammy responsiveness on modern guitars.", false),
        };

    public BetterGh3TweaksService(Gh3Service gh3, SyncLogger logger)
    {
        _gh3 = gh3;
        _logger = logger;
    }

    public string PluginsDir => Path.Combine(_gh3.GamePath ?? "", "PLUGINS");
    public string DisabledDir => Path.Combine(PluginsDir, "Disabled");
    public string HighwaysDir => Path.Combine(_gh3.GamePath ?? "", "DATA", "IMAGES");
    public string X360ceExePath => Path.Combine(_gh3.GamePath ?? "", "x360ce.exe");

    public bool IsBetterGh3PluginsPresent() => Directory.Exists(PluginsDir);

    public List<BetterGh3PluginInfo> GetPlugins()
    {
        var list = new List<BetterGh3PluginInfo>();
        if (!Directory.Exists(PluginsDir)) return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dll in Directory.GetFiles(PluginsDir, "*.dll").OrderBy(Path.GetFileName))
        {
            var name = Path.GetFileName(dll);
            seen.Add(name);
            list.Add(BuildInfo(name, dll, enabled: true));
        }

        if (Directory.Exists(DisabledDir))
        {
            foreach (var dll in Directory.GetFiles(DisabledDir, "*.dll").OrderBy(Path.GetFileName))
            {
                var name = Path.GetFileName(dll);
                if (seen.Contains(name)) continue;
                list.Add(BuildInfo(name, dll, enabled: false));
            }
        }

        return list.OrderByDescending(p => p.IsCoreProtected).ThenBy(p => p.DisplayName).ToList();
    }

    private static BetterGh3PluginInfo BuildInfo(string fileName, string fullPath, bool enabled)
    {
        if (KnownPlugins.TryGetValue(fileName, out var meta))
        {
            return new BetterGh3PluginInfo
            {
                FileName = fileName,
                DisplayName = meta.Title,
                Description = meta.Desc,
                IsEnabled = enabled,
                IsCoreProtected = meta.Core,
                FullPath = fullPath
            };
        }

        return new BetterGh3PluginInfo
        {
            FileName = fileName,
            DisplayName = Path.GetFileNameWithoutExtension(fileName),
            Description = "Community BetterGH3 plugin module.",
            IsEnabled = enabled,
            IsCoreProtected = false,
            FullPath = fullPath
        };
    }

    public (bool Ok, string Message) SetPluginEnabled(string fileName, bool enable)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return (false, "Invalid plugin filename.");

        if (fileName.Equals("core.dll", StringComparison.OrdinalIgnoreCase))
            return (false, "core.dll is required by BetterGH3 and cannot be disabled.");

        if (!Directory.Exists(PluginsDir))
            return (false, "BetterGH3 PLUGINS folder was not found.");

        try
        {
            Directory.CreateDirectory(DisabledDir);
            var activePath = Path.Combine(PluginsDir, fileName);
            var disabledPath = Path.Combine(DisabledDir, fileName);

            if (enable)
            {
                if (File.Exists(activePath)) return (true, $"{fileName} is already enabled.");
                if (!File.Exists(disabledPath)) return (false, $"{fileName} was not found in PLUGINS\\Disabled.");
                File.Move(disabledPath, activePath);
                _logger.LogInfo($"[BETTERGH3] Enabled plugin: {fileName}");
                return (true, $"Enabled {fileName}. Restart GH3 if currently running.");
            }
            else
            {
                if (File.Exists(disabledPath) && !File.Exists(activePath)) return (true, $"{fileName} is already disabled.");
                if (!File.Exists(activePath)) return (false, $"{fileName} was not found in PLUGINS.");
                if (File.Exists(disabledPath)) File.Delete(disabledPath);
                File.Move(activePath, disabledPath);
                _logger.LogInfo($"[BETTERGH3] Disabled plugin: {fileName}");
                return (true, $"Disabled {fileName} (moved safely to PLUGINS\\Disabled).");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[BETTERGH3] Could not toggle {fileName}: {ex.Message}");
            return (false, $"Could not toggle {fileName}: {ex.Message}");
        }
    }
}
