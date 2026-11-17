using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace GH3_Sync;

public class AudioAnalysisResult
{
    public double Bpm { get; set; } = 120.0;
    public double DownbeatOffsetMs { get; set; } = 0.0;
    public double DurationSeconds { get; set; } = 0.0;
    public List<double> OnsetTimestampsSeconds { get; set; } = new();
    public List<float> OnsetEnergies { get; set; } = new();
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// High-accuracy audio analysis engine for ONOGAMI Song Creator.
/// Detects exact BPM, downbeat timing, and transient onsets from audio files
/// using spectral flux and autocorrelation via FFmpeg PCM decoding.
/// </summary>
public class AudioAnalysisService
{
    private readonly SyncLogger? _logger;

    public AudioAnalysisService(SyncLogger? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// Analyzes an audio file to detect BPM, downbeat offset, and rhythmic onsets.
    /// </summary>
    public AudioAnalysisResult AnalyzeAudio(string audioFilePath, string? ffmpegPath = null)
    {
        var result = new AudioAnalysisResult();

        if (!File.Exists(audioFilePath))
        {
            result.Message = "Audio file not found: " + audioFilePath;
            return result;
        }

        var resolvedFfmpeg = !string.IsNullOrEmpty(ffmpegPath) && File.Exists(ffmpegPath)
            ? ffmpegPath
            : HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());

        if (string.IsNullOrEmpty(resolvedFfmpeg) || !File.Exists(resolvedFfmpeg))
        {
            result.Message = "FFmpeg binary not found for audio decoding.";
            return result;
        }

        try
        {
            // 1. Decode audio to 22050 Hz mono 16-bit PCM float samples via FFmpeg stdout
            const int sampleRate = 22050;
            var samples = DecodeAudioToMonoSamples(audioFilePath, resolvedFfmpeg, sampleRate, maxSeconds: 180);

            if (samples.Count < sampleRate * 5)
            {
                result.Message = "Audio file too short or failed to decode.";
                return result;
            }

            result.DurationSeconds = (double)samples.Count / sampleRate;

            // 2. Compute onset energy envelope using Short-Time Spectral Energy (50 fps)
            const int frameSize = 1024;
            const int hopSize = 441; // ~50 frames per second
            var fps = (double)sampleRate / hopSize;

            var onsetEnvelope = ComputeOnsetEnvelope(samples, frameSize, hopSize);
            if (onsetEnvelope.Length < 100)
            {
                result.Bpm = 120.0;
                result.Success = true;
                result.Message = "Audio too short for precise tempo analysis; defaulted to 120 BPM.";
                return result;
            }

            // 3. Autocorrelate onset envelope over plausible tempo range (70 - 200 BPM)
            var detectedBpm = DetectBpmFromAutocorrelation(onsetEnvelope, fps);
            result.Bpm = Math.Round(detectedBpm, 1);

            // 4. Detect downbeat offset (time to first prominent beat)
            var beatIntervalSec = 60.0 / result.Bpm;
            var downbeatMs = DetectDownbeatOffsetMs(onsetEnvelope, fps, beatIntervalSec);
            result.DownbeatOffsetMs = Math.Round(downbeatMs, 1);

            // 5. Extract significant onsets for dynamic note generation
            ExtractOnsets(onsetEnvelope, fps, result.OnsetTimestampsSeconds, result.OnsetEnergies);

            result.Success = true;
            result.Message = $"Detected {result.Bpm} BPM (Downbeat offset: {result.DownbeatOffsetMs} ms, {result.OnsetTimestampsSeconds.Count} note transients).";
            _logger?.LogInfo($"[AUDIO ANALYSIS] {Path.GetFileName(audioFilePath)}: {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[AUDIO ANALYSIS] Failed for {audioFilePath}: {ex.Message}");
            result.Message = "Analysis error: " + ex.Message;
            result.Bpm = 120.0;
            return result;
        }
    }

