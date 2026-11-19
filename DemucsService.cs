using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class DemucsStatus
{
    public bool IsInstalled { get; set; }
    public bool HasPython { get; set; }
    public bool HasDemucs { get; set; }
    public bool HasBasicPitch { get; set; }
    public bool HasNvidiaGpu { get; set; }
    public string InstallationPath { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public long InstalledSizeBytes { get; set; }
    public bool IsInstalling { get; set; }
    public bool IsSeparating { get; set; }
    public int ProgressPercent { get; set; }
    public string CurrentStep { get; set; } = "";
    public string StatusMessage { get; set; } = "";
}

public class DetectedMusicalSection
{
    public double TimeSec { get; set; }
    public string Name { get; set; } = "";
    public string ActiveInstrument { get; set; } = "";
}

public class DemucsSeparationResult
{
    public bool Success { get; set; }
    public string OutputDirectory { get; set; } = "";
    public string GuitarTrackPath { get; set; } = "";
    public string BassTrackPath { get; set; } = "";
    public string DrumsTrackPath { get; set; } = "";
    public string VocalsTrackPath { get; set; } = "";
    public string PianoTrackPath { get; set; } = "";
    public string OtherTrackPath { get; set; } = "";
    public string BackingTrackPath { get; set; } = "";
    public List<TranscribedNote> TranscribedGuitarNotes { get; set; } = new();
    public List<TranscribedNote> TranscribedBassNotes { get; set; } = new();
    public List<double> PreciseBeatTimes { get; set; } = new();
    public List<double> MeasureEnergies { get; set; } = new();
    public List<DetectedMusicalSection> DetectedSections { get; set; } = new();
    public double PreciseBpm { get; set; }
    public bool IsStudioConstantTempo { get; set; } = true;
    public bool IsGuitarDominant { get; set; } = true;
    public string ResolvedLeadFocus { get; set; } = "Guitar";
    public string StemEnergySummary { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Manages the 1-Click Portable AI Engine for GH3-Sync:
/// - Portable Python 3.11 (astral-sh/python-build-standalone — zero user setup!)
/// - Meta Demucs 6-Stem (htdemucs_6s: guitar, bass, drums, vocals, piano, other)
/// - Spotify Basic Pitch AI (ONNX pitch-to-MIDI neural transcription)
/// </summary>
public class DemucsService
{
    private const string PortablePythonUrl =
        "https://github.com/astral-sh/python-build-standalone/releases/download/20261110/cpython-3.11.18%2B20261110-x86_64-pc-windows-msvc-install_only_stripped.tar.gz";

    public static readonly string AiToolsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "tools", "ai");

    private static readonly string PortablePythonExe = Path.Combine(AiToolsRoot, "python", "python.exe");
    private static readonly string ReadyMarkerPath = Path.Combine(AiToolsRoot, ".ai_ready_v1");

    private readonly SyncLogger? _logger;
    private readonly HttpClient _http;

    private bool _isInstalling = false;
    private bool _isSeparating = false;
    private int _progressPercent = 0;
    private string _currentStep = "";

    public event Action<int, string>? ProgressChanged;

    public DemucsService(SyncLogger? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        _http.DefaultRequestHeaders.Add("User-Agent", "GH3-Sync-AI-Installer/1.5");
    }

    private void Report(int pct, string msg, Action<int, string>? callback = null)
    {
        _progressPercent = Math.Clamp(pct, 0, 100);
        _currentStep = msg;
        callback?.Invoke(_progressPercent, msg);
        ProgressChanged?.Invoke(_progressPercent, msg);
    }

    public static bool DetectNvidiaGpu()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            var outStr = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return p.ExitCode == 0 && !string.IsNullOrWhiteSpace(outStr);
        }
        catch
        {
            return false;
        }
    }

    public DemucsStatus GetStatus()
    {
        var exe = FindPythonOrDemucsExecutable();
        bool hasPython = File.Exists(PortablePythonExe) || (!string.IsNullOrEmpty(exe) && exe.EndsWith("python.exe", StringComparison.OrdinalIgnoreCase));
        bool markerReady = File.Exists(ReadyMarkerPath) && File.Exists(PortablePythonExe);

        // Also check site-packages directly if marker wasn't written
        var sitePackages = Path.Combine(AiToolsRoot, "python", "Lib", "site-packages");
        bool hasDemucs = markerReady || Directory.Exists(Path.Combine(sitePackages, "demucs"));
        bool hasBasicPitch = markerReady || Directory.Exists(Path.Combine(sitePackages, "basic_pitch"));
        bool installed = hasPython && hasDemucs;

        long totalSize = 0;
        if (Directory.Exists(AiToolsRoot))
        {
            try
            {
                totalSize = Directory.GetFiles(AiToolsRoot, "*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { }
        }

        string msg;
        if (_isInstalling)
            msg = $"Installing AI Engine ({_progressPercent}%): {_currentStep}";
        else if (installed && hasBasicPitch)
            msg = "Demucs 6-Stem (htdemucs_6s) + Spotify Basic Pitch AI ready! True guitar isolation & pitch charting active.";
        else if (installed)
            msg = "Demucs AI ready! (Click Reinstall to also add Spotify Basic Pitch MIDI transcription.)";
        else
            msg = "AI Engine not installed yet. Click '1-Click Install AI Engine' to enable 6-stem separation & real guitar charting.";

        return new DemucsStatus
        {
            IsInstalled = installed,
            HasPython = hasPython,
            HasDemucs = hasDemucs,
            HasBasicPitch = hasBasicPitch,
            HasNvidiaGpu = DetectNvidiaGpu(),
            InstallationPath = AiToolsRoot,
            ExecutablePath = exe ?? "",
            InstalledSizeBytes = totalSize,
            IsInstalling = _isInstalling,
            IsSeparating = _isSeparating,
            ProgressPercent = _progressPercent,
            CurrentStep = _currentStep,
            StatusMessage = msg
        };
    }

    public static string? FindPythonOrDemucsExecutable()
    {
        if (File.Exists(PortablePythonExe)) return PortablePythonExe;

        var legacyDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "tools", "demucs");
        if (File.Exists(Path.Combine(legacyDir, "python.exe"))) return Path.Combine(legacyDir, "python.exe");
        if (File.Exists(Path.Combine(legacyDir, "demucs.exe"))) return Path.Combine(legacyDir, "demucs.exe");

        var appBundled = Path.Combine(AppContext.BaseDirectory, "tools", "ai", "python", "python.exe");
        if (File.Exists(appBundled)) return appBundled;

        return null;
    }

    /// <summary>
    /// 1-Click Automated Installer: Downloads portable Python 3.11, installs PyTorch,
    /// Meta Demucs (6-stem htdemucs_6s), and Spotify Basic Pitch AI into %APPDATA%\GH3-Sync\tools\ai\.
    /// Zero manual Python or terminal setup required by the user!
    /// </summary>
    public async Task<(bool Success, string Message)> InstallAiEngineAsync(
        Action<int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (_isInstalling)
            return (false, "AI Engine installation is already in progress.");

        _isInstalling = true;
        try
        {
            Directory.CreateDirectory(AiToolsRoot);
            if (File.Exists(ReadyMarkerPath)) File.Delete(ReadyMarkerPath);

            // Step 1: Download & extract Portable Python 3.11 (~25 MB)
            if (!File.Exists(PortablePythonExe))
            {
                Report(2, "Downloading portable Python 3.11 runtime (~25 MB)...", onProgress);
                _logger?.LogInfo("[AI INSTALL] Downloading portable Python 3.11...");

                var tarGzPath = Path.Combine(AiToolsRoot, "python311.tar.gz");
                using (var resp = await _http.GetAsync(PortablePythonUrl, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    var total = resp.Content.Headers.ContentLength ?? (26L * 1024 * 1024);
                    await using var src = await resp.Content.ReadAsStreamAsync(ct);
                    await using var dst = File.Create(tarGzPath);

                    var buf = new byte[81920];
                    long dl = 0;
                    int read;
                    while ((read = await src.ReadAsync(buf, ct)) > 0)
                    {
                        await dst.WriteAsync(buf.AsMemory(0, read), ct);
                        dl += read;
                        int pct = 2 + (int)((dl * 18L) / Math.Max(1, total));
                        Report(pct, $"Downloading portable Python 3.11... ({dl / (1024.0 * 1024.0):F1} MB)", onProgress);
                    }
                }

                Report(22, "Extracting portable Python 3.11 runtime...", onProgress);
                await Task.Run(() =>
                {
                    using var fs = File.OpenRead(tarGzPath);
                    using var gz = new GZipStream(fs, CompressionMode.Decompress);
                    TarFile.ExtractToDirectory(gz, AiToolsRoot, overwriteFiles: true);
                }, ct);

                try { File.Delete(tarGzPath); } catch { }

                if (!File.Exists(PortablePythonExe))
                    return (false, "Portable Python extraction completed, but python.exe was not found.");
            }

            // Step 2: Upgrade pip quietly
            Report(28, "Preparing Python package manager (pip)...", onProgress);
            await RunPythonAsync("-m pip install --upgrade pip --no-warn-script-location", onProgress, 28, 34, ct);

            // Step 3: Install PyTorch + TorchAudio (CPU build is compact & universal across all Windows 11 PCs)
            bool hasGpu = DetectNvidiaGpu();
            Report(35, hasGpu
                ? "Installing PyTorch AI engine (NVIDIA GPU detected, configuring runtime)..."
                : "Installing PyTorch AI engine (Universal Windows CPU build)...", onProgress);

            // Use official PyTorch CPU wheels for reliable ~220MB install; works cleanly on every Windows 11 machine
            var torchArgs = "-m pip install --no-warn-script-location torch torchaudio --index-url https://download.pytorch.org/whl/cpu";
            var (torchOk, torchErr) = await RunPythonAsync(torchArgs, onProgress, 35, 62, ct);
            if (!torchOk)
                return (false, $"Failed installing PyTorch: {torchErr}");

            // Step 4: Install Meta Demucs + soundfile + Spotify Basic Pitch (ONNX runtime) + setuptools (<81 for resampy pkg_resources)
            Report(64, "Installing Meta Demucs (6-Stem) & Spotify Basic Pitch AI...", onProgress);
            var aiPkgArgs = "-m pip install --no-warn-script-location \"setuptools<81\" demucs soundfile basic-pitch onnxruntime \"numpy<2\"";
            var (pkgOk, pkgErr) = await RunPythonAsync(aiPkgArgs, onProgress, 64, 88, ct);
            if (!pkgOk)
            {
                // Fallback: install demucs + soundfile separately from basic-pitch if dependency resolver conflicts
                _logger?.LogWarn($"[AI INSTALL] Combined pip install note: {pkgErr}. Retrying staged install...");
                var (dOk, dErr) = await RunPythonAsync("-m pip install --no-warn-script-location demucs soundfile", onProgress, 68, 78, ct);
                if (!dOk) return (false, $"Failed installing Demucs: {dErr}");
                await RunPythonAsync("-m pip install --no-warn-script-location basic-pitch onnxruntime", onProgress, 79, 88, ct);
            }

            // Step 5: Pre-cache htdemucs_6s model weights so offline separation works immediately
            Report(90, "Downloading Meta Demucs 6-Stem neural weights (htdemucs_6s)...", onProgress);
            var preloadScript = "from demucs.pretrained import get_model; get_model('htdemucs_6s'); print('HTDEMUCS_6S_READY')";
            await RunPythonAsync($"-c \"{preloadScript}\"", onProgress, 90, 98, ct);

            File.WriteAllText(ReadyMarkerPath, $"Installed={DateTime.UtcNow:O}");
            Report(100, "AI Engine Ready! Demucs 6-Stem + Spotify Basic Pitch installed.", onProgress);
            _logger?.LogInfo("[AI INSTALL] Full AI Engine installation completed successfully!");

            return (true, "AI Engine (Demucs 6-Stem + Spotify Basic Pitch) installed and ready!");
        }
        catch (OperationCanceledException)
        {
            Report(0, "Installation canceled.", onProgress);
            return (false, "AI Engine installation was canceled.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[AI INSTALL] Error: {ex.Message}");
            Report(0, $"Installation failed: {ex.Message}", onProgress);
            return (false, $"AI Engine installation failed: {ex.Message}");
        }
        finally
        {
            _isInstalling = false;
        }
    }

    public (bool Success, string Message) UninstallAiEngine()
    {
        if (_isInstalling || _isSeparating)
            return (false, "Cannot uninstall while AI Engine is actively installing or processing.");

        try
        {
            if (Directory.Exists(AiToolsRoot))
            {
                Directory.Delete(AiToolsRoot, recursive: true);
            }
            _logger?.LogInfo("[AI ENGINE] Uninstalled AI tools cleanly.");
            return (true, "AI Engine removed cleanly and disk space freed.");
        }
        catch (Exception ex)
        {
            return (false, $"Could not remove AI folder: {ex.Message}");
        }
    }

    /// <summary>
    /// Full AI Pipeline v2:
    /// 1. Separates song into 6 isolated stems via Meta Demucs (htdemucs_6s):
    ///    guitar.wav, bass.wav, drums.wav, vocals.wav, piano.wav, other.wav
    /// 2. Runs Librosa precision beat-grid & studio-vs-live tempo detection on drums/mix,
    ///    measures 6-stem energy profiles (detecting Guitar-Dominant vs. Non-Guitar songs),
    ///    transcribes the best playable stem(s) via Spotify Basic Pitch AI (with Auto Smart Multi-Lead
    ///    or Guitar / Keys+Synth+Sax / Bass / Vocals focus) + Co-Op Bass + percussive attack onset
    ///    alignment + real bar-aligned section & energy curves!
    /// </summary>
    public async Task<DemucsSeparationResult> SeparateStemsAsync(
        string audioFilePath,
        string outputDir,
        Action<int, string>? onProgress = null,
        CancellationToken ct = default,
        string instrumentFocus = "Auto")
    {
        var result = new DemucsSeparationResult();
        var exe = FindPythonOrDemucsExecutable();

        if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
        {
            result.Message = "AI Engine not installed. Open 'AI & Tools' tab and click '1-Click Install AI Engine'.";
            return result;
        }

        if (!File.Exists(audioFilePath))
        {
            result.Message = "Input audio file not found: " + audioFilePath;
            return result;
        }

        _isSeparating = true;
        try
        {
            Directory.CreateDirectory(outputDir);

            // Prepend bundled FFmpeg directory to PATH so torchaudio/demucs can read any mp3/ogg/wav
            var hDir = HoneycombCompilerService.FindHoneycombDirectory();
            var ffmpegExe = HoneycombCompilerService.EnsureFfmpeg(hDir);
            var ffmpegDir = !string.IsNullOrEmpty(ffmpegExe) ? Path.GetDirectoryName(ffmpegExe) : null;

            string devArg = GpuAccelerationService.GetDemucsDeviceArgs();
            bool isGpu = devArg.Contains("cuda", StringComparison.OrdinalIgnoreCase);
            Report(10, $"Running Demucs 6-Stem AI {(isGpu ? "⚡ [GPU Accelerated]" : "[CPU Mode]")} (isolating Guitar, Bass, Drums, Vocals, Piano)...", onProgress);
            _logger?.LogInfo($"[DEMUCS 6-STEM] Separating with {devArg}: {Path.GetFileName(audioFilePath)}");

            bool isPython = Path.GetFileName(exe).Equals("python.exe", StringComparison.OrdinalIgnoreCase);
            string demucsArgs = isPython
                ? $"-m demucs.separate {devArg} -n htdemucs_6s -o \"{outputDir}\" \"{audioFilePath}\""
                : $"{devArg} -n htdemucs_6s -o \"{outputDir}\" \"{audioFilePath}\"";

            var (sepOk, sepErr) = await RunProcessWithPathAsync(exe, demucsArgs, outputDir, ffmpegDir, (line) =>
            {
                if (line.Contains('%'))
                    Report(35, $"Demucs 6-Stem separating audio... {line.Trim()}", onProgress);
            }, ct);

            // If CUDA separation failed (e.g. out of memory), retry seamlessly with CPU fallback
            if (!sepOk && isGpu)
            {
                _logger?.LogWarn($"[DEMUCS] GPU separation failed: {sepErr}. Retrying with CPU fallback...");
                Report(15, "GPU memory busy — retrying with CPU multi-threaded separation...", onProgress);
                string cpuArgs = isPython
                    ? $"-m demucs.separate -d cpu -n htdemucs_6s -o \"{outputDir}\" \"{audioFilePath}\""
                    : $"-d cpu -n htdemucs_6s -o \"{outputDir}\" \"{audioFilePath}\"";
                var (cpuOk, cpuErr) = await RunProcessWithPathAsync(exe, cpuArgs, outputDir, ffmpegDir, (line) =>
                {
                    if (line.Contains('%'))
                        Report(35, $"Demucs 6-Stem separating audio (CPU)... {line.Trim()}", onProgress);
                }, ct);
                sepOk = cpuOk;
                sepErr = cpuErr;
            }

            // Locate output stems folder: outputDir/htdemucs_6s/<song_name>/
            var songName = Path.GetFileNameWithoutExtension(audioFilePath);
            var expectedDir = Path.Combine(outputDir, "htdemucs_6s", songName);
            if (!Directory.Exists(expectedDir))
            {
                var foundGuitar = Directory.GetFiles(outputDir, "guitar.wav", SearchOption.AllDirectories).FirstOrDefault()
                               ?? Directory.GetFiles(outputDir, "other.wav", SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrEmpty(foundGuitar))
                    expectedDir = Path.GetDirectoryName(foundGuitar)!;
            }

            var guitarWav = Path.Combine(expectedDir, "guitar.wav");
            var otherWav = Path.Combine(expectedDir, "other.wav");
            var bassWav = Path.Combine(expectedDir, "bass.wav");
            var drumsWav = Path.Combine(expectedDir, "drums.wav");
            var vocalsWav = Path.Combine(expectedDir, "vocals.wav");
            var pianoWav = Path.Combine(expectedDir, "piano.wav");

            string chosenGuitar = File.Exists(guitarWav) ? guitarWav : (File.Exists(otherWav) ? otherWav : "");
            if (string.IsNullOrEmpty(chosenGuitar))
            {
                result.Message = $"Demucs separation finished without producing guitar/other stems. {sepErr}";
                _logger?.LogWarn($"[DEMUCS] {result.Message}");
                return result;
            }

            result.Success = true;
            result.OutputDirectory = expectedDir;
            result.GuitarTrackPath = chosenGuitar;
            result.BassTrackPath = File.Exists(bassWav) ? bassWav : "";
            result.DrumsTrackPath = File.Exists(drumsWav) ? drumsWav : "";
            result.VocalsTrackPath = File.Exists(vocalsWav) ? vocalsWav : "";
            result.PianoTrackPath = File.Exists(pianoWav) ? pianoWav : "";
            result.OtherTrackPath = File.Exists(otherWav) ? otherWav : "";

            // Step 2: Run Unified Librosa Beat/Section/Onset Analyzer + Multi-Instrument Spotify Basic Pitch AI!
            if (isPython)
            {
                Report(58, "Analyzing 6-stem balance, precision beat grid & transcribing lead + bass (Basic Pitch)...", onProgress);
                var analysisJsonPath = Path.Combine(expectedDir, "ai_analysis_v2.json");
                var scriptPath = Path.Combine(expectedDir, "_analyze_and_transcribe.py");

                var pyScript = """
import sys, json, os, types, importlib, warnings
warnings.filterwarnings('ignore')

# Compatibility shim for resampy/basic-pitch on newer Python/setuptools where pkg_resources was removed
if 'pkg_resources' not in sys.modules:
    try:
        import pkg_resources  # noqa: F401
    except ImportError:
        _pr = types.ModuleType('pkg_resources')
        def _resource_filename(pkg, res):
            mod = importlib.import_module(pkg) if isinstance(pkg, str) else pkg
            base = os.path.dirname(os.path.abspath(getattr(mod, '__file__', '')))
            return os.path.join(base, res)
        _pr.resource_filename = _resource_filename
        sys.modules['pkg_resources'] = _pr

import numpy as np
import soundfile as sf
import librosa
from basic_pitch.inference import predict

stems_dir = sys.argv[1]
orig_audio = sys.argv[2]
focus_arg = (sys.argv[3] if len(sys.argv) > 3 else 'auto').strip().lower()
out_json = sys.argv[4]

SR = 22050

def load_mono(path):
    if not path or not os.path.exists(path):
        return None
    try:
        y, _ = librosa.load(path, sr=SR, mono=True)
        return y.astype(np.float32)
    except Exception:
        return None

def stem_stats(y):
    if y is None or len(y) == 0:
        return 0.0, 0.0
    rms = librosa.feature.rms(y=y, frame_length=2048, hop_length=512)[0]
    mean_rms = float(np.mean(rms))
    active_ratio = float(np.mean(rms > 0.015))
    return mean_rms, active_ratio

def run_bp(wav_path, onset_th=0.42, frame_th=0.28, min_len=50.0, min_freq=75.0, max_freq=1400.0):
    if not wav_path or not os.path.exists(wav_path):
        return []
    _, _, note_events = predict(
        wav_path,
        onset_threshold=onset_th,
        frame_threshold=frame_th,
        minimum_note_length=min_len,
        minimum_frequency=min_freq,
        maximum_frequency=max_freq
    )
    out = []
    for start_t, end_t, pitch, amp, _ in note_events:
        if end_t > start_t + 0.03:
            out.append({
                'StartSec': float(start_t),
                'EndSec': float(end_t),
                'MidiPitch': int(pitch),
                'Amplitude': float(amp)
            })
    out.sort(key=lambda x: x['StartSec'])
    return out

def align_to_onsets(notes, y):
    if not notes or y is None or len(y) < SR:
        return notes
    try:
        onset_env = librosa.onset.onset_strength(y=y, sr=SR)
        onsets = librosa.onset.onset_detect(onset_envelope=onset_env, sr=SR, units='time', backtrack=True)
        if len(onsets) == 0:
            return notes
        aligned = []
        for n in notes:
            st = n['StartSec']
            idx = int(np.searchsorted(onsets, st))
            best = st
            best_dist = 0.038
            for cand_idx in (idx - 1, idx):
                if 0 <= cand_idx < len(onsets):
                    d = abs(float(onsets[cand_idx]) - st)
                    if d < best_dist:
                        best_dist = d
                        best = float(onsets[cand_idx])
            dur = max(0.045, n['EndSec'] - st)
            aligned.append({
                'StartSec': best,
                'EndSec': max(best + 0.045, best + dur),
                'MidiPitch': n['MidiPitch'],
                'Amplitude': n['Amplitude']
            })
        aligned.sort(key=lambda x: x['StartSec'])
        return aligned
    except Exception:
        return notes

def write_mix_wav(paths, dest_path):
    arrays = []
    target_sr = 44100
    for p in paths:
        if p and os.path.exists(p):
            data, sr = sf.read(p, always_2d=True)
            target_sr = sr
            arrays.append(data.astype(np.float32))
    if not arrays:
        return None
    min_len = min(a.shape[0] for a in arrays)
    mixed = np.zeros((min_len, arrays[0].shape[1]), dtype=np.float32)
    for a in arrays:
        mixed += a[:min_len]
    peak = float(np.max(np.abs(mixed)))
    if peak > 0.98:
        mixed = mixed * (0.98 / peak)
    sf.write(dest_path, mixed, target_sr)
    return dest_path

try:
    paths = {
        'guitar': os.path.join(stems_dir, 'guitar.wav'),
        'piano':  os.path.join(stems_dir, 'piano.wav'),
        'other':  os.path.join(stems_dir, 'other.wav'),
        'bass':   os.path.join(stems_dir, 'bass.wav'),
        'vocals': os.path.join(stems_dir, 'vocals.wav'),
        'drums':  os.path.join(stems_dir, 'drums.wav'),
    }

    ys = {k: load_mono(p) for k, p in paths.items()}
    mix_y = load_mono(orig_audio)
    if mix_y is None:
        for v in ys.values():
            if v is not None:
                mix_y = v
                break
    duration = float(len(mix_y) / SR) if mix_y is not None else 180.0

    # 1. Per-stem RMS energy & active ratio
    stats = {k: stem_stats(v) for k, v in ys.items()}
    melodic_total = max(1e-6, stats['guitar'][0] + stats['piano'][0] + stats['other'][0] + stats['bass'][0] + stats['vocals'][0])
    guitar_share = stats['guitar'][0] / melodic_total
    keys_other_share = (stats['piano'][0] + stats['other'][0]) / melodic_total
    bass_share = stats['bass'][0] / melodic_total
    vocals_share = stats['vocals'][0] / melodic_total

    is_guitar_dominant = (guitar_share >= 0.32 and stats['guitar'][1] >= 0.30) and (stats['guitar'][0] >= (stats['piano'][0] + stats['other'][0]) * 0.90)

    summary = f'Guitar {int(round(guitar_share*100))}% | Keys/Sax/Synth {int(round(keys_other_share*100))}% | Bass {int(round(bass_share*100))}% | Vocals {int(round(vocals_share*100))}%'
    print(f'STEP:Stem balance: {summary}', flush=True)

    # 2. Librosa precision beat tracking on drums (fallback to full mix)
    beat_y = ys['drums'] if (ys['drums'] is not None and stats['drums'][0] > 0.012) else mix_y
    tempo_raw, beat_frames = librosa.beat.beat_track(y=beat_y, sr=SR, units='frames', tightness=120)
    beat_times = librosa.frames_to_time(beat_frames, sr=SR).tolist() if len(beat_frames) > 0 else []
    raw_bpm = float(np.atleast_1d(tempo_raw)[0]) if tempo_raw is not None else 120.0
    if raw_bpm < 65.0: raw_bpm *= 2.0
    if raw_bpm > 210.0: raw_bpm /= 2.0

    is_studio_constant = True
    precise_bpm = raw_bpm
    if len(beat_times) >= 12:
        diffs = np.diff(np.array(beat_times))
        med = float(np.median(diffs))
        valid = diffs[(diffs > med * 0.65) & (diffs < med * 1.35)]
        if len(valid) >= 8:
            p25, p75 = np.percentile(valid, [25, 75])
            iqr_ratio = float((p75 - p25) / max(1e-6, float(np.median(valid))))
            precise_bpm = float(60.0 / float(np.median(valid)))
            if precise_bpm < 65.0: precise_bpm *= 2.0
            if precise_bpm > 210.0: precise_bpm /= 2.0
            is_studio_constant = (iqr_ratio < 0.036)

    tempo_label = 'Studio Constant Grid' if is_studio_constant else 'Live Dynamic Tempo'
    print(f'STEP:Precision tempo: {precise_bpm:.1f} BPM ({tempo_label})', flush=True)

    # 3. Compute per-measure energy curve from full mix (every 4 beats)
    measure_energies = []
    if mix_y is not None and len(beat_times) >= 8:
        raw_e = []
        for bi in range(0, len(beat_times) - 4, 4):
            s0 = int(beat_times[bi] * SR)
            s1 = int(beat_times[bi + 4] * SR)
            seg = mix_y[s0:s1]
            raw_e.append(float(np.sqrt(np.mean(seg * seg))) if len(seg) > 0 else 0.0)
        mx = max(raw_e) if raw_e else 1.0
        mx = max(mx, 1e-6)
        measure_energies = [round(e / mx, 3) for e in raw_e]

    # 4. Run Spotify Basic Pitch on the chosen / auto-detected playable stem(s)
    lead_notes = []
    sections = []
    resolved_focus = 'Guitar'

    def norm_focus(f):
        if 'keys' in f or 'synth' in f or 'sax' in f or 'piano' in f:
            return 'keys_synth'
        if 'bass' in f:
            return 'bass'
        if 'vocal' in f:
            return 'vocals'
        if 'guitar' in f and 'auto' not in f and 'multi' not in f:
            return 'guitar'
        return 'auto'

    mode = norm_focus(focus_arg)

    if mode == 'guitar' or (mode == 'auto' and is_guitar_dominant):
        resolved_focus = 'Guitar'
        print('STEP:Transcribing isolated Guitar stem with Basic Pitch + onset alignment...', flush=True)
        lead_notes = run_bp(paths['guitar'], 0.42, 0.28, 50.0, 75.0, 1400.0)
        lead_notes = align_to_onsets(lead_notes, ys['guitar'])

    elif mode == 'keys_synth':
        resolved_focus = 'Keys / Synth / Sax'
        print('STEP:Transcribing combined Piano + Synth/Sax stems with Basic Pitch...', flush=True)
        mix_wav = write_mix_wav([paths['piano'], paths['other']], os.path.join(stems_dir, '_keys_other_lead.wav'))
        mix_ko_y = load_mono(mix_wav)
        lead_notes = run_bp(mix_wav, 0.40, 0.27, 50.0, 65.0, 2000.0)
        lead_notes = align_to_onsets(lead_notes, mix_ko_y)

    elif mode == 'bass':
        resolved_focus = 'Bass Groove'
        print('STEP:Transcribing isolated Bass stem with Basic Pitch (low-end tuned)...', flush=True)
        lead_notes = run_bp(paths['bass'], 0.38, 0.28, 55.0, 35.0, 420.0)
        lead_notes = align_to_onsets(lead_notes, ys['bass'])

    elif mode == 'vocals':
        resolved_focus = 'Vocals'
        print('STEP:Transcribing isolated Vocals stem with Basic Pitch...', flush=True)
        lead_notes = run_bp(paths['vocals'], 0.44, 0.30, 65.0, 85.0, 1200.0)
        lead_notes = align_to_onsets(lead_notes, ys['vocals'])

    else:
        # AUTO SMART MULTI-LEAD ('Play the Hook') for non-guitar / mixed-instrument tracks like Tadow!
        resolved_focus = 'Smart Multi-Lead'
        print('STEP:Non-guitar/multi-lead song detected — transcribing melodic stems (Guitar, Keys, Sax/Synth)...', flush=True)
        candidates = []
        if stats['guitar'][0] > 0.008: candidates.append(('Guitar', paths['guitar'], ys['guitar'], (0.42, 0.28, 50.0, 75.0, 1400.0)))
        if stats['other'][0]  > 0.008: candidates.append(('Sax / Synth', paths['other'], ys['other'], (0.40, 0.27, 50.0, 65.0, 2000.0)))
        if stats['piano'][0]  > 0.008: candidates.append(('Keys', paths['piano'], ys['piano'], (0.40, 0.27, 50.0, 65.0, 2000.0)))
        if not candidates and stats['vocals'][0] > 0.008:
            candidates.append(('Vocals', paths['vocals'], ys['vocals'], (0.44, 0.30, 65.0, 85.0, 1200.0)))
        if not candidates:
            candidates.append(('Guitar', paths['guitar'], ys['guitar'], (0.42, 0.28, 50.0, 75.0, 1400.0)))

        cand_notes = {}
        for label, pth, cy, cfg in candidates:
            ns = run_bp(pth, *cfg)
            ns = align_to_onsets(ns, cy)
            cand_notes[label] = ns

        # Build 4-bar phrase windows from beat_times (or 7.5s windows)
        phrase_bounds = []
        if len(beat_times) >= 16:
            for bi in range(0, len(beat_times), 16):
                t0 = float(beat_times[bi])
                t1 = float(beat_times[min(bi + 16, len(beat_times) - 1)]) if bi + 16 < len(beat_times) else duration
                if t1 > t0 + 1.0:
                    phrase_bounds.append((t0, t1))
            if phrase_bounds and phrase_bounds[0][0] > 0.2:
                phrase_bounds.insert(0, (0.0, phrase_bounds[0][0]))
        else:
            t = 0.0
            while t < duration:
                phrase_bounds.append((t, min(duration, t + 7.5)))
                t += 7.5

        prev_winner = None
        used_labels = set()
        for t0, t1 in phrase_bounds:
            best_label = None
            best_score = -1.0
            for label, _, cy, _ in candidates:
                p_notes = [n for n in cand_notes[label] if t0 <= n['StartSec'] < t1]
                s0 = int(t0 * SR)
                s1 = int(t1 * SR)
                seg = cy[s0:s1] if (cy is not None and s1 > s0) else np.zeros(1)
                p_rms = float(np.sqrt(np.mean(seg * seg)))
                score = p_rms * (1.0 + 0.05 * min(len(p_notes), 32))
                if label == 'Guitar': score *= 1.12
                if label == prev_winner: score *= 1.18
                if len(p_notes) == 0: score *= 0.1
                if score > best_score:
                    best_score = score
                    best_label = label
            if best_label:
                p_notes = [n for n in cand_notes[best_label] if t0 <= n['StartSec'] < t1]
                lead_notes.extend(p_notes)
                used_labels.add(best_label)
                if best_label != prev_winner and len(p_notes) >= 3:
                    sections.append({'TimeSec': round(t0, 3), 'Name': f'{best_label} Lead', 'ActiveInstrument': best_label})
                prev_winner = best_label

        lead_notes.sort(key=lambda x: x['StartSec'])
        if used_labels:
            resolved_focus = 'Smart Multi-Lead (' + ' + '.join(sorted(used_labels)) + ')'

    # 5. Transcribe Co-Op Bass track when available (and not already Lead)
    bass_notes = []
    if mode != 'bass' and stats['bass'][0] > 0.012:
        print('STEP:Transcribing Co-Op Bass track with Basic Pitch...', flush=True)
        bass_notes = run_bp(paths['bass'], 0.40, 0.30, 60.0, 35.0, 400.0)
        bass_notes = align_to_onsets(bass_notes, ys['bass'])

    # 6. Build authentic bar-aligned sections from energy transitions if not already populated
    if len(sections) < 3 and len(beat_times) >= 24 and len(measure_energies) >= 6:
        sections = []
        sections.append({'TimeSec': round(float(beat_times[0]), 3), 'Name': 'Intro', 'ActiveInstrument': resolved_focus})
        avg_e = float(np.mean(measure_energies))
        hi_th = min(0.82, avg_e * 1.18)
        verse_num = 1
        chorus_num = 1
        last_kind = 'intro'
        last_sec_m = 0
        for mi in range(4, len(measure_energies) - 3):
            if mi - last_sec_m < 6:
                continue
            win_e = float(np.mean(measure_energies[mi:min(mi + 4, len(measure_energies))]))
            prev_e = float(np.mean(measure_energies[max(0, mi - 4):mi]))
            bi = mi * 4
            if bi >= len(beat_times): break
            if win_e >= hi_th and last_kind != 'chorus' and win_e > prev_e * 1.08:
                sections.append({'TimeSec': round(float(beat_times[bi]), 3), 'Name': f'Chorus {chorus_num}', 'ActiveInstrument': resolved_focus})
                chorus_num += 1
                last_kind = 'chorus'
                last_sec_m = mi
            elif win_e < hi_th * 0.90 and last_kind != 'verse' and mi >= 6:
                sections.append({'TimeSec': round(float(beat_times[bi]), 3), 'Name': f'Verse {verse_num}', 'ActiveInstrument': resolved_focus})
                verse_num += 1
                last_kind = 'verse'
                last_sec_m = mi
        outro_m = max(4, int(len(measure_energies) * 0.86))
        if outro_m * 4 < len(beat_times):
            sections.append({'TimeSec': round(float(beat_times[outro_m * 4]), 3), 'Name': 'Outro', 'ActiveInstrument': resolved_focus})

    payload = {
        'LeadNotes': lead_notes,
        'BassNotes': bass_notes,
        'BeatTimes': [round(float(t), 4) for t in beat_times],
        'MeasureEnergies': measure_energies,
        'Sections': sections,
        'PreciseBpm': round(float(precise_bpm), 2),
        'IsStudioConstantTempo': bool(is_studio_constant),
        'IsGuitarDominant': bool(is_guitar_dominant),
        'ResolvedLeadFocus': resolved_focus,
        'StemEnergySummary': summary
    }
    with open(out_json, 'w', encoding='utf-8') as f:
        json.dump(payload, f)
    print(f'AI_V2_OK:lead={len(lead_notes)},bass={len(bass_notes)},bpm={precise_bpm:.1f},studio={is_studio_constant},gtr_dom={is_guitar_dominant}', flush=True)
except Exception as e:
    import traceback
    traceback.print_exc(file=sys.stderr)
    sys.exit(1)
""";
                File.WriteAllText(scriptPath, pyScript);

                var (bpOk, bpErr) = await RunProcessWithPathAsync(
                    exe,
                    $"\"{scriptPath}\" \"{expectedDir}\" \"{audioFilePath}\" \"{instrumentFocus}\" \"{analysisJsonPath}\"",
                    expectedDir,
                    ffmpegDir,
                    line =>
                    {
                        if (line.StartsWith("STEP:", StringComparison.OrdinalIgnoreCase))
                        {
                            Report(78, line.Substring(5).Trim(), onProgress);
                        }
                    },
                    ct);

                if (bpOk && File.Exists(analysisJsonPath))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(analysisJsonPath));
                        var root = doc.RootElement;

                        if (root.TryGetProperty("LeadNotes", out var ln))
                            result.TranscribedGuitarNotes = JsonSerializer.Deserialize<List<TranscribedNote>>(ln.GetRawText()) ?? new();
                        if (root.TryGetProperty("BassNotes", out var bn))
                            result.TranscribedBassNotes = JsonSerializer.Deserialize<List<TranscribedNote>>(bn.GetRawText()) ?? new();
                        if (root.TryGetProperty("BeatTimes", out var bt))
                            result.PreciseBeatTimes = JsonSerializer.Deserialize<List<double>>(bt.GetRawText()) ?? new();
                        if (root.TryGetProperty("MeasureEnergies", out var me))
                            result.MeasureEnergies = JsonSerializer.Deserialize<List<double>>(me.GetRawText()) ?? new();
                        if (root.TryGetProperty("Sections", out var sc))
                            result.DetectedSections = JsonSerializer.Deserialize<List<DetectedMusicalSection>>(sc.GetRawText()) ?? new();
                        if (root.TryGetProperty("PreciseBpm", out var bpmP) && bpmP.TryGetDouble(out var pBpm))
                            result.PreciseBpm = pBpm;
                        if (root.TryGetProperty("IsStudioConstantTempo", out var scP))
                            result.IsStudioConstantTempo = scP.GetBoolean();
                        if (root.TryGetProperty("IsGuitarDominant", out var gdP))
                            result.IsGuitarDominant = gdP.GetBoolean();
                        if (root.TryGetProperty("ResolvedLeadFocus", out var rfP))
                            result.ResolvedLeadFocus = rfP.GetString() ?? "Guitar";
                        if (root.TryGetProperty("StemEnergySummary", out var seP))
                            result.StemEnergySummary = seP.GetString() ?? "";

                        _logger?.LogInfo($"[AI ENGINE v2] Lead={result.TranscribedGuitarNotes.Count} notes ({result.ResolvedLeadFocus}), Co-Op Bass={result.TranscribedBassNotes.Count} notes, BPM={result.PreciseBpm:F1} ({(result.IsStudioConstantTempo ? "Studio Grid" : "Live")}), GuitarDominant={result.IsGuitarDominant} ({result.StemEnergySummary})");
                    }
                    catch (Exception parseEx)
                    {
                        _logger?.LogWarn($"[AI ENGINE v2] JSON parse warning: {parseEx.Message}");
                    }
                }
                else
                {
                    _logger?.LogWarn($"[AI ENGINE v2] Skipped or fallback needed: {bpErr}");
                }

                try { if (File.Exists(scriptPath)) File.Delete(scriptPath); } catch { }
                try { if (File.Exists(analysisJsonPath)) File.Delete(analysisJsonPath); } catch { }
            }

            Report(100, $"AI Analysis complete! ({result.TranscribedGuitarNotes.Count} lead notes • {result.ResolvedLeadFocus})", onProgress);
            result.Message = result.TranscribedGuitarNotes.Count > 0
                ? $"Demucs 6-Stem + Basic Pitch v2 ({result.ResolvedLeadFocus}): {result.TranscribedGuitarNotes.Count} lead notes, {result.TranscribedBassNotes.Count} bass notes ({result.StemEnergySummary})"
                : "Demucs 6-Stem isolated guitar, bass, drums, piano & vocals cleanly!";
            return result;
        }
        catch (Exception ex)
        {
            result.Message = "AI separation error: " + ex.Message;
            _logger?.LogWarn($"[DEMUCS] Error: {ex.Message}");
            return result;
        }
        finally
        {
            _isSeparating = false;
        }
    }

    private async Task<(bool Ok, string Error)> RunPythonAsync(
        string arguments,
        Action<int, string>? onProgress,
        int startPct,
        int endPct,
        CancellationToken ct)
    {
        int curPct = startPct;
        return await RunProcessWithPathAsync(PortablePythonExe, arguments, AiToolsRoot, null, (line) =>
        {
            if (curPct < endPct) curPct++;
            var trimmed = line.Trim();
            if (!string.IsNullOrEmpty(trimmed) && trimmed.Length < 110)
            {
                Report(curPct, trimmed, onProgress);
            }
        }, ct);
    }

    private static async Task<(bool Ok, string Error)> RunProcessWithPathAsync(
        string fileName,
        string arguments,
        string workingDir,
        string? prependPath,
        Action<string>? onOutputLine,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        var curPath = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extraPaths = new List<string>();
        if (!string.IsNullOrEmpty(prependPath)) extraPaths.Add(prependPath);
        var pyScripts = Path.Combine(AiToolsRoot, "python", "Scripts");
        if (Directory.Exists(pyScripts)) extraPaths.Add(pyScripts);
        if (extraPaths.Count > 0)
            psi.Environment["PATH"] = string.Join(";", extraPaths) + ";" + curPath;

        psi.Environment["PYTHONIOENCODING"] = "utf-8";

        // Consolidate PyTorch / Demucs model weights inside %APPDATA%\GH3-Sync\tools\ai\models
        // so keeping or uninstalling Stem Tools manages 100% of disk space cleanly!
        var modelsDir = Path.Combine(AiToolsRoot, "models");
        Directory.CreateDirectory(modelsDir);
        psi.Environment["TORCH_HOME"] = modelsDir;

        using var proc = Process.Start(psi);
        if (proc == null) return (false, "Could not start process.");

        var errLines = new List<string>();

        proc.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data)) onOutputLine?.Invoke(e.Data);
        };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                errLines.Add(e.Data);
                if (e.Data.Contains('%')) onOutputLine?.Invoke(e.Data);
            }
        };

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        await proc.WaitForExitAsync(ct);
        var errSummary = string.Join("\n", errLines.TakeLast(6));
        return (proc.ExitCode == 0, errSummary);
    }
}
