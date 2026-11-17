using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace GH3_Sync;

public class DemucsStatus
{
    public bool IsInstalled { get; set; }
    public string InstallationPath { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public bool IsSeparating { get; set; }
    public int ProgressPercent { get; set; }
    public string CurrentStep { get; set; } = "";
    public string StatusMessage { get; set; } = "";
}

public class DemucsSeparationResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string GuitarTrackPath { get; set; } = "";
    public string BassTrackPath { get; set; } = "";
    public string DrumsTrackPath { get; set; } = "";
    public string VocalsTrackPath { get; set; } = "";
    public string BackingTrackPath { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Manages Demucs AI Stem Separation for Guitar Hero III multi-track customs.
/// Splits songs into Guitar (other.wav), Bass, Drums, and Vocals.
/// When you miss a note in-game, only the guitar stem mutes!
/// </summary>
public class DemucsService
{
    private static readonly string DemucsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "tools", "demucs");

    private readonly SyncLogger? _logger;
    private readonly HttpClient _http = new();
    private bool _isSeparating = false;
    private int _progressPercent = 0;
    private string _currentStep = "";

    public DemucsService(SyncLogger? logger = null)
    {
        _logger = logger;
    }

    public DemucsStatus GetStatus()
    {
        var exe = FindDemucsExecutable();
        var installed = !string.IsNullOrEmpty(exe) && File.Exists(exe);

        return new DemucsStatus
        {
            IsInstalled = installed,
            InstallationPath = DemucsDirectory,
            ExecutablePath = exe ?? "",
            IsSeparating = _isSeparating,
            ProgressPercent = _progressPercent,
            CurrentStep = _currentStep,
            StatusMessage = installed
                ? "Demucs AI Stem Separation is ready. Multi-track stem isolation enabled."
                : "Demucs AI is not installed. Single mix audio will be used."
        };
    }

    public static string? FindDemucsExecutable()
    {
        // 1. Check local AppData tools folder
        if (Directory.Exists(DemucsDirectory))
        {
            var standalone = Path.Combine(DemucsDirectory, "demucs.exe");
            if (File.Exists(standalone)) return standalone;

            var pythonInDemucs = Path.Combine(DemucsDirectory, "python.exe");
            if (File.Exists(pythonInDemucs)) return pythonInDemucs;

            var scriptsDemucs = Path.Combine(DemucsDirectory, "Scripts", "demucs.exe");
            if (File.Exists(scriptsDemucs)) return scriptsDemucs;
        }

        // 2. Check bundled tools inside app directory
        var appBundled = Path.Combine(AppContext.BaseDirectory, "tools", "demucs", "demucs.exe");
        if (File.Exists(appBundled)) return appBundled;

        // 3. Check system PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var cand = Path.Combine(dir.Trim(), "demucs.exe");
                if (File.Exists(cand)) return cand;
            }
            catch { }
        }

        return null;
    }

    /// <summary>
    /// Separates a single audio file into isolated multi-track stems:
    /// - other.wav -> Guitar
    /// - bass.wav -> Bass / Rhythm
    /// - drums.wav -> Drums
    /// - vocals.wav -> Vocals
    /// </summary>
    public async Task<DemucsSeparationResult> SeparateStemsAsync(string audioFilePath, string outputDir)
    {
        var result = new DemucsSeparationResult();
        var exe = FindDemucsExecutable();

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            result.Message = "Demucs AI executable not found. Please install the Demucs AI engine.";
            return result;
        }

        if (!File.Exists(audioFilePath))
        {
            result.Message = "Input audio file not found: " + audioFilePath;
            return result;
        }

        _isSeparating = true;
        _progressPercent = 10;
        _currentStep = "Initializing Demucs AI model (htdemucs)...";
        _logger?.LogInfo($"[DEMUCS] Beginning stem separation for: {Path.GetFileName(audioFilePath)}");

        try
        {
            Directory.CreateDirectory(outputDir);

            var psi = new ProcessStartInfo
            {
                FileName = exe,
                WorkingDirectory = outputDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            // If pointing to python.exe, invoke demucs as module
            if (Path.GetFileName(exe).Equals("python.exe", StringComparison.OrdinalIgnoreCase))
            {
                psi.Arguments = $"-m demucs.separate -n htdemucs -o \"{outputDir}\" \"{audioFilePath}\"";
            }
            else
            {
                psi.Arguments = $"-n htdemucs -o \"{outputDir}\" \"{audioFilePath}\"";
            }

            using var proc = Process.Start(psi);
            if (proc == null)
            {
                result.Message = "Failed to launch Demucs process.";
                return result;
            }

            _currentStep = "Separating drums, bass, vocals, and guitar stems...";
            _progressPercent = 40;

            var errOutput = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();

            _progressPercent = 90;

            // Demucs outputs to: outputDir / htdemucs / <song_name> / [drums.wav, bass.wav, other.wav, vocals.wav]
            var songName = Path.GetFileNameWithoutExtension(audioFilePath);
            var expectedDir = Path.Combine(outputDir, "htdemucs", songName);

            if (!Directory.Exists(expectedDir))
            {
                // Search for other.wav anywhere in outputDir
                var foundOther = Directory.GetFiles(outputDir, "other.wav", SearchOption.AllDirectories);
                if (foundOther.Length > 0)
                {
                    expectedDir = Path.GetDirectoryName(foundOther[0])!;
                }
            }

            var guitar = Path.Combine(expectedDir, "other.wav");
            var bass = Path.Combine(expectedDir, "bass.wav");
            var drums = Path.Combine(expectedDir, "drums.wav");
            var vocals = Path.Combine(expectedDir, "vocals.wav");

            if (File.Exists(guitar))
            {
                result.Success = true;
                result.OutputDirectory = expectedDir;
                result.GuitarTrackPath = guitar;
                result.BassTrackPath = File.Exists(bass) ? bass : "";
                result.DrumsTrackPath = File.Exists(drums) ? drums : "";
                result.VocalsTrackPath = File.Exists(vocals) ? vocals : "";
                result.Message = "Successfully separated all stems with Demucs AI!";
                _logger?.LogInfo($"[DEMUCS] Stem separation completed successfully. Guitar stem: {guitar}");
            }
            else
            {
                result.Message = "Demucs completed, but isolated stem files were not found. " + errOutput;
                _logger?.LogWarn($"[DEMUCS] Output missing: {result.Message}");
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Message = "Demucs separation error: " + ex.Message;
            _logger?.LogWarn($"[DEMUCS] Error: {ex.Message}");
            return result;
        }
        finally
        {
            _isSeparating = false;
            _progressPercent = 100;
            _currentStep = "Done";
        }
    }
}
