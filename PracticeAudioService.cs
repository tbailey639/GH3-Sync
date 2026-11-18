using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GH3_Sync;

public class SongLoudnessReport
{
    public CustomSongInfo Song { get; set; } = new();
    public string AudioPath { get; set; } = "";
    public double MeanVolumeDb { get; set; } = -14.0;
    public double MaxVolumeDb { get; set; } = -1.0;
    public bool NeedsNormalization => Math.Abs(MeanVolumeDb - (-14.0)) > 2.5 || MaxVolumeDb > -0.2;
    public string StatusLabel =>
        MaxVolumeDb > -0.2 ? $"Clipping ({MeanVolumeDb:F1} dB)" :
        MeanVolumeDb < -17.5 ? $"Too Quiet ({MeanVolumeDb:F1} dB)" :
        MeanVolumeDb > -10.5 ? $"Too Loud ({MeanVolumeDb:F1} dB)" :
        $"Balanced ({MeanVolumeDb:F1} dB)";
}

/// <summary>
/// Handles Batch Library Volume Soundcheck (-14 LUFS loudness scanning & normalization)
/// and Real-Guitar Practice Stem Exports (Guitarless Backing, Solo Guitar, 75% Slowdown Practice MP3).
/// </summary>
public class PracticeAudioService
{
    private readonly HoneycombCompilerService _compiler;
    private readonly DemucsService _demucs;
    private readonly SyncLogger _logger;

    public PracticeAudioService(HoneycombCompilerService compiler, DemucsService demucs, SyncLogger logger)
    {
        _compiler = compiler;
        _demucs = demucs;
        _logger = logger;
    }

    public async Task<List<SongLoudnessReport>> ScanLibraryLoudnessAsync(
        List<CustomSongInfo> songs,
        Action<int, string>? onProgress = null)
    {
        var results = new List<SongLoudnessReport>();
        var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
        if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) return results;

        var candidates = songs
            .Select(s => (Song: s, Audio: Gh3Service.FindSongAudioPath(s, preferGuitarStem: false)))
            .Where(x => !string.IsNullOrEmpty(x.Audio) && File.Exists(x.Audio))
            .ToList();

        for (int i = 0; i < candidates.Count; i++)
        {
            var (song, audio) = candidates[i];
            onProgress?.Invoke((int)((i + 1) * 100.0 / Math.Max(1, candidates.Count)), $"Scanning volume: {song.Title}...");
            var (meanDb, maxDb) = await Task.Run(() => MeasureVolumeDb(ff, audio!));
            results.Add(new SongLoudnessReport
            {
                Song = song,
                AudioPath = audio!,
                MeanVolumeDb = meanDb,
                MaxVolumeDb = maxDb
            });
        }

