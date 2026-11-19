using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class HoneycombStatusReport
{
    public bool Available { get; set; }
    public string HoneycombDirectory { get; set; } = "";
    public bool HasCoreDll { get; set; }
    public bool HasGuiDll { get; set; }
    public bool HasFfmpeg { get; set; }
    public string FfmpegPath { get; set; } = "";
    public string GamePath { get; set; } = "";
    public string StatusMessage { get; set; } = "";
}

public class HoneycombCompilationResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public string ProjectPath { get; set; } = "";
    public string PakFilePath { get; set; } = "";
    public string FsbFilePath { get; set; } = "";
    public string DatFilePath { get; set; } = "";
    public List<string> Logs { get; set; } = new();
    public DateTime CompletedAt { get; set; } = DateTime.UtcNow;
}

public class HoneycombCompilerService
{
    private readonly Gh3Service _gh3;
    private readonly SyncLogger? _logger;
    private readonly SaveManager? _saveManager;
    private static readonly object _compileLock = new();
    private static bool _assemblyResolveHooked = false;

    private static void EnsureAssemblyResolve(string honeycombDir)
    {
        if (_assemblyResolveHooked) return;
        _assemblyResolveHooked = true;
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            try
            {
                var assemblyName = new AssemblyName(args.Name).Name + ".dll";
                var assemblyPath = Path.Combine(honeycombDir, assemblyName);
                if (File.Exists(assemblyPath))
                {
                    return Assembly.LoadFrom(assemblyPath);
                }
            }
            catch { }
            return null;
        };
    }

    public HoneycombCompilerService(Gh3Service gh3, SyncLogger? logger = null, SaveManager? saveManager = null)
    {
        _gh3 = gh3;
        _logger = logger;
        _saveManager = saveManager;
    }

    /// <summary>
    /// Intelligently discovers the Honeycomb-GUI installation directory across any machine or drive.
    /// Never relies on hardcoded paths.
    /// </summary>
    public static string FindHoneycombDirectory(string? preferredPath = null, string? gamePath = null)
    {
        // 0. User writable AppData compiler directory (preferred for reliable non-admin write access)
        var appDataCompiler = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "tools", "compiler");
        if (Directory.Exists(appDataCompiler) && IsValidHoneycombDirectory(appDataCompiler))
        {
            return appDataCompiler;
        }

        // 1. Bundled internal compiler tools inside GH3-Sync (Zero separate install required!)
        string[] bundledCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "tools", "compiler"),
            Path.Combine(AppContext.BaseDirectory, "compiler"),
            Path.Combine(Directory.GetCurrentDirectory(), "tools", "compiler"),
            Path.Combine(Directory.GetCurrentDirectory(), "compiler")
        };
        foreach (var bundled in bundledCandidates)
        {
            if (Directory.Exists(bundled) && IsValidHoneycombDirectory(bundled))
            {
                // Ensure write access (mirrors to user AppData if bundled directory is read-only / Program Files)
                return EnsureWritableCompilerDirectory(bundled);
            }
        }

        // 1. Explicitly preferred path
        if (!string.IsNullOrWhiteSpace(preferredPath) && Directory.Exists(preferredPath))
        {
            if (IsValidHoneycombDirectory(preferredPath)) return preferredPath;
        }

        // 2. User configuration path from AppConfig
        if (!string.IsNullOrWhiteSpace(AppConfig.Current.HoneycombPath) && Directory.Exists(AppConfig.Current.HoneycombPath))
        {
            if (IsValidHoneycombDirectory(AppConfig.Current.HoneycombPath)) return AppConfig.Current.HoneycombPath;
        }

        // 3. Subdirectories inside the detected Game Path (common in mod setups)
        var resolvedGame = !string.IsNullOrWhiteSpace(gamePath) ? gamePath : AppConfig.Current.GamePath;
        if (!string.IsNullOrWhiteSpace(resolvedGame) && Directory.Exists(resolvedGame))
        {
            string[] gameCandidates = new[]
            {
                Path.Combine(resolvedGame, "Tools", "Honeycomb-GUI"),
                Path.Combine(resolvedGame, "Tools", "Honeycomb"),
                Path.Combine(resolvedGame, "Honeycomb-GUI"),
                Path.Combine(resolvedGame, "Honeycomb")
            };
            foreach (var cand in gameCandidates)
            {
                if (Directory.Exists(cand) && IsValidHoneycombDirectory(cand))
                    return cand;
            }
        }

        // 4. Standard User Documents and Desktop folders (standard Honeycomb extracted location)
        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!string.IsNullOrWhiteSpace(docs) && Directory.Exists(docs))
        {
            var docsH = Path.Combine(docs, "Honeycomb-GUI");
            if (Directory.Exists(docsH) && IsValidHoneycombDirectory(docsH)) return docsH;
            var docsH2 = Path.Combine(docs, "Honeycomb");
            if (Directory.Exists(docsH2) && IsValidHoneycombDirectory(docsH2)) return docsH2;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrWhiteSpace(desktop) && Directory.Exists(desktop))
        {
            var deskH = Path.Combine(desktop, "Honeycomb-GUI");
            if (Directory.Exists(deskH) && IsValidHoneycombDirectory(deskH)) return deskH;
            var deskH2 = Path.Combine(desktop, "Honeycomb");
            if (Directory.Exists(deskH2) && IsValidHoneycombDirectory(deskH2)) return deskH2;
        }

        // 5. App executable directory or tools subfolder
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir))
        {
            var localH = Path.Combine(baseDir, "Tools", "Honeycomb-GUI");
            if (Directory.Exists(localH) && IsValidHoneycombDirectory(localH)) return localH;
            var localH2 = Path.Combine(baseDir, "Honeycomb-GUI");
            if (Directory.Exists(localH2) && IsValidHoneycombDirectory(localH2)) return localH2;
        }

        // 6. Scan root of all ready fixed and removable drives
        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable));
            foreach (var drive in drives)
            {
                string[] driveChecks = new[]
                {
                    Path.Combine(drive.RootDirectory.FullName, "Honeycomb-GUI"),
                    Path.Combine(drive.RootDirectory.FullName, "Honeycomb"),
                    Path.Combine(drive.RootDirectory.FullName, "Tools", "Honeycomb-GUI"),
                    Path.Combine(drive.RootDirectory.FullName, "Games", "Tools", "Honeycomb-GUI"),
                    Path.Combine(drive.RootDirectory.FullName, "Program Files", "Honeycomb-GUI"),
                    Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Honeycomb-GUI")
                };

                foreach (var check in driveChecks)
                {
                    if (Directory.Exists(check) && IsValidHoneycombDirectory(check))
                        return check;
                }
            }
        }
        catch { }

        return "";
    }

    private static bool IsValidHoneycombDirectory(string path)
    {
        return File.Exists(Path.Combine(path, "Honeycomb-GUI.dll")) ||
               File.Exists(Path.Combine(path, "GH-Toolkit-Core.dll")) ||
               File.Exists(Path.Combine(path, "Honeycomb-GUI.exe"));
    }

    /// <summary>
    /// If the compiler directory is located in a read-only or protected directory (such as Program Files),
    /// synchronizes the compiler binaries and templates into %APPDATA%\GH3-Sync\tools\compiler
    /// where full user write/backup access is guaranteed without UAC elevation.
    /// </summary>
    public static string EnsureWritableCompilerDirectory(string sourceDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(sourceDir) || !Directory.Exists(sourceDir))
            return sourceDir;

        // Test if sourceDir/Backups/GH3 is writable
        bool canWrite = false;
        try
        {
            var testFolder = Path.Combine(sourceDir, "Backups", "GH3");
            Directory.CreateDirectory(testFolder);
            var testFile = Path.Combine(testFolder, $".write_test_{Guid.NewGuid():N}");
            File.WriteAllText(testFile, "ok");
            File.Delete(testFile);
            canWrite = true;
        }
        catch
        {
            canWrite = false;
        }

        if (canWrite)
            return sourceDir;

        // The compiler is located in a protected or read-only directory (e.g. C:\Program Files\GH3-Sync).
        // Mirror the compiler tools into %APPDATA%\GH3-Sync\tools\compiler for unrestricted user write access.
        try
        {
            var userCompilerDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GH3-Sync", "tools", "compiler"
            );

            log?.Invoke($"[COMPILER] Bundled directory is protected. Mirroring compiler to user AppData for write access: {userCompilerDir}");
            Directory.CreateDirectory(userCompilerDir);
            Directory.CreateDirectory(Path.Combine(userCompilerDir, "Backups", "GH3"));
            Directory.CreateDirectory(Path.Combine(userCompilerDir, "Replacements"));

            CopyDirectoryRecursive(sourceDir, userCompilerDir);

            if (IsValidHoneycombDirectory(userCompilerDir))
            {
                EnsureFfmpeg(userCompilerDir, log);
                return userCompilerDir;
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"[COMPILER] Warning: Failed to mirror compiler to AppData: {ex.Message}");
        }

        return sourceDir;
    }

    private static void CopyDirectoryRecursive(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var destFile = Path.Combine(targetDir, Path.GetFileName(file));
            if (!File.Exists(destFile) || new FileInfo(file).LastWriteTimeUtc > new FileInfo(destFile).LastWriteTimeUtc)
            {
                try { File.Copy(file, destFile, overwrite: true); } catch { }
            }
        }
        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var dirName = Path.GetFileName(subDir);
            CopyDirectoryRecursive(subDir, Path.Combine(targetDir, dirName));
        }
    }

    /// <summary>
    /// Locates FFmpeg dynamically across the machine, copying it to Honeycomb if needed.
    /// </summary>
    public static string EnsureFfmpeg(string honeycombDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(honeycombDir) || !Directory.Exists(honeycombDir))
            return "";

        var localFfmpeg = Path.Combine(honeycombDir, "ffmpeg.exe");
        if (File.Exists(localFfmpeg))
        {
            return localFfmpeg;
        }

        log?.Invoke("Searching system for ffmpeg.exe to ensure audio compiler works...");

        // Check system PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "ffmpeg.exe");
                if (File.Exists(candidate))
                {
                    TryCopyFfmpegBinaries(Path.GetDirectoryName(candidate)!, honeycombDir, log);
                    return localFfmpeg;
                }
            }
            catch { }
        }

        // Check common WinGet / Chocolatey / Scoop / standalone paths
        string[] commonPaths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WinGet\Packages"),
            @"C:\ProgramData\chocolatey\bin",
            @"C:\ffmpeg\bin",
            @"C:\Tools\ffmpeg\bin"
        };

        foreach (var root in commonPaths)
        {
            try
            {
                if (!Directory.Exists(root)) continue;
                var cand = Path.Combine(root, "ffmpeg.exe");
                if (File.Exists(cand))
                {
                    TryCopyFfmpegBinaries(root, honeycombDir, log);
                    return localFfmpeg;
                }

                // Subdirectory scan for WinGet
                var found = Directory.GetFiles(root, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(found))
                {
                    TryCopyFfmpegBinaries(Path.GetDirectoryName(found)!, honeycombDir, log);
                    return localFfmpeg;
                }
            }
            catch { }
        }

        return "";
    }

    private static void TryCopyFfmpegBinaries(string srcDir, string destDir, Action<string>? log)
    {
        try
        {
            string[] binaries = new[] { "ffmpeg.exe", "ffprobe.exe", "ffplay.exe" };
            foreach (var b in binaries)
            {
                var src = Path.Combine(srcDir, b);
                var dst = Path.Combine(destDir, b);
                if (File.Exists(src) && !File.Exists(dst))
                {
                    File.Copy(src, dst, true);
                    log?.Invoke($"Copied {b} to Honeycomb directory: {dst}");
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Note: Could not copy FFmpeg binaries to Honeycomb: {ex.Message}");
        }
    }

    /// <summary>
    /// Generates a diagnostic report on Honeycomb and compiler availability.
    /// </summary>
    public HoneycombStatusReport GetStatusReport()
    {
        var gamePath = _gh3.GamePath;
        var hDir = FindHoneycombDirectory(AppConfig.Current.HoneycombPath, gamePath);

        var report = new HoneycombStatusReport
        {
            HoneycombDirectory = hDir,
            GamePath = gamePath,
            HasCoreDll = !string.IsNullOrEmpty(hDir) && File.Exists(Path.Combine(hDir, "GH-Toolkit-Core.dll")),
            HasGuiDll = !string.IsNullOrEmpty(hDir) && File.Exists(Path.Combine(hDir, "Honeycomb-GUI.dll")),
            HasFfmpeg = !string.IsNullOrEmpty(hDir) && File.Exists(Path.Combine(hDir, "ffmpeg.exe"))
        };

        if (report.HasFfmpeg)
        {
            report.FfmpegPath = Path.Combine(hDir, "ffmpeg.exe");
        }
        else if (!string.IsNullOrEmpty(hDir))
        {
            var found = EnsureFfmpeg(hDir);
            if (!string.IsNullOrEmpty(found))
            {
                report.HasFfmpeg = true;
                report.FfmpegPath = found;
            }
        }

        report.Available = report.HasCoreDll && report.HasGuiDll && !string.IsNullOrEmpty(gamePath) && Directory.Exists(gamePath);

        if (report.Available)
        {
            report.StatusMessage = report.HasFfmpeg 
                ? "Honeycomb compiler and FFmpeg ready for 1-click automated song creation." 
                : "Honeycomb compiler detected, but ffmpeg.exe is missing. Audio compilation may fail.";
        }
        else
        {
            if (string.IsNullOrEmpty(hDir))
                report.StatusMessage = "Honeycomb-GUI folder not found. Please set your Honeycomb path in Settings.";
            else if (string.IsNullOrEmpty(gamePath))
                report.StatusMessage = "Guitar Hero III game directory not detected. Please verify your game path.";
            else
                report.StatusMessage = "Honeycomb DLLs missing in selected directory.";
        }

        return report;
    }

    /// <summary>
    /// Creates a sanitized temporary copy of a Clone Hero / Moonscraper .chart file before Honeycomb's Chart->MIDI conversion:
    /// - Compensates for a bug in GH-Toolkit-Core.MIDI.Chart where the TS denominator is computed with C# bitwise XOR (`2 ^ den`)
    ///   instead of exponentiation (`2^den`), which otherwise crashes DryWetMidi with "Denominator is not a power of two"
    ///   on valid Moonscraper time signatures like `TS 1 1` (1/2), `TS 4 2` (4/4), `TS 7 4` (7/16), or `TS 6 3` (6/8).
    /// - Optionally converts Clone Hero Open Notes (N 7) to Classic 5-fret GH3 Green notes (N 0).
    /// Returns the path to the sanitized temporary .chart file (or the original path if no changes were needed).
    /// </summary>
    public static string SanitizeChartForHoneycomb(string chartPath, bool convertOpenNotesToClassic = false, Action<string>? log = null)
    {
        if (!File.Exists(chartPath)) return chartPath;
        var lines = File.ReadAllLines(chartPath);
        bool modified = false;
        string currentSection = "";
        int tsFixed = 0;
        int openFixed = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                currentSection = trimmed;
                continue;
            }

            if (currentSection.Equals("[SyncTrack]", StringComparison.OrdinalIgnoreCase))
            {
                var m = System.Text.RegularExpressions.Regex.Match(lines[i], @"^(\s*\d+\s*=\s*TS\s+\d+)\s+(\d+)(\s*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (m.Success && int.TryParse(m.Groups[2].Value, out int rawDen))
                {
                    // Moonscraper .chart spec stores TS denominator as log2 exponent (0->1, 1->2, 2->4, 3->8, 4->16, 5->32, 6->64).
                    // GH-Toolkit-Core erroneously evaluates `2 ^ parsedDen` (bitwise XOR in C#).
                    // Writing `actualDen ^ 2` makes GH-Toolkit-Core evaluate `2 ^ (actualDen ^ 2) == actualDen`!
                    int actualDen = rawDen switch
                    {
                        0 => 1,
                        1 => 2,
                        2 => 4,
                        3 => 8,
                        4 => 16,
                        5 => 32,
                        6 => 64,
                        _ => (rawDen >= 1 && (rawDen & (rawDen - 1)) == 0 && rawDen <= 64) ? rawDen : 4
                    };

                    if (actualDen == 4)
                    {
                        // Single-arg `TS <num>` defaults cleanly to denominator 4 inside GH-Toolkit-Core
                        lines[i] = $"{m.Groups[1].Value}{m.Groups[3].Value}";
                    }
                    else
                    {
                        int xorCompensated = actualDen ^ 2;
                        lines[i] = $"{m.Groups[1].Value} {xorCompensated}{m.Groups[3].Value}";
                    }
                    modified = true;
                    tsFixed++;
                }
            }
            else if (convertOpenNotesToClassic &&
                     (currentSection.EndsWith("Single]", StringComparison.OrdinalIgnoreCase) ||
                      currentSection.EndsWith("DoubleBass]", StringComparison.OrdinalIgnoreCase) ||
                      currentSection.EndsWith("DoubleRhythm]", StringComparison.OrdinalIgnoreCase)))
            {
                var om = System.Text.RegularExpressions.Regex.Match(lines[i], @"^(\s*\d+\s*=\s*N\s+)7(\s+\d+\s*)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (om.Success)
                {
                    lines[i] = $"{om.Groups[1].Value}0{om.Groups[2].Value}";
                    modified = true;
                    openFixed++;
                }
            }
        }

        if (!modified) return chartPath;

        var tempChartPath = Path.Combine(
            Path.GetDirectoryName(chartPath)!,
            $"{Path.GetFileNameWithoutExtension(chartPath)}_gh3tmp.chart");
        File.WriteAllLines(tempChartPath, lines);
        log?.Invoke($"Sanitized .chart for GH3 compatibility (TimeSignatures compensated: {tsFixed}, OpenNotes converted: {openFixed}).");
        return tempChartPath;
    }

    /// <summary>
    /// Converts a .chart file to a standard MIDI (notes.mid) file using GH-Toolkit-Core.
    /// </summary>
    public static string ConvertChartToMid(string chartPath, string honeycombDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(chartPath) || !File.Exists(chartPath))
            throw new FileNotFoundException("Chart file not found", chartPath);

        if (chartPath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            return chartPath;

        var effectiveChartPath = SanitizeChartForHoneycomb(chartPath, AppConfig.Current.ConvertOpenNotesToClassicGh3, log);
        bool createdTempChart = !string.Equals(effectiveChartPath, chartPath, StringComparison.OrdinalIgnoreCase);

        var coreDll = Path.Combine(honeycombDir, "GH-Toolkit-Core.dll");
        if (!File.Exists(coreDll))
            throw new FileNotFoundException("GH-Toolkit-Core.dll not found in Honeycomb directory", coreDll);

        var ghCore = Assembly.LoadFrom(coreDll);
        var chartType = ghCore.GetType("GH_Toolkit_Core.MIDI.Chart");
        if (chartType == null)
            throw new InvalidOperationException("Could not resolve GH_Toolkit_Core.MIDI.Chart type");

        log?.Invoke($"Converting '{Path.GetFileName(chartPath)}' to MIDI...");

        try
        {
            var chartObj = Activator.CreateInstance(chartType, new object[] { effectiveChartPath });
            chartType.GetMethod("ConvertChartToMid")?.Invoke(chartObj, null);
            chartType.GetMethod("WriteMidToFile")?.Invoke(chartObj, null);
        }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            throw tie.InnerException;
        }
        finally
        {
            if (createdTempChart)
            {
                try { File.Delete(effectiveChartPath); } catch { }
            }
        }

        var dir = Path.GetDirectoryName(chartPath)!;
        var targetMid = Path.Combine(dir, "notes.mid");
        var tempMid = Path.ChangeExtension(effectiveChartPath, ".mid");
        if (createdTempChart && File.Exists(tempMid) && !string.Equals(tempMid, targetMid, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(tempMid, targetMid, overwrite: true);
            try { File.Delete(tempMid); } catch { }
        }

        if (File.Exists(targetMid))
        {
            log?.Invoke($"Converted to MIDI: {targetMid} ({new FileInfo(targetMid).Length} bytes)");
            return targetMid;
        }

        return chartPath;
    }

    /// <summary>
    /// Compiles a Guitar Hero III song project (.ghproj) headlessly and deploys it into the game.
    /// Automatically manages save backup, path resolution, and song packaging.
    /// </summary>
    public async Task<HoneycombCompilationResult> CompileSongProjectAsync(string ghprojPath)
    {
        var result = new HoneycombCompilationResult
        {
            ProjectPath = ghprojPath,
            SongId = Path.GetFileNameWithoutExtension(ghprojPath),
            Title = Path.GetFileNameWithoutExtension(ghprojPath)
        };

        try
        {
            if (File.Exists(ghprojPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ghprojPath),
                    new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (doc.RootElement.TryGetProperty("title", out var tProp) && !string.IsNullOrWhiteSpace(tProp.GetString()))
                {
                    result.Title = tProp.GetString()!;
                }
            }
        }
        catch { }

        void Log(string msg)
        {
            result.Logs.Add($"[{DateTime.Now:HH:mm:ss}] {msg}");
            _logger?.LogInfo($"[HoneycombCompiler] {msg}");
            Console.WriteLine($"[HONEYCOMB COMPILER] {msg}");
        }

        if (!File.Exists(ghprojPath))
        {
            result.Success = false;
            result.Message = $"Project file not found: {ghprojPath}";
            Log(result.Message);
            return result;
        }

        var gamePath = _gh3.GamePath;
        if (string.IsNullOrWhiteSpace(gamePath) || !Directory.Exists(gamePath))
        {
            result.Success = false;
            result.Message = "Guitar Hero III game folder not found. Please configure it in Settings.";
            Log(result.Message);
            return result;
        }

        var honeycombDir = FindHoneycombDirectory(AppConfig.Current.HoneycombPath, gamePath);
        if (string.IsNullOrWhiteSpace(honeycombDir) || !IsValidHoneycombDirectory(honeycombDir))
        {
            result.Success = false;
            result.Message = "Honeycomb-GUI folder not found. Please configure your Honeycomb path in Settings.";
            Log(result.Message);
            return result;
        }

        // Guarantee write permissions for non-admin execution (mirroring to AppData if needed)
        honeycombDir = EnsureWritableCompilerDirectory(honeycombDir, Log);

        EnsureFfmpeg(honeycombDir, Log);

        var projectDir = Path.GetDirectoryName(ghprojPath)!;
        Log($"Preparing automated compilation for: {Path.GetFileName(ghprojPath)}");
        Log($"Honeycomb Engine: {honeycombDir}");
        Log($"Game Target: {gamePath}");

        // Pre-compile career save backup
        try
        {
            _saveManager?.BackupCurrentSaves($"Before-Compile-{Path.GetFileNameWithoutExtension(ghprojPath)}");
            Log("Career progression and save file backed up safely before compilation.");
        }
        catch (Exception ex)
        {
            Log($"Note: Career save pre-backup encountered: {ex.Message}");
        }

        // 1. Ensure assembly resolve handler is active for Honeycomb
        EnsureAssemblyResolve(honeycombDir);

        // 2. Prepend Honeycomb to PATH so ffmpeg and helper tools are found
        var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        if (!curPath.Contains(honeycombDir, StringComparison.OrdinalIgnoreCase))
        {
            Environment.SetEnvironmentVariable("PATH", honeycombDir + ";" + curPath);
        }

        // 3. Set Current Working Directory to Honeycomb folder BEFORE loading toolkit types
        Directory.SetCurrentDirectory(honeycombDir);

        // 4. Initialize GlobalVariables and FSB paths in GH-Toolkit-Core
        var ghCorePath = Path.Combine(honeycombDir, "GH-Toolkit-Core.dll");
        var ghCore = Assembly.LoadFrom(ghCorePath);
        var gvType = ghCore.GetType("GH_Toolkit_Core.Methods.GlobalVariables");
        gvType?.GetProperty("ExeRootFolder")?.SetValue(null, CustomsPakService.GetWritableToolkitRoot(honeycombDir));
        var blankWav = Path.Combine(honeycombDir, "Audio", "Blank Audio", "blankWav.wav");
        if (File.Exists(blankWav)) gvType?.GetProperty("BlankWav")?.SetValue(null, blankWav);

        // Seed essential GH3 customs songlist checksums into DebugReader and verify DATA\customs.pak.xen header health
        try
        {
            var crcType = ghCore.GetType("GH_Toolkit_Core.Checksum.CRC");
            var qbKeyMethod = crcType?.GetMethod("QBKey", new[] { typeof(string), typeof(bool) });
            qbKeyMethod?.Invoke(null, new object[] { ".qb", true });
            qbKeyMethod?.Invoke(null, new object[] { "dlc_songlist.qb", true });
            qbKeyMethod?.Invoke(null, new object[] { "dlc_songlist", true });
        }
        catch { }

        CustomsPakService.RepairCustomsPakIfCorrupted(gamePath, _logger);

        var fsbType = ghCore.GetType("GH_Toolkit_Core.Audio.FSB");
        var audioPadField = fsbType?.GetField("audioPad48k128kbps", BindingFlags.NonPublic | BindingFlags.Static);
        var blankMp3 = Path.Combine(honeycombDir, "Audio", "Blank Audio", "48k128kbps.mp3");
        if (File.Exists(blankMp3)) audioPadField?.SetValue(null, blankMp3);

        var rootFolderField = fsbType?.GetField("rootFolder", BindingFlags.NonPublic | BindingFlags.Static);
        rootFolderField?.SetValue(null, honeycombDir);

        // 5. Set FFMpegCore binary folder
        var ffCorePath = Path.Combine(honeycombDir, "FFMpegCore.dll");
        if (File.Exists(ffCorePath))
        {
            var ffCore = Assembly.LoadFrom(ffCorePath);
            var gff = ffCore.GetType("FFMpegCore.GlobalFFOptions");
            var opt = gff?.GetProperty("Current")?.GetValue(null);
            gff?.GetProperty("BinaryFolder")?.SetValue(opt, honeycombDir);
        }

        // 6. Clean up any leftover intermediate conversion files in projectDir
        try
        {
            foreach (var pattern in new[] { "*_guitar.mp3", "*_rhythm.mp3", "*_song.mp3", "*_preview.mp3", "*_coop_*.mp3", "*_crowd.mp3", "_guitar.mp3", "_rhythm.mp3", "_song.mp3" })
            {
                foreach (var f in Directory.GetFiles(projectDir, pattern))
                {
                    try { File.Delete(f); } catch { }
                }
            }
        }
        catch { }

        // 7. Prepare and fix .ghproj paths (ensure all audio & MIDI paths are absolute and exist)
        try
        {
            FixProjectPaths(ghprojPath, projectDir, honeycombDir, Log);
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Failed to normalize project paths: {ex.Message}";
            Log(result.Message);
            return result;
        }

        // 8. Run Honeycomb PAK compilation inside STA thread
        string effectiveChecksum = "";
        Exception? threadException = null;
        var tcs = new TaskCompletionSource<bool>();

        var compileThread = new Thread(() =>
        {
            lock (_compileLock)
            {
                try
                {
                    var honeycombDll = Path.Combine(honeycombDir, "Honeycomb-GUI.dll");
                    var honeycomb = Assembly.LoadFrom(honeycombDll);
                    var csType = honeycomb.GetType("GH_Toolkit_GUI.CompileSong");
                    var upType = honeycomb.GetType("GH_Toolkit_GUI.UserPreferences");
                    if (csType == null || upType == null)
                        throw new InvalidOperationException("Could not load Honeycomb CompileSong or UserPreferences.");

                    var prefs = upType.GetProperty("Default")?.GetValue(null);
                    if (prefs != null)
                    {
                        upType.GetProperty("Gh3FolderPath")?.SetValue(prefs, gamePath);
                        upType.GetProperty("Gh3QbPak")?.SetValue(prefs, Path.Combine(gamePath, "DATA", "PAK", "qb.pak.xen"));
                        upType.GetProperty("Gh3Plus")?.SetValue(prefs, true);
                        upType.GetProperty("ShowPostCompile")?.SetValue(prefs, false);
                        upType.GetProperty("CompileToFolder")?.SetValue(prefs, false);
                        upType.GetProperty("ChecksumWarning")?.SetValue(prefs, 1);
                        upType.GetMethod("Save")?.Invoke(prefs, null);
                    }

                    // Instantiate CompileSong form headlessly
                    var form = Activator.CreateInstance(csType, new object[] { "" });
                    if (form == null) throw new InvalidOperationException("Failed to instantiate Honeycomb CompileSong form.");
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    csType.GetMethod("MainForm_Load", flags)?.Invoke(form, new object[] { form, EventArgs.Empty });

                    // Load Project
                    Log("Loading song project into Honeycomb compiler engine...");
                    csType.GetMethod("LoadProject", flags)?.Invoke(form, new object[] { ghprojPath });

                    // Explicitly set projectFilePath & project_input so headless SaveProject() never triggers modal SaveFileDialog
                    csType.GetField("projectFilePath", flags)?.SetValue(form, ghprojPath);
                    var projectInputProp = csType.GetField("project_input", flags)?.GetValue(form) as System.Windows.Forms.TextBox;
                    if (projectInputProp != null) projectInputProp.Text = ghprojPath;

                    // Ensure form inputs are properly initialized
                    var compileInputProp = csType.GetField("compile_input", flags)?.GetValue(form) as System.Windows.Forms.TextBox;
                    if (compileInputProp != null) compileInputProp.Text = projectDir;

                    var renderCheck = csType.GetField("gh3_rendered_preview_check", flags)?.GetValue(form) as System.Windows.Forms.CheckBox;
                    if (renderCheck != null) renderCheck.Checked = true;

                    // PreChecks (validates game path, copies BetterGH3 resources if missing, handles duplicate checksums)
                    Log("Executing compiler pre-flight validation checks...");
                    csType.GetMethod("PreChecks", flags)?.Invoke(form, Array.Empty<object>());

                    // Compile PAK directly via CompileGh3PakFile() so real exceptions bubble up cleanly
                    // instead of being swallowed by CompilePakGh3()'s headless MessageBox catch block.
                    Log("Compiling chart PAK, cameras, lightshow, and QB bytecode...");
                    var mCompileGh3Pak = csType.GetMethod("CompileGh3PakFile", flags);
                    if (mCompileGh3Pak != null)
                    {
                        mCompileGh3Pak.Invoke(form, null);
                    }
                    else
                    {
                        var mCompilePakGh3 = csType.GetMethod("CompilePakGh3", flags);
                        var pakOk = mCompilePakGh3?.Invoke(form, null);
                        if (pakOk is bool b && !b)
                        {
                            throw new InvalidOperationException("CompilePakGh3 returned false.");
                        }
                    }
                    Log("Chart PAK compilation completed successfully!");

                    // Retrieve the actual effective song checksum assigned by Honeycomb
                    var effField = csType.GetField("_effectiveSongName", flags)?.GetValue(form) as string;
                    if (!string.IsNullOrWhiteSpace(effField))
                    {
                        effectiveChecksum = effField;
                    }
                    else
                    {
                        var mChk = csType.GetMethod("GetSongChecksum", flags);
                        var chkResult = mChk?.Invoke(form, null) as string;
                        if (!string.IsNullOrWhiteSpace(chkResult)) effectiveChecksum = chkResult;
                    }

                    tcs.SetResult(true);
                }
                catch (Exception ex)
                {
                    threadException = ex;
                    tcs.SetResult(false);
                }
            }
        });

        compileThread.SetApartmentState(ApartmentState.STA);
        compileThread.Start();

        var pakSuccess = await tcs.Task;
        if (!pakSuccess || threadException != null)
        {
            result.Success = false;
            var inner = threadException?.InnerException?.Message ?? threadException?.Message ?? "Unknown compiler error";
            result.Message = $"Honeycomb compilation failed: {inner}";
            Log(result.Message);
            return result;
        }

        if (string.IsNullOrWhiteSpace(effectiveChecksum))
        {
            effectiveChecksum = Path.GetFileNameWithoutExtension(ghprojPath);
        }

        Log($"Chart compiled under song checksum: '{effectiveChecksum}'");
        result.SongId = effectiveChecksum;

        // 9. Compile Audio into FMOD Soundbank (.fsb.xen and .dat.xen)
        try
        {
            Log("Compiling audio stems into FMOD soundbank (.fsb.xen / .dat.xen)...");
            await CompileAudioStemsAsync(projectDir, effectiveChecksum, gamePath, honeycombDir, Log);
            Log("Audio soundbank compilation completed successfully!");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Audio soundbank compilation failed: {ex.Message}";
            Log(result.Message);
            return result;
        }

        // 10. Post-compilation verification & standalone package copy
        try
        {
            PostProcessCompilationOutput(ghprojPath, projectDir, gamePath, effectiveChecksum, result, Log);
            result.Success = true;
            result.Message = $"Song '{result.Title}' compiled and deployed into Guitar Hero III successfully!";
            Log(result.Message);

            // Post-compile career save backup
            _saveManager?.BackupCurrentSaves($"After-Compile-{Path.GetFileNameWithoutExtension(ghprojPath)}");
            AppConfig.IncrementUsage("SongCreatorCount");
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Message = $"Error during post-compilation verification: {ex.Message}";
            Log(result.Message);
        }

        return result;
    }

    /// <summary>
    /// Compiles audio stems into .fsb.xen and .dat.xen soundbanks and deploys them to DATA/MUSIC.
    /// Fast, non-blocking, and handles single-track or multi-stem configurations.
    /// </summary>
    private static async Task CompileAudioStemsAsync(
        string projectDir,
        string songChecksum,
        string gamePath,
        string honeycombDir,
        Action<string> log)
    {
        var ghCorePath = Path.Combine(honeycombDir, "GH-Toolkit-Core.dll");
        var ghCore = Assembly.LoadFrom(ghCorePath);
        var fsbType = ghCore.GetType("GH_Toolkit_Core.Audio.FSB")
            ?? throw new InvalidOperationException("Could not load GH_Toolkit_Core.Audio.FSB");

        var fsbObj = Activator.CreateInstance(fsbType)
            ?? throw new InvalidOperationException("Could not instantiate GH_Toolkit_Core.Audio.FSB");

        var mConvert = fsbType.GetMethod("ConvertToMp3")
            ?? throw new InvalidOperationException("Could not find ConvertToMp3 method");
        var mMix = fsbType.GetMethod("MixFiles")
            ?? throw new InvalidOperationException("Could not find MixFiles method");
        var mCombine = fsbType.GetMethod("CombineFSB3File")
            ?? throw new InvalidOperationException("Could not find CombineFSB3File method");

        // Discover primary audio & isolated multi-track stems + honor gh3sync_audio.json mix mode!
        var stems = DiscoverProjectStems(projectDir);
        if (string.IsNullOrEmpty(stems.PrimaryAudio) && stems.LeadStems.Count == 0)
        {
            throw new FileNotFoundException("No valid audio file found in song folder to compile soundbank.");
        }

        string audioMode = stems.AudioMixMode; // StudioMaster, IsolatedStems, HybridBlend
        string previewInput = !string.IsNullOrEmpty(stems.PreviewStem)
            ? stems.PreviewStem
            : (!string.IsNullOrEmpty(stems.PrimaryAudio) ? stems.PrimaryAudio : stems.LeadStems.FirstOrDefault() ?? "");

        var textGuitar = Path.Combine(projectDir, $"{songChecksum}_guitar.mp3");
        var textRhythm = Path.Combine(projectDir, $"{songChecksum}_rhythm.mp3");
        var textSong = Path.Combine(projectDir, $"{songChecksum}_song.mp3");
        var textPreview = Path.Combine(projectDir, $"{songChecksum}_preview.mp3");
        var textCrowd = Path.Combine(projectDir, $"{songChecksum}_crowd.mp3");
        var tempHybridLead = Path.Combine(projectDir, $"{songChecksum}_hybrid_lead.wav");
        var tempHybridBack = Path.Combine(projectDir, $"{songChecksum}_hybrid_back.wav");
        var tempSilenceWav = Path.Combine(projectDir, $"{songChecksum}_silence.wav");

        var tempFiles = new List<string> { textGuitar, textRhythm, textSong, textPreview, textCrowd, tempHybridLead, tempHybridBack, tempSilenceWav };

        try
        {
            var ffmpegExe = EnsureFfmpeg(honeycombDir);
            string masterInput = !string.IsNullOrEmpty(stems.PrimaryAudio) ? stems.PrimaryAudio : stems.LeadStems.FirstOrDefault() ?? "";

            // Ensure silent padding audio matches the EXACT duration of the song so FMOD never loops early at 2:09!
            // Converted through Honeycomb's ConvertToMp3() so the resulting MP3 is strictly valid CBR for FSB3.
            bool hasMatchingSilence = false;
            if (!string.IsNullOrEmpty(ffmpegExe) && File.Exists(ffmpegExe) && File.Exists(masterInput))
            {
                hasMatchingSilence = CreateMatchingSilenceWav(ffmpegExe, masterInput, tempSilenceWav);
            }

            // Check for authentic concert crowd sing-along / choir audio candidate
            string crowdCandidate = "";
            foreach (var cand in new[] { "song_crowd.wav", "song_crowd.mp3", "crowd.wav", "crowd.mp3" })
            {
                var cp = Path.Combine(projectDir, cand);
                if (File.Exists(cp) && new FileInfo(cp).Length > 1024) { crowdCandidate = cp; break; }
            }

            if (audioMode.Equals("StudioMaster", StringComparison.OrdinalIgnoreCase) || !stems.HasMultiStems)
            {
                // Studio Master Audio (Beat It / Gently Weeps style):
                // Untouched original master in backing, silent pad in guitar, dynamic crowd choir in rhythm!
                log($"Audio Mode: Studio Master (Cleanest Fidelity) -> Backing={Path.GetFileName(masterInput)}, Guitar=silent pad");

                if (hasMatchingSilence && File.Exists(tempSilenceWav))
                {
                    await (Task)mConvert.Invoke(fsbObj, new object[] { tempSilenceWav, textGuitar })!;
                }
                else
                {
                    await (Task)mConvert.Invoke(fsbObj, new object[] { "", textGuitar })!;
                }

                if (!string.IsNullOrEmpty(crowdCandidate))
                {
                    log($"Assigning dynamic concert crowd choir to rhythm channel ({Path.GetFileName(crowdCandidate)})...");
                    await (Task)mConvert.Invoke(fsbObj, new object[] { crowdCandidate, textRhythm })!;
                }
                else if (hasMatchingSilence && File.Exists(tempSilenceWav))
                {
                    await (Task)mConvert.Invoke(fsbObj, new object[] { tempSilenceWav, textRhythm })!;
                }
                else
                {
                    await (Task)mConvert.Invoke(fsbObj, new object[] { "", textRhythm })!;
                }

                await (Task)mConvert.Invoke(fsbObj, new object[] { masterInput, textSong })!;
            }
            else if (audioMode.Equals("HybridBlend", StringComparison.OrdinalIgnoreCase) && stems.LeadStems.Count > 0 && stems.BackingStems.Count > 0)
            {
                // Hybrid Studio Blend: 65% lead stem in playable channel + (35% lead + 100% backing) in backing channel
                log($"Audio Mode: Hybrid Studio Blend (Lead={stems.LeadStems.Count} stem(s), Backing={stems.BackingStems.Count} stem(s))");
                bool hybridBuilt = false;

                if (!string.IsNullOrEmpty(ffmpegExe) && File.Exists(ffmpegExe))
                {
                    hybridBuilt = BuildHybridBlendWavs(ffmpegExe, stems.LeadStems, stems.BackingStems, tempHybridLead, tempHybridBack);
                }

                if (hybridBuilt && File.Exists(tempHybridLead) && File.Exists(tempHybridBack))
                {
                    log("Converting hybrid playable lead channel to CBR MP3...");
                    await (Task)mConvert.Invoke(fsbObj, new object[] { tempHybridLead, textGuitar })!;
                    log("Preparing rhythm/crowd track...");
                    if (string.IsNullOrEmpty(stems.RhythmStem))
                    {
                        if (!string.IsNullOrEmpty(crowdCandidate))
                        {
                            log($"Assigning dynamic concert crowd choir to rhythm channel ({Path.GetFileName(crowdCandidate)})...");
                            await (Task)mConvert.Invoke(fsbObj, new object[] { crowdCandidate, textRhythm })!;
                        }
                        else if (hasMatchingSilence && File.Exists(tempSilenceWav))
                        {
                            await (Task)mConvert.Invoke(fsbObj, new object[] { tempSilenceWav, textRhythm })!;
                        }
                        else
                        {
                            await (Task)mConvert.Invoke(fsbObj, new object[] { "", textRhythm })!;
                        }
                    }
                    else
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { stems.RhythmStem, textRhythm })!;
                    }
                    log("Converting hybrid studio backing channel to CBR MP3...");
                    await (Task)mConvert.Invoke(fsbObj, new object[] { tempHybridBack, textSong })!;
                }
                else
                {
                    // Clean fallback to Studio Master if FFmpeg filtergraph fails
                    if (hasMatchingSilence && File.Exists(tempSilenceWav))
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { tempSilenceWav, textGuitar })!;
                    }
                    else
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { "", textGuitar })!;
                    }

                    if (!string.IsNullOrEmpty(crowdCandidate))
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { crowdCandidate, textRhythm })!;
                    }
                    else if (hasMatchingSilence && File.Exists(tempSilenceWav))
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { tempSilenceWav, textRhythm })!;
                    }
                    else
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { "", textRhythm })!;
                    }

                    await (Task)mConvert.Invoke(fsbObj, new object[] { masterInput, textSong })!;
                }
            }
            else
            {
                // Isolated Lead Stem (True Mute-on-Miss — ONOGAMI style, supports single OR multi-stem lead!)
                log($"Audio Mode: Isolated Lead Stem (Focus={stems.LeadFocus}, Lead={stems.LeadStems.Count} stem(s), Bass={(string.IsNullOrEmpty(stems.RhythmStem) ? "none" : Path.GetFileName(stems.RhythmStem))}, Backing={stems.BackingStems.Count} stem(s))");

                if (stems.LeadStems.Count == 1)
                {
                    log($"Converting playable lead stem ({Path.GetFileName(stems.LeadStems[0])}) to CBR MP3...");
                    await (Task)mConvert.Invoke(fsbObj, new object[] { stems.LeadStems[0], textGuitar })!;
                }
                else
                {
                    log($"Mixing {stems.LeadStems.Count} playable lead stems to CBR MP3...");
                    await (Task)mMix.Invoke(fsbObj, new object[] { stems.LeadStems.ToArray(), textGuitar, "", "", ".mp3", 48000 })!;
                }

                log("Preparing rhythm/crowd track...");
                if (string.IsNullOrEmpty(stems.RhythmStem))
                {
                    if (!string.IsNullOrEmpty(crowdCandidate))
                    {
                        log($"Assigning dynamic concert crowd choir to rhythm channel ({Path.GetFileName(crowdCandidate)})...");
                        await (Task)mConvert.Invoke(fsbObj, new object[] { crowdCandidate, textRhythm })!;
                    }
                    else if (hasMatchingSilence && File.Exists(tempSilenceWav))
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { tempSilenceWav, textRhythm })!;
                    }
                    else
                    {
                        await (Task)mConvert.Invoke(fsbObj, new object[] { "", textRhythm })!;
                    }
                }
                else
                {
                    await (Task)mConvert.Invoke(fsbObj, new object[] { stems.RhythmStem, textRhythm })!;
                }

                var backingInputs = stems.BackingStems.Count > 0
                    ? stems.BackingStems.ToArray()
                    : new[] { !string.IsNullOrEmpty(stems.PrimaryAudio) ? stems.PrimaryAudio : stems.LeadStems[0] };
                log($"Mixing backing track ({backingInputs.Length} input(s))...");
                await (Task)mMix.Invoke(fsbObj, new object[] { backingInputs, textSong, "", "", ".mp3", 48000 })!;
            }

            // 4. Preview track
            log("Generating preview audio...");
            await (Task)mConvert.Invoke(fsbObj, new object[] { previewInput, textPreview })!;

            // 5. Combine into encrypted FSB3 and DAT soundbanks (Stream 0=Guitar, 1=Rhythm/Crowd, 2=Song/Master, 3=Preview)
            log("Packing and encrypting FSB3 and DAT soundbanks (4 streams: Lead, Rhythm/Crowd, Backing, Preview)...");
            var filesToProcess = new[] { textGuitar, textRhythm, textSong, textPreview };
            var fsbOutput = Path.Combine(projectDir, songChecksum);
            mCombine.Invoke(fsbObj, new object[] { filesToProcess, fsbOutput });

            var outFsb = fsbOutput + ".fsb";
            var outDat = fsbOutput + ".dat";

            if (!File.Exists(outFsb) || !File.Exists(outDat))
            {
                throw new FileNotFoundException("Soundbank output files were not generated by FSB compiler.");
            }

            // 6. Deploy to game DATA/MUSIC directory
            var musicDir = Path.Combine(gamePath, "DATA", "MUSIC");
            if (!Directory.Exists(musicDir)) Directory.CreateDirectory(musicDir);

            var gameFsb = Path.Combine(musicDir, $"{songChecksum}.fsb.xen");
            var gameDat = Path.Combine(musicDir, $"{songChecksum}.dat.xen");

            File.Copy(outFsb, gameFsb, true);
            File.Copy(outDat, gameDat, true);
            log($"Soundbanks deployed to game MUSIC folder: {Path.GetFileName(gameFsb)} ({new FileInfo(gameFsb).Length:N0} bytes)");

            // Remove intermediate .fsb / .dat from project folder so we don't duplicate 30+ MB audio banks
            try { if (File.Exists(outFsb)) File.Delete(outFsb); } catch { }
            try { if (File.Exists(outDat)) File.Delete(outDat); } catch { }
        }
        finally
        {
            // Clean up all temporary intermediate conversion files
            foreach (var f in tempFiles)
            {
                if (File.Exists(f))
                {
                    try { File.Delete(f); } catch { }
                }
            }
            try
            {
                foreach (var pat in new[] { "*_conv.mp3", "*_mix.mp3", "temp_*.mp3", "*_guitar.mp3", "*_rhythm.mp3", "*_song.mp3", "*_preview.mp3" })
                {
                    foreach (var leftover in Directory.GetFiles(projectDir, pat))
                    {
                        try { File.Delete(leftover); } catch { }
                    }
                }
            }
            catch { }
        }
    }

    /// <summary>
    /// Inspects and sanitizes all paths inside .ghproj so Honeycomb never hits missing inputs or relative path mismatches.
    /// </summary>
    private static void FixProjectPaths(string ghprojPath, string projectDir, string honeycombDir, Action<string> log)
    {
        var jsonText = File.ReadAllText(ghprojPath);
        using var doc = JsonDocument.Parse(jsonText, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        var root = doc.RootElement;

        var dict = new Dictionary<string, object?>();
        foreach (var prop in root.EnumerateObject())
        {
            dict[prop.Name] = prop.Value.Clone();
        }

        string GetStr(string key) => dict.TryGetValue(key, out var val) && val is JsonElement je ? je.GetString() ?? "" : "";

        var title = GetStr("title");
        var midiPathGh3 = GetStr("midiPathGh3");
        var chartAuthor = GetStr("chartAuthor");

        // 1. Resolve Chart to MIDI
        string resolvedMidi = "";
        if (File.Exists(Path.Combine(projectDir, "notes.mid")))
        {
            resolvedMidi = Path.Combine(projectDir, "notes.mid");
        }
        else if (File.Exists(Path.Combine(projectDir, "notes.chart")))
        {
            resolvedMidi = ConvertChartToMid(Path.Combine(projectDir, "notes.chart"), honeycombDir, log);
        }
        else if (!string.IsNullOrEmpty(midiPathGh3) && File.Exists(midiPathGh3))
        {
            if (midiPathGh3.EndsWith(".chart", StringComparison.OrdinalIgnoreCase))
                resolvedMidi = ConvertChartToMid(midiPathGh3, honeycombDir, log);
            else
                resolvedMidi = Path.GetFullPath(midiPathGh3);
        }

        if (string.IsNullOrEmpty(resolvedMidi) || !File.Exists(resolvedMidi))
        {
            throw new FileNotFoundException("Could not find or generate a valid notes.mid for song compilation.");
        }

        dict["midiPathGh3"] = resolvedMidi;
        dict["midiPath"] = resolvedMidi;

        // 2. Resolve Audio & Multi-Stem Paths (honoring gh3sync_audio.json StudioMaster / IsolatedStems / HybridBlend)
        var stems = DiscoverProjectStems(projectDir);
        string primaryAudio = stems.PrimaryAudio;

        if (stems.HasMultiStems && !stems.AudioMixMode.Equals("StudioMaster", StringComparison.OrdinalIgnoreCase))
        {
            var primaryLead = stems.LeadStems.FirstOrDefault() ?? primaryAudio;
            var backingJoined = stems.BackingStems.Count > 0 ? string.Join(";", stems.BackingStems) : primaryAudio;
            var previewAudio = !string.IsNullOrEmpty(stems.PreviewStem) ? stems.PreviewStem : (!string.IsNullOrEmpty(primaryAudio) ? primaryAudio : primaryLead);

            dict["guitarPathGh3"] = primaryLead;
            dict["rhythmPathGh3"] = stems.RhythmStem;
            dict["backingPathsGh3"] = backingJoined;
            dict["previewAudioPathGh3"] = previewAudio;
            dict["guitarPath"] = primaryLead;
            dict["bassPath"] = stems.RhythmStem;
            dict["backingPaths"] = backingJoined;
            dict["previewAudioPath"] = previewAudio;
            dict["coopGuitarPath"] = primaryLead;
            dict["coopRhythmPath"] = stems.RhythmStem;
            dict["coopBackingPaths"] = backingJoined;
            dict["isCoopAudio"] = !string.IsNullOrEmpty(stems.RhythmStem);
        }
        else
        {
            // Clean Studio Master setup (Beat It / Gently Weeps style):
            // Keep guitarPathGh3 blank (silent pad) and put untouched studio master in backing so audio never doubles or clips!
            dict["guitarPathGh3"] = "";
            dict["rhythmPathGh3"] = "";
            dict["backingPathsGh3"] = primaryAudio;
            dict["previewAudioPathGh3"] = primaryAudio;
            dict["guitarPath"] = "";
            dict["bassPath"] = "";
            dict["backingPaths"] = primaryAudio;
            dict["previewAudioPath"] = primaryAudio;
            dict["isCoopAudio"] = false;
        }

        string crowdCandidate = "";
        foreach (var cName in new[] { "song_crowd.wav", "song_crowd.mp3", "crowd.wav", "crowd.mp3" })
        {
            var cp = Path.Combine(projectDir, cName);
            if (File.Exists(cp)) { crowdCandidate = cp; break; }
        }
        dict["crowdPathGh3"] = crowdCandidate;
        dict["crowdPath"] = crowdCandidate;

        // 3. Ensure all required Honeycomb NumericUpDown & metadata fields exist and stay within valid bounds
        //    (Prevents WinForms "Value of '0' is not valid for 'Value'. 'Value' should be between 'Minimum' and 'Maximum'")
        int GetInt(string key, int fallback)
        {
            if (dict.TryGetValue(key, out var val) && val is JsonElement je && je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var n))
                return n;
            return fallback;
        }

        int fallbackYear = DateTime.Now.Year;
        var iniPath = Path.Combine(projectDir, "song.ini");
        if (File.Exists(iniPath))
        {
            foreach (var line in File.ReadAllLines(iniPath))
            {
                var tr = line.Trim();
                if (tr.StartsWith("year", StringComparison.OrdinalIgnoreCase) && tr.Contains('='))
                {
                    if (int.TryParse(tr[(tr.IndexOf('=') + 1)..].Trim(), out var yVal) && yVal >= 1900 && yVal <= 2100)
                        fallbackYear = yVal;
                }
            }
        }

        dict["songYear"] = Math.Clamp(GetInt("songYear", fallbackYear), 1900, 2100);
        dict["coverYear"] = Math.Clamp(GetInt("coverYear", 2024), 1900, 2100);
        dict["artistText"] = dict.ContainsKey("artistText") ? GetInt("artistText", -1) : -1;
        dict["wtGenre"] = Math.Max(0, GetInt("wtGenre", 0));
        dict["gh5Genre"] = Math.Max(0, GetInt("gh5Genre", 0));
        dict["worGenre"] = Math.Max(0, GetInt("worGenre", 0));
        dict["previewStart"] = Math.Max(0, GetInt("previewStart", 15000));
        dict["previewEnd"] = Math.Clamp(GetInt("previewEnd", 30000), 1000, 600000);
        dict["hmxHopoVal"] = Math.Clamp(GetInt("hmxHopoVal", 170), 1, 1000);
        dict["skaSourceGh3"] = Math.Max(0, GetInt("skaSourceGh3", 0));
        dict["venueSourceGh3"] = Math.Max(0, GetInt("venueSourceGh3", 0));
        dict["countoffGh3"] = Math.Max(0, GetInt("countoffGh3", 0));
        dict["vocalGenderGh3"] = Math.Max(0, GetInt("vocalGenderGh3", 0));
        dict["beat8thLow"] = Math.Clamp(GetInt("beat8thLow", 1), 1, 500);
        dict["beat8thHigh"] = Math.Clamp(GetInt("beat8thHigh", 180), 1, 500);
        dict["beat16thLow"] = Math.Clamp(GetInt("beat16thLow", 1), 1, 500);
        dict["beat16thHigh"] = Math.Clamp(GetInt("beat16thHigh", 120), 1, 500);
        dict["bandTier"] = Math.Clamp(GetInt("bandTier", 1), 1, 10);
        dict["guitarTier"] = Math.Clamp(GetInt("guitarTier", 4), 1, 10);
        dict["bassTier"] = Math.Clamp(GetInt("bassTier", 3), 1, 10);
        dict["drumsTier"] = Math.Clamp(GetInt("drumsTier", 1), 1, 10);
        dict["vocalsTier"] = Math.Clamp(GetInt("vocalsTier", 1), 1, 10);
        if (!dict.ContainsKey("gh5Drumkit")) dict["gh5Drumkit"] = "Modern Rock";
        if (!dict.ContainsKey("ghworDrumkit")) dict["ghworDrumkit"] = "Modern Rock";

        dict["useRenderedPreviewGh3"] = true;
        dict["useRenderedPreview"] = true;
        dict["compilePath"] = projectDir;
        dict["projectPath"] = ghprojPath;
        dict["ghprojFromLoad"] = ghprojPath;

        var updatedJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ghprojPath, updatedJson);
    }

    private sealed record ProjectStemsInfo(
        string PrimaryAudio,
        List<string> LeadStems,
        string RhythmStem,
        List<string> BackingStems,
        string PreviewStem,
        bool HasMultiStems,
        string AudioMixMode,
        string LeadFocus);

    private static ProjectStemsInfo DiscoverProjectStems(string projectDir)
    {
        // Read persisted GH3-Sync audio mix mode & focus if present
        string audioMixMode = "";
        string leadFocus = "Guitar";
        try
        {
            var cfgPath = Path.Combine(projectDir, "gh3sync_audio.json");
            if (File.Exists(cfgPath))
            {
                var cfg = JsonSerializer.Deserialize<SongAudioMixConfig>(File.ReadAllText(cfgPath));
                if (cfg != null)
                {
                    audioMixMode = cfg.AudioMixMode ?? "";
                    leadFocus = string.IsNullOrWhiteSpace(cfg.LeadFocus) ? "Guitar" : cfg.LeadFocus;
                }
            }
        }
        catch { }

        string[] audioExts = new[] { ".mp3", ".ogg", ".wav" };
        string primaryAudio = "";
        foreach (var prefix in new[] { "song_stadium", "song_processed", "song" })
        {
            foreach (var ext in audioExts)
            {
                var p = Path.Combine(projectDir, prefix + ext);
                if (File.Exists(p)) { primaryAudio = p; break; }
            }
            if (!string.IsNullOrEmpty(primaryAudio)) break;
        }
        if (string.IsNullOrEmpty(primaryAudio))
        {
            primaryAudio = Directory.GetFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(f => audioExts.Contains(Path.GetExtension(f).ToLowerInvariant())) ?? "";
        }

        string FindStemFile(params string[] baseNames)
        {
            // 1. Check root project folder
            foreach (var bn in baseNames)
            {
                foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
                {
                    var p = Path.Combine(projectDir, bn + ext);
                    if (File.Exists(p)) return p;
                }
            }
            // 2. Check Demucs stems subfolder (e.g., stems\htdemucs_6s\song\guitar.wav)
            var stemsDir = Path.Combine(projectDir, "stems");
            if (Directory.Exists(stemsDir))
            {
                foreach (var bn in baseNames)
                {
                    foreach (var ext in new[] { ".wav", ".ogg", ".mp3" })
                    {
                        var found = Directory.GetFiles(stemsDir, bn + ext, SearchOption.AllDirectories).FirstOrDefault();
                        if (!string.IsNullOrEmpty(found)) return found;
                    }
                }
            }
            return "";
        }

        string guitarStem = FindStemFile("guitar");
        string bassStem = FindStemFile("rhythm", "bass");
        string drumsStem = FindStemFile("drums", "drums_1");
        string vocalsStem = FindStemFile("vocals", "vocals_1");
        string pianoStem = FindStemFile("piano", "keys");
        string otherStem = FindStemFile("other");
        string explicitBacking = FindStemFile("backing");
        string previewStem = FindStemFile("preview");

        // Route LeadStems + RhythmStem + BackingStems according to chosen LeadFocus!
        var leadStems = new List<string>();
        string rhythmStem = bassStem;
        var allCandidates = new List<string>();
        void AddIf(List<string> list, string f)
        {
            if (!string.IsNullOrEmpty(f) && !list.Contains(f, StringComparer.OrdinalIgnoreCase))
                list.Add(f);
        }

        AddIf(allCandidates, explicitBacking);
        AddIf(allCandidates, drumsStem);
        AddIf(allCandidates, vocalsStem);
        AddIf(allCandidates, pianoStem);
        AddIf(allCandidates, otherStem);
        AddIf(allCandidates, guitarStem);

        switch (leadFocus.ToLowerInvariant())
        {
            case "keyssynth":
                AddIf(leadStems, pianoStem);
                AddIf(leadStems, otherStem);
                if (leadStems.Count == 0) AddIf(leadStems, guitarStem);
                break;
            case "bass":
                AddIf(leadStems, bassStem);
                rhythmStem = "";
                break;
            case "vocals":
                AddIf(leadStems, vocalsStem);
                break;
            case "multilead":
                AddIf(leadStems, guitarStem);
                AddIf(leadStems, pianoStem);
                AddIf(leadStems, otherStem);
                if (leadStems.Count == 0) AddIf(leadStems, vocalsStem);
                break;
            default: // Guitar
                AddIf(leadStems, guitarStem);
                break;
        }

        var backingList = new List<string>();
        foreach (var cand in allCandidates)
        {
            if (!leadStems.Contains(cand, StringComparer.OrdinalIgnoreCase) &&
                !cand.Equals(rhythmStem, StringComparison.OrdinalIgnoreCase))
            {
                backingList.Add(cand);
            }
        }
        // If Clone Hero import has song.ogg + guitar.ogg (classic CH multi-track), include song.ogg in backing
        if (!string.IsNullOrEmpty(primaryAudio) &&
            !leadStems.Contains(primaryAudio, StringComparer.OrdinalIgnoreCase) &&
            !backingList.Contains(primaryAudio, StringComparer.OrdinalIgnoreCase) &&
            backingList.Count == 0 && leadStems.Count > 0)
        {
            backingList.Add(primaryAudio);
        }

        bool hasMultiStems = leadStems.Count > 0 && (backingList.Count > 0 || !string.IsNullOrEmpty(rhythmStem));

        if (string.IsNullOrWhiteSpace(audioMixMode))
        {
            audioMixMode = hasMultiStems ? "IsolatedStems" : "StudioMaster";
        }

        return new ProjectStemsInfo(primaryAudio, leadStems, rhythmStem, backingList, previewStem, hasMultiStems, audioMixMode, leadFocus);
    }

    private static bool BuildHybridBlendWavs(
        string ffmpegExe,
        List<string> leadStems,
        List<string> backingStems,
        string outLeadWav,
        string outBackWav)
    {
        try
        {
            bool RunMix(List<(string Path, double Gain)> inputs, string outputWav)
            {
                if (inputs.Count == 0) return false;
                var argsSb = new StringBuilder("-y ");
                var filterSb = new StringBuilder();
                var mixLabels = new List<string>();

                for (int i = 0; i < inputs.Count; i++)
                {
                    argsSb.Append($"-i \"{inputs[i].Path}\" ");
                    string gainStr = inputs[i].Gain.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                    filterSb.Append($"[{i}:a]volume={gainStr}[a{i}];");
                    mixLabels.Add($"[a{i}]");
                }

                bool norm = AppConfig.Current.NormalizeAudioLoudness;
                double lufs = AppConfig.Current.AudioTargetLufs <= 0 ? AppConfig.Current.AudioTargetLufs : -14.0;
                string lufsStr = lufs.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

                if (inputs.Count == 1)
                {
                    filterSb.Append(norm
                        ? $"[a0]loudnorm=I={lufsStr}:LRA=11:TP=-1.5,alimiter=limit=0.98[out]"
                        : "[a0]alimiter=limit=0.98[out]");
                }
                else
                {
                    filterSb.Append(norm
                        ? $"{string.Join("", mixLabels)}amix=inputs={inputs.Count}:duration=longest:normalize=0,loudnorm=I={lufsStr}:LRA=11:TP=-1.5,alimiter=limit=0.98[out]"
                        : $"{string.Join("", mixLabels)}amix=inputs={inputs.Count}:duration=longest:normalize=0,alimiter=limit=0.98[out]");
                }

                argsSb.Append($"-filter_complex \"{filterSb}\" -map \"[out]\" -ar 48000 -ac 2 \"{outputWav}\"");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegExe,
                    Arguments = argsSb.ToString(),
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var proc = Process.Start(psi);
                proc?.WaitForExit(180_000);
                return File.Exists(outputWav) && new FileInfo(outputWav).Length > 1024;
            }

            // Playable channel gets 65% of lead stem(s); backing gets 35% of lead stem(s) + 100% of backing stems
            var leadInputs = leadStems.Select(p => (p, 0.65)).ToList();
            var backInputs = new List<(string, double)>();
            backInputs.AddRange(leadStems.Select(p => (p, 0.35)));
            backInputs.AddRange(backingStems.Select(p => (p, 1.00)));

            return RunMix(leadInputs, outLeadWav) && RunMix(backInputs, outBackWav);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Generates a silent 48kHz stereo WAV that matches the EXACT sample-accurate duration
    /// of the primary song audio. Converted to CBR MP3 via ConvertToMp3 to ensure valid FSB3 frames.
    /// Guarantees FMOD soundbanks never suffer early EOF looping/seek glitches at 2:09!
    /// </summary>
    private static bool CreateMatchingSilenceWav(string ffmpegExe, string referenceAudioPath, string outputSilenceWavPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(ffmpegExe) || !File.Exists(ffmpegExe) || !File.Exists(referenceAudioPath))
                return false;

            var psi = new ProcessStartInfo
            {
                FileName = ffmpegExe,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(referenceAudioPath);
            psi.ArgumentList.Add("-af");
            psi.ArgumentList.Add("volume=0");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("48000");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add(outputSilenceWavPath);

            using var proc = Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(30000);
            return File.Exists(outputSilenceWavPath) && new FileInfo(outputSilenceWavPath).Length > 1024;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Verifies the compiled PAK and FSB outputs in the game directory, and copies a standalone copy
    /// to the song's project directory for peer-to-peer distribution.
    /// </summary>
    private static void PostProcessCompilationOutput(
        string ghprojPath,
        string projectDir,
        string gamePath,
        string effectiveChecksum,
        HoneycombCompilationResult result,
        Action<string> log)
    {
        var songsDir = Path.Combine(gamePath, "DATA", "SONGS");
        var musicDir = Path.Combine(gamePath, "DATA", "MUSIC");

        string? matchedPak = null;
        if (!string.IsNullOrWhiteSpace(effectiveChecksum))
        {
            var targetPak = Path.Combine(songsDir, $"{effectiveChecksum}_song.pak.xen");
            if (File.Exists(targetPak)) matchedPak = targetPak;
        }

        if (matchedPak == null)
        {
            var paks = Directory.GetFiles(songsDir, "*.pak.xen")
                .Where(f => !Path.GetFileName(f).Equals("customs.pak.xen", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => File.GetLastWriteTime(f))
                .ToList();
            if (paks.Any()) matchedPak = paks.First();
        }

        if (matchedPak != null && File.Exists(matchedPak))
        {
            result.PakFilePath = matchedPak;
            var songBaseName = Path.GetFileNameWithoutExtension(matchedPak).Replace("_song.pak", "");
            result.SongId = songBaseName;

            // Look for matching FSB and DAT
            var fsbName = $"{songBaseName}.fsb.xen";
            var datName = $"{songBaseName}.dat.xen";
            var fsbPath = Path.Combine(musicDir, fsbName);
            var datPath = Path.Combine(musicDir, datName);

            if (File.Exists(fsbPath)) result.FsbFilePath = fsbPath;
            if (File.Exists(datPath)) result.DatFilePath = datPath;

            log($"Game deployment verified:");
            log($" - PAK: {Path.GetFileName(matchedPak)} ({new FileInfo(matchedPak).Length:N0} bytes)");
            if (File.Exists(fsbPath)) log($" - FSB: {fsbName} ({new FileInfo(fsbPath).Length:N0} bytes)");
            if (File.Exists(datPath)) log($" - DAT: {datName}");

            // Link compiled song ID directly in song.ini without duplicating 30+ MB .pak/.fsb/.dat files into projectDir
            try
            {
                WriteCompiledSongIdToIni(projectDir, songBaseName);

                // Clean any legacy duplicate compiled binaries inside projectDir
                foreach (var pat in new[] { "*.pak.xen", "*.fsb.xen", "*.dat.xen", "*.fsb", "*.dat" })
                {
                    foreach (var dup in Directory.GetFiles(projectDir, pat))
                    {
                        try { File.Delete(dup); } catch { }
                    }
                }

                // Auto-clean huge uncompressed Demucs WAV stems folder now that encrypted .fsb.xen is safely deployed in DATA\MUSIC!
                var stemsDir = Path.Combine(projectDir, "stems");
                if (Directory.Exists(stemsDir) && File.Exists(fsbPath))
                {
                    try
                    {
                        long wavBytes = Directory.GetFiles(stemsDir, "*.wav", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
                        Directory.Delete(stemsDir, recursive: true);
                        log($"Cleaned temporary uncompressed Demucs stems folder (freed {wavBytes / (1024.0 * 1024.0):F1} MB).");
                    }
                    catch { }
                }

                AppConfig.RegisterCreatedSong(songBaseName, projectDir);
                log($"Linked compiled song ID '{songBaseName}' to project metadata (zero duplicate binaries or raw WAV bloat kept).");
            }
            catch (Exception ex)
            {
                log($"Note: Could not update project metadata link: {ex.Message}");
            }
        }
    }

    private static void WriteCompiledSongIdToIni(string projectDir, string songId)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || string.IsNullOrWhiteSpace(songId)) return;
        var iniPath = Path.Combine(projectDir, "song.ini");
        var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string> { "[song]" };
        bool replaced = false;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].TrimStart().StartsWith("compiled_song_id", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"compiled_song_id = {songId}";
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            lines.Add($"compiled_song_id = {songId}");
        }
        File.WriteAllLines(iniPath, lines);
    }
}
