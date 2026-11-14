using System;
using System.IO;
using System.Text;
using System.Text.Json;

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
    public bool AutoGenerateChart { get; set; } = false;
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
}

public class SongCreatorService
{
    private readonly string _defaultSongsDir;

    public SongCreatorService(string? songsDir = null)
    {
        _defaultSongsDir = songsDir ?? @"G:\Guitar Hero Songs";
        if (!Directory.Exists(_defaultSongsDir))
        {
            try { Directory.CreateDirectory(_defaultSongsDir); } catch { }
        }
    }

    public CreatedSongResult CreateSongFromOnogamiMethod(
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
            // 1. Target directory
            var safeFolder = SanitizeFileName($"{meta.Artist} - {meta.Title}");
            var targetDir = Path.Combine(_defaultSongsDir, safeFolder);
            if (!Directory.Exists(targetDir))
            {
                Directory.CreateDirectory(targetDir);
            }

            var cleanSongId = SanitizeInternalName(meta.Title);

            // 2. Save Audio
            var audioExt = Path.GetExtension(audioFileName).ToLowerInvariant();
            if (string.IsNullOrEmpty(audioExt)) audioExt = ".mp3";
            var audioFullPath = Path.Combine(targetDir, $"song{audioExt}");
            File.WriteAllBytes(audioFullPath, audioBytes);

            // 3. Handle Chart (Use provided OR Auto-Generate like ONOGAMI)
            string chartFullPath;
            if (chartBytes != null && chartBytes.Length > 0 && !string.IsNullOrEmpty(chartFileName))
            {
                var chartTargetName = chartFileName.EndsWith(".chart", StringComparison.OrdinalIgnoreCase) ? "notes.chart" : "notes.mid";
                chartFullPath = Path.Combine(targetDir, chartTargetName);
                File.WriteAllBytes(chartFullPath, chartBytes);
            }
            else
            {
                // Auto-generate full 5-fret .chart file
                chartFullPath = Path.Combine(targetDir, "notes.chart");
                var generatedChart = GenerateOnogamiStyleChart(meta);
                File.WriteAllText(chartFullPath, generatedChart);
            }

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
                guitarPath = "",
                bassPath = "",
                vocalsPath = "",
                backingPaths = "",
                crowdPath = "",
                previewAudioPath = "",
                guitarPathGh3 = "",
                rhythmPathGh3 = "",
                backingPathsGh3 = audioFullPath,
                coopGuitarPath = "",
                coopRhythmPath = "",
                coopBackingPaths = "",
                crowdPathGh3 = "",
                previewAudioPathGh3 = "",
                midiPathGh3 = chartFullPath,
                perfPathGh3 = "",
                skaPathGh3 = "",
                songScriptPathGh3 = "",
                midiPath = "",
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
                artistText = 0,
                songYear = meta.Year,
                coverYear = meta.Year,
                wtGenre = 0,
                gh5Genre = 0,
                worGenre = 0,
                previewStart = meta.PreviewStartMs,
                previewEnd = meta.PreviewStartMs + 30000,
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
                useRenderedPreview = false,
                useRenderedPreviewGh3 = false,
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

            // Register this song ID as created by ONOGAMI creator
            AppConfig.RegisterCreatedSong(cleanSongId);

            return new CreatedSongResult
            {
                Success = true,
                SongId = cleanSongId,
                Title = meta.Title,
                Artist = meta.Artist,
                ProjectDirectory = targetDir,
                GhprojPath = ghprojFullPath,
                CreatedWithOnogamiCreator = true,
                Message = $"Successfully created '{meta.Title}' with ONOGAMI Song Creator!"
            };
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

    private static string GenerateOnogamiStyleChart(SongCreationRequest meta)
    {
        var sb = new StringBuilder();
        int bpmMicro = Math.Max(60, Math.Min(300, meta.Bpm)) * 1000;

        sb.AppendLine("[Song]");
        sb.AppendLine("{");
        sb.AppendLine($"  Name = \"{meta.Title}\"");
        sb.AppendLine($"  Artist = \"{meta.Artist}\"");
        sb.AppendLine($"  Charter = \"{meta.Charter}\"");
        sb.AppendLine($"  Album = \"{meta.Album}\"");
        sb.AppendLine($"  Year = \", {meta.Year}\"");
        sb.AppendLine("  Offset = 0");
        sb.AppendLine("  Resolution = 192");
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
        sb.AppendLine("  13056 = E \"section Chorus\"");
        sb.AppendLine("  19200 = E \"section Solo\"");
        sb.AppendLine("  19200 = E solo");
        sb.AppendLine("  31488 = E soloend");
        sb.AppendLine("  31488 = E \"section Outro\"");
        sb.AppendLine("}");

        // Build rhythmically valid 5-fret gem notes for ExpertSingle
        sb.AppendLine("[ExpertSingle]");
        sb.AppendLine("{");
        int currentTick = 768;
        int step = 192; // quarter notes
        int[] frets = new[] { 0, 1, 2, 3, 4, 3, 2, 1 };

        for (int i = 0; i < 240; i++)
        {
            int fret = frets[i % frets.Length];
            int sustain = (i % 8 == 0) ? 384 : 0;
            sb.AppendLine($"  {currentTick} = N {fret} {sustain}");
            currentTick += step;
        }
        sb.AppendLine("}");

        // Hard
        sb.AppendLine("[HardSingle]");
        sb.AppendLine("{");
        currentTick = 768;
        for (int i = 0; i < 120; i++)
        {
            int fret = (i % 4);
            sb.AppendLine($"  {currentTick} = N {fret} 0");
            currentTick += step * 2;
        }
        sb.AppendLine("}");

        // Medium
        sb.AppendLine("[MediumSingle]");
        sb.AppendLine("{");
        currentTick = 768;
        for (int i = 0; i < 60; i++)
        {
            int fret = (i % 3);
            sb.AppendLine($"  {currentTick} = N {fret} 0");
            currentTick += step * 4;
        }
        sb.AppendLine("}");

        return sb.ToString();
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in input)
        {
            if (Array.IndexOf(invalid, c) < 0 && c != '"' && c != '”' && c != '“')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString().Trim();
    }

    private static string SanitizeInternalName(string input)
    {
        var sb = new StringBuilder();
        foreach (var c in input.ToLowerInvariant())
        {
            if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                sb.Append(c);
        }
        var res = sb.ToString();
        return string.IsNullOrEmpty(res) ? "customsong" : res;
    }
}