        return results;
    }

    public async Task<(int Normalized, int Failed, string Summary)> NormalizeSongsBatchAsync(
        List<SongLoudnessReport> targets,
        Action<int, string>? onProgress = null)
    {
        int ok = 0;
        int fail = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            var item = targets[i];
            var projDir = item.Song.ProjectDirectory;
            if (string.IsNullOrWhiteSpace(projDir) || !Directory.Exists(projDir)) { fail++; continue; }

            onProgress?.Invoke((int)((i * 100.0) / Math.Max(1, targets.Count)), $"[{i + 1}/{targets.Count}] Normalizing {item.Song.Title} to -14 LUFS...");
            var normPath = await Task.Run(() => SongCreatorService.TryPreprocessAudioWithFfmpeg(item.AudioPath, projDir, 0, 0, 0, normalizeLoudness: true));
            if (string.IsNullOrEmpty(normPath)) { fail++; continue; }

            var ghproj = Directory.GetFiles(projDir, "*.ghproj").FirstOrDefault();
            if (!string.IsNullOrEmpty(ghproj))
            {
                onProgress?.Invoke((int)(((i + 0.5) * 100.0) / Math.Max(1, targets.Count)), $"[{i + 1}/{targets.Count}] Recompiling {item.Song.Title} into GH3...");
                var comp = await _compiler.CompileSongProjectAsync(ghproj);
                if (comp.Success) ok++;
                else fail++;
            }
            else ok++;
        }

        var msg = $"Soundcheck complete: normalized {ok} song(s) to -14 LUFS{(fail > 0 ? $" ({fail} failed/skipped)" : "")}.";
        _logger.LogInfo($"[SOUNDCHECK] {msg}");
        return (ok, fail, msg);
    }

    /// <summary>
    /// Exports Real-Guitar Practice Tracks from any custom song:
    /// - "slow75": Pitch-preserved 75% tempo practice MP3
    /// - "guitarless": Backing track with guitar removed (via Demucs if not already isolated)
    /// - "solo": Isolated guitar track for learning riffs by ear
    /// </summary>
    public async Task<(bool Ok, string OutputPath, string Message)> ExportPracticeTrackAsync(
        CustomSongInfo song,
        string mode,
        string outputFolder,
        Action<int, string>? onProgress = null)
    {
        var audioPath = Gh3Service.FindSongAudioPath(song, preferGuitarStem: false);
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
            return (false, "", "No source audio file found in this song's project folder.");

        var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
        if (string.IsNullOrEmpty(ff) || !File.Exists(ff))
            return (false, "", "FFmpeg was not found.");

        Directory.CreateDirectory(outputFolder);
        var safeBase = string.Concat($"{song.Artist} - {song.Title}".Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim();
        if (string.IsNullOrWhiteSpace(safeBase)) safeBase = song.SongId;

        try
        {
            if (mode.Equals("slow75", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(30, "Rendering pitch-preserved 75% Practice MP3 via FFmpeg...");
                var outMp3 = Path.Combine(outputFolder, $"{safeBase} (75% Practice Slowdown).mp3");
                bool ok = await Task.Run(() => RunFfmpeg(ff, $"-y -i \"{audioPath}\" -af \"atempo=0.75\" -c:a libmp3lame -b:a 192k \"{outMp3}\""));
                return ok
                    ? (true, outMp3, $"Exported 75% pitch-preserved practice MP3:\n{outMp3}")
                    : (false, "", "FFmpeg slowdown export failed.");
            }

            // Guitarless Backing or Solo Guitar isolation
            var existingGuitar = Gh3Service.FindSongAudioPath(song, preferGuitarStem: true);
            string? guitarWav = (existingGuitar != audioPath && File.Exists(existingGuitar)) ? existingGuitar : null;
            string? backingWav = null;
            string? tempStemsDir = null;

            if (guitarWav == null || mode.Equals("guitarless", StringComparison.OrdinalIgnoreCase))
            {
                if (!_demucs.GetStatus().IsInstalled)
                    return (false, "", "Demucs 6-Stem tools are required to isolate guitar/backing stems. Install them in Tools & Stems first.");

                tempStemsDir = Path.Combine(Path.GetTempPath(), $"gh3_practice_{Guid.NewGuid():N}");
                onProgress?.Invoke(15, "Separating Guitar & Backing stems via Demucs 6-Stem...");
                var sep = await _demucs.SeparateStemsAsync(audioPath, tempStemsDir, (p, s) => onProgress?.Invoke(15 + (int)(p * 0.7), s));
                if (!sep.Success) return (false, "", $"Stem separation failed: {sep.Message}");
                guitarWav = sep.GuitarTrackPath;
                backingWav = sep.BackingTrackPath;
            }

            try
            {
                string src = mode.Equals("solo", StringComparison.OrdinalIgnoreCase) ? (guitarWav ?? "") : (backingWav ?? "");
                if (string.IsNullOrEmpty(src) || !File.Exists(src))
                    return (false, "", "Could not locate isolated stem audio.");

                string suffix = mode.Equals("solo", StringComparison.OrdinalIgnoreCase) ? "(Solo Guitar Isolated)" : "(Guitarless Backing Track)";
                var outMp3 = Path.Combine(outputFolder, $"{safeBase} {suffix}.mp3");
                onProgress?.Invoke(90, "Encoding high-quality MP3...");
                bool ok = await Task.Run(() => RunFfmpeg(ff, $"-y -i \"{src}\" -c:a libmp3lame -b:a 256k \"{outMp3}\""));
                return ok
                    ? (true, outMp3, $"Exported {suffix}:\n{outMp3}")
                    : (false, "", "MP3 encoding failed.");
            }
            finally
            {
                if (!string.IsNullOrEmpty(tempStemsDir))
                    try { Directory.Delete(tempStemsDir, true); } catch { }
            }
        }
        catch (Exception ex)
        {
            return (false, "", $"Practice export failed: {ex.Message}");
        }
    }

    private static (double MeanDb, double MaxDb) MeasureVolumeDb(string ffmpeg, string audioPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{audioPath}\" -t 90 -af volumedetect -f null NUL",
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var p = Process.Start(psi);
            if (p == null) return (-14.0, -1.0);
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit(15000);

            double mean = -14.0, max = -1.0;
            var mm = Regex.Match(err, @"mean_volume:\s*([-\d\.]+)\s*dB");
            if (mm.Success) double.TryParse(mm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out mean);
            var mx = Regex.Match(err, @"max_volume:\s*([-\d\.]+)\s*dB");
            if (mx.Success) double.TryParse(mx.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out max);
            return (mean, max);
        }
        catch { return (-14.0, -1.0); }
    }

    private static bool RunFfmpeg(string ffmpeg, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = args,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi);
        p?.WaitForExit(120_000);
        return p != null && p.ExitCode == 0;
    }
}
