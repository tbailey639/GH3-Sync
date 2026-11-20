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

    private static readonly Lazy<byte[]> AirhornWav = new(BuildAirhornWav);
    private static readonly Lazy<byte[]> StadiumCheerWav = new(BuildStadiumCheerWav);
    private static readonly Lazy<byte[]> BooWav = new(BuildBooWav);
    private static readonly Lazy<byte[]> DrumFillWav = new(BuildDrumFillWav);
    private static readonly Lazy<byte[]> LightningWav = new(BuildLightningWav);
    private static readonly Lazy<byte[]> SoloLickWav = new(BuildSoloLickWav);

    public static void PlayStarPowerChime() => PlayWavAsync(StarPowerWav.Value);
    public static void PlayCompilePowerChord() => PlayWavAsync(PowerChordWav.Value);
    public static void PlayNoteMissClunk() => PlayWavAsync(MissClunkWav.Value);
    public static void PlayFcFanfare() => PlayWavAsync(FcFanfareWav.Value);
    public static void PlayMetronomeClick() => PlayWavAsync(MetronomeClickWav.Value, forceEvenIfSfxMuted: true);

    public static void PlayAirhorn() => PlayWavAsync(AirhornWav.Value);
    public static void PlayStadiumCheer() => PlayWavAsync(StadiumCheerWav.Value);
    public static void PlayBoo() => PlayWavAsync(BooWav.Value);
    public static void PlayDrumFill() => PlayWavAsync(DrumFillWav.Value);
    public static void PlayLightning() => PlayWavAsync(LightningWav.Value);
    public static void PlaySoloLick() => PlayWavAsync(SoloLickWav.Value);

    public static void PlayHypeSound(string? sfxType)
    {
        switch ((sfxType ?? "airhorn").ToLowerInvariant())
        {
            case "airhorn":
                PlayAirhorn();
                break;
            case "cheer":
                PlayStadiumCheer();
                break;
            case "boo":
                PlayBoo();
                break;
            case "drum":
                PlayDrumFill();
                break;
            case "lightning":
                PlayLightning();
                break;
            case "solo":
                PlaySoloLick();
                break;
            default:
                PlayAirhorn();
                break;
        }
    }

    private static void PlayWavAsync(byte[] wavBytes, bool forceEvenIfSfxMuted = false)
    {
        if (!forceEvenIfSfxMuted && !AppConfig.Current.EnableSoundEffects) return;
        try
        {
            // CRITICAL: When Guitar Hero III (GH3.exe) is running, background waveOut calls can cause
            // DirectX/DirectSound device contention and freeze the game engine. SFX is silenced while GH3 runs.
            if (System.Diagnostics.Process.GetProcessesByName("GH3").Length > 0) return;
        }
        catch { }

        _ = Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(wavBytes, writable: false);
                using var sp = new SoundPlayer(ms);
                sp.Play();
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

    private static byte[] BuildAirhornWav()
    {
        const int sr = 22050;
        const double durationSec = 0.85;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        double[] freqs = { 466.16, 587.33, 698.46, 932.33 };

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double blastEnv = 0.0;
            if (t >= 0.00 && t < 0.12)
            {
                double bt = t;
                blastEnv = Math.Min(1.0, bt / 0.01) * Math.Min(1.0, (0.12 - t) / 0.015);
            }
            else if (t >= 0.18 && t < 0.30)
            {
                double bt = t - 0.18;
                blastEnv = Math.Min(1.0, bt / 0.01) * Math.Min(1.0, (0.30 - t) / 0.015);
            }
            else if (t >= 0.36 && t < 0.82)
            {
                double bt = t - 0.36;
                blastEnv = Math.Min(1.0, bt / 0.01) * Math.Exp(-bt * 2.8);
            }

            if (blastEnv <= 0.001) continue;

            double pitchMod = 1.0 - (t > 0.6 ? (t - 0.6) * 0.25 : 0.0);
            double v = 0;
            foreach (var f in freqs)
            {
                double phase = 2 * Math.PI * (f * pitchMod) * t;
                double s = Math.Sin(phase) + 0.35 * Math.Sin(2 * phase) + 0.15 * Math.Sin(3 * phase);
                v += s;
            }
            v = Math.Tanh(v * 1.8) * blastEnv * 0.45;
            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildStadiumCheerWav()
    {
        const int sr = 22050;
        const double durationSec = 1.25;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        var rng = new Random(42);
        double b0 = 0, b1 = 0, b2 = 0;

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double env = Math.Min(1.0, t / 0.25) * Math.Exp(-Math.Max(0.0, t - 0.65) * 2.2);

            double white = rng.NextDouble() * 2.0 - 1.0;
            b0 = 0.95 * b0 + white * 0.05;
            b1 = 0.90 * b1 + white * 0.10;
            b2 = 0.80 * b2 + white * 0.20;
            double noise = (b0 + b1 + b2) * 0.8;

            double cheerTone = 0;
            double[] cheerFreqs = { 329.63, 415.30, 493.88, 659.25 };
            foreach (var cf in cheerFreqs)
            {
                cheerTone += Math.Sin(2 * Math.PI * cf * (1.0 + 0.05 * Math.Sin(2 * Math.PI * 4.5 * t)) * t);
            }
            cheerTone = (cheerTone / cheerFreqs.Length) * 0.35;

            double clap = 0;
            if (rng.NextDouble() < 0.003) clap = (rng.NextDouble() * 2.0 - 1.0) * 0.6;

            double v = (noise * 0.55 + cheerTone + clap) * env * 0.40;
            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildBooWav()
    {
        const int sr = 22050;
        const double durationSec = 1.1;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        var rng = new Random(99);

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double freq = 135.0 - (t / durationSec) * 50.0;
            double env = Math.Min(1.0, t / 0.15) * Math.Exp(-Math.Max(0.0, t - 0.5) * 2.5);

            double s = Math.Sin(2 * Math.PI * freq * t)
                     + 0.5 * Math.Sin(2 * Math.PI * (freq * 1.5) * t)
                     + 0.25 * (rng.NextDouble() * 2.0 - 1.0);

            double v = Math.Tanh(s * 1.6) * env * 0.42;
            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildDrumFillWav()
    {
        const int sr = 22050;
        const double durationSec = 0.95;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        var rng = new Random(1234);

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double v = 0.0;

            if (t >= 0.0 && t < 0.22)
            {
                double dt = t;
                double f = 220.0 * Math.Exp(-dt * 18.0);
                v += Math.Sin(2 * Math.PI * f * dt) * Math.Exp(-dt * 12.0) * 0.45;
            }
            if (t >= 0.22 && t < 0.44)
            {
                double dt = t - 0.22;
                double f = 165.0 * Math.Exp(-dt * 18.0);
                v += Math.Sin(2 * Math.PI * f * dt) * Math.Exp(-dt * 12.0) * 0.48;
            }
            if (t >= 0.44 && t < 0.66)
            {
                double dt = t - 0.44;
                double tone = Math.Sin(2 * Math.PI * 180.0 * dt) * Math.Exp(-dt * 20.0);
                double snap = (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-dt * 28.0);
                v += (tone * 0.35 + snap * 0.65) * 0.50;
            }
            if (t >= 0.66)
            {
                double dt = t - 0.66;
                double sizzle = (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-dt * 5.5);
                v += sizzle * 0.55;
            }

            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildLightningWav()
    {
        const int sr = 22050;
        const double durationSec = 1.0;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];
        var rng = new Random(777);

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            double v = 0;

            if (t < 0.08)
            {
                double crackNoise = (rng.NextDouble() * 2.0 - 1.0) * Math.Exp(-t * 30.0);
                double chirpFreq = 3200.0 * Math.Exp(-t * 60.0);
                double chirp = Math.Sin(2 * Math.PI * chirpFreq * t) * Math.Exp(-t * 25.0);
                v += (crackNoise * 0.7 + chirp * 0.5);
            }

            if (t >= 0.04)
            {
                double dt = t - 0.04;
                double rumbleEnv = Math.Min(1.0, dt / 0.1) * Math.Exp(-dt * 2.4);
                double lowNoise = (rng.NextDouble() * 2.0 - 1.0);
                double subTone = Math.Sin(2 * Math.PI * 65.0 * dt) + 0.5 * Math.Sin(2 * Math.PI * 45.0 * dt);
                v += (subTone * 0.4 + lowNoise * 0.3) * rumbleEnv;
            }

            v = Math.Tanh(v * 1.5) * 0.48;
            pcm[i] = (short)Math.Clamp(v * 32767.0, -32767, 32767);
        }
        return WrapPcm16MonoWav(pcm, sr);
    }

    private static byte[] BuildSoloLickWav()
    {
        const int sr = 22050;
        double[] noteFreqs = { 659.25, 783.99, 880.0, 987.77, 1318.51 };
        double noteStep = 0.09;
        double sustainTail = 0.55;
        double durationSec = noteStep * 4 + sustainTail;
        int samples = (int)(sr * durationSec);
        var pcm = new short[samples];

        for (int i = 0; i < samples; i++)
        {
            double t = i / (double)sr;
            int noteIndex = Math.Min(noteFreqs.Length - 1, (int)(t / noteStep));
            double noteTime = t - (noteIndex * noteStep);
            double freq = noteFreqs[noteIndex];

            if (noteIndex == noteFreqs.Length - 1)
            {
                double vibrato = Math.Sin(2 * Math.PI * 6.5 * noteTime) * 18.0;
                freq += vibrato;
            }

            double env = Math.Min(1.0, noteTime / 0.008) * Math.Exp(-noteTime * (noteIndex == noteFreqs.Length - 1 ? 2.5 : 8.0));
            double wave = Math.Sin(2 * Math.PI * freq * t) + 0.45 * Math.Sin(4 * Math.PI * freq * t) + 0.2 * Math.Sin(6 * Math.PI * freq * t);

            double distorted = Math.Tanh(wave * 3.8) * env * 0.40;
            pcm[i] = (short)Math.Clamp(distorted * 32767.0, -32767, 32767);
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
