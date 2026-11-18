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
    public string Charter { get; set; } = "ONOGAMI AI Creator";
    public int DiffGuitar { get; set; } = 4;
    public int DiffRhythm { get; set; } = 3;
    public int PreviewStartMs { get; set; } = 5000;
    public double Bpm { get; set; } = 130.0;
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
    public int TranscribedNoteCount { get; set; }
    public string ChartingEngineUsed { get; set; } = "";
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

            // 1. Save Audio File
            var audioExt = Path.GetExtension(audioFileName);
            if (string.IsNullOrEmpty(audioExt)) audioExt = ".mp3";
            var audioFullPath = Path.Combine(targetDir, $"song{audioExt.ToLowerInvariant()}");
            File.WriteAllBytes(audioFullPath, audioBytes);

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

            // 3. Optional Demucs 6-Stem Isolation + Spotify Basic Pitch AI Transcription
            bool stemsSeparated = false;
            string guitarAudioPath = audioFullPath;
            string rhythmAudioPath = "";
            string backingAudioPath = audioFullPath;
            List<TranscribedNote> bestNotes = new();
            string chartingEngine = "Uploaded Chart/MIDI";

            if (meta.SeparateStemsWithDemucs && _demucs != null && _demucs.GetStatus().IsInstalled)
            {
                onProgress?.Invoke(20, "Running Demucs 6-Stem AI (isolating Guitar, Bass, Drums, Vocals, Piano)...");
                var stemsDir = Path.Combine(targetDir, "stems");
                var demucsRes = await _demucs.SeparateStemsAsync(audioFullPath, stemsDir, (pct, step) =>
                {
                    int mapped = 20 + (int)(pct * 0.45);
                    onProgress?.Invoke(mapped, step);
                });

                if (demucsRes.Success && !string.IsNullOrEmpty(demucsRes.GuitarTrackPath))
                {
                    stemsSeparated = true;
                    guitarAudioPath = demucsRes.GuitarTrackPath;
                    rhythmAudioPath = demucsRes.BassTrackPath;

                    // Combine non-guitar stems (drums + vocals + piano + other if guitar.wav was separate) into backing
                    var backingList = new List<string>();
                    if (!string.IsNullOrEmpty(demucsRes.DrumsTrackPath)) backingList.Add(demucsRes.DrumsTrackPath);
                    if (!string.IsNullOrEmpty(demucsRes.VocalsTrackPath)) backingList.Add(demucsRes.VocalsTrackPath);
                    if (!string.IsNullOrEmpty(demucsRes.PianoTrackPath)) backingList.Add(demucsRes.PianoTrackPath);
                    if (!string.IsNullOrEmpty(demucsRes.OtherTrackPath) &&
                        !string.Equals(demucsRes.OtherTrackPath, demucsRes.GuitarTrackPath, StringComparison.OrdinalIgnoreCase))
                    {
                        backingList.Add(demucsRes.OtherTrackPath);
                    }
                    backingAudioPath = backingList.Count > 0 ? string.Join(";", backingList) : audioFullPath;

                    if (demucsRes.TranscribedGuitarNotes.Count >= 15)
                    {
                        bestNotes = demucsRes.TranscribedGuitarNotes;
                        chartingEngine = "Demucs 6-Stem + Spotify Basic Pitch AI";
                    }
                    else if (_audioAnalysis != null)
                    {
                        onProgress?.Invoke(66, "Extracting pitch contours from isolated guitar.wav...");
                        var stemAnalysis = _audioAnalysis.AnalyzeAudio(demucsRes.GuitarTrackPath);
                        if (stemAnalysis.Success && stemAnalysis.TranscribedNotes.Count >= 10)
                        {
                            bestNotes = stemAnalysis.TranscribedNotes;
                            chartingEngine = "Demucs 6-Stem + FFT/YIN Guitar Pitch Engine";
                        }
                    }
                }
            }

            // Fallback to FFT/YIN pitched notes from full mix if AI stems weren't used
            if (bestNotes.Count == 0 && mixAnalysis != null && mixAnalysis.TranscribedNotes.Count > 0)
            {
                bestNotes = mixAnalysis.TranscribedNotes;
                chartingEngine = "FFT Guitar-Bandpass + YIN Pitch Engine";
            }

            // 4. Handle Chart (Use uploaded OR build true pitch-mapped chart!)
            onProgress?.Invoke(70, "Building 5-fret Guitar Hero III chart & MIDI...");
            string chartFullPath;
            string midiFullPath = Path.Combine(targetDir, "notes.mid");

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
                    meta, bestNotes, exactBpm, songDuration, chartingEngine);
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

            File.WriteAllText(ghprojFullPath, JsonSerializer.Serialize(projData, new JsonSerializerOptions { WriteIndented = true }));
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
                DetectedBpm = exactBpm,
                StemsSeparated = stemsSeparated,
                TranscribedNoteCount = bestNotes.Count,
                ChartingEngineUsed = chartingEngine,
                Message = $"Successfully created '{meta.Title}' ({chartingEngine}, {exactBpm:F1} BPM)!"
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

                    result.Message = $"Compiled & installed '{meta.Title}' directly into Guitar Hero III! ({chartingEngine}, {exactBpm:F1} BPM, {(stemsSeparated ? "6-Stem Mute-on-Miss Audio" : "Single Mix")})";
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