    private static List<float> DecodeAudioToMonoSamples(string audioPath, string ffmpegPath, int targetRate, int maxSeconds)
    {
        var samples = new List<float>();

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = $"-v error -i \"{audioPath}\" -t {maxSeconds} -ar {targetRate} -ac 1 -f s16le -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return samples;

        using var stream = proc.StandardOutput.BaseStream;
        var buffer = new byte[8192];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < bytesRead - 1; i += 2)
            {
                short pcm = (short)(buffer[i] | (buffer[i + 1] << 8));
                samples.Add(pcm / 32768.0f);
            }
        }

        proc.WaitForExit(15000);
        return samples;
    }

    private static float[] ComputeOnsetEnvelope(List<float> samples, int frameSize, int hopSize)
    {
        var numFrames = (samples.Count - frameSize) / hopSize;
        if (numFrames <= 0) return Array.Empty<float>();

        var energy = new float[numFrames];
        for (int f = 0; f < numFrames; f++)
        {
            int start = f * hopSize;
            float sumSquares = 0;
            for (int i = 0; i < frameSize; i++)
            {
                var val = samples[start + i];
                sumSquares += val * val;
            }
            energy[f] = MathF.Sqrt(sumSquares / frameSize);
        }

        // Half-wave rectified first difference (spectral flux / energy surge)
        var onsets = new float[numFrames];
        for (int f = 1; f < numFrames; f++)
        {
            var diff = energy[f] - energy[f - 1];
            onsets[f] = diff > 0 ? diff : 0;
        }

        return onsets;
    }

    private static double DetectBpmFromAutocorrelation(float[] envelope, double fps)
    {
        // Plausible tempo bounds in Guitar Hero / modern rock
        const double minBpm = 70.0;
        const double maxBpm = 200.0;

        int minLag = (int)(fps * 60.0 / maxBpm);
        int maxLag = (int)(fps * 60.0 / minBpm);

        if (maxLag >= envelope.Length) maxLag = envelope.Length - 1;
        if (minLag >= maxLag) return 120.0;

        double bestCorr = -1;
        int bestLag = (int)(fps * 60.0 / 120.0);

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            int count = envelope.Length - lag;
            for (int i = 0; i < count; i++)
            {
                sum += envelope[i] * envelope[i + lag];
            }
            double corr = sum / count;

            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        var detectedBpm = (fps * 60.0) / bestLag;

        // Check for common half-tempo / double-tempo octave errors
        // Normalize typical rock songs into the 85 - 170 BPM sweet spot
        if (detectedBpm < 80.0) detectedBpm *= 2.0;
        else if (detectedBpm > 185.0) detectedBpm /= 2.0;

        return detectedBpm;
    }

    private static double DetectDownbeatOffsetMs(float[] envelope, double fps, double beatIntervalSec)
    {
        int searchFrames = (int)(fps * Math.Min(4.0, beatIntervalSec * 2));
        if (searchFrames > envelope.Length) searchFrames = envelope.Length;

        int peakFrame = 0;
        float maxVal = 0;

        for (int i = 0; i < searchFrames; i++)
        {
            if (envelope[i] > maxVal)
            {
                maxVal = envelope[i];
                peakFrame = i;
            }
        }

        var timeSec = peakFrame / fps;
        return timeSec * 1000.0;
    }

    private static void ExtractOnsets(float[] envelope, double fps, List<double> timestamps, List<float> energies)
    {
        if (envelope.Length == 0) return;

        float maxVal = envelope.Max();
        if (maxVal <= 0.0001f) return;

        float threshold = maxVal * 0.18f; // Dynamic onset sensitivity threshold

        for (int i = 2; i < envelope.Length - 2; i++)
        {
            var cur = envelope[i];
            if (cur > threshold &&
                cur > envelope[i - 1] && cur > envelope[i - 2] &&
                cur >= envelope[i + 1] && cur >= envelope[i + 2])
            {
                timestamps.Add(i / fps);
                energies.Add(cur / maxVal);
            }
        }
    }
}
