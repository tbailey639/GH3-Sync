using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace GH3_Sync;

public class CloneHeroImportResult
{
    public bool Success { get; set; }
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string SongId { get; set; } = "";
    public string ProjectDirectory { get; set; } = "";
    public bool CompiledSuccessfully { get; set; }
    public string Message { get; set; } = "";
}

/// <summary>
/// 1-Click Clone Hero / YARG Song Converter:
/// Accepts a Clone Hero folder or .zip (song.ini + notes.chart/.mid + isolated stems/audio),
/// maps all stems into a Honeycomb .ghproj, converts .chart to notes.mid,
/// and compiles directly into Guitar Hero III / BetterGH3.
/// </summary>
public class CloneHeroImportService
{
    private readonly HoneycombCompilerService _compiler;
    private readonly SyncLogger _logger;

    public CloneHeroImportService(HoneycombCompilerService compiler, SyncLogger logger)
    {
        _compiler = compiler;
        _logger = logger;
    }

    public static bool IsCloneHeroFolder(string dir)
    {
        if (!Directory.Exists(dir)) return false;
        bool hasChart = File.Exists(Path.Combine(dir, "notes.chart")) || File.Exists(Path.Combine(dir, "notes.mid"));
        bool hasIni = File.Exists(Path.Combine(dir, "song.ini"));
        return hasChart || (hasIni && Directory.GetFiles(dir, "*.ogg").Length > 0);
    }

