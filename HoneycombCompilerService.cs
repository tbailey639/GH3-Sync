using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Converts a .chart file to a standard MIDI (notes.mid) file using GH-Toolkit-Core.
    /// </summary>
    public static string ConvertChartToMid(string chartPath, string honeycombDir, Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(chartPath) || !File.Exists(chartPath))
            throw new FileNotFoundException("Chart file not found", chartPath);

        if (chartPath.EndsWith(".mid", StringComparison.OrdinalIgnoreCase))
            return chartPath;

        var coreDll = Path.Combine(honeycombDir, "GH-Toolkit-Core.dll");
        if (!File.Exists(coreDll))
            throw new FileNotFoundException("GH-Toolkit-Core.dll not found in Honeycomb directory", coreDll);

        var ghCore = Assembly.LoadFrom(coreDll);
        var chartType = ghCore.GetType("GH_Toolkit_Core.MIDI.Chart");
        if (chartType == null)
            throw new InvalidOperationException("Could not resolve GH_Toolkit_Core.MIDI.Chart type");

        log?.Invoke($"Converting '{Path.GetFileName(chartPath)}' to MIDI...");

        var chartObj = Activator.CreateInstance(chartType, new object[] { chartPath });
        chartType.GetMethod("ConvertChartToMid")?.Invoke(chartObj, null);
        chartType.GetMethod("WriteMidToFile")?.Invoke(chartObj, null);

        var outMid = Path.Combine(Path.GetDirectoryName(chartPath)!, "notes.mid");
        if (File.Exists(outMid))
        {
            log?.Invoke($"Converted to MIDI: {outMid} ({new FileInfo(outMid).Length} bytes)");
            return outMid;
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
            SongId = Path.GetFileNameWithoutExtension(ghprojPath)
        };

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

        // Prepare and fix .ghproj paths (ensure all audio & MIDI paths are absolute and exist)
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

        // Run Honeycomb compilation inside STA thread
        Exception? threadException = null;
        var tcs = new TaskCompletionSource<bool>();

        var compileThread = new Thread(() =>
        {
            lock (_compileLock)
            {
                try
                {
                    // 1. Prepend Honeycomb to PATH
                    var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
                    if (!curPath.Contains(honeycombDir, StringComparison.OrdinalIgnoreCase))
                    {
                        Environment.SetEnvironmentVariable("PATH", honeycombDir + ";" + curPath);
                    }

                    // 2. Set FFMpegCore binary folder
                    var ffCorePath = Path.Combine(honeycombDir, "FFMpegCore.dll");
                    if (File.Exists(ffCorePath))
                    {
                        var ffCore = Assembly.LoadFrom(ffCorePath);
                        var gff = ffCore.GetType("FFMpegCore.GlobalFFOptions");
                        var opt = gff?.GetProperty("Current")?.GetValue(null);
                        gff?.GetProperty("BinaryFolder")?.SetValue(opt, honeycombDir);
                    }

                    // 3. Set GlobalVariables ExeRootFolder
                    var ghCorePath = Path.Combine(honeycombDir, "GH-Toolkit-Core.dll");
                    var ghCore = Assembly.LoadFrom(ghCorePath);
                    var gvType = ghCore.GetType("GH_Toolkit_Core.Methods.GlobalVariables");
                    gvType?.GetProperty("ExeRootFolder")?.SetValue(null, honeycombDir);

                    // 4. Configure UserPreferences
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
                        upType.GetMethod("Save")?.Invoke(prefs, null);
                    }

                    // Set working directory to project folder
                    Directory.SetCurrentDirectory(projectDir);

                    // 5. Instantiate CompileSong form headlessly
                    var form = Activator.CreateInstance(csType, new object[] { "" });
                    if (form == null) throw new InvalidOperationException("Failed to instantiate Honeycomb CompileSong form.");
                    var flags = BindingFlags.NonPublic | BindingFlags.Instance;
                    csType.GetMethod("MainForm_Load", flags)?.Invoke(form, new object[] { form, EventArgs.Empty });

                    // 6. Load Project
                    Log("Loading song project into Honeycomb compiler engine...");
                    csType.GetMethod("LoadProject", flags)?.Invoke(form, new object[] { ghprojPath });

                    // 7. PreChecks
                    Log("Executing compiler pre-flight validation checks...");
                    csType.GetMethod("PreChecks", flags)?.Invoke(form, Array.Empty<object>());

                    // 8. Compile PAK (chart, scripts, cameras, lightshow, QB)
                    Log("Compiling chart PAK, cameras, lightshow, and QB bytecode...");
                    var mCompilePakGh3 = csType.GetMethod("CompilePakGh3", flags);
                    var pakOk = mCompilePakGh3?.Invoke(form, null);
                    if (pakOk is bool b && !b)
                    {
                        throw new InvalidOperationException("CompilePakGh3 returned false.");
                    }
                    Log("Chart PAK compilation completed successfully!");

                    // 9. Compile Audio (multi-track / stems into FMOD soundbanks .fsb.xen / .dat.xen)
                    Log("Compiling audio stems into FMOD soundbank (.fsb.xen / .dat.xen)...");
                    var mCompileGh3Audio = csType.GetMethod("CompileGh3Audio", flags);
                    if (mCompileGh3Audio != null)
                    {
                        var audioTask = (Task)mCompileGh3Audio.Invoke(form, null)!;
                        audioTask.GetAwaiter().GetResult();
                    }
                    Log("Audio soundbank compilation completed successfully!");

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

        var success = await tcs.Task;
        if (!success || threadException != null)
        {
            result.Success = false;
            var inner = threadException?.InnerException?.Message ?? threadException?.Message ?? "Unknown compiler error";
            result.Message = $"Honeycomb compilation failed: {inner}";
            Log(result.Message);
            return result;
        }

        // Post-compilation verification & standalone package copy
        try
        {
            PostProcessCompilationOutput(ghprojPath, projectDir, gamePath, result, Log);
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
    /// Inspects and sanitizes all paths inside .ghproj so Honeycomb never hits missing inputs or relative path mismatches.
    /// </summary>
    private static void FixProjectPaths(string ghprojPath, string projectDir, string honeycombDir, Action<string> log)
    {
        var jsonText = File.ReadAllText(ghprojPath);
        using var doc = JsonDocument.Parse(jsonText);
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

        // 2. Resolve Audio Paths
        var guitarPathGh3 = GetStr("guitarPathGh3");
        var rhythmPathGh3 = GetStr("rhythmPathGh3");
        var backingPathsGh3 = GetStr("backingPathsGh3");

        // Find primary audio in project directory
        string primaryAudio = "";
        string[] audioExtensions = new[] { ".mp3", ".ogg", ".wav" };
        foreach (var ext in audioExtensions)
        {
            var p = Path.Combine(projectDir, "song" + ext);
            if (File.Exists(p)) { primaryAudio = p; break; }
        }

        if (string.IsNullOrEmpty(primaryAudio))
        {
            var anyAudio = Directory.GetFiles(projectDir, "*.*")
                .FirstOrDefault(f => audioExtensions.Contains(Path.GetExtension(f).ToLowerInvariant()));
            if (!string.IsNullOrEmpty(anyAudio)) primaryAudio = anyAudio;
        }

        // Check for isolated stem files
        var guitarStem = Path.Combine(projectDir, "guitar.mp3");
        if (!File.Exists(guitarStem)) guitarStem = Path.Combine(projectDir, "guitar.ogg");
        var rhythmStem = Path.Combine(projectDir, "rhythm.mp3");
        if (!File.Exists(rhythmStem)) rhythmStem = Path.Combine(projectDir, "rhythm.ogg");
        var backingStem = Path.Combine(projectDir, "backing.mp3");
        if (!File.Exists(backingStem)) backingStem = Path.Combine(projectDir, "backing.ogg");
        var vocalsStem = Path.Combine(projectDir, "vocals.mp3");
        var drumsStem = Path.Combine(projectDir, "drums.mp3");

        bool hasStems = File.Exists(guitarStem);

        if (hasStems)
        {
            dict["guitarPathGh3"] = guitarStem;
            dict["rhythmPathGh3"] = File.Exists(rhythmStem) ? rhythmStem : "";
            dict["backingPathsGh3"] = File.Exists(backingStem) ? backingStem : primaryAudio;
            dict["previewAudioPathGh3"] = guitarStem;
            dict["guitarPath"] = guitarStem;
            dict["bassPath"] = File.Exists(rhythmStem) ? rhythmStem : "";
            dict["backingPaths"] = File.Exists(backingStem) ? backingStem : primaryAudio;
            dict["vocalsPath"] = File.Exists(vocalsStem) ? vocalsStem : "";
            dict["previewAudioPath"] = guitarStem;
            dict["isCoopAudio"] = File.Exists(rhythmStem);
        }
        else
        {
            // Single track setup: map primary audio across guitar and backing so Honeycomb audio mixer never hits inputs=0
            dict["guitarPathGh3"] = primaryAudio;
            dict["rhythmPathGh3"] = "";
            dict["backingPathsGh3"] = primaryAudio;
            dict["previewAudioPathGh3"] = primaryAudio;
            dict["guitarPath"] = primaryAudio;
            dict["bassPath"] = "";
            dict["backingPaths"] = primaryAudio;
            dict["previewAudioPath"] = primaryAudio;
            dict["isCoopAudio"] = false;
        }

        dict["compilePath"] = projectDir;
        dict["projectPath"] = ghprojPath;
        dict["ghprojFromLoad"] = ghprojPath;

        var updatedJson = JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ghprojPath, updatedJson);
    }

    /// <summary>
    /// Verifies the compiled PAK and FSB outputs in the game directory, and copies a standalone copy
    /// to the song's project directory for peer-to-peer distribution.
    /// </summary>
    private static void PostProcessCompilationOutput(
        string ghprojPath,
        string projectDir,
        string gamePath,
        HoneycombCompilationResult result,
        Action<string> log)
    {
        var songsDir = Path.Combine(gamePath, "DATA", "SONGS");
        var musicDir = Path.Combine(gamePath, "DATA", "MUSIC");

        // Find newly compiled files
        var paks = Directory.GetFiles(songsDir, "*.pak.xen")
            .Where(f => !Path.GetFileName(f).Equals("customs.pak.xen", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(f => File.GetLastWriteTime(f))
            .ToList();

        if (paks.Any())
        {
            var newestPak = paks.First();
            result.PakFilePath = newestPak;
            var songBaseName = Path.GetFileNameWithoutExtension(newestPak).Replace("_song.pak", "");

            // Look for matching FSB and DAT
            var fsbName = $"{songBaseName}.fsb.xen";
            var datName = $"{songBaseName}.dat.xen";
            var fsbPath = Path.Combine(musicDir, fsbName);
            var datPath = Path.Combine(musicDir, datName);

            if (File.Exists(fsbPath)) result.FsbFilePath = fsbPath;
            if (File.Exists(datPath)) result.DatFilePath = datPath;

            log($"Game deployment verified:");
            log($" - PAK: {Path.GetFileName(newestPak)} ({new FileInfo(newestPak).Length:N0} bytes)");
            if (File.Exists(fsbPath)) log($" - FSB: {fsbName} ({new FileInfo(fsbPath).Length:N0} bytes)");
            if (File.Exists(datPath)) log($" - DAT: {datName}");

            // Copy standalone compiled files to project directory
            try
            {
                var standalonePak = Path.Combine(projectDir, Path.GetFileName(newestPak));
                File.Copy(newestPak, standalonePak, true);

                if (File.Exists(fsbPath))
                {
                    var standaloneFsb = Path.Combine(projectDir, fsbName);
                    File.Copy(fsbPath, standaloneFsb, true);
                }

                if (File.Exists(datPath))
                {
                    var standaloneDat = Path.Combine(projectDir, datName);
                    File.Copy(datPath, standaloneDat, true);
                }

                log($"Standalone compilation package archived in song directory for P2P sync.");
            }
            catch (Exception ex)
            {
                log($"Note: Could not copy standalone package to song folder: {ex.Message}");
            }
        }
    }
}
