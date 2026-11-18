using System;
using System.Diagnostics;
using System.IO;
using System.Media;
using System.Threading.Tasks;

namespace GH3_Sync;

public class AudioWaveformProfile
{
    public string SourcePath { get; set; } = "";
    public double DurationSeconds { get; set; }
    public double[] Peaks { get; set; } = Array.Empty<double>(); // 180 normalized amplitude buckets [0..1]
}

/// <summary>
/// 1) Extracts real audio amplitude waveforms via bundled FFmpeg (ArgumentList-only) so the bottom
///    Audio Preview Bar draws a real waveform scrubber & beat-reactive Rock Meter / 5-Fret Spectrum!
/// 2) Synthesizes crisp in-memory retro guitar audio cues (Star Power, Power-Chord, Missed Clunk, FC Fanfare, Metronome)
///    with zero external files and zero copyrighted assets.
/// </summary>
public static class AudioWaveformAndSfxService
{
    private static readonly Lazy<byte[]> StarPowerWav = new(() => BuildChordWav(new[] { 440.0, 554.37, 659.25, 880.0 }, 0.42, 0.28, bright: true));
    private static readonly Lazy<byte[]> PowerChordWav = new(() => BuildChordWav(new[] { 164.81, 246.94, 329.63 }, 0.48, 0.30, bright: false));
    private static readonly Lazy<byte[]> MissClunkWav = new(() => BuildChordWav(new[] { 98.0, 103.8 }, 0.13, 0.24, bright: false));
    private static readonly Lazy<byte[]> FcFanfareWav = new(() => BuildArpeggioWav(new[] { 523.25, 659.25, 783.99, 1046.5 }, 0.11, 0.26));
    private static readonly Lazy<byte[]> MetronomeClickWav = new(() => BuildClickWav(1100.0, 0.032, 0.42));

    public static void PlayStarPowerChime() => PlayWavAsync(StarPowerWav.Value);
    public static void PlayCompilePowerChord() => PlayWavAsync(PowerChordWav.Value);
    public static void PlayNoteMissClunk() => PlayWavAsync(MissClunkWav.Value);
    public static void PlayFcFanfare() => PlayWavAsync(FcFanfareWav.Value);
    public static void PlayMetronomeClick() => PlayWavAsync(MetronomeClickWav.Value, forceEvenIfSfxMuted: true);

    private static void PlayWavAsync(byte[] wavBytes, bool forceEvenIfSfxMuted = false)
    {
        if (!forceEvenIfSfxMuted && !AppConfig.Current.EnableSoundEffects) return;
        _ = Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(wavBytes, writable: false);
                using var sp = new SoundPlayer(ms);
                sp.PlaySync();
            }
            catch { }
        });
    }

    /// <summary>
    /// Fast-reads mono 8kHz 16-bit PCM via bundled FFmpeg into 180 RMS peak buckets for waveform rendering & beat reactivity.
    /// </summary>
    public static async Task<AudioWaveformProfile?> ExtractWaveformAsync(string audioPath, int bucketCount = 180)
    {
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath)) return null;
        return await Task.Run(() =>
        {
            try
            {
                var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
                if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) return null;

                var psi = new ProcessStartInfo
                {
                    FileName = ff,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("-v");
                psi.ArgumentList.Add("error");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(audioPath);
                psi.ArgumentList.Add("-ac");
                psi.ArgumentList.Add("1");
                psi.ArgumentList.Add("-ar");
                psi.ArgumentList.Add("4000");
                psi.ArgumentList.Add("-f");
                psi.ArgumentList.Add("s16le");
                psi.ArgumentList.Add("pipe:1");

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                using var pcmMs = new MemoryStream();
                proc.StandardOutput.BaseStream.CopyTo(pcmMs);
                proc.WaitForExit(15000);

                var bytes = pcmMs.ToArray();
                int totalSamples = bytes.Length / 2;
                if (totalSamples < 400) return null;

                var peaks = new double[bucketCount];
                int samplesPerBucket = Math.Max(1, totalSamples / bucketCount);
                double maxRms = 0.0001;

                for (int b = 0; b < bucketCount; b++)
                {
                    int start = b * samplesPerBucket;
                    int end = Math.Min(totalSamples, start + samplesPerBucket);
                    double sumSq = 0;
                    int cnt = 0;
                    for (int s = start; s < end; s++)
                    {
                        short sample = (short)(bytes[s * 2] | (bytes[s * 2 + 1] << 8));
                        double f = sample / 32768.0;
                        sumSq += f * f;
                        cnt++;
                    }
                    double rms = cnt > 0 ? Math.Sqrt(sumSq / cnt) : 0;
                    peaks[b] = rms;
                    if (rms > maxRms) maxRms = rms;
                }

                for (int b = 0; b < bucketCount; b++)
                    peaks[b] = Math.Clamp(peaks[b] / maxRms, 0.05, 1.0);

                return new AudioWaveformProfile
                {
                    SourcePath = audioPath,
                    DurationSeconds = totalSamples / 4000.0,
                    Peaks = peaks
                };
            }
            catch
            {
                return null;
            }
        });
    }

    public static double SampleEnergyAt(AudioWaveformProfile? profile, double positionSec, double durationSec)
    {
        if (profile == null || profile.Peaks.Length == 0 || durationSec <= 0.1) return 0.5;
        double ratio = Math.Clamp(positionSec / durationSec, 0.0, 0.999);
        int idx = Math.Clamp((int)(ratio * profile.Peaks.Length), 0, profile.Peaks.Length - 1);
        return profile.Peaks[idx];
    }

    private static byte[] BuildChordWav(double[] freqs, double durationSec, double gain, bool bright)
    {
        const int sr = 22050;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double env = Math.Min(1.0, t / 0.012) * Math.Exp(-t * (bright ? 4.8 : 5.5));
            double v = 0;
            foreach (var f in freqs)
            {
                double s = Math.Sin(2 * Math.PI * f * t);
                if (!bright) s = Math.Tanh(s * 1.9); // subtle warm overdrive
                else s += 0.35 * Math.Sin(4 * Math.PI * f * t);
                v += s;
            }
            v = (v / freqs.Length) * env * gain;
            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildArpeggioWav(double[] freqs, double noteDurSec, double gain)
    {
        const int sr = 22050;
        int noteSamples = (int)(sr * noteDurSec);
        int total = noteSamples * freqs.Length + (int)(sr * 0.22);
        var pcm = new short[total];
        for (int n = 0; n < freqs.Length; n++)
        {
            int start = n * noteSamples;
            for (int i = 0; i < total - start; i++)
            {
                double t = i / (double)sr;
                double env = Math.Min(1.0, t / 0.008) * Math.Exp(-t * 6.5);
                double s = (Math.Sin(2 * Math.PI * freqs[n] * t) + 0.3 * Math.Sin(4 * Math.PI * freqs[n] * t)) * env * gain;
                int mixed = pcm[start + i] + (int)(s * 32767.0);
                pcm[start + i] = (short)Math.Clamp(mixed, -32767, 32767);
            }
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildClickWav(double freq, double durationSec, double gain)
    {
        const int sr = 22050;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double env = Math.Exp(-t * 95.0);
            double s = Math.Sin(2 * Math.PI * freq * t) * env * gain;
            pcm[i] = (short)Math.Clamp(s * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] WrapPcm16MonoWav(short[] samples, int sampleRate)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        int dataBytes = samples.Length * 2;
        bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataBytes);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write((short)1); // Mono
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        bw.Write(dataBytes);
        foreach (var s in samples) bw.Write(s);
        return ms.ToArray();
    }
}
