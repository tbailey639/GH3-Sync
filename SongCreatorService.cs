using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GH3_Sync;

public class SongAudioMixConfig
{
    public string AudioMixMode { get; set; } = "StudioMaster"; // StudioMaster, IsolatedStems, HybridBlend
    public string LeadFocus { get; set; } = "Guitar";          // Guitar, KeysSynth, Bass, Vocals, MultiLead
}

public class SongAiAnalysisCache
{
    public string InstrumentFocusRequested { get; set; } = "Auto";
    public string ResolvedLeadFocus { get; set; } = "Guitar";
    public string StemEnergySummary { get; set; } = "";
    public double PreciseBpm { get; set; } = 120.0;
    public double SongDurationSeconds { get; set; } = 180.0;
    public bool IsStudioConstantTempo { get; set; } = true;
    public bool IsGuitarDominant { get; set; } = true;
    public List<double> PreciseBeatTimes { get; set; } = new();
    public List<double> MeasureEnergies { get; set; } = new();
    public List<DetectedMusicalSection> DetectedSections { get; set; } = new();
    public List<TranscribedNote> TranscribedGuitarNotes { get; set; } = new();
    public List<TranscribedNote> TranscribedBassNotes { get; set; } = new();
}

public class SongCreationRequest
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Year { get; set; } = DateTime.Now.Year;
    public string Genre { get; set; } = "Rock";
    public string Charter { get; set; } = "Mad Scientist";
    public string Venue { get; set; } = "The Desert Rock Arena";
    public int DiffGuitar { get; set; } = 4;
    public int DiffRhythm { get; set; } = 3;
    public int PreviewStartMs { get; set; } = 5000;
    public double Bpm { get; set; } = 130.0;
    public bool AutoDetectBpm { get; set; } = true;
    public bool UseVariableTempoMap { get; set; } = true;
    public string ChartFeel { get; set; } = "Standard";         // Casual, Standard, Shredder
    public string InstrumentFocus { get; set; } = "Auto";       // Auto, Guitar, Keys / Synth / Sax, Bass, Vocals
    public string AudioMixMode { get; set; } = "Auto";          // Auto, StudioMaster, IsolatedStems, HybridBlend
    public double GuitarVolumeDb { get; set; } = 0.0;
    public double BandVolumeDb { get; set; } = 0.0;
    public bool SeparateStemsWithDemucs { get; set; } = false;
    public bool AutoGenerateChart { get; set; } = false;
    public bool AutoCompile { get; set; } = true;
    public bool NormalizeLoudness { get; set; } = true;         // FFmpeg EBU R128 -14 LUFS loudness matching
    public double TrimStartSeconds { get; set; } = 0.0;         // Optional intro trim (seconds)
    public double TrimEndSeconds { get; set; } = 0.0;           // Optional outro trim (0 = full track)
    public double FadeOutSeconds { get; set; } = 0.0;           // Optional smooth fade-out at end
    public int AudioOffsetMs { get; set; } = 0;                 // Optional ±ms chart sync nudge
    public bool UseCachedAiAnalysis { get; set; } = true;       // Instant 5-second re-chart cache when available
    public bool EnableStadiumChants { get; set; } = true;       // AI Stadium crowd sing-along & chants
    public byte[]? CoverArtBytes { get; set; }                  // Chosen album cover artwork bytes
    public string? CoverArtPath { get; set; }                   // Optional local cover artwork image file path
}

public class CreatedSongResult
{
    public bool Success { get; set; }
    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public string GhprojPath { get; set; } = "";
    public string Message { get; set; } = "";
    public bool CreatedWithOnogamiCreator { get; set; } = true;
    public bool CompiledSuccessfully { get; set; }
    public string CompilationMessage { get; set; } = "";
    public string PakFilePath { get; set; } = "";
    public string FsbFilePath { get; set; } = "";
    public double DetectedBpm { get; set; }
    public bool StemsSeparated { get; set; }
    public int TranscribedNoteCount { get; set; }
    public int BassNoteCount { get; set; }
    public int RepeatedRiffsMatched { get; set; }
    public int ForcedMarkersApplied { get; set; }
    public string ChartingEngineUsed { get; set; } = "";
    public string AudioModeUsed { get; set; } = "";
    public string StemEnergySummary { get; set; } = "";
}

public class SongCreatorService
{
    private readonly string _defaultSongsDir;
    private readonly HoneycombCompilerService? _compiler;
    private readonly AudioAnalysisService? _audioAnalysis;
    private readonly DemucsService? _demucs;
    private readonly Gh3Service _gh3;
    private readonly AiStadiumChantsService _stadiumChants = new();

    public SongCreatorService(
        HoneycombCompilerService? compiler = null,
        AudioAnalysisService? audioAnalysis = null,
        DemucsService? demucs = null,
        string? songsDir = null,
        Gh3Service? gh3 = null)
    {
        _compiler = compiler;
        _audioAnalysis = audioAnalysis;
        _demucs = demucs;
        _gh3 = gh3 ?? new Gh3Service();
        _defaultSongsDir = ResolveSongsDirectory(songsDir);
    }

    public static string ResolveSongsDirectory(string? preferred = null)
    {
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            try
            {
                if (!Directory.Exists(preferred)) Directory.CreateDirectory(preferred);
                return preferred;
            }
            catch { }
        }

        if (!string.IsNullOrWhiteSpace(AppConfig.Current.CustomSongsPath) && Directory.Exists(AppConfig.Current.CustomSongsPath))
            return AppConfig.Current.CustomSongsPath;

        if (!string.IsNullOrWhiteSpace(AppConfig.Current.GamePath) && Directory.Exists(AppConfig.Current.GamePath))
        {
            var gameCustoms = Path.Combine(AppConfig.Current.GamePath, "Custom_Songs");
            try
            {
                if (!Directory.Exists(gameCustoms)) Directory.CreateDirectory(gameCustoms);
                return gameCustoms;
            }
            catch { }
        }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var docsSongs = Path.Combine(docs, "Guitar Hero Songs");
        try
        {
            if (!Directory.Exists(docsSongs)) Directory.CreateDirectory(docsSongs);
            return docsSongs;
        }
        catch { }