    public async Task<CloneHeroImportResult> ImportFromZipOrFolderAsync(
        string sourcePath,
        bool autoCompile = true,
        Action<int, string>? onProgress = null)
    {
        string? tempExtractDir = null;
        try
        {
            string songSourceDir = sourcePath;

            if (File.Exists(sourcePath) && sourcePath.EndsWith(".sng", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(10, "Unpacking Chorus Encore / Clone Hero .sng package...");
                tempExtractDir = Path.Combine(Path.GetTempPath(), $"gh3_sng_import_{Guid.NewGuid():N}");
                CommunityChartService.ExtractSngFile(sourcePath, tempExtractDir, _logger);
                songSourceDir = tempExtractDir;
            }
            else if (File.Exists(sourcePath) && sourcePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                onProgress?.Invoke(10, "Scanning & extracting Clone Hero archive safely...");
                tempExtractDir = Path.Combine(Path.GetTempPath(), $"gh3_ch_import_{Guid.NewGuid():N}");
                SecurityService.ExtractCloneHeroZipSafely(sourcePath, tempExtractDir, _logger);

                // Find folder containing notes.chart, notes.mid, or song.ini
                var foundChart = Directory.GetFiles(tempExtractDir, "notes.chart", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(tempExtractDir, "notes.mid", SearchOption.AllDirectories).FirstOrDefault()
                              ?? Directory.GetFiles(tempExtractDir, "song.ini", SearchOption.AllDirectories).FirstOrDefault();

                if (string.IsNullOrEmpty(foundChart))
                    return new CloneHeroImportResult { Success = false, Message = "No notes.chart, notes.mid, or song.ini found inside the archive." };

                songSourceDir = Path.GetDirectoryName(foundChart)!;
            }

            if (!Directory.Exists(songSourceDir))
                return new CloneHeroImportResult { Success = false, Message = "Selected Clone Hero song folder does not exist." };

            onProgress?.Invoke(20, "Reading song.ini metadata & mapping Clone Hero stems...");
            var iniMeta = ParseSongIni(Path.Combine(songSourceDir, "song.ini"));

            string title = iniMeta.GetValueOrDefault("name", Path.GetFileName(songSourceDir));
            string artist = iniMeta.GetValueOrDefault("artist", "Unknown Artist");
            string album = iniMeta.GetValueOrDefault("album", "");
            string genre = iniMeta.GetValueOrDefault("genre", "Rock");
            string charter = iniMeta.GetValueOrDefault("charter", "Clone Hero Import");
            int year = int.TryParse(iniMeta.GetValueOrDefault("year", ""), out var y) ? y : DateTime.Now.Year;
            int diffGuitar = Math.Clamp(int.TryParse(iniMeta.GetValueOrDefault("diff_guitar", "4"), out var dg) ? dg : 4, 0, 6);
            int diffRhythm = Math.Clamp(int.TryParse(iniMeta.GetValueOrDefault("diff_rhythm", "3"), out var dr) ? dr : 3, 0, 6);
            int previewMs = int.TryParse(iniMeta.GetValueOrDefault("preview_start_time", "15000"), out var pv) && pv >= 0 ? pv : 15000;

            // Prepare target folder in Custom_Songs
            var songsRoot = SongCreatorService.ResolveSongsDirectory();
            var targetDir = Path.Combine(songsRoot, $"{Sanitize(artist)} - {Sanitize(title)}");
            Directory.CreateDirectory(targetDir);

            // Copy all chart, ini, and audio files cleanly
            string[] audioExts = new[] { ".ogg", ".mp3", ".wav", ".opus", ".flac" };
            foreach (var file in Directory.GetFiles(songSourceDir))
            {
                var ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".chart" || ext == ".mid" || ext == ".ini" || audioExts.Contains(ext))
                {
                    File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
                }
            }

            // Locate chart / MIDI and convert if needed
            string midiPath = Path.Combine(targetDir, "notes.mid");
            string chartPath = Path.Combine(targetDir, "notes.chart");
            if (!File.Exists(midiPath) && File.Exists(chartPath))
            {
                onProgress?.Invoke(35, "Converting Clone Hero notes.chart to GH3 notes.mid...");
                var hDir = HoneycombCompilerService.FindHoneycombDirectory();
                if (!string.IsNullOrEmpty(hDir))
                {
                    HoneycombCompilerService.ConvertChartToMid(chartPath, hDir);
                }
            }

            if (!File.Exists(midiPath))
                return new CloneHeroImportResult { Success = false, Message = "Could not locate or convert notes.chart / notes.mid." };

            // Map isolated Clone Hero audio stems cleanly!
            string FindStem(params string[] baseNames)
            {
                foreach (var bn in baseNames)
                {
                    foreach (var ext in audioExts)
                    {
                        var p = Path.Combine(targetDir, bn + ext);
                        if (File.Exists(p)) return p;
                    }
                }
                return "";
            }

            // Convert any .opus / .flac stems to .mp3 via bundled FFmpeg so Honeycomb FSB compiler handles them cleanly
            var ffmpegExe = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
            if (!string.IsNullOrEmpty(ffmpegExe) && File.Exists(ffmpegExe))
            {
                foreach (var f in Directory.GetFiles(targetDir))
                {
                    var fExt = Path.GetExtension(f).ToLowerInvariant();
                    if (fExt == ".opus" || fExt == ".flac")
                    {
                        var mp3Out = Path.ChangeExtension(f, ".mp3");
                        try
                        {
                            var psi = new System.Diagnostics.ProcessStartInfo
                            {
                                FileName = ffmpegExe,
                                Arguments = $"-y -i \"{f}\" -ar 48000 -b:a 192k \"{mp3Out}\"",
                                UseShellExecute = false,
                                CreateNoWindow = true
                            };
                            using var proc = System.Diagnostics.Process.Start(psi);
                            proc?.WaitForExit(120_000);
                            if (File.Exists(mp3Out) && new FileInfo(mp3Out).Length > 1024)
                            {
                                try { File.Delete(f); } catch { }
                            }
                        }
                        catch { }
                    }
                }
            }

            string songMix = FindStem("song");
            string guitarStem = FindStem("guitar");
            string rhythmStem = FindStem("rhythm", "bass");
            string vocalsStem = FindStem("vocals", "vocals_1");
            string drumsStem = FindStem("drums", "drums_1");
            string keysStem = FindStem("keys");
            string previewStem = FindStem("preview");

            // Fallback if song only has a single audio file
            if (string.IsNullOrEmpty(guitarStem) && string.IsNullOrEmpty(songMix))
            {
                var anyAudio = Directory.GetFiles(targetDir).FirstOrDefault(f => audioExts.Contains(Path.GetExtension(f).ToLowerInvariant()));
                if (string.IsNullOrEmpty(anyAudio))
                    return new CloneHeroImportResult { Success = false, Message = "No audio files (.ogg, .mp3, .wav) found in Clone Hero folder." };
                songMix = anyAudio;
            }

            // Clean Studio Master routing when single-track (no separate guitar stem):
            // keep guitarPathGh3 blank so the song isn't double-played on top of itself!
            bool hasTrueMultiStems = !string.IsNullOrEmpty(guitarStem);
            string finalGuitar = hasTrueMultiStems ? guitarStem : "";
            string finalRhythm = rhythmStem;
            var backingList = new List<string>();
            if (!string.IsNullOrEmpty(songMix)) backingList.Add(songMix);
            if (!string.IsNullOrEmpty(drumsStem)) backingList.Add(drumsStem);
            if (!string.IsNullOrEmpty(vocalsStem)) backingList.Add(vocalsStem);
            if (!string.IsNullOrEmpty(keysStem)) backingList.Add(keysStem);
            if (backingList.Count == 0 && !string.IsNullOrEmpty(guitarStem)) backingList.Add(guitarStem);

            string finalBacking = string.Join(";", backingList.Distinct(StringComparer.OrdinalIgnoreCase));
            string finalPreview = !string.IsNullOrEmpty(previewStem) ? previewStem : (!string.IsNullOrEmpty(songMix) ? songMix : guitarStem);
            string audioMixMode = hasTrueMultiStems ? "IsolatedStems" : "StudioMaster";

            // Generate .ghproj
            string cleanId = SanitizeId(title);
            string ghprojPath = Path.Combine(targetDir, $"{Sanitize(title)}.ghproj");

            var projData = new
            {
                GhprojVersion = 1,
                gameSelect = "GH3",
                platformSelect = "PC",
                songName = cleanId,
                chartAuthor = charter,
                title = title,
                artist = artist,
                artistTextCustom = "",
                coverArtist = "",
                album = album,
                kickPath = "",
                snarePath = "",
                cymbalsPath = "",
                tomsPath = "",
                guitarPath = finalGuitar,
                bassPath = finalRhythm,
                vocalsPath = vocalsStem,
                backingPaths = finalBacking,
                crowdPath = "",
                previewAudioPath = finalPreview,
                guitarPathGh3 = finalGuitar,
                rhythmPathGh3 = finalRhythm,
                backingPathsGh3 = finalBacking,
                coopGuitarPath = finalGuitar,
                coopRhythmPath = finalRhythm,
                coopBackingPaths = finalBacking,
                crowdPathGh3 = "",
                previewAudioPathGh3 = finalPreview,
                midiPathGh3 = midiPath,
                perfPathGh3 = "",
                skaPathGh3 = "",
                songScriptPathGh3 = "",
                midiPath = midiPath,
                perfPath = "",
                skaPath = "",
                lipsyncPath = "",
                songScriptPath = "",
                compilePath = targetDir,
                projectPath = ghprojPath,
                gameIcon = "clone_hero_import",
                gameCategory = "",
                ghprojFromLoad = ghprojPath,
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
                songYear = Math.Clamp(year, 1900, 2100),
                coverYear = 2024,
                wtGenre = 0,
                gh5Genre = 0,
                worGenre = 0,
                previewStart = Math.Max(0, previewMs),
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
                guitarTier = Math.Clamp(diffGuitar, 1, 10),
                bassTier = Math.Clamp(diffRhythm, 1, 10),
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

            File.WriteAllText(ghprojPath, JsonSerializer.Serialize(projData, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllText(Path.Combine(targetDir, "gh3sync_audio.json"), JsonSerializer.Serialize(new SongAudioMixConfig
            {
                AudioMixMode = audioMixMode,
                LeadFocus = "Guitar"
            }, new JsonSerializerOptions { WriteIndented = true }));

            // Mark song.ini cleanly as a Clone Hero import (not authored by Song Creator)
            var targetIni = Path.Combine(targetDir, "song.ini");
            try
            {
                var iniLines = File.Exists(targetIni) ? File.ReadAllLines(targetIni).ToList() : new List<string> { "[song]", $"name = {title}", $"artist = {artist}", $"charter = {charter}" };
                if (!iniLines.Any(l => l.TrimStart().StartsWith("import_source", StringComparison.OrdinalIgnoreCase)))
                    iniLines.Add("import_source = clone_hero");
                File.WriteAllLines(targetIni, iniLines);
            }
            catch { }

            var result = new CloneHeroImportResult
            {
                Success = true,
                Title = title,
                Artist = artist,
                SongId = cleanId,
                ProjectDirectory = targetDir,
                Message = $"Converted Clone Hero song '{title}' by {artist}."
            };

            if (autoCompile)
            {
                onProgress?.Invoke(60, $"Compiling '{title}' into Guitar Hero III...");
                var comp = await _compiler.CompileSongProjectAsync(ghprojPath);
                result.CompiledSuccessfully = comp.Success;
                if (comp.Success)
                {
                    if (!string.IsNullOrEmpty(comp.SongId)) result.SongId = comp.SongId;
                    result.Message = $"Converted & installed Clone Hero song '{title}' by {artist} directly into Guitar Hero III!";
                }
                else
                {
                    result.Message = $"Converted '{title}' to project folder, but compilation noted: {comp.Message}";
                }
            }

            onProgress?.Invoke(100, "Done!");
            _logger.LogInfo($"[CLONE HERO IMPORT] {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[CLONE HERO IMPORT] Failed: {ex.Message}");
            return new CloneHeroImportResult { Success = false, Message = $"Clone Hero import error: {ex.Message}" };
        }
        finally
        {
            if (!string.IsNullOrEmpty(tempExtractDir) && Directory.Exists(tempExtractDir))
            {
                try { Directory.Delete(tempExtractDir, true); } catch { }
            }
        }
    }

    private static Dictionary<string, string> ParseSongIni(string path)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return dict;

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('[') || line.StartsWith(';') || line.StartsWith('#'))
                continue;
            int eq = line.IndexOf('=');
            if (eq > 0)
            {
                var k = line[..eq].Trim();
                var v = line[(eq + 1)..].Trim();
                dict[k] = v;
            }
        }
        return dict;
    }

    private static string Sanitize(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder();
        foreach (var c in input)
        {
            if (!invalid.Contains(c) && c != '\'' && c != '\"') sb.Append(c);
            else if (c != '\'' && c != '\"') sb.Append('_');
        }
        var res = sb.ToString().Trim();
        return string.IsNullOrEmpty(res) ? "CloneHeroSong" : res;
    }

    private static string SanitizeId(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title)
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        var s = sb.ToString();
        if (s.Length > 12) s = s[..12];
        if (string.IsNullOrEmpty(s)) s = "chsong";
        return $"{s}_{Guid.NewGuid():N}"[..16];
    }
}
