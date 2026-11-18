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

public class SongCreationRequest
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Year { get; set; } = DateTime.Now.Year;
    public string Genre { get; set; } = "Rock";
    public string Charter { get; set; } = "Mad Scientist";
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

    public SongCreatorService(
        HoneycombCompilerService? compiler = null,
        AudioAnalysisService? audioAnalysis = null,
        DemucsService? demucs = null,
        string? songsDir = null)
    {
        _compiler = compiler;
        _audioAnalysis = audioAnalysis;
        _demucs = demucs;
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

            try
            {
                var albumArtOut = Path.Combine(targetDir, "album.jpg");
                if (!File.Exists(albumArtOut))
                {
                    var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
                    if (!string.IsNullOrEmpty(ff) && File.Exists(ff))
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = ff,
                            Arguments = $"-y -i \"{audioFullPath}\" -an -frames:v 1 -q:v 3 \"{albumArtOut}\"",
                            UseShellExecute = false,
                            CreateNoWindow = true
                        };
                        using var p = System.Diagnostics.Process.Start(psi);
                        p?.WaitForExit(5000);
                        if (File.Exists(albumArtOut) && new FileInfo(albumArtOut).Length < 512)
                            try { File.Delete(albumArtOut); } catch { }
                    }
                }
            }
            catch { }

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

            if (meta.SeparateStemsWithDemucs && _demucs != null && _demucs.GetStatus().IsInstalled)
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

            // 6. Generate .ghproj
            var ghprojName = $"{meta.Title}.ghproj";
            var ghprojFullPath = Path.Combine(targetDir, ghprojName);
            var cleanSongId = SanitizeSongId(cleanTitle);

            int safeYear = Math.Clamp(meta.Year, 1900, 2100);
            int safeGuitarTier = Math.Clamp(meta.DiffGuitar, 1, 10);
            int safeBassTier = Math.Clamp(meta.DiffRhythm, 1, 10);

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
                bassPath = rhythmAudioPath,
                vocalsPath = "",
                backingPaths = backingAudioPath,
                crowdPath = "",
                previewAudioPath = audioFullPath,
                guitarPathGh3 = guitarAudioPath,
                rhythmPathGh3 = rhythmAudioPath,
                backingPathsGh3 = backingAudioPath,
                coopGuitarPath = guitarAudioPath,
                coopRhythmPath = rhythmAudioPath,
                coopBackingPaths = backingAudioPath,
                crowdPathGh3 = "",
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
        bool recompile)
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
