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
    public List<double> BeatTimestampsSeconds { get; set; } = new();
    public List<TranscribedNote> TranscribedNotes { get; set; } = new();
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// Pure-C# DSP Audio Analysis Engine for ONOGAMI Song Creator.
/// Uses Cooley-Tukey FFT guitar-bandpass spectral flux (130 Hz - 3200 Hz),
/// adaptive onset peaking, autocorrelation BPM detection, and YIN pitch estimation
/// so even without the AI engine installed, notes follow real musical pitch & rhythm.
/// </summary>
public class AudioAnalysisService
{
    private readonly SyncLogger? _logger;

    public AudioAnalysisService(SyncLogger? logger = null)
    {
        _logger = logger;
    }

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
            const int sampleRate = 22050;
            var samples = DecodeAudioToMonoSamples(audioFilePath, resolvedFfmpeg, sampleRate, maxSeconds: 420);

            if (samples.Length < sampleRate * 3)
            {
                result.Message = "Audio file too short or failed to decode.";
                return result;
            }

            result.DurationSeconds = (double)samples.Length / sampleRate;

            // 1. Compute Guitar-Bandpass (130 Hz - 3200 Hz) Spectral Flux using FFT
            const int fftSize = 1024;
            const int hopSize = 256; // ~86.13 fps (~11.6 ms precision!)
            double fps = (double)sampleRate / hopSize;

            var (flux, bandEnergy, chordfulness) = ComputeGuitarSpectralFlux(samples, sampleRate, fftSize, hopSize);

            if (flux.Length < 100)
            {
                result.Bpm = 120.0;
                result.Success = true;
                result.Message = "Audio too short for full analysis; defaulted to 120 BPM.";
                return result;
            }

            // 2. Autocorrelate flux for accurate BPM & run Ellis Dynamic Programming Beat Tracker
            result.Bpm = Math.Round(DetectBpmFromAutocorrelation(flux, fps), 1);
            double beatIntervalSec = 60.0 / result.Bpm;
            result.DownbeatOffsetMs = Math.Round(DetectDownbeatOffsetMs(flux, fps, beatIntervalSec), 1);
            result.BeatTimestampsSeconds = TrackDynamicBeats(flux, fps, result.Bpm);

            // 3. Adaptive local peak picking for guitar transients
            var peakFrames = PickAdaptiveOnsets(flux, fps);

            // 4. Estimate actual musical MIDI pitch (YIN) & sustain length at every onset!
            for (int p = 0; p < peakFrames.Count; p++)
            {
                int frame = peakFrames[p];
                double startSec = frame / fps;
                float energy = flux[frame];

                result.OnsetTimestampsSeconds.Add(startSec);
                result.OnsetEnergies.Add(energy);

                int sampleStart = frame * hopSize;
                // Offset slightly forward (15ms) past the pick attack transient into the pitched ring-out
                int pitchSampleStart = Math.Min(samples.Length - 1024, sampleStart + (int)(sampleRate * 0.015));
                if (pitchSampleStart < 0) pitchSampleStart = 0;

                double freqHz = EstimatePitchYin(samples, pitchSampleStart, 1024, sampleRate);
                int midiPitch = FrequencyToMidi(freqHz);

                // Intelligent Musical Sustain Tracking:
                // Guitar pick transient is an impulsive attack spike; smooth string resonance settles at ~12-18% of attack.
                // Track sustain until string vibrations genuinely decay or a major new pick transient begins.
                int nextPeakFrame = flux.Length - 1;
                for (int nextP = p + 1; nextP < peakFrames.Count; nextP++)
                {
                    // Ignore tiny background noise / cymbal fluctuations (< 0.28 energy); only stop sustain on strong new guitar pick onsets
                    if (flux[peakFrames[nextP]] >= 0.28f || nextP == p + 1)
                    {
                        nextPeakFrame = peakFrames[nextP];
                        break;
                    }
                }

                float floorEnergy = bandEnergy.Length > 0 ? (bandEnergy.Take(Math.Min(300, bandEnergy.Length)).Average() * 0.7f) : 0.005f;
                float sustainThreshold = Math.Max(bandEnergy[frame] * 0.14f, floorEnergy);
                int endFrame = frame + 4;
                while (endFrame < nextPeakFrame && endFrame < bandEnergy.Length && bandEnergy[endFrame] >= sustainThreshold)
                {
                    endFrame++;
                }

                // Generous natural ring-out: at least 0.18s for any pitched onset, or full tracked ring
                double endSec = Math.Max(startSec + 0.18, endFrame / fps);

                result.TranscribedNotes.Add(new TranscribedNote
                {
                    StartSec = startSec,
                    EndSec = endSec,
                    MidiPitch = midiPitch,
                    Amplitude = Math.Clamp(energy, 0.2f, 1.0f)
                });

                // If spectral chordfulness is high (strong power chord harmonics), emit upper 5th note
                if (chordfulness[frame] > 0.65f && energy > 0.50f)
                {
                    result.TranscribedNotes.Add(new TranscribedNote
                    {
                        StartSec = startSec,
                        EndSec = endSec,
                        MidiPitch = Math.Min(88, midiPitch + 7),
                        Amplitude = Math.Clamp(energy * 0.85f, 0.2f, 1.0f)
                    });
                }
            }