        var appDataSongs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "CreatedSongs");
        if (!Directory.Exists(appDataSongs)) Directory.CreateDirectory(appDataSongs);
        return appDataSongs;
    }

    public Task<CreatedSongResult> CreateSongFromFilesAsync(
        SongCreationRequest meta,
        string audioFilePath,
        string? chartFilePath = null,
        Action<int, string>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
            return Task.FromResult(new CreatedSongResult { Success = false, Message = "Audio file not found." });

        var audioBytes = File.ReadAllBytes(audioFilePath);
        byte[]? chartBytes = null;
        string? chartName = null;
        if (!string.IsNullOrWhiteSpace(chartFilePath) && File.Exists(chartFilePath))
        {
            chartBytes = File.ReadAllBytes(chartFilePath);
            chartName = Path.GetFileName(chartFilePath);
        }

        return CreateSongFromOnogamiMethodAsync(meta, audioBytes, Path.GetFileName(audioFilePath), chartBytes, chartName, onProgress);
    }

    public async Task<CreatedSongResult> CreateSongFromOnogamiMethodAsync(
        SongCreationRequest meta,
        byte[] audioBytes,
        string audioFileName,
        byte[]? chartBytes = null,
        string? chartFileName = null,
        Action<int, string>? onProgress = null)
    {
        if (string.IsNullOrWhiteSpace(meta.Title))
            return new CreatedSongResult { Success = false, Message = "Song title is required." };
        if (string.IsNullOrWhiteSpace(meta.Artist))
            return new CreatedSongResult { Success = false, Message = "Artist is required." };
        if (audioBytes == null || audioBytes.Length == 0)
            return new CreatedSongResult { Success = false, Message = "Audio file is required." };

        try
        {
            onProgress?.Invoke(5, "Creating ONOGAMI song project directory...");

            var cleanTitle = SanitizeFileName(meta.Title);
            var cleanArtist = SanitizeFileName(meta.Artist);
            var dirName = $"{cleanArtist} - {cleanTitle}";
            var targetDir = Path.Combine(_defaultSongsDir, dirName);
            Directory.CreateDirectory(targetDir);

            // 1. Save Audio File + extract embedded album cover art if present
            var audioExt = Path.GetExtension(audioFileName);
            if (string.IsNullOrEmpty(audioExt)) audioExt = ".mp3";
            var audioFullPath = Path.Combine(targetDir, $"song{audioExt.ToLowerInvariant()}");
            File.WriteAllBytes(audioFullPath, audioBytes);

            // 1a. Persist Chosen Album Artwork (or extract embedded art from audio if not supplied)
            try
            {
                var albumArtJpg = Path.Combine(targetDir, "album.jpg");
                var albumArtPng = Path.Combine(targetDir, "album.png");

                if (meta.CoverArtBytes != null && meta.CoverArtBytes.Length > 1024)
                {
                    File.WriteAllBytes(albumArtJpg, meta.CoverArtBytes);
                    File.WriteAllBytes(albumArtPng, meta.CoverArtBytes);
                }
                else if (!string.IsNullOrWhiteSpace(meta.CoverArtPath) && File.Exists(meta.CoverArtPath))
                {
                    File.Copy(meta.CoverArtPath, albumArtJpg, true);
                    File.Copy(meta.CoverArtPath, albumArtPng, true);
                }
                else if (!File.Exists(albumArtJpg))
                {
                    var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
                    if (!string.IsNullOrEmpty(ff) && File.Exists(ff))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ff,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        psi.ArgumentList.Add("-y");
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(audioFullPath);
                        psi.ArgumentList.Add("-an");
                        psi.ArgumentList.Add("-frames:v");
                        psi.ArgumentList.Add("1");
                        psi.ArgumentList.Add("-q:v");
                        psi.ArgumentList.Add("3");
                        psi.ArgumentList.Add(albumArtJpg);
                        using var p = System.Diagnostics.Process.Start(psi);
                        p?.WaitForExit(5000);
                        if (File.Exists(albumArtJpg) && new FileInfo(albumArtJpg).Length >= 512)
                        {
                            try { File.Copy(albumArtJpg, albumArtPng, true); } catch { }
                        }
                        else if (File.Exists(albumArtJpg))
                        {
                            try { File.Delete(albumArtJpg); } catch { }
                        }
                    }
                }
            }
            catch { }

            // 1b. Optional Audio Trim, Fade-Out, & Auto-Loudness Normalization (-14 LUFS) via bundled FFmpeg
            bool needTrim = meta.TrimStartSeconds > 0.1 || meta.TrimEndSeconds > 1.0;
            bool needFade = meta.FadeOutSeconds > 0.2;
            bool needLoudnorm = meta.NormalizeLoudness;
            if (needTrim || needFade || needLoudnorm)
            {
                onProgress?.Invoke(9, needTrim
                    ? "Applying audio trim, fade-out & -14 LUFS loudness normalization..."
                    : "Normalizing audio loudness (-14 LUFS) to match official GH3 volume...");
                var processedPath = TryPreprocessAudioWithFfmpeg(audioFullPath, targetDir, meta.TrimStartSeconds, meta.TrimEndSeconds, meta.FadeOutSeconds, needLoudnorm);
                if (!string.IsNullOrEmpty(processedPath) && File.Exists(processedPath))
                {
                    audioFullPath = processedPath;
                }
            }

            // 2. Analyze Full Mix for BPM & Duration
            onProgress?.Invoke(12, "Analyzing audio tempo (BPM) & guitar frequencies...");
            AudioAnalysisResult? mixAnalysis = _audioAnalysis?.AnalyzeAudio(audioFullPath);
            double exactBpm = meta.Bpm > 20 ? meta.Bpm : 120.0;
            double songDuration = mixAnalysis?.DurationSeconds ?? 180.0;

            if (mixAnalysis != null && mixAnalysis.Success && (meta.AutoDetectBpm || meta.Bpm <= 0))
            {
                exactBpm = mixAnalysis.Bpm;
                meta.Bpm = exactBpm;
                if (mixAnalysis.DownbeatOffsetMs > 0 && meta.PreviewStartMs == 5000)
                {
                    meta.PreviewStartMs = (int)Math.Max(5000, mixAnalysis.DownbeatOffsetMs + 12000);
                }
            }

            // 3. Optional Demucs 6-Stem Isolation + Librosa Beat/Section Engine + Multi-Instrument Basic Pitch AI
            bool stemsSeparated = false;
            string guitarAudioPath = ""; // Blank guitar pad by default for clean Studio Master audio (Beat It style)
            string rhythmAudioPath = "";
            string backingAudioPath = audioFullPath;
            List<TranscribedNote> bestNotes = new();
            List<TranscribedNote> bassNotes = new();
            List<double>? bestBeats = mixAnalysis?.BeatTimestampsSeconds;
            List<double>? measureEnergies = null;
            List<DetectedMusicalSection>? detectedSections = null;
            bool isStudioConstantTempo = true;
            bool isGuitarDominant = true;
            string resolvedLeadFocus = meta.InstrumentFocus ?? "Auto";
            string stemEnergySummary = "";
            string chartingEngine = "Uploaded Chart/MIDI";

            var aiCachePath = Path.Combine(targetDir, "gh3sync_ai_cache.json");
            bool usedAiCache = false;
            bool wantOnlyStudioOrCached = meta.UseCachedAiAnalysis && File.Exists(aiCachePath);

            if (wantOnlyStudioOrCached &&
                (meta.AudioMixMode.Equals("StudioMaster", StringComparison.OrdinalIgnoreCase) || !meta.SeparateStemsWithDemucs ||
                 (meta.AudioMixMode.Equals("Auto", StringComparison.OrdinalIgnoreCase))))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<SongAiAnalysisCache>(File.ReadAllText(aiCachePath));
                    bool sameFocus = cached != null && string.Equals(cached.InstrumentFocusRequested, meta.InstrumentFocus ?? "Auto", StringComparison.OrdinalIgnoreCase);
                    bool canReuseAudio = cached != null && (!cached.IsGuitarDominant || meta.AudioMixMode.Equals("StudioMaster", StringComparison.OrdinalIgnoreCase) || !meta.SeparateStemsWithDemucs);
                    if (cached != null && sameFocus && canReuseAudio && cached.TranscribedGuitarNotes.Count >= 12)
                    {
                        onProgress?.Invoke(62, "Instant Re-Chart Cache hit! Skipping Demucs & Basic Pitch (~5s build)...");
                        usedAiCache = true;
                        isGuitarDominant = cached.IsGuitarDominant;
                        isStudioConstantTempo = cached.IsStudioConstantTempo;
                        resolvedLeadFocus = cached.ResolvedLeadFocus;
                        stemEnergySummary = cached.StemEnergySummary;
                        measureEnergies = cached.MeasureEnergies;
                        detectedSections = cached.DetectedSections;
                        if (cached.PreciseBpm >= 55 && cached.PreciseBpm <= 230 && (meta.AutoDetectBpm || meta.Bpm <= 0))
                        {
                            exactBpm = cached.PreciseBpm;
                            meta.Bpm = exactBpm;
                        }
                        if (cached.PreciseBeatTimes.Count >= 8) bestBeats = cached.PreciseBeatTimes;
                        bestNotes = cached.TranscribedGuitarNotes;
                        bassNotes = cached.TranscribedBassNotes;
                        chartingEngine = $"ONOGAMI AI v2 [Instant Cache] ({resolvedLeadFocus})";
                    }
                }
                catch { }
            }

            if (!usedAiCache && meta.SeparateStemsWithDemucs && _demucs != null && _demucs.GetStatus().IsInstalled)
            {
                onProgress?.Invoke(18, "Separating Guitar, Bass, Drums, Vocals & Piano stems (Demucs 6-Stem)...");
                var stemsDir = Path.Combine(targetDir, "stems");
                var demucsRes = await _demucs.SeparateStemsAsync(
                    audioFullPath,
                    stemsDir,
                    (pct, step) =>
                    {
                        int mapped = 18 + (int)(pct * 0.50);
                        onProgress?.Invoke(mapped, step);
                    },
                    default,
                    meta.InstrumentFocus ?? "Auto");

                if (demucsRes.Success && !string.IsNullOrEmpty(demucsRes.GuitarTrackPath))
                {
                    stemsSeparated = true;
                    isGuitarDominant = demucsRes.IsGuitarDominant;
                    isStudioConstantTempo = demucsRes.IsStudioConstantTempo;
                    resolvedLeadFocus = demucsRes.ResolvedLeadFocus;
                    stemEnergySummary = demucsRes.StemEnergySummary;
                    measureEnergies = demucsRes.MeasureEnergies;
                    detectedSections = demucsRes.DetectedSections;

                    // Prefer Librosa precision beat tracker & BPM on drums/mix!
                    if (demucsRes.PreciseBpm >= 55 && demucsRes.PreciseBpm <= 230 && (meta.AutoDetectBpm || meta.Bpm <= 0))
                    {
                        exactBpm = demucsRes.PreciseBpm;
                        meta.Bpm = exactBpm;
                    }
                    if (demucsRes.PreciseBeatTimes.Count >= 8)
                    {
                        bestBeats = demucsRes.PreciseBeatTimes;
                    }

                    if (demucsRes.TranscribedGuitarNotes.Count >= 12)
                    {
                        bestNotes = demucsRes.TranscribedGuitarNotes;
                        chartingEngine = $"ONOGAMI AI v2 ({resolvedLeadFocus})";
                    }
                    if (demucsRes.TranscribedBassNotes.Count >= 16)
                    {
                        bassNotes = demucsRes.TranscribedBassNotes;
                    }

                    // Persist lightweight ~80 KB AI cache for 5-second future re-chart tweaks!
                    try
                    {
                        var cacheObj = new SongAiAnalysisCache
                        {
                            InstrumentFocusRequested = meta.InstrumentFocus ?? "Auto",
                            ResolvedLeadFocus = resolvedLeadFocus,
                            StemEnergySummary = stemEnergySummary,
                            PreciseBpm = exactBpm,
                            SongDurationSeconds = songDuration,
                            IsStudioConstantTempo = isStudioConstantTempo,
                            IsGuitarDominant = isGuitarDominant,
                            PreciseBeatTimes = bestBeats ?? new(),
                            MeasureEnergies = measureEnergies ?? new(),
                            DetectedSections = detectedSections ?? new(),
                            TranscribedGuitarNotes = bestNotes,
                            TranscribedBassNotes = bassNotes
                        };
                        File.WriteAllText(aiCachePath, JsonSerializer.Serialize(cacheObj));
                    }
                    catch { }
                }
            }

            // Fallback to built-in FFT/YIN pitched notes from full mix
            if (bestNotes.Count == 0 && mixAnalysis != null && mixAnalysis.TranscribedNotes.Count > 0)
            {
                bestNotes = mixAnalysis.TranscribedNotes;
                chartingEngine = "Built-in Pitch Analyzer";
            }

            // Resolve Audio Mix Mode (Auto picks Isolated Stems for Guitar-Dominant Rock, and Studio Master for Non-Guitar/Multi-Lead!)
            string reqAudioMode = (meta.AudioMixMode ?? "Auto").Trim();
            string effectiveAudioMode;
            if (reqAudioMode.Equals("StudioMaster", StringComparison.OrdinalIgnoreCase) || reqAudioMode.Contains("Studio", StringComparison.OrdinalIgnoreCase))
            {
                effectiveAudioMode = "StudioMaster";
            }
            else if (reqAudioMode.Equals("HybridBlend", StringComparison.OrdinalIgnoreCase) || reqAudioMode.Contains("Hybrid", StringComparison.OrdinalIgnoreCase))
            {
                effectiveAudioMode = stemsSeparated ? "HybridBlend" : "StudioMaster";
            }
            else if (reqAudioMode.Equals("IsolatedStems", StringComparison.OrdinalIgnoreCase) || reqAudioMode.Contains("Isolated", StringComparison.OrdinalIgnoreCase))
            {
                effectiveAudioMode = stemsSeparated ? "IsolatedStems" : "StudioMaster";
            }
            else
            {
                // AUTO mode: rock/metal guitar tracks get true mute-on-miss stems (ONOGAMI style);
                // non-guitar / multi-lead tracks (like Tadow, jazz, pop, synth, hip-hop) get crystal-clear Studio Master audio (Beat It style)!
                bool focusIsGuitar = resolvedLeadFocus.Equals("Guitar", StringComparison.OrdinalIgnoreCase);
                effectiveAudioMode = (stemsSeparated && isGuitarDominant && focusIsGuitar) ? "IsolatedStems" : "StudioMaster";
            }

            string normalizedLeadKey = resolvedLeadFocus.ToLowerInvariant() switch
            {
                var s when s.Contains("multi") => "MultiLead",
                var s when s.Contains("keys") || s.Contains("synth") || s.Contains("sax") => "KeysSynth",
                var s when s.Contains("bass") => "Bass",
                var s when s.Contains("vocal") => "Vocals",
                _ => "Guitar"
            };

            // Persist audio mix choice so Honeycomb's SaveProject() never wipes it on future re-compiles!
            var audioCfgPath = Path.Combine(targetDir, "gh3sync_audio.json");
            File.WriteAllText(audioCfgPath, JsonSerializer.Serialize(new SongAudioMixConfig
            {
                AudioMixMode = effectiveAudioMode,
                LeadFocus = normalizedLeadKey
            }, new JsonSerializerOptions { WriteIndented = true }));

            // 4. Handle Chart (Use uploaded OR build ONOGAMI AI v2 chart with Riff Memory, HOPOs, Chords & Co-Op Bass!)
            onProgress?.Invoke(72, "Building 5-fret Guitar Hero III chart (Riff Memory + HOPOs + Chords + Co-Op Bass)...");
            string chartFullPath;
            string midiFullPath = Path.Combine(targetDir, "notes.mid");
            var chartStats = new ChartBuildStats();

            if (chartBytes != null && chartBytes.Length > 0 && !meta.AutoGenerateChart)
            {
                var chartExt = Path.GetExtension(chartFileName ?? ".chart").ToLowerInvariant();
                chartFullPath = Path.Combine(targetDir, $"notes{chartExt}");
                File.WriteAllBytes(chartFullPath, chartBytes);
                chartingEngine = "Uploaded Chart/MIDI";

                if (chartExt == ".mid" || chartExt == ".midi")
                {
                    File.Copy(chartFullPath, midiFullPath, overwrite: true);
                }
                else if (chartExt == ".chart")
                {
                    TryConvertChart(chartFullPath);
                }
            }
            else
            {
                // Apply optional ±ms audio-chart sync offset cleanly to note timestamps
                if (meta.AudioOffsetMs != 0)
                {
                    double deltaSec = meta.AudioOffsetMs / 1000.0;
                    bestNotes = bestNotes.Select(n => new TranscribedNote
                    {
                        StartSec = Math.Max(0, n.StartSec + deltaSec),
                        EndSec = Math.Max(0.05, n.EndSec + deltaSec),
                        MidiPitch = n.MidiPitch,
                        Amplitude = n.Amplitude
                    }).ToList();
                    bassNotes = bassNotes.Select(n => new TranscribedNote
                    {
                        StartSec = Math.Max(0, n.StartSec + deltaSec),
                        EndSec = Math.Max(0.05, n.EndSec + deltaSec),
                        MidiPitch = n.MidiPitch,
                        Amplitude = n.Amplitude
                    }).ToList();
                }

                chartFullPath = Path.Combine(targetDir, "notes.chart");
                var chartContent = MidiToGh3ChartMapper.BuildChartFromNotes(
                    meta,
                    bestNotes,
                    exactBpm,
                    songDuration,
                    chartingEngine,
                    bestBeats,
                    bassNotes: bassNotes,
                    isStudioConstantTempo: isStudioConstantTempo,
                    measureEnergies: measureEnergies,
                    detectedSections: detectedSections,
                    statsOut: chartStats);
                File.WriteAllText(chartFullPath, chartContent);
                TryConvertChart(chartFullPath);
            }

            var chosenMidiPath = File.Exists(midiFullPath) ? midiFullPath : "";

            // 5. Generate song.ini
            var iniPath = Path.Combine(targetDir, "song.ini");
            var iniSb = new StringBuilder();
            iniSb.AppendLine("[song]");
            iniSb.AppendLine($"name = {meta.Title}");
            iniSb.AppendLine($"artist = {meta.Artist}");
            iniSb.AppendLine($"album = {meta.Album}");
            iniSb.AppendLine($"genre = {meta.Genre}");
            iniSb.AppendLine($"year = {meta.Year}");
            iniSb.AppendLine($"charter = {meta.Charter}");
            iniSb.AppendLine($"diff_guitar = {meta.DiffGuitar}");
            iniSb.AppendLine($"diff_rhythm = {meta.DiffRhythm}");
            iniSb.AppendLine($"preview_start_time = {meta.PreviewStartMs}");
            iniSb.AppendLine("icon = onogami_creator");
            File.WriteAllText(iniPath, iniSb.ToString());

            // 5b. Optional AI Stadium Crowd Sing-Along & Chants (Chorus & melodic vocal formant choir)
            if (meta.EnableStadiumChants && !string.IsNullOrEmpty(backingAudioPath) && File.Exists(backingAudioPath))
            {
                onProgress?.Invoke(77, "Synthesizing authentic stadium concert crowd sing-along choir...");
                await _stadiumChants.GenerateStadiumChantsAsync(backingAudioPath, chartFullPath, exactBpm, (p, m) => onProgress?.Invoke(p, m));
                // Note: The concert crowd choir is saved to song_crowd.wav and packed as the dynamic rhythm stream,
                // keeping backingAudioPath untouched at pristine studio fidelity!
            }

            // 6. Generate .ghproj
            var ghprojName = $"{meta.Title}.ghproj";
            var ghprojFullPath = Path.Combine(targetDir, ghprojName);
            var cleanSongId = SanitizeSongId(cleanTitle);

            int safeYear = Math.Clamp(meta.Year, 1900, 2100);
            int safeGuitarTier = Math.Clamp(meta.DiffGuitar, 1, 10);
            int safeBassTier = Math.Clamp(meta.DiffRhythm, 1, 10);

            var crowdStemWav = Path.Combine(targetDir, "song_crowd.wav");
            string effectiveCrowd = File.Exists(crowdStemWav) ? crowdStemWav : "";
            string effectiveRhythm = !string.IsNullOrEmpty(rhythmAudioPath) ? rhythmAudioPath : effectiveCrowd;

            var projData = new
            {
                GhprojVersion = 1,
                gameSelect = "GH3",
                platformSelect = "PC",
                songName = cleanSongId,
                chartAuthor = meta.Charter,
                title = meta.Title,
                artist = meta.Artist,
                artistTextCustom = "",
                coverArtist = "",
                album = meta.Album,
                kickPath = "",
                snarePath = "",
                cymbalsPath = "",
                tomsPath = "",
                guitarPath = guitarAudioPath,
                bassPath = effectiveRhythm,
                vocalsPath = "",
                backingPaths = backingAudioPath,
                crowdPath = effectiveCrowd,
                previewAudioPath = audioFullPath,
                guitarPathGh3 = guitarAudioPath,
                rhythmPathGh3 = effectiveRhythm,
                backingPathsGh3 = backingAudioPath,
                coopGuitarPath = guitarAudioPath,
                coopRhythmPath = effectiveRhythm,
                coopBackingPaths = backingAudioPath,
                crowdPathGh3 = effectiveCrowd,
                previewAudioPathGh3 = audioFullPath,
                midiPathGh3 = chosenMidiPath,
                perfPathGh3 = "",
                skaPathGh3 = "",
                songScriptPathGh3 = "",
                midiPath = chosenMidiPath,
                perfPath = "",
                skaPath = "",
                lipsyncPath = "",
                songScriptPath = "",
                compilePath = targetDir,
                projectPath = ghprojFullPath,
                gameIcon = "onogami_creator",
                gameCategory = "",
                ghprojFromLoad = ghprojFullPath,
                bandWtde = "",
                modsSubfolder = "",
                gSkeleton = "Default",
                bSkeleton = "Default",
                dSkeleton = "Default",
                vSkeleton = "Default",
                ghwtDrumkit = "Modern Rock",
                gh5Drumkit = "Modern Rock",
                ghworDrumkit = "Modern Rock",
                artistText = -1,
                songYear = safeYear,
                coverYear = 2024,
                wtGenre = 0,
                gh5Genre = 0,
                worGenre = 0,
                previewStart = Math.Max(0, meta.PreviewStartMs),
                previewEnd = 30000,
                hmxHopoVal = 170,
                skaSourceGh3 = 0,
                venueSourceGh3 = 0,
                countoffGh3 = 0,
                vocalGenderGh3 = 0,
                bassistSelect = 0,
                skaSource = 0,
                venueSource = 2,
                countoff = 0,
                vocalGender = 0,
                hopoMode = 0,
                beat8thLow = 1,
                beat8thHigh = 180,
                beat16thLow = 1,
                beat16thHigh = 120,
                bandTier = 1,
                guitarTier = safeGuitarTier,
                bassTier = safeBassTier,
                drumsTier = 1,
                vocalsTier = 1,
                guitarCareerTier = 0,
                bassCareerTier = 0,
                drumsCareerTier = 0,
                vocalsCareerTier = 0,
                bandCareerTier = 0,
                gtrVolumeGh3 = meta.GuitarVolumeDb,
                bandVolumeGh3 = meta.BandVolumeDb,
                vocalScrollSpeed = 1.0,
                vocalTuningCents = 0.0,
                sustainThreshold = 0.5,
                overallVolume = 0.0,
                previewVolume = -5.0,
                previewVolumeGh3 = -5.0,
                isCover = false,
                isP2Rhythm = true,
                isCoopAudio = false,
                useRenderedPreview = true,
                useRenderedPreviewGh3 = true,
                setEnd = false,
                useBeatTrack = false,
                guitarMic = false,
                bassMic = false,
                useNewClips = false,
                modernStrobes = false,
                easyOpen = false
            };

            File.WriteAllText(ghprojFullPath, JsonSerializer.Serialize(projData, new JsonSerializerOptions { WriteIndented = true }));
            AppConfig.RegisterCreatedSong(cleanSongId, targetDir);

            string audioModeLabel = effectiveAudioMode switch
            {
                "IsolatedStems" => "Isolated Lead Stem (Mute-on-Miss)",
                "HybridBlend" => "Hybrid Studio Blend (Soft Ducking)",
                _ => "Studio Master Audio (Cleanest Fidelity)"
            };

            var result = new CreatedSongResult
            {
                Success = true,
                SongId = cleanSongId,
                Title = meta.Title,
                Artist = meta.Artist,
                ProjectDirectory = targetDir,
                GhprojPath = ghprojFullPath,
                CreatedWithOnogamiCreator = true,
                DetectedBpm = exactBpm,
                StemsSeparated = stemsSeparated,
                TranscribedNoteCount = chartStats.ExpertNoteCount > 0 ? chartStats.ExpertNoteCount : bestNotes.Count,
                BassNoteCount = chartStats.BassNoteCount,
                RepeatedRiffsMatched = chartStats.RepeatedRiffsMatched,
                ForcedMarkersApplied = chartStats.ForcedMarkersApplied,
                ChartingEngineUsed = chartingEngine,
                AudioModeUsed = audioModeLabel,
                StemEnergySummary = stemEnergySummary,
                Message = $"Successfully created '{meta.Title}' ({chartingEngine}, {exactBpm:F1} BPM, {audioModeLabel})!"
            };

            // 7. Automated Bundled Honeycomb Compilation
            if (meta.AutoCompile && _compiler != null)
            {
                onProgress?.Invoke(82, "Compiling chart PAK & encrypted FSB audio soundbank into Guitar Hero III...");
                var compileResult = await _compiler.CompileSongProjectAsync(ghprojFullPath);
                result.CompiledSuccessfully = compileResult.Success;
                result.CompilationMessage = compileResult.Message;
                result.PakFilePath = compileResult.PakFilePath;
                result.FsbFilePath = compileResult.FsbFilePath;

                if (compileResult.Success)
                {
                    if (!string.IsNullOrEmpty(compileResult.SongId))
                        result.SongId = compileResult.SongId;

                    var extras = new List<string> { chartingEngine, $"{exactBpm:F1} BPM", audioModeLabel };
                    if (chartStats.RepeatedRiffsMatched > 0) extras.Add($"{chartStats.RepeatedRiffsMatched} repeating riffs locked");
                    if (chartStats.BassNoteCount > 0) extras.Add($"Co-Op Bass ({chartStats.BassNoteCount} notes)");
                    result.Message = $"Compiled & installed '{meta.Title}' directly into Guitar Hero III! ({string.Join(" • ", extras)})";
                }
                else
                {
                    result.Message = $"Project created at '{targetDir}'. Compiler note: {compileResult.Message}";
                }
            }

            onProgress?.Invoke(100, "Done!");
            return result;
        }
        catch (Exception ex)
        {
            return new CreatedSongResult
            {
                Success = false,
                Message = $"Failed to create song: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Edits Title, Artist, Album, Year, Genre, Difficulties, and Guitar/Band Volumes inside an existing
    /// custom song's .ghproj & song.ini, then optionally recompiles directly into BetterGH3.
    /// </summary>
    public async Task<(bool Success, string Message)> UpdateSongMetadataAndRecompileAsync(
        string projectDir,
        string title,
        string artist,
        string album,
        int year,
        string genre,
        int diffGuitar,
        int diffRhythm,
        double guitarVolumeDb,
        double bandVolumeDb,
        bool recompile,
        string? charter = null,
        int? venueSource = null)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir))
            return (false, "Project folder not found.");

        try
        {
            var ghproj = Directory.GetFiles(projectDir, "*.ghproj").FirstOrDefault();
            if (!string.IsNullOrEmpty(ghproj) && File.Exists(ghproj))
            {
                var node = System.Text.Json.Nodes.JsonNode.Parse(
                    File.ReadAllText(ghproj),
                    documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject();
                if (node != null)
                {
                    node["title"] = title;
                    node["artist"] = artist;
                    node["album"] = album;
                    node["songYear"] = Math.Clamp(year, 1900, 2100);
                    node["guitarTier"] = Math.Clamp(diffGuitar, 1, 10);
                    node["bassTier"] = Math.Clamp(diffRhythm, 1, 10);
                    node["gtrVolumeGh3"] = guitarVolumeDb;
                    node["bandVolumeGh3"] = bandVolumeDb;
                    if (!string.IsNullOrWhiteSpace(charter))
                        node["charter"] = charter.Trim();
                    if (venueSource.HasValue)
                        node["venueSource"] = Math.Clamp(venueSource.Value, 0, 8);
                    File.WriteAllText(ghproj, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }

            var iniPath = Path.Combine(projectDir, "song.ini");
            var iniLines = new List<string>
            {
                "[song]",
                $"name = {title}",
                $"artist = {artist}",
                $"album = {album}",
                $"genre = {genre}",
                $"year = {year}",
                $"charter = {(string.IsNullOrWhiteSpace(charter) ? "Mad Scientist" : charter.Trim())}",
                $"diff_guitar = {diffGuitar}",
                $"diff_rhythm = {diffRhythm}",
                "icon = onogami_creator"
            };
            File.WriteAllText(iniPath, string.Join(Environment.NewLine, iniLines));

            if (recompile && _compiler != null && !string.IsNullOrEmpty(ghproj))
            {
                var comp = await _compiler.CompileSongProjectAsync(ghproj);
                return (comp.Success, comp.Success ? $"Updated & recompiled '{title}' into Guitar Hero III!" : comp.Message);
            }

            return (true, $"Saved updated metadata for '{title}'.");
        }
        catch (Exception ex)
        {
            return (false, $"Failed updating song info: {ex.Message}");
        }
    }

    /// <summary>
    /// Batch-updates Genre, Year, Charter, Guitar/Band Volume, and GH3 Stage Venue across multiple selected songs.
    /// </summary>
    public async Task<(int Updated, int Recompiled, string Summary)> BulkUpdateMetadataAsync(
        List<CustomSongInfo> songs,
        string? genre = null,
        int? year = null,
        string? charter = null,
        double? guitarVolumeDb = null,
        double? bandVolumeDb = null,
        int? venueSource = null,
        bool recompile = false,
        Action<int, int, string>? onProgress = null)
    {
        int updated = 0;
        int recompiled = 0;
        for (int i = 0; i < songs.Count; i++)
        {
            var s = songs[i];
            if (string.IsNullOrWhiteSpace(s.ProjectDirectory) || !Directory.Exists(s.ProjectDirectory)) continue;
            onProgress?.Invoke(i + 1, songs.Count, $"Updating '{s.Title}'...");

            try
            {
                var ghproj = Directory.GetFiles(s.ProjectDirectory, "*.ghproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(ghproj) && File.Exists(ghproj))
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(ghproj),
                        documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })?.AsObject();
                    if (node != null)
                    {
                        if (year.HasValue) node["songYear"] = Math.Clamp(year.Value, 1900, 2100);
                        if (!string.IsNullOrWhiteSpace(charter)) node["charter"] = charter.Trim();
                        if (guitarVolumeDb.HasValue) node["gtrVolumeGh3"] = guitarVolumeDb.Value;
                        if (bandVolumeDb.HasValue) node["bandVolumeGh3"] = bandVolumeDb.Value;
                        if (venueSource.HasValue) node["venueSource"] = Math.Clamp(venueSource.Value, 0, 8);
                        File.WriteAllText(ghproj, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                }

                var iniPath = Path.Combine(s.ProjectDirectory, "song.ini");
                var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string> { "[song]" };
                void SetIni(string k, string v)
                {
                    var idx = lines.FindIndex(l => l.TrimStart().StartsWith(k + " ", StringComparison.OrdinalIgnoreCase) || l.TrimStart().StartsWith(k + "=", StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) lines[idx] = $"{k} = {v}";
                    else lines.Add($"{k} = {v}");
                }
                if (!string.IsNullOrWhiteSpace(genre)) SetIni("genre", genre.Trim());
                if (year.HasValue) SetIni("year", year.Value.ToString());
                if (!string.IsNullOrWhiteSpace(charter)) SetIni("charter", charter.Trim());
                File.WriteAllText(iniPath, string.Join(Environment.NewLine, lines));
                updated++;

                if (recompile && _compiler != null && !string.IsNullOrEmpty(ghproj))
                {
                    var comp = await _compiler.CompileSongProjectAsync(ghproj);
                    if (comp.Success) recompiled++;
                }
            }
            catch { }
        }

        var summary = recompile
            ? $"Bulk updated {updated}/{songs.Count} song(s) and recompiled {recompiled} into Guitar Hero III!"
            : $"Bulk updated metadata for {updated}/{songs.Count} song(s)!";
        return (updated, recompiled, summary);
    }

    public static bool HasAiCache(string? projectDir)
        => !string.IsNullOrWhiteSpace(projectDir) && File.Exists(Path.Combine(projectDir, "gh3sync_ai_cache.json"));

    /// <summary>
    /// 5-second Instant Re-Chart using the cached Basic Pitch + Librosa analysis (zero Demucs wait!).
    /// Allows rapid iteration on Chart Feel (Casual / Standard / Shredder) and ±ms Sync Offset.
    /// </summary>
    public async Task<(bool Ok, string Message)> InstantRechartFromCacheAsync(
        string projectDir,
        string chartFeel,
        int offsetMs = 0,
        bool recompile = true,
        Action<string>? onProgress = null)
    {
        var cachePath = Path.Combine(projectDir, "gh3sync_ai_cache.json");
        if (!File.Exists(cachePath))
            return (false, "No AI analysis cache found for this song yet. Run a full 'Re-Chart AI v2' once to build the cache.");

        try
        {
            onProgress?.Invoke("Loading cached Basic Pitch & beat-grid analysis...");
            var cache = JsonSerializer.Deserialize<SongAiAnalysisCache>(File.ReadAllText(cachePath));
            if (cache == null || cache.TranscribedGuitarNotes.Count < 8)
                return (false, "Cached AI analysis is empty or invalid.");

            var iniPath = Path.Combine(projectDir, "song.ini");
            string title = Path.GetFileName(projectDir);
            string artist = "Unknown Artist";
            if (File.Exists(iniPath))
            {
                foreach (var l in File.ReadAllLines(iniPath))
                {
                    var eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = l.Substring(0, eq).Trim();
                    var v = l.Substring(eq + 1).Trim();
                    if (k.Equals("name", StringComparison.OrdinalIgnoreCase)) title = v;
                    else if (k.Equals("artist", StringComparison.OrdinalIgnoreCase)) artist = v;
                }
            }

            var meta = new SongCreationRequest
            {
                Title = title,
                Artist = artist,
                Bpm = cache.PreciseBpm,
                ChartFeel = chartFeel,
                InstrumentFocus = cache.InstrumentFocusRequested,
                AudioOffsetMs = offsetMs,
                UseVariableTempoMap = !cache.IsStudioConstantTempo
            };

            var leadNotes = cache.TranscribedGuitarNotes;
            var bassNotes = cache.TranscribedBassNotes;
            if (offsetMs != 0)
            {
                double delta = offsetMs / 1000.0;
                leadNotes = leadNotes.Select(n => new TranscribedNote { StartSec = Math.Max(0, n.StartSec + delta), EndSec = Math.Max(0.05, n.EndSec + delta), MidiPitch = n.MidiPitch, Amplitude = n.Amplitude }).ToList();
                bassNotes = bassNotes.Select(n => new TranscribedNote { StartSec = Math.Max(0, n.StartSec + delta), EndSec = Math.Max(0.05, n.EndSec + delta), MidiPitch = n.MidiPitch, Amplitude = n.Amplitude }).ToList();
            }

            onProgress?.Invoke($"Generating {chartFeel} chart from instant AI cache...");
            var chartPath = Path.Combine(projectDir, "notes.chart");
            var stats = new ChartBuildStats();
            var content = MidiToGh3ChartMapper.BuildChartFromNotes(
                meta, leadNotes, cache.PreciseBpm, cache.SongDurationSeconds,
                $"ONOGAMI AI v2 [Instant Cache] ({cache.ResolvedLeadFocus})",
                cache.PreciseBeatTimes, bassNotes,
                isStudioConstantTempo: cache.IsStudioConstantTempo,
                measureEnergies: cache.MeasureEnergies,
                detectedSections: cache.DetectedSections,
                statsOut: stats);
            File.WriteAllText(chartPath, content);
            TryConvertChart(chartPath);

            if (recompile && _compiler != null)
            {
                var ghproj = Directory.GetFiles(projectDir, "*.ghproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(ghproj))
                {
                    // Clean up existing compiled song registration from customs.pak.xen and game folders
                    // so Honeycomb re-compiles the song in-place without generating duplicate cards!
                    var idsToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    if (File.Exists(iniPath))
                    {
                        foreach (var l in File.ReadAllLines(iniPath))
                        {
                            var eq = l.IndexOf('=');
                            if (eq <= 0) continue;
                            var k = l.Substring(0, eq).Trim();
                            if (k.Equals("compiled_song_id", StringComparison.OrdinalIgnoreCase))
                            {
                                var oldId = l.Substring(eq + 1).Trim();
                                if (!string.IsNullOrWhiteSpace(oldId)) idsToRemove.Add(oldId);
                                break;
                            }
                        }
                    }

                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(ghproj), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                        if (doc.RootElement.TryGetProperty("songName", out var sn) && !string.IsNullOrWhiteSpace(sn.GetString()))
                            idsToRemove.Add(sn.GetString()!);
                    }
                    catch { }

                    var gamePath = _gh3.GamePath;
                    if (!string.IsNullOrWhiteSpace(gamePath) && Directory.Exists(gamePath))
                    {
                        foreach (var id in idsToRemove)
                        {
                            try
                            {
                                CustomsPakService.RemoveSongFromCustomsPak(gamePath, id);
                                var oldPak = Path.Combine(gamePath, "DATA", "SONGS", $"{id}_song.pak.xen");
                                if (File.Exists(oldPak)) File.Delete(oldPak);
                                var oldFsb = Path.Combine(gamePath, "DATA", "MUSIC", $"{id}.fsb.xen");
                                if (File.Exists(oldFsb)) File.Delete(oldFsb);
                                var oldDat = Path.Combine(gamePath, "DATA", "MUSIC", $"{id}.dat.xen");
                                if (File.Exists(oldDat)) File.Delete(oldDat);
                            }
                            catch { }
                        }
                    }

                    onProgress?.Invoke("Recompiling updated chart in-place into Guitar Hero III...");
                    var comp = await _compiler.CompileSongProjectAsync(ghproj);
                    if (!comp.Success) return (false, $"Chart rebuilt ({stats.ExpertNoteCount} notes), but recompile failed: {comp.Message}");
                }
            }

            return (true, $"Instant Re-Chart complete! '{title}' ({chartFeel}, {stats.ExpertNoteCount:N0} Expert notes, {stats.RepeatedRiffsMatched} locked riffs) updated in seconds.");
        }
        catch (Exception ex)
        {
            return (false, $"Instant Re-Chart failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Applies optional Start/End Trim, Fade-Out, and EBU R128 Loudness Normalization (-14 LUFS)
    /// using bundled FFmpeg so every custom song plays at balanced official GH3 volume.
    /// </summary>
    public static string? TryPreprocessAudioWithFfmpeg(
        string inputPath,
        string targetDir,
        double trimStartSec,
        double trimEndSec,
        double fadeOutSec,
        bool normalizeLoudness)
    {
        try
        {
            var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
            if (string.IsNullOrEmpty(ff) || !File.Exists(ff)) return null;

            var outPath = Path.Combine(targetDir, "song_processed.mp3");
            var filters = new List<string>();
            if (normalizeLoudness)
                filters.Add("loudnorm=I=-14:TP=-1.5:LRA=11");

            if (fadeOutSec > 0.2 && trimEndSec > trimStartSec + fadeOutSec + 1.0)
            {
                double trimmedDur = trimEndSec - Math.Max(0, trimStartSec);
                double fadeStart = Math.Max(0, trimmedDur - fadeOutSec);
                filters.Add($"afade=t=out:st={fadeStart.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}:d={fadeOutSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}");
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = ff,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-y");
            if (trimStartSec > 0.1)
            {
                psi.ArgumentList.Add("-ss");
                psi.ArgumentList.Add(trimStartSec.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(inputPath);
            if (trimEndSec > trimStartSec + 1.0)
            {
                double dur = trimEndSec - Math.Max(0, trimStartSec);
                psi.ArgumentList.Add("-t");
                psi.ArgumentList.Add(dur.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            }
            if (filters.Count > 0)
            {
                psi.ArgumentList.Add("-af");
                psi.ArgumentList.Add(string.Join(",", filters));
            }
            psi.ArgumentList.Add("-vn");
            psi.ArgumentList.Add("-c:a");
            psi.ArgumentList.Add("libmp3lame");
            psi.ArgumentList.Add("-b:a");
            psi.ArgumentList.Add("192k");
            psi.ArgumentList.Add("-ar");
            psi.ArgumentList.Add("44100");
            psi.ArgumentList.Add("-ac");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add(outPath);
            using var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit(90_000);

            if (File.Exists(outPath) && new FileInfo(outPath).Length > 10_000)
            {
                var finalPath = Path.Combine(targetDir, "song.mp3");
                if (!string.Equals(inputPath, finalPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(inputPath); } catch { }
                }
                File.Move(outPath, finalPath, overwrite: true);
                return finalPath;
            }
        }
        catch { }
        return null;
    }

    private static void TryConvertChart(string chartPath)
    {
        try
        {
            var hDir = HoneycombCompilerService.FindHoneycombDirectory();
            if (!string.IsNullOrEmpty(hDir))
                HoneycombCompilerService.ConvertChartToMid(chartPath, hDir);
        }
        catch { }
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in input)
        {
            if (!invalid.Contains(c) && c != '\'' && c != '\"') sb.Append(c);
            else if (c == '\'' || c == '\"') continue;
            else sb.Append('_');
        }
        var res = sb.ToString().Trim();
        return string.IsNullOrEmpty(res) ? "CustomSong" : res;
    }

    private static string SanitizeSongId(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        var baseStr = sb.ToString();
        if (baseStr.Length > 12) baseStr = baseStr.Substring(0, 12);
        if (string.IsNullOrEmpty(baseStr)) baseStr = "custom";
        return $"{baseStr}_{Guid.NewGuid():N}".Substring(0, 16);
    }
}
