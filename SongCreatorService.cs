using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GH3_Sync;

public class SongCreationRequest
{
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public int Year { get; set; } = DateTime.Now.Year;
    public string Genre { get; set; } = "Rock";
    public string Charter { get; set; } = "Antigravity";
    public int DiffGuitar { get; set; } = 4;
    public int DiffRhythm { get; set; } = 3;
    public int PreviewStartMs { get; set; } = 5000;
    public int Bpm { get; set; } = 130;
    public bool AutoDetectBpm { get; set; } = true;
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
        {
            return AppConfig.Current.CustomSongsPath;
        }

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

    public CreatedSongResult CreateSongFromOnogamiMethod(
        SongCreationRequest meta,
        byte[] audioBytes,
        string audioFileName,
        byte[]? chartBytes = null,
        string? chartFileName = null)
    {
        return CreateSongFromOnogamiMethodAsync(meta, audioBytes, audioFileName, chartBytes, chartFileName).GetAwaiter().GetResult();
    }

    public async Task<CreatedSongResult> CreateSongFromOnogamiMethodAsync(
        SongCreationRequest meta,
        byte[] audioBytes,
        string audioFileName,
        byte[]? chartBytes = null,
        string? chartFileName = null)
    {
        if (string.IsNullOrWhiteSpace(meta.Title))
            return new CreatedSongResult { Success = false, Message = "Song title is required." };
        if (string.IsNullOrWhiteSpace(meta.Artist))
            return new CreatedSongResult { Success = false, Message = "Artist is required." };
        if (audioBytes == null || audioBytes.Length == 0)
            return new CreatedSongResult { Success = false, Message = "Audio file is required." };

        try
        {
            // 1. Create Target Directory
            var cleanTitle = SanitizeFileName(meta.Title);
            var cleanArtist = SanitizeFileName(meta.Artist);
            var dirName = $"{cleanArtist} - {cleanTitle}";
            var targetDir = Path.Combine(_defaultSongsDir, dirName);

            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            // 2. Save Audio File
            var audioExt = Path.GetExtension(audioFileName);
            if (string.IsNullOrEmpty(audioExt)) audioExt = ".mp3";
            var audioFullPath = Path.Combine(targetDir, $"song{audioExt.ToLowerInvariant()}");
            File.WriteAllBytes(audioFullPath, audioBytes);

            // 2b. Audio Analysis & BPM Detection
            AudioAnalysisResult? analysis = null;
            if (_audioAnalysis != null)
            {
                analysis = _audioAnalysis.AnalyzeAudio(audioFullPath);
                if (analysis.Success && (meta.AutoDetectBpm || meta.Bpm <= 0))
                {
                    meta.Bpm = (int)Math.Round(analysis.Bpm);
                    if (analysis.DownbeatOffsetMs > 0 && meta.PreviewStartMs == 5000)
                    {
                        meta.PreviewStartMs = (int)Math.Max(5000, analysis.DownbeatOffsetMs + 10000);
                    }
                }
            }

            // 2c. Optional Demucs AI Stem Separation
            bool stemsSeparated = false;
            string guitarAudioPath = audioFullPath;
            string rhythmAudioPath = "";
            string backingAudioPath = audioFullPath;

            if (meta.SeparateStemsWithDemucs && _demucs != null)
            {
                var stemsDir = Path.Combine(targetDir, "stems");
                var demucsResult = await _demucs.SeparateStemsAsync(audioFullPath, stemsDir);
                if (demucsResult.Success && !string.IsNullOrEmpty(demucsResult.GuitarTrackPath))
                {
                    stemsSeparated = true;
                    guitarAudioPath = demucsResult.GuitarTrackPath;
                    rhythmAudioPath = demucsResult.BassTrackPath;
                    backingAudioPath = !string.IsNullOrEmpty(demucsResult.VocalsTrackPath) && !string.IsNullOrEmpty(demucsResult.DrumsTrackPath)
                        ? $"{demucsResult.VocalsTrackPath};{demucsResult.DrumsTrackPath}"
                        : audioFullPath;
                }
            }

            // 3. Handle Chart (Use provided OR Auto-Generate like ONOGAMI)
            string chartFullPath;
            string midiFullPath = Path.Combine(targetDir, "notes.mid");
            if (chartBytes != null && chartBytes.Length > 0 && !meta.AutoGenerateChart)
            {
                var chartExt = Path.GetExtension(chartFileName ?? ".chart").ToLowerInvariant();
                chartFullPath = Path.Combine(targetDir, $"notes{chartExt}");
                File.WriteAllBytes(chartFullPath, chartBytes);

                if (chartExt == ".mid" || chartExt == ".midi")
                {
                    File.Copy(chartFullPath, midiFullPath, overwrite: true);
                }
                else if (chartExt == ".chart")
                {
                    try
                    {
                        var hDir = HoneycombCompilerService.FindHoneycombDirectory();
                        if (!string.IsNullOrEmpty(hDir))
                        {
                            HoneycombCompilerService.ConvertChartToMid(chartFullPath, hDir);
                        }
                    }
                    catch { }
                }
            }
            else
            {
                // Dynamic Audio-Driven Onset & Rhythmic Chart Generation
                chartFullPath = Path.Combine(targetDir, "notes.chart");
                var chartContent = GenerateDynamicChart(meta, analysis);
                File.WriteAllText(chartFullPath, chartContent);

                try
                {
                    var hDir = HoneycombCompilerService.FindHoneycombDirectory();
                    if (!string.IsNullOrEmpty(hDir))
                    {
                        HoneycombCompilerService.ConvertChartToMid(chartFullPath, hDir);
                    }
                }
                catch { }
            }

            var chosenMidiPath = File.Exists(midiFullPath) ? midiFullPath : "";

            // 4. Generate song.ini
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

            // 5. Generate .ghproj (The ONOGAMI template for Honeycomb)
            var ghprojName = $"{meta.Title}.ghproj";
            var ghprojFullPath = Path.Combine(targetDir, ghprojName);
            var cleanSongId = SanitizeSongId(cleanTitle);

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
                guitarTier = meta.DiffGuitar,
                bassTier = meta.DiffRhythm,
                drumsTier = 1,
                vocalsTier = 1,
                guitarCareerTier = 0,
                bassCareerTier = 0,
                drumsCareerTier = 0,
                vocalsCareerTier = 0,
                bandCareerTier = 0,
                gtrVolumeGh3 = 0.0,
                bandVolumeGh3 = 0.0,
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

            var ghprojJson = JsonSerializer.Serialize(projData, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ghprojFullPath, ghprojJson);

            // Register this song ID and project directory as created by ONOGAMI creator
            AppConfig.RegisterCreatedSong(cleanSongId, targetDir);

            var result = new CreatedSongResult
            {
                Success = true,
                SongId = cleanSongId,
                Title = meta.Title,
                Artist = meta.Artist,
                ProjectDirectory = targetDir,
                GhprojPath = ghprojFullPath,
                CreatedWithOnogamiCreator = true,
                DetectedBpm = meta.Bpm,
                StemsSeparated = stemsSeparated,
                Message = $"Successfully created '{meta.Title}' with ONOGAMI Song Creator!"
            };

            // Automated Honeycomb Compilation
            if (meta.AutoCompile && _compiler != null)
            {
                var compileResult = await _compiler.CompileSongProjectAsync(ghprojFullPath);
                result.CompiledSuccessfully = compileResult.Success;
                result.CompilationMessage = compileResult.Message;
                result.PakFilePath = compileResult.PakFilePath;
                result.FsbFilePath = compileResult.FsbFilePath;

                if (compileResult.Success)
                {
                    if (!string.IsNullOrEmpty(compileResult.SongId))
                    {
                        result.SongId = compileResult.SongId;
                    }
                    result.Message = $"Successfully created and compiled '{meta.Title}' directly into Guitar Hero III (Ready to Play)!";
                }
                else
                {
                    result.Message = $"Song created at '{targetDir}'. Honeycomb compilation note: {compileResult.Message}";
                }
            }

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
    /// Generates dynamic, music-driven 5-fret charts with section events,
    /// power chords, melodically shifting fret progressions, Star Power phrases,
    /// and sustains synced to detected audio onsets and energy peaks.
    /// </summary>
    private static string GenerateDynamicChart(SongCreationRequest meta, AudioAnalysisResult? analysis)
    {
        var sb = new StringBuilder();
        int bpmMicro = Math.Max(60, Math.Min(300, meta.Bpm)) * 1000;
        int resolution = 192; // Standard Neversoft GH3 resolution (192 ticks per beat)

        sb.AppendLine("[Song]");
        sb.AppendLine("{");
        sb.AppendLine($"  Name = \"{meta.Title}\"");
        sb.AppendLine($"  Artist = \"{meta.Artist}\"");
        sb.AppendLine($"  Charter = \"{meta.Charter}\"");
        sb.AppendLine($"  Album = \"{meta.Album}\"");
        sb.AppendLine($"  Year = \", {meta.Year}\"");
        sb.AppendLine($"  Offset = {Math.Round(analysis?.DownbeatOffsetMs ?? 0.0, 1)}");
        sb.AppendLine($"  Resolution = {resolution}");
        sb.AppendLine("  Player2 = rhythm");
        sb.AppendLine($"  Difficulty = {meta.DiffGuitar}");
        sb.AppendLine($"  PreviewStart = {meta.PreviewStartMs}");
        sb.AppendLine($"  PreviewEnd = {meta.PreviewStartMs + 30000}");
        sb.AppendLine($"  Genre = \"{meta.Genre.ToLowerInvariant()}\"");
        sb.AppendLine("  MediaType = \"cd\"");
        sb.AppendLine("  MusicStream = \"song.mp3\"");
        sb.AppendLine("}");

        sb.AppendLine("[SyncTrack]");
        sb.AppendLine("{");
        sb.AppendLine("  0 = TS 4");
        sb.AppendLine($"  0 = B {bpmMicro}");
        sb.AppendLine("}");

        sb.AppendLine("[Events]");
        sb.AppendLine("{");
        sb.AppendLine("  768 = E \"section Intro\"");
        sb.AppendLine("  3840 = E \"section Verse 1\"");
        sb.AppendLine("  11520 = E \"section Chorus 1\"");
        sb.AppendLine("  19200 = E \"section Verse 2\"");
        sb.AppendLine("  26880 = E \"section Solo\"");
        sb.AppendLine("  26880 = E solo");
        sb.AppendLine("  38400 = E soloend");
        sb.AppendLine("  38400 = E \"section Chorus 2\"");
        sb.AppendLine("  46080 = E \"section Outro\"");
        sb.AppendLine("}");

        // Build notes from audio transients if available
        List<int> onsetTicks = new();
        List<float> onsetEnergies = new();

        if (analysis != null && analysis.OnsetTimestampsSeconds.Count > 10)
        {
            var offsetSec = analysis.DownbeatOffsetMs / 1000.0;
            var ticksPerSec = resolution * (meta.Bpm / 60.0);

            for (int i = 0; i < analysis.OnsetTimestampsSeconds.Count; i++)
            {
                var timeSec = analysis.OnsetTimestampsSeconds[i];
                if (timeSec < offsetSec) continue;

                var rawTick = (int)Math.Round((timeSec - offsetSec) * ticksPerSec);
                if (rawTick < 768) continue; // Start after 1 intro bar

                // Quantize to 16th-note grid (48 ticks)
                var quantized = ((rawTick + 24) / 48) * 48;

                if (!onsetTicks.Contains(quantized))
                {
                    onsetTicks.Add(quantized);
                    onsetEnergies.Add(analysis.OnsetEnergies[i]);
                }
            }
        }

        // Fallback procedural rhythm grid if no transients
        if (onsetTicks.Count < 20)
        {
            onsetTicks.Clear();
            onsetEnergies.Clear();
            int cur = 768;
            for (int i = 0; i < 300; i++)
            {
                onsetTicks.Add(cur);
                onsetEnergies.Add(0.5f + (float)Math.Sin(i * 0.3) * 0.4f);
                cur += (i % 4 == 3) ? 96 : 192;
            }
        }

        // Generate 4 difficulties: Expert, Hard, Medium, Easy
        GenerateDifficulty(sb, "ExpertSingle", onsetTicks, onsetEnergies, fretSpread: 5, chordChance: 0.28f, densityStep: 1);
        GenerateDifficulty(sb, "HardSingle", onsetTicks, onsetEnergies, fretSpread: 4, chordChance: 0.15f, densityStep: 2);
        GenerateDifficulty(sb, "MediumSingle", onsetTicks, onsetEnergies, fretSpread: 3, chordChance: 0.05f, densityStep: 4);
        GenerateDifficulty(sb, "EasySingle", onsetTicks, onsetEnergies, fretSpread: 2, chordChance: 0.0f, densityStep: 8);

        return sb.ToString();
    }

    private static void GenerateDifficulty(
        StringBuilder sb,
        string trackName,
        List<int> ticks,
        List<float> energies,
        int fretSpread,
        float chordChance,
        int densityStep)
    {
        sb.AppendLine($"[{trackName}]");
        sb.AppendLine("{");

        int noteIndex = 0;
        int lastFret = 0;
        int direction = 1;

        for (int i = 0; i < ticks.Count; i += densityStep)
        {
            int tick = ticks[i];
            float energy = energies[i];

            // Melodic progression: walking and leaping across available frets
            if (direction > 0)
            {
                lastFret++;
                if (lastFret >= fretSpread - 1) direction = -1;
            }
            else
            {
                lastFret--;
                if (lastFret <= 0) direction = 1;
            }

            int fret = Math.Clamp(lastFret, 0, fretSpread - 1);

            // Sustain calculation: if gap to next note >= 2 beats (384 ticks)
            int sustain = 0;
            if (i + densityStep < ticks.Count)
            {
                int gap = ticks[i + densityStep] - tick;
                if (gap >= 384) sustain = gap - 96;
            }

            // High energy chord placement
            if (energy > 0.75f && chordChance > 0 && fret < fretSpread - 1 && noteIndex % 3 == 0)
            {
                sb.AppendLine($"  {tick} = N {fret} {sustain}");
                sb.AppendLine($"  {tick} = N {fret + 1} {sustain}");
            }
            else
            {
                sb.AppendLine($"  {tick} = N {fret} {sustain}");
            }

            // Star Power phrases every 32 measures (~6144 ticks)
            if (noteIndex % 64 == 0)
            {
                sb.AppendLine($"  {tick} = S 2 768");
            }

            noteIndex++;
        }

        sb.AppendLine("}");
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in input)
        {
            if (!invalid.Contains(c) && c != '\'' && c != '\"') sb.Append(c);
            else if (c == '\'' || c == '\"') sb.Append("");
            else sb.Append('_');
        }
        var res = sb.ToString().Trim();
        return string.IsNullOrEmpty(res) ? "CustomSong" : res;
    }

    private static string SanitizeSongId(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        var baseStr = sb.ToString();
        if (baseStr.Length > 12) baseStr = baseStr.Substring(0, 12);
        if (string.IsNullOrEmpty(baseStr)) baseStr = "custom";
        return $"{baseStr}_{Guid.NewGuid():N}".Substring(0, 16);
    }
}