            result.Success = true;
            result.Message = $"Detected {result.Bpm} BPM ({result.OnsetTimestampsSeconds.Count} pitched guitar onsets, {result.DurationSeconds:F1}s).";
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

    private static float[] DecodeAudioToMonoSamples(string audioPath, string ffmpegPath, int targetRate, int maxSeconds)
    {
        var samples = new List<float>(targetRate * 180);

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
        if (proc == null) return Array.Empty<float>();

        using var stream = proc.StandardOutput.BaseStream;
        var buffer = new byte[16384];
        int bytesRead;

        while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < bytesRead - 1; i += 2)
            {
                short pcm = (short)(buffer[i] | (buffer[i + 1] << 8));
                samples.Add(pcm / 32768.0f);
            }
        }

        proc.WaitForExit(20000);
        return samples.ToArray();
    }

    private static (float[] Flux, float[] BandEnergy, float[] Chordfulness) ComputeGuitarSpectralFlux(
        float[] samples, int sampleRate, int fftSize, int hopSize)
    {
        int numFrames = Math.Max(0, (samples.Length - fftSize) / hopSize);
        var flux = new float[numFrames];
        var bandEnergy = new float[numFrames];
        var chordfulness = new float[numFrames];
        if (numFrames == 0) return (flux, bandEnergy, chordfulness);

        // Precompute Hann window
        var window = new float[fftSize];
        for (int i = 0; i < fftSize; i++)
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / (fftSize - 1)));

        // Guitar harmonic frequency bins: 130 Hz (just above kick fundamental) to 3200 Hz (below hi-hat hiss)
        int minBin = Math.Max(1, (int)(130.0 * fftSize / sampleRate));
        int maxBin = Math.Min(fftSize / 2 - 1, (int)(3200.0 * fftSize / sampleRate));

        var prevMag = new float[fftSize / 2];
        var re = new float[fftSize];
        var im = new float[fftSize];

        float maxFlux = 1e-6f;

        for (int f = 0; f < numFrames; f++)
        {
            int start = f * hopSize;
            for (int i = 0; i < fftSize; i++)
            {
                re[i] = samples[start + i] * window[i];
                im[i] = 0f;
            }

            FftInPlace(re, im);

            float frameFlux = 0f;
            float frameBandSum = 0f;
            int activePeaks = 0;
            float maxBinMag = 1e-6f;

            for (int b = minBin; b <= maxBin; b++)
            {
                float mag = MathF.Sqrt(re[b] * re[b] + im[b] * im[b]);
                // Log-compress magnitude so loud & quiet sections both respond musically
                float logMag = MathF.Log10(1f + 10f * mag);
                float diff = logMag - prevMag[b];
                if (diff > 0f) frameFlux += diff;
                prevMag[b] = logMag;

                frameBandSum += mag;
                if (mag > maxBinMag) maxBinMag = mag;
            }

            // Estimate spectral richness (chords spread energy across many bins vs single-note spike)
            float avgMag = frameBandSum / Math.Max(1, maxBin - minBin + 1);
            for (int b = minBin + 1; b < maxBin; b++)
            {
                float mag = MathF.Sqrt(re[b] * re[b] + im[b] * im[b]);
                if (mag > maxBinMag * 0.35f) activePeaks++;
            }

            flux[f] = frameFlux;
            bandEnergy[f] = avgMag;
            chordfulness[f] = Math.Clamp(activePeaks / 18.0f, 0f, 1f);
            if (frameFlux > maxFlux) maxFlux = frameFlux;
        }

        // Normalize flux to 0..1
        for (int f = 0; f < numFrames; f++) flux[f] /= maxFlux;
        return (flux, bandEnergy, chordfulness);
    }

    private static List<int> PickAdaptiveOnsets(float[] flux, double fps)
    {
        var peaks = new List<int>();
        int halfWin = Math.Max(6, (int)(fps * 0.18)); // ~360ms local sliding average window
        int minGapFrames = Math.Max(4, (int)(fps * 0.085)); // min 85ms between guitar strikes
        int lastPeak = -minGapFrames;

        for (int i = 2; i < flux.Length - 2; i++)
        {
            float val = flux[i];
            if (val < 0.07f) continue;

            // Local maximum check
            if (val <= flux[i - 1] || val < flux[i + 1] || val <= flux[i - 2] || val < flux[i + 2])
                continue;

            if (i - lastPeak < minGapFrames) continue;

            // Compute local mean around frame i (adapts to quiet verses vs loud choruses!)
            int lo = Math.Max(0, i - halfWin);
            int hi = Math.Min(flux.Length - 1, i + halfWin);
            float sum = 0f;
            for (int k = lo; k <= hi; k++) sum += flux[k];
            float localMean = sum / (hi - lo + 1);

            if (val > localMean * 1.65f + 0.04f)
            {
                peaks.Add(i);
                lastPeak = i;
            }
        }

        return peaks;
    }

    /// <summary>
    /// YIN pitch estimator (de Cheveigné & Kawahara) restricted to guitar range (80 Hz - 1100 Hz).
    /// </summary>
    private static double EstimatePitchYin(float[] samples, int start, int windowSize, int sampleRate)
    {
        int half = windowSize / 2;
        if (start + windowSize >= samples.Length) return 220.0;

        int minTau = Math.Max(2, sampleRate / 1100); // ~1100 Hz (C#6)
        int maxTau = Math.Min(half - 1, sampleRate / 80); // ~80 Hz (E2 low guitar string)

        var diff = new float[maxTau + 1];
        for (int tau = 1; tau <= maxTau; tau++)
        {
            float s = 0f;
            for (int j = 0; j < half; j++)
            {
                float d = samples[start + j] - samples[start + j + tau];
                s += d * d;
            }
            diff[tau] = s;
        }

        // Cumulative mean normalized difference function (CMNDF)
        var cmndf = new float[maxTau + 1];
        cmndf[0] = 1f;
        float runningSum = 0f;
        for (int tau = 1; tau <= maxTau; tau++)
        {
            runningSum += diff[tau];
            cmndf[tau] = runningSum > 1e-8f ? diff[tau] * tau / runningSum : 1f;
        }

        const float yinThreshold = 0.22f;
        int bestTau = -1;

        for (int tau = minTau; tau < maxTau; tau++)
        {
            if (cmndf[tau] < yinThreshold)
            {
                while (tau + 1 < maxTau && cmndf[tau + 1] < cmndf[tau])
                    tau++;
                bestTau = tau;
                break;
            }
        }

        if (bestTau < 0)
        {
            float minVal = float.MaxValue;
            bestTau = minTau;
            for (int tau = minTau; tau <= maxTau; tau++)
            {
                if (cmndf[tau] < minVal)
                {
                    minVal = cmndf[tau];
                    bestTau = tau;
                }
            }
        }

        // Parabolic interpolation for sub-sample frequency precision
        double betterTau = bestTau;
        if (bestTau > minTau && bestTau < maxTau)
        {
            float s0 = cmndf[bestTau - 1];
            float s1 = cmndf[bestTau];
            float s2 = cmndf[bestTau + 1];
            float denom = 2f * (2f * s1 - s2 - s0);
            if (MathF.Abs(denom) > 1e-6f)
                betterTau = bestTau + (s2 - s0) / denom;
        }

        return betterTau > 0 ? sampleRate / betterTau : 220.0;
    }

    private static int FrequencyToMidi(double freqHz)
    {
        if (freqHz <= 20.0) return 48;
        int midi = (int)Math.Round(69.0 + 12.0 * Math.Log2(freqHz / 440.0));
        return Math.Clamp(midi, 36, 88);
    }

    private static double DetectBpmFromAutocorrelation(float[] envelope, double fps)
    {
        const double minBpm = 72.0;
        const double maxBpm = 196.0;

        int minLag = (int)(fps * 60.0 / maxBpm);
        int maxLag = (int)(fps * 60.0 / minBpm);

        if (maxLag >= envelope.Length) maxLag = envelope.Length - 1;
        if (minLag >= maxLag) return 120.0;

        // Analyze up to first 90 seconds for fast, accurate autocorrelation
        int limit = Math.Min(envelope.Length, (int)(fps * 90.0));
        double bestCorr = -1;
        int bestLag = (int)(fps * 60.0 / 120.0);

        for (int lag = minLag; lag <= maxLag; lag++)
        {
            double sum = 0;
            int count = limit - lag;
            if (count <= 0) continue;
            for (int i = 0; i < count; i++)
                sum += envelope[i] * envelope[i + lag];

            // Slight perceptual weight favoring 95-155 BPM (avoids half/double octave traps)
            double candBpm = (fps * 60.0) / lag;
            double weight = 1.0 - 0.12 * Math.Abs(Math.Log2(candBpm / 122.0));
            double corr = (sum / count) * weight;

            if (corr > bestCorr)
            {
                bestCorr = corr;
                bestLag = lag;
            }
        }

        double bpm = (fps * 60.0) / bestLag;
        if (bpm < 78.0) bpm *= 2.0;
        else if (bpm > 186.0) bpm /= 2.0;
        return bpm;
    }

    private static double DetectDownbeatOffsetMs(float[] envelope, double fps, double beatIntervalSec)
    {
        int searchFrames = Math.Min(envelope.Length, (int)(fps * Math.Min(4.0, beatIntervalSec * 2)));
        int peakFrame = 0;
        float maxVal = 0f;
        for (int i = 0; i < searchFrames; i++)
        {
            if (envelope[i] > maxVal)
            {
                maxVal = envelope[i];
                peakFrame = i;
            }
        }
        return (peakFrame / fps) * 1000.0;
    }

    /// <summary>
    /// Ellis Dynamic Programming Beat Tracker: recovers exact timestamp of every beat
    /// across the song, naturally tracking tempo drift on unquantized live recordings.
    /// </summary>
    private static List<double> TrackDynamicBeats(float[] flux, double fps, double avgBpm)
    {
        var beats = new List<double>();
        if (flux.Length < 100 || avgBpm <= 30) return beats;

        double period = fps * 60.0 / avgBpm;
        int minStep = Math.Max(4, (int)Math.Round(period * 0.65));
        int maxStep = Math.Min(flux.Length - 1, (int)Math.Round(period * 1.55));
        if (minStep >= maxStep) return beats;

        int n = flux.Length;
        var cumScore = new double[n];
        var backlink = new int[n];
        Array.Fill(backlink, -1);

        // Precompute Gaussian log-tempo transition penalty (tightness = 100)
        const double tightness = 100.0;
        var penalty = new double[maxStep + 1];
        for (int tau = minStep; tau <= maxStep; tau++)
        {
            double logRatio = Math.Log2(tau / period);
            penalty[tau] = tightness * logRatio * logRatio;
        }

        for (int t = 0; t < n; t++)
        {
            double bestPrev = 0.0;
            int bestIdx = -1;

            int lo = Math.Max(0, t - maxStep);
            int hi = t - minStep;
            for (int prev = lo; prev <= hi; prev++)
            {
                double cand = cumScore[prev] - penalty[t - prev];
                if (bestIdx < 0 || cand > bestPrev)
                {
                    bestPrev = cand;
                    bestIdx = prev;
                }
            }

            cumScore[t] = flux[t] + (bestIdx >= 0 ? bestPrev : 0.0);
            backlink[t] = bestIdx;
        }

        // Backtrack from strongest beat near the end of the track
        int tailStart = Math.Max(0, n - maxStep * 2);
        int endFrame = tailStart;
        double bestEndScore = double.NegativeInfinity;
        for (int t = tailStart; t < n; t++)
        {
            if (cumScore[t] > bestEndScore)
            {
                bestEndScore = cumScore[t];
                endFrame = t;
            }
        }

        var frames = new List<int>();
        int cur = endFrame;
        while (cur >= 0)
        {
            frames.Add(cur);
            cur = backlink[cur];
        }
        frames.Reverse();

        foreach (var f in frames)
            beats.Add(f / fps);

        return beats;
    }

    private static void FftInPlace(float[] re, float[] im)
    {
        int n = re.Length;
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
            int m = n >> 1;
            while (m >= 1 && j >= m)
            {
                j -= m;
                m >>= 1;
            }
            j += m;
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            float ang = -2f * MathF.PI / len;
            float wLenRe = MathF.Cos(ang);
            float wLenIm = MathF.Sin(ang);
            int halfLen = len >> 1;

            for (int i = 0; i < n; i += len)
            {
                float wRe = 1f, wIm = 0f;
                for (int k = 0; k < halfLen; k++)
                {
                    int u = i + k;
                    int v = u + halfLen;
                    float tRe = re[v] * wRe - im[v] * wIm;
                    float tIm = re[v] * wIm + im[v] * wRe;
                    re[v] = re[u] - tRe;
                    im[v] = im[u] - tIm;
                    re[u] += tRe;
                    im[u] += tIm;

                    float nextWRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextWRe;
                }
            }
        }
    }
}
