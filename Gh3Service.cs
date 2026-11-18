using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.IO.Compression;

namespace GH3_Sync;

public class GameCompatibilityReport
{
    public bool IsValid { get; set; }
    public bool HasGh3Exe { get; set; }
    public bool IsBetterGh3 { get; set; }
    public bool HasCustomsPartition { get; set; }
    public string StatusMessage { get; set; } = "";
    public string GamePath { get; set; } = "";
}

public class CustomSongInfo
{
    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Charter { get; set; } = "";
    public string Year { get; set; } = "";
    public string Album { get; set; } = "";
    public long PakSizeBytes { get; set; }
    public long FsbSizeBytes { get; set; }
    public long TotalSizeBytes => PakSizeBytes + FsbSizeBytes;
    public string PakHash { get; set; } = "";
    public string FsbHash { get; set; } = "";
    public DateTime LastModified { get; set; }
    public bool IsCreatedLocally { get; set; } = false;
    public bool CreatedWithOnogamiCreator { get; set; } = false;
    public bool IsCloneHeroImport { get; set; } = false;
    public bool IsTemporarySessionSong { get; set; } = false;
    public string OnogamiFingerprint { get; set; } = "";
    public bool IsDuplicate { get; set; } = false;
    public string DuplicateReason { get; set; } = "";
    public bool IsProject { get; set; } = false;
    public string ProjectDirectory { get; set; } = "";
    public bool IsCompiled { get; set; } = true;
    public string GhprojPath { get; set; } = "";
    public bool IsFavorite { get; set; } = false;
    public bool IsInLobbySetlist { get; set; } = false;
    public string AlbumArtPath { get; set; } = "";
    public string AudioModeTag { get; set; } = "";
    public bool HasCoopBass { get; set; } = false;
    public int ExpertNoteCount { get; set; } = 0;
}

public class ImportedSongMetaRecord
{
    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Charter { get; set; } = "";
    public string Year { get; set; } = "";
    public string Album { get; set; } = "";
    public bool CreatedWithCreator { get; set; }
    public bool IsCloneHeroImport { get; set; }
}

public class LibraryManifest
{
    public string MachineName { get; set; } = Environment.MachineName;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string CustomsPakHash { get; set; } = "";
    public long CustomsPakSize { get; set; }
    public int MaxLobbyAutoSyncSongs { get; set; } = 10;
    public List<string> LobbySetlistIds { get; set; } = new();
    public List<CustomSongInfo> Songs { get; set; } = new();
}

public class LibraryDiff
{
    public List<CustomSongInfo> InSync { get; set; } = new();
    public List<CustomSongInfo> MissingLocally { get; set; } = new();
    public List<CustomSongInfo> MissingOnRemote { get; set; } = new();
    public bool CustomsPakMatches { get; set; }
}

public class Gh3Service
{
    public string GamePath
    {
        get
        {
            if (!string.IsNullOrEmpty(AppConfig.Current.GamePath) && Directory.Exists(AppConfig.Current.GamePath))
                return AppConfig.Current.GamePath;
            return DetectGamePath();
        }
    }

    public bool IsDetected => !string.IsNullOrEmpty(GamePath) && Directory.Exists(GamePath) && File.Exists(Path.Combine(GamePath, "GH3.exe"));

    public string DataPath => Path.Combine(GamePath, "DATA");
    public string SongsPath => Path.Combine(DataPath, "SONGS");
    public string MusicPath => Path.Combine(DataPath, "MUSIC");
    public string CustomsPakPath => Path.Combine(DataPath, "customs.pak.xen");
    public string StagingPath
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(GamePath) && Directory.Exists(GamePath))
            {
                var p = Path.Combine(GamePath, "Staging_Downloads");
                if (!Directory.Exists(p)) { try { Directory.CreateDirectory(p); } catch { } }
                return p;
            }
            var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "Staging_Downloads");
            if (!Directory.Exists(fallback)) { try { Directory.CreateDirectory(fallback); } catch { } }
            return fallback;
        }
    }

    public Gh3Service()
    {
        if (string.IsNullOrEmpty(AppConfig.Current.GamePath))
        {
            var detected = DetectGamePath();
            if (!string.IsNullOrEmpty(detected))
            {
                AppConfig.UpdateGamePath(detected);
            }
        }
    }

    public static string DetectGamePath(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath) && Directory.Exists(overridePath) && File.Exists(Path.Combine(overridePath, "GH3.exe")))
        {
            return overridePath;
        }

        string[] candidates = new[]
        {
            @"G:\Home\Games\Guitar Hero III",
            @"C:\Games\Guitar Hero III",
            @"D:\Games\Guitar Hero III",
            @"C:\Program Files (x86)\Aspyr\Guitar Hero III",
            @"C:\Program Files\Aspyr\Guitar Hero III"
        };

        foreach (var c in candidates)
        {
            if (Directory.Exists(c) && File.Exists(Path.Combine(c, "GH3.exe")))
            {
                return c;
            }
        }

        try
        {
            var drives = DriveInfo.GetDrives().Where(d => d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable));
            foreach (var d in drives)
            {
                string[] driveChecks = new[]
                {
                    Path.Combine(d.RootDirectory.FullName, "Games", "Guitar Hero III"),
                    Path.Combine(d.RootDirectory.FullName, "Games", "Guitar Hero 3"),
                    Path.Combine(d.RootDirectory.FullName, "Guitar Hero III"),
                    Path.Combine(d.RootDirectory.FullName, "Guitar Hero 3"),
                    Path.Combine(d.RootDirectory.FullName, "Program Files (x86)", "Aspyr", "Guitar Hero III"),
                    Path.Combine(d.RootDirectory.FullName, "Program Files", "Aspyr", "Guitar Hero III"),
                    Path.Combine(d.RootDirectory.FullName, "Home", "Games", "Guitar Hero III")
                };

                foreach (var check in driveChecks)
                {
                    if (Directory.Exists(check) && File.Exists(Path.Combine(check, "GH3.exe")))
                    {
                        return check;
                    }
                }
            }
        }
        catch { }

        try
        {
            var userDocs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var hConfig = Path.Combine(userDocs, "Honeycomb-GUI", "Honeycomb-GUI.dll.config");
            if (File.Exists(hConfig))
            {
                var content = File.ReadAllText(hConfig);
                var match = System.Text.RegularExpressions.Regex.Match(content, @"<setting name=""Gh3FolderPath""[^>]*>\s*<value>([^<]+)</value>");
                if (match.Success && Directory.Exists(match.Groups[1].Value.Trim()))
                {
                    var found = match.Groups[1].Value.Trim();
                    if (File.Exists(Path.Combine(found, "GH3.exe")))
                        return found;
                }
            }
        }
        catch { }

        return "";
    }

    public GameCompatibilityReport VerifyGamePath(string path)
    {
        var report = new GameCompatibilityReport { GamePath = path };
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            report.StatusMessage = "Directory does not exist.";
            return report;
        }

        report.HasGh3Exe = File.Exists(Path.Combine(path, "GH3.exe"));
        if (!report.HasGh3Exe)
        {
            report.StatusMessage = "GH3.exe was not found in this directory.";
            return report;
        }

        // Check for BetterGH3 / GH3+ plugins
        var pluginsDir = Path.Combine(path, "PLUGINS");
        var hasDinput = File.Exists(Path.Combine(path, "dinput8.dll"));
        var hasFastGh3 = Directory.Exists(pluginsDir) && File.Exists(Path.Combine(pluginsDir, "FastGH3.dll"));
        report.IsBetterGh3 = hasDinput || hasFastGh3;

        var customsPak = Path.Combine(path, "DATA", "customs.pak.xen");
        report.HasCustomsPartition = File.Exists(customsPak);

        report.IsValid = true;
        if (report.IsBetterGh3)
        {
            report.StatusMessage = "100% Compatible (BetterGH3 / GH3+ mod detected)";
        }
        else
        {
            report.StatusMessage = "Warning: Vanilla GH3 detected. BetterGH3 mod is recommended for customs.";
        }

        return report;
    }

    public List<string> GetCreatedSongsRootDirectories()
    {
        var dirs = new List<string>();

        if (!string.IsNullOrWhiteSpace(AppConfig.Current.CustomSongsPath) && Directory.Exists(AppConfig.Current.CustomSongsPath))
            dirs.Add(AppConfig.Current.CustomSongsPath);

        dirs.Add(SongCreatorService.ResolveSongsDirectory());

        if (!string.IsNullOrWhiteSpace(GamePath) && Directory.Exists(GamePath))
        {
            dirs.Add(Path.Combine(GamePath, "Custom_Songs"));
            dirs.Add(Path.Combine(GamePath, "Staging_Downloads"));
        }

        var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        dirs.Add(Path.Combine(docs, "Guitar Hero Songs"));

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        dirs.Add(Path.Combine(appData, "GH3-Sync", "CreatedSongs"));
        dirs.Add(Path.Combine(appData, "GH3-Sync", "Staging_Downloads"));

        try
        {
            if (Directory.Exists(@"G:\Guitar Hero Songs")) dirs.Add(@"G:\Guitar Hero Songs");
        }
        catch { }

        if (AppConfig.Current.CreatedSongDirectories != null)
        {
            foreach (var d in AppConfig.Current.CreatedSongDirectories)
            {
                if (Directory.Exists(d))
                {
                    dirs.Add(d);
                    var parent = Directory.GetParent(d)?.FullName;
                    if (!string.IsNullOrEmpty(parent) && Directory.Exists(parent))
                        dirs.Add(parent);
                }
            }
        }

        return dirs.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToList();
    }

    public static CustomSongInfo? ParseProjectDirectory(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return null;

            var iniFile = Path.Combine(dirPath, "song.ini");
            var ghprojFiles = Directory.GetFiles(dirPath, "*.ghproj");
            var chartFiles = Directory.GetFiles(dirPath, "*.chart");
            var midFiles = Directory.GetFiles(dirPath, "*.mid");

            if (!File.Exists(iniFile) && ghprojFiles.Length == 0 && chartFiles.Length == 0 && midFiles.Length == 0)
                return null;

            string title = "";
            string artist = "";
            string songId = "";
            string compiledSongId = "";
            string charter = "";
            string year = "";
            string album = "";
            bool isOnogami = false;
            bool isCloneHero = false;

            // 1. Try reading song.ini
            if (File.Exists(iniFile))
            {
                var lines = File.ReadAllLines(iniFile);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (!trimmed.Contains('=')) continue;
                    var eq = trimmed.IndexOf('=');
                    var key = trimmed.Substring(0, eq).Trim();
                    var val = trimmed.Substring(eq + 1).Trim();

                    if (key.Equals("name", StringComparison.OrdinalIgnoreCase) || key.Equals("title", StringComparison.OrdinalIgnoreCase))
                        title = val;
                    else if (key.Equals("artist", StringComparison.OrdinalIgnoreCase))
                        artist = val;
                    else if (key.Equals("charter", StringComparison.OrdinalIgnoreCase))
                        charter = val;
                    else if (key.Equals("year", StringComparison.OrdinalIgnoreCase))
                        year = val;
                    else if (key.Equals("album", StringComparison.OrdinalIgnoreCase))
                        album = val;
                    else if (key.Equals("compiled_song_id", StringComparison.OrdinalIgnoreCase))
                        compiledSongId = val;
                    else if (key.Equals("import_source", StringComparison.OrdinalIgnoreCase) && val.Equals("clone_hero", StringComparison.OrdinalIgnoreCase))
                        isCloneHero = true;
                    else if (key.Equals("icon", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Equals("onogami_creator", StringComparison.OrdinalIgnoreCase) || val.Equals("gh3sync_creator", StringComparison.OrdinalIgnoreCase))
                            isOnogami = true;
                        else if (val.Equals("clone_hero_import", StringComparison.OrdinalIgnoreCase))
                            isCloneHero = true;
                    }
                }
            }

            // 2. Try reading .ghproj
            if (ghprojFiles.Length > 0)
            {
                try
                {
                    var json = File.ReadAllText(ghprojFiles[0]);
                    using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                    var root = doc.RootElement;
                    if (string.IsNullOrEmpty(title) && root.TryGetProperty("title", out var tProp))
                        title = tProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(artist) && root.TryGetProperty("artist", out var aProp))
                        artist = aProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(songId) && root.TryGetProperty("songName", out var sProp))
                        songId = sProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(charter) && root.TryGetProperty("chartAuthor", out var cProp))
                        charter = cProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(album) && root.TryGetProperty("album", out var alProp))
                        album = alProp.GetString() ?? "";
                    if (root.TryGetProperty("gameIcon", out var giProp))
                    {
                        var gi = giProp.GetString() ?? "";
                        if (gi.Equals("clone_hero_import", StringComparison.OrdinalIgnoreCase))
                            isCloneHero = true;
                        else if (gi.Equals("onogami_creator", StringComparison.OrdinalIgnoreCase) || gi.Equals("gh3sync_creator", StringComparison.OrdinalIgnoreCase))
                            isOnogami = true;
                    }
                }
                catch { }
            }

            // 3. Detect legacy standalone *_song.pak.xen inside folder to resolve actual compiled checksum ID
            var standalonePaks = Directory.GetFiles(dirPath, "*_song.pak.xen");
            if (string.IsNullOrWhiteSpace(compiledSongId) && standalonePaks.Length > 0)
            {
                var pName = Path.GetFileName(standalonePaks[0]);
                compiledSongId = pName.Substring(0, pName.Length - "_song.pak.xen".Length);
            }
            if (!string.IsNullOrWhiteSpace(compiledSongId))
            {
                songId = compiledSongId;
            }

            var dirName = Path.GetFileName(dirPath);
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist))
            {
                if (dirName.Contains(" - "))
                {
                    var parts = dirName.Split(new[] { " - " }, 2, StringSplitOptions.None);
                    if (string.IsNullOrWhiteSpace(artist)) artist = parts[0].Trim();
                    if (string.IsNullOrWhiteSpace(title)) title = parts[1].Trim();
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(title)) title = dirName;
                    if (string.IsNullOrWhiteSpace(artist)) artist = "Custom Artist";
                }
            }

            if (string.IsNullOrWhiteSpace(songId))
            {
                var sb = new System.Text.StringBuilder();
                foreach (var c in title.ToLowerInvariant())
                {
                    if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
                }
                songId = sb.Length > 0 ? sb.ToString() : SanitizeFileName(title).ToLowerInvariant();
            }

            if (charter.Contains("Clone Hero", StringComparison.OrdinalIgnoreCase))
            {
                isCloneHero = true;
            }

            if (!isCloneHero &&
                (charter.Contains("ONOGAMI", StringComparison.OrdinalIgnoreCase) ||
                 charter.Contains("GH3-Sync", StringComparison.OrdinalIgnoreCase) ||
                 AppConfig.Current.CreatedSongIds.Any(id => songId.Equals(id, StringComparison.OrdinalIgnoreCase))))
            {
                isOnogami = true;
            }
            if (isCloneHero) isOnogami = false;

            long totalBytes = 0;
            DateTime lastWrite = Directory.GetLastWriteTimeUtc(dirPath);
            try
            {
                foreach (var f in Directory.GetFiles(dirPath, "*.*", SearchOption.AllDirectories))
                {
                    var fi = new FileInfo(f);
                    totalBytes += fi.Length;
                    if (fi.LastWriteTimeUtc > lastWrite) lastWrite = fi.LastWriteTimeUtc;
                }
            }
            catch { }

            var ghprojPath = ghprojFiles.FirstOrDefault() ?? "";
            bool hasStandalonePak = standalonePaks.Length > 0 || !string.IsNullOrWhiteSpace(compiledSongId);

            // Detect album art thumbnail
            string albumArt = "";
            foreach (var artName in new[] { "album.png", "album.jpg", "album.jpeg", "cover.png", "cover.jpg" })
            {
                var ap = Path.Combine(dirPath, artName);
                if (File.Exists(ap)) { albumArt = ap; break; }
            }

            // Detect audio mix mode tag
            string audioModeTag = "";
            try
            {
                var cfgPath = Path.Combine(dirPath, "gh3sync_audio.json");
                if (File.Exists(cfgPath))
                {
                    var cfg = JsonSerializer.Deserialize<SongAudioMixConfig>(File.ReadAllText(cfgPath));
                    audioModeTag = cfg?.AudioMixMode switch
                    {
                        "IsolatedStems" => "Multi-Stem",
                        "HybridBlend" => "Hybrid Audio",
                        "StudioMaster" => "Studio Master",
                        _ => ""
                    };
                }
            }
            catch { }

            // Fast scan notes.chart for Co-Op Bass & Expert note count
            bool hasCoopBass = false;
            int expertNotes = 0;
            try
            {
                var chartFile = Path.Combine(dirPath, "notes.chart");
                if (File.Exists(chartFile))
                {
                    bool inExpert = false;
                    foreach (var line in File.ReadLines(chartFile))
                    {
                        var tr = line.Trim();
                        if (tr.Equals("[ExpertSingle]", StringComparison.OrdinalIgnoreCase)) { inExpert = true; continue; }
                        if (tr.StartsWith("["))
                        {
                            inExpert = false;
                            if (tr.Equals("[ExpertDoubleBass]", StringComparison.OrdinalIgnoreCase)) hasCoopBass = true;
                            continue;
                        }
                        if (inExpert && tr.Contains("= N ")) expertNotes++;
                    }
                }
            }
            catch { }

            return new CustomSongInfo
            {
                SongId = songId,
                Title = title,
                Artist = artist,
                Charter = charter,
                Year = year,
                Album = album,
                PakSizeBytes = totalBytes,
                FsbSizeBytes = 0,
                LastModified = lastWrite,
                IsCreatedLocally = !isCloneHero,
                CreatedWithOnogamiCreator = isOnogami,
                IsCloneHeroImport = isCloneHero,
                OnogamiFingerprint = ComputeFingerprint(title, artist),
                IsProject = true,
                ProjectDirectory = dirPath,
                IsCompiled = hasStandalonePak,
                GhprojPath = ghprojPath,
                AlbumArtPath = albumArt,
                AudioModeTag = audioModeTag,
                HasCoopBass = hasCoopBass,
                ExpertNoteCount = expertNotes
            };
        }
        catch
        {
            return null;
        }
    }

    public string? FindProjectDirectory(string songId)
    {
        var roots = GetCreatedSongsRootDirectories();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            var direct = ParseProjectDirectory(root);
            if (direct != null && string.Equals(direct.SongId, songId, StringComparison.OrdinalIgnoreCase))
                return root;

            try
            {
                foreach (var sub in Directory.GetDirectories(root))
                {
                    var p = ParseProjectDirectory(sub);
                    if (p != null && (string.Equals(p.SongId, songId, StringComparison.OrdinalIgnoreCase) ||
                                     Path.GetFileName(sub).Contains(songId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return sub;
                    }
                }
            }
            catch { }
        }

        if (AppConfig.Current.CreatedSongDirectories != null)
        {
            foreach (var d in AppConfig.Current.CreatedSongDirectories)
            {
                if (Directory.Exists(d))
                {
                    var p = ParseProjectDirectory(d);
                    if (p != null && string.Equals(p.SongId, songId, StringComparison.OrdinalIgnoreCase))
                        return d;
                }
            }
        }

        return null;
    }

    private static readonly string SongMetaCachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GH3-Sync", "song_meta_cache.json");

    private static Dictionary<string, ImportedSongMetaRecord> LoadSongMetaCache()
    {
        try
        {
            if (File.Exists(SongMetaCachePath))
            {
                var json = File.ReadAllText(SongMetaCachePath);
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ImportedSongMetaRecord>>(json);
                if (loaded != null)
                    return new Dictionary<string, ImportedSongMetaRecord>(loaded, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
        return new Dictionary<string, ImportedSongMetaRecord>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveSongMetaCacheRecord(ImportedSongMetaRecord rec)
    {
        if (string.IsNullOrWhiteSpace(rec.SongId)) return;
        try
        {
            var cache = LoadSongMetaCache();
            cache[rec.SongId] = rec;
            var dir = Path.GetDirectoryName(SongMetaCachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SongMetaCachePath, JsonSerializer.Serialize(cache, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>
    /// Detects whether the user has completed the one-time custom save setup in GH3.
    /// Returns true if Documents\Aspyr\Guitar Hero III\Save\Honeycomb}{Honeycomb\s000.d does not exist yet.
    /// </summary>
    public bool IsFirstTimeCustomSetupNeeded()
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var customSaveFile = Path.Combine(docs, "Aspyr", "Guitar Hero III", "Save", "Honeycomb}{Honeycomb", "s000.d");
            if (File.Exists(customSaveFile)) return false;

            // Also check if DATA\customs.pak.xen already has custom songs beyond vanilla DLC
            var pakMap = CustomsPakService.ReadSongMetadataMap(GamePath);
            bool hasNonDlc = pakMap.Keys.Any(k => !IsVanillaSong(k));
            return !hasNonDlc;
        }
        catch
        {
            return false;
        }
    }

    public List<CustomSongInfo> GetInstalledCustomSongs()
    {
        var songs = new List<CustomSongInfo>();
        var seenSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pakMetaMap = IsDetected ? CustomsPakService.ReadSongMetadataMap(GamePath) : new Dictionary<string, CustomsPakSongEntry>(StringComparer.OrdinalIgnoreCase);
        var metaCache = LoadSongMetaCache();

        // 1. Scan compiled game customs in DATA/SONGS
        if (IsDetected && Directory.Exists(SongsPath))
        {
            try
            {
                var songPakFiles = Directory.GetFiles(SongsPath, "*_song.pak.xen");
                foreach (var pakFile in songPakFiles)
                {
                    var fileName = Path.GetFileName(pakFile);
                    var songId = fileName.Substring(0, fileName.Length - "_song.pak.xen".Length);

                    if (IsVanillaSong(songId)) continue;

                    var pakInfo = new FileInfo(pakFile);
                    var fsbFile = Path.Combine(MusicPath, $"{songId}.fsb.xen");
                    var fsbInfo = File.Exists(fsbFile) ? new FileInfo(fsbFile) : null;

                    var (fallbackTitle, fallbackArtist, isLocalFallback) = ResolveSongMetadata(songId);
                    string title = fallbackTitle;
                    string artist = fallbackArtist;
                    string charter = "";
                    string year = "";
                    string album = "";
                    bool isCreator = isLocalFallback;
                    bool isCloneHero = false;

                    if (pakMetaMap.TryGetValue(songId, out var pm))
                    {
                        if (!string.IsNullOrWhiteSpace(pm.Title)) title = pm.Title;
                        if (!string.IsNullOrWhiteSpace(pm.Artist)) artist = pm.Artist;
                        charter = pm.Charter;
                        year = pm.Year;
                        album = pm.Album;
                        if (charter.Contains("ONOGAMI", StringComparison.OrdinalIgnoreCase) ||
                            charter.Contains("GH3-Sync", StringComparison.OrdinalIgnoreCase))
                            isCreator = true;
                        if (charter.Contains("Clone Hero", StringComparison.OrdinalIgnoreCase))
                        {
                            isCloneHero = true;
                            isCreator = false;
                        }
                    }

                    if (metaCache.TryGetValue(songId, out var cached))
                    {
                        if (!string.IsNullOrWhiteSpace(cached.Title) && (title == fallbackTitle || title.Equals("song", StringComparison.OrdinalIgnoreCase)))
                            title = cached.Title;
                        if (!string.IsNullOrWhiteSpace(cached.Artist) && artist == "Custom Artist")
                            artist = cached.Artist;
                        if (string.IsNullOrWhiteSpace(charter)) charter = cached.Charter;
                        if (string.IsNullOrWhiteSpace(year)) year = cached.Year;
                        if (string.IsNullOrWhiteSpace(album)) album = cached.Album;
                        if (cached.CreatedWithCreator) isCreator = true;
                        if (cached.IsCloneHeroImport) { isCloneHero = true; isCreator = false; }
                    }

                    if (!isCloneHero && AppConfig.Current.CreatedSongIds.Any(id => songId.Equals(id, StringComparison.OrdinalIgnoreCase)))
                    {
                        isCreator = true;
                    }

                    songs.Add(new CustomSongInfo
                    {
                        SongId = songId,
                        Title = title,
                        Artist = artist,
                        Charter = charter,
                        Year = year,
                        Album = album,
                        PakSizeBytes = pakInfo.Length,
                        FsbSizeBytes = fsbInfo?.Length ?? 0,
                        LastModified = pakInfo.LastWriteTimeUtc,
                        IsCreatedLocally = isCreator,
                        CreatedWithOnogamiCreator = isCreator,
                        IsCloneHeroImport = isCloneHero,
                        OnogamiFingerprint = ComputeFingerprint(title, artist),
                        IsProject = false,
                        IsCompiled = true
                    });
                    seenSongIds.Add(songId);
                }
            }
            catch { }
        }

        // 2. Scan created/project songs across all songs directories and merge cleanly (zero duplicates!)
        var rootDirs = GetCreatedSongsRootDirectories();
        foreach (var root in rootDirs)
        {
            if (!Directory.Exists(root)) continue;

            var directSong = ParseProjectDirectory(root);
            if (directSong != null)
            {
                AddOrMergeSong(directSong, songs, seenSongIds);
            }

            try
            {
                foreach (var subDir in Directory.GetDirectories(root))
                {
                    var projectSong = ParseProjectDirectory(subDir);
                    if (projectSong != null)
                    {
                        AddOrMergeSong(projectSong, songs, seenSongIds);
                    }
                }
            }
            catch { }
        }

        if (AppConfig.Current.CreatedSongDirectories != null)
        {
            foreach (var regDir in AppConfig.Current.CreatedSongDirectories)
            {
                if (Directory.Exists(regDir))
                {
                    var projectSong = ParseProjectDirectory(regDir);
                    if (projectSong != null)
                    {
                        AddOrMergeSong(projectSong, songs, seenSongIds);
                    }
                }
            }
        }

        var favs = AppConfig.Current.FavoriteSongIds ?? new List<string>();
        var lobby = AppConfig.Current.LobbySetlistIds ?? new List<string>();
        var tempIds = TemporarySessionService.GetActiveTempSongIds();
        foreach (var s in songs)
        {
            s.IsFavorite = favs.Any(f => f.Equals(s.SongId, StringComparison.OrdinalIgnoreCase));
            s.IsInLobbySetlist = lobby.Any(l => l.Equals(s.SongId, StringComparison.OrdinalIgnoreCase));
            s.IsTemporarySessionSong = tempIds.Contains(s.SongId);
        }

        return songs;
    }

    public static string? FindSongAudioPath(CustomSongInfo song, bool preferGuitarStem = false)
    {
        if (song == null || string.IsNullOrWhiteSpace(song.ProjectDirectory) || !Directory.Exists(song.ProjectDirectory))
            return null;

        if (preferGuitarStem)
        {
            var guitarStem = Directory.GetFiles(song.ProjectDirectory, "guitar.wav", SearchOption.AllDirectories).FirstOrDefault()
                          ?? Directory.GetFiles(song.ProjectDirectory, "guitar.ogg", SearchOption.AllDirectories).FirstOrDefault()
                          ?? Directory.GetFiles(song.ProjectDirectory, "other.wav", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrEmpty(guitarStem)) return guitarStem;
        }

        foreach (var name in new[] { "song.mp3", "song.wav", "song.ogg", "guitar.mp3", "guitar.ogg", "guitar.wav" })
        {
            var p = Path.Combine(song.ProjectDirectory, name);
            if (File.Exists(p)) return p;
        }

        return Directory.GetFiles(song.ProjectDirectory)
            .FirstOrDefault(f => f.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase));
    }

    private static void AddOrMergeSong(CustomSongInfo projectSong, List<CustomSongInfo> songs, HashSet<string> seenSongIds)
    {
        var existing = songs.FirstOrDefault(s =>
            s.SongId.Equals(projectSong.SongId, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(projectSong.OnogamiFingerprint) &&
             s.OnogamiFingerprint.Equals(projectSong.OnogamiFingerprint, StringComparison.OrdinalIgnoreCase)));

        if (existing != null)
        {
            // Keep the compiled game song entry as primary, enrich it with project metadata & directory
            if (string.IsNullOrWhiteSpace(existing.ProjectDirectory) || projectSong.LastModified > existing.LastModified)
            {
                existing.ProjectDirectory = projectSong.ProjectDirectory;
                if (!string.IsNullOrEmpty(projectSong.GhprojPath)) existing.GhprojPath = projectSong.GhprojPath;
            }
            if (projectSong.IsCloneHeroImport)
            {
                existing.IsCloneHeroImport = true;
                existing.CreatedWithOnogamiCreator = false;
                existing.IsCreatedLocally = false;
            }
            else if (projectSong.CreatedWithOnogamiCreator)
            {
                existing.CreatedWithOnogamiCreator = true;
                existing.IsCreatedLocally = true;
            }
            if (!string.IsNullOrWhiteSpace(projectSong.Charter) && string.IsNullOrWhiteSpace(existing.Charter))
                existing.Charter = projectSong.Charter;
            if (!string.IsNullOrWhiteSpace(projectSong.Title) &&
                (string.IsNullOrWhiteSpace(existing.Title) ||
                 existing.Title.Equals("song", StringComparison.OrdinalIgnoreCase) ||
                 existing.Title.Equals(existing.SongId, StringComparison.OrdinalIgnoreCase)))
                existing.Title = projectSong.Title;
            if (!string.IsNullOrWhiteSpace(projectSong.Artist) &&
                (string.IsNullOrWhiteSpace(existing.Artist) || existing.Artist == "Custom Artist"))
                existing.Artist = projectSong.Artist;
            if (!string.IsNullOrEmpty(projectSong.AlbumArtPath)) existing.AlbumArtPath = projectSong.AlbumArtPath;
            if (!string.IsNullOrEmpty(projectSong.AudioModeTag)) existing.AudioModeTag = projectSong.AudioModeTag;
            if (projectSong.HasCoopBass) existing.HasCoopBass = true;
            if (projectSong.ExpertNoteCount > 0) existing.ExpertNoteCount = projectSong.ExpertNoteCount;
        }
        else
        {
            songs.Add(projectSong);
            seenSongIds.Add(projectSong.SongId);
        }
    }

    public CustomSongInfo? FindDuplicateInstalledSong(string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        string fp = ComputeFingerprint(title, artist ?? "");
        var installed = GetInstalledCustomSongs();
        return installed.FirstOrDefault(s =>
            s.IsCompiled &&
            ((!string.IsNullOrEmpty(fp) && s.OnogamiFingerprint.Equals(fp, StringComparison.OrdinalIgnoreCase)) ||
             (s.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase) &&
              (string.IsNullOrWhiteSpace(artist) || s.Artist.Equals(artist.Trim(), StringComparison.OrdinalIgnoreCase)))));
    }

    public LibraryManifest GenerateManifest()
    {
        var manifest = new LibraryManifest
        {
            MaxLobbyAutoSyncSongs = Math.Clamp(AppConfig.Current.MaxLobbyAutoSyncSongs, 1, 25),
            LobbySetlistIds = (AppConfig.Current.LobbySetlistIds ?? new List<string>()).ToList()
        };
        if (File.Exists(CustomsPakPath))
        {
            var fi = new FileInfo(CustomsPakPath);
            manifest.CustomsPakSize = fi.Length;
            manifest.CustomsPakHash = ComputeSha256(CustomsPakPath);
        }

        var songs = GetInstalledCustomSongs();
        foreach (var song in songs)
        {
            var pakFile = Path.Combine(SongsPath, $"{song.SongId}_song.pak.xen");
            var fsbFile = Path.Combine(MusicPath, $"{song.SongId}.fsb.xen");

            if (File.Exists(pakFile)) song.PakHash = ComputeSha256(pakFile);
            if (File.Exists(fsbFile)) song.FsbHash = ComputeSha256(fsbFile);

            if (song.IsProject && !string.IsNullOrEmpty(song.ProjectDirectory))
            {
                var iniPath = Path.Combine(song.ProjectDirectory, "song.ini");
                if (File.Exists(iniPath)) song.PakHash = ComputeSha256(iniPath);
            }

            manifest.Songs.Add(song);
        }

        return manifest;
    }

    public byte[] PackageSong(string songId)
    {
        var pakFile = Path.Combine(SongsPath, $"{songId}_song.pak.xen");
        var fsbFile = Path.Combine(MusicPath, $"{songId}.fsb.xen");
        var datFile = Path.Combine(MusicPath, $"{songId}.dat.xen");

        var installedSong = GetInstalledCustomSongs()
            .FirstOrDefault(s => s.SongId.Equals(songId, StringComparison.OrdinalIgnoreCase));

        if (File.Exists(pakFile))
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                archive.CreateEntryFromFile(pakFile, Path.GetFileName(pakFile));
                if (File.Exists(fsbFile)) archive.CreateEntryFromFile(fsbFile, Path.GetFileName(fsbFile));
                if (File.Exists(datFile)) archive.CreateEntryFromFile(datFile, Path.GetFileName(datFile));

                var (fbTitle, fbArtist, fbLocal) = ResolveSongMetadata(songId);
                string title = installedSong?.Title ?? fbTitle;
                string artist = installedSong?.Artist ?? fbArtist;
                string charter = installedSong?.Charter ?? "";
                string year = installedSong?.Year ?? "";
                string album = installedSong?.Album ?? "";
                bool createdWithCreator = installedSong?.CreatedWithOnogamiCreator ?? fbLocal;
                bool isCloneHero = installedSong?.IsCloneHeroImport ?? false;

                var metaJson = JsonSerializer.Serialize(new
                {
                    songId,
                    title,
                    artist,
                    charter,
                    year,
                    album,
                    isProject = false,
                    createdWithCreator,
                    createdWithOnogamiCreator = createdWithCreator,
                    isCloneHeroImport = isCloneHero,
                    pakHash = ComputeSha256(pakFile),
                    fsbHash = File.Exists(fsbFile) ? ComputeSha256(fsbFile) : "",
                    onogamiFingerprint = ComputeFingerprint(title, artist),
                    exportedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true });

                var metaEntry = archive.CreateEntry("metadata.json");
                using var writer = new StreamWriter(metaEntry.Open());
                writer.Write(metaJson);
            }

            return ms.ToArray();
        }

        var projectDir = FindProjectDirectory(songId);
        if (projectDir != null && Directory.Exists(projectDir))
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                var proj = ParseProjectDirectory(projectDir);
                foreach (var file in Directory.GetFiles(projectDir, "*.*", SearchOption.TopDirectoryOnly))
                {
                    archive.CreateEntryFromFile(file, Path.GetFileName(file));
                }

                var metaJson = JsonSerializer.Serialize(new
                {
                    songId,
                    title = proj?.Title ?? songId,
                    artist = proj?.Artist ?? "Custom Artist",
                    charter = proj?.Charter ?? "",
                    year = proj?.Year ?? "",
                    album = proj?.Album ?? "",
                    isProject = true,
                    createdWithCreator = proj?.CreatedWithOnogamiCreator ?? false,
                    createdWithOnogamiCreator = proj?.CreatedWithOnogamiCreator ?? false,
                    isCloneHeroImport = proj?.IsCloneHeroImport ?? false,
                    onogamiFingerprint = proj?.OnogamiFingerprint ?? ComputeFingerprint(songId, ""),
                    exportedAt = DateTime.UtcNow
                }, new JsonSerializerOptions { WriteIndented = true });

                var metaEntry = archive.CreateEntry("metadata.json");
                using var writer = new StreamWriter(metaEntry.Open());
                writer.Write(metaJson);
            }

            return ms.ToArray();
        }

        throw new FileNotFoundException($"Song package not found for ID: {songId}");
    }

    public ImportedSongMetaRecord? InstallSongPackage(
        byte[] zipBytes,
        SyncLogger logger,
        string? expectedPakHash = null,
        string? expectedFsbHash = null,
        bool mergeIntoCustomsPak = true)
    {
        ImportedSongMetaRecord? meta = null;
        string detectedSongId = "";

        using (var testMs = new MemoryStream(zipBytes))
        using (var testArchive = new ZipArchive(testMs, ZipArchiveMode.Read))
        {
            var metaEntry = testArchive.GetEntry("metadata.json");
            if (metaEntry != null)
            {
                try
                {
                    using var s = metaEntry.Open();
                    using var doc = JsonDocument.Parse(s);
                    var r = doc.RootElement;
                    string GetS(string k) => r.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? (p.GetString() ?? "") : "";
                    bool GetB(string k) => r.TryGetProperty(k, out var p) && (p.ValueKind == JsonValueKind.True || p.ValueKind == JsonValueKind.False) && p.GetBoolean();

                    meta = new ImportedSongMetaRecord
                    {
                        SongId = GetS("songId"),
                        Title = GetS("title"),
                        Artist = GetS("artist"),
                        Charter = GetS("charter"),
                        Year = GetS("year"),
                        Album = GetS("album"),
                        CreatedWithCreator = GetB("createdWithCreator") || GetB("createdWithOnogamiCreator"),
                        IsCloneHeroImport = GetB("isCloneHeroImport")
                    };
                    if (string.IsNullOrWhiteSpace(expectedPakHash)) expectedPakHash = GetS("pakHash");
                    if (string.IsNullOrWhiteSpace(expectedFsbHash)) expectedFsbHash = GetS("fsbHash");
                }
                catch { }
            }

            foreach (var e in testArchive.Entries)
            {
                if (e.Name.EndsWith("_song.pak.xen", StringComparison.OrdinalIgnoreCase))
                {
                    detectedSongId = e.Name.Substring(0, e.Name.Length - "_song.pak.xen".Length);
                    break;
                }
            }

            bool isProject = testArchive.Entries.Any(e =>
                e.Name.EndsWith(".ghproj", StringComparison.OrdinalIgnoreCase) ||
                e.Name.Equals("song.ini", StringComparison.OrdinalIgnoreCase));

            if (isProject)
            {
                var targetRoot = SongCreatorService.ResolveSongsDirectory();
                string folderName = "Imported_Song_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
                if (meta != null && !string.IsNullOrWhiteSpace(meta.Artist) && !string.IsNullOrWhiteSpace(meta.Title))
                {
                    folderName = $"{meta.Artist} - {meta.Title}";
                }

                var targetDir = Path.Combine(targetRoot, SanitizeFileName(folderName));
                SecurityService.ExtractProjectZip(zipBytes, targetDir, logger);
                if (meta != null && meta.CreatedWithCreator)
                    AppConfig.RegisterCreatedSong(meta.SongId, targetDir);
                logger.LogInfo($"[IMPORT] Song project installed to: {targetDir}");
                return meta;
            }
        }

        string effectiveSongId = !string.IsNullOrWhiteSpace(detectedSongId) ? detectedSongId : (meta?.SongId ?? "");

        if (AppConfig.Current.DirectInstallToGame)
        {
            if (!IsDetected) throw new InvalidOperationException("GH3 game directory is not detected.");
            SecurityService.ValidateAndExtractZip(zipBytes, SongsPath, MusicPath, logger);

            // SHA-256 verification on extracted game binaries
            if (!string.IsNullOrWhiteSpace(effectiveSongId))
            {
                var pakFile = Path.Combine(SongsPath, $"{effectiveSongId}_song.pak.xen");
                var fsbFile = Path.Combine(MusicPath, $"{effectiveSongId}.fsb.xen");
                if (!string.IsNullOrWhiteSpace(expectedPakHash) && File.Exists(pakFile) && !SecurityService.VerifyFileSha256(pakFile, expectedPakHash))
                {
                    try { File.Delete(pakFile); } catch { }
                    throw new System.Security.SecurityException($"SHA-256 verification failed for {Path.GetFileName(pakFile)}.");
                }
                if (!string.IsNullOrWhiteSpace(expectedFsbHash) && File.Exists(fsbFile) && !SecurityService.VerifyFileSha256(fsbFile, expectedFsbHash))
                {
                    try { File.Delete(fsbFile); } catch { }
                    throw new System.Security.SecurityException($"SHA-256 verification failed for {Path.GetFileName(fsbFile)}.");
                }

                meta ??= new ImportedSongMetaRecord { SongId = effectiveSongId, Title = effectiveSongId, Artist = "Custom Artist" };
                meta.SongId = effectiveSongId;
                SaveSongMetaCacheRecord(meta);

                if (mergeIntoCustomsPak)
                {
                    CustomsPakService.MergeSongIntoCustomsPak(
                        GamePath,
                        effectiveSongId,
                        string.IsNullOrWhiteSpace(meta.Title) ? effectiveSongId : meta.Title,
                        string.IsNullOrWhiteSpace(meta.Artist) ? "Custom Artist" : meta.Artist,
                        meta.Charter,
                        meta.Year,
                        meta.Album,
                        logger);
                }
            }
        }
        else
        {
            if (!Directory.Exists(StagingPath)) Directory.CreateDirectory(StagingPath);
            var stagingSongs = Path.Combine(StagingPath, "SONGS");
            var stagingMusic = Path.Combine(StagingPath, "MUSIC");
            Directory.CreateDirectory(stagingSongs);
            Directory.CreateDirectory(stagingMusic);
            SecurityService.ValidateAndExtractZip(zipBytes, stagingSongs, stagingMusic, logger);
            logger.LogInfo($"[STAGING] Song installed to staging folder: {StagingPath}");
        }

        return meta;
    }

    private static string SanitizeFileName(string input)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in input)
        {
            if (Array.IndexOf(invalid, c) < 0 && c != '"' && c != '”' && c != '“')
                sb.Append(c);
            else
                sb.Append('_');
        }
        return sb.ToString().Trim();
    }

    public int ImportArchiveFile(byte[] archiveBytes, string fileName, SyncLogger logger)
    {
        if (!IsDetected) throw new InvalidOperationException("GH3 game directory is not detected.");

        int count = 0;
        using var ms = new MemoryStream(archiveBytes);
        using var archive = new ZipArchive(ms, ZipArchiveMode.Read);

        bool hasNestedSongs = archive.Entries.Any(e => e.Name.EndsWith(".gh3song", StringComparison.OrdinalIgnoreCase));
        if (hasNestedSongs)
        {
            // Only deploy a bundled customs.pak.xen if the user does NOT already have one;
            // otherwise preserve their existing customs.pak.xen and merge each song cleanly!
            if (!File.Exists(CustomsPakPath))
            {
                var pakEntry = archive.Entries.FirstOrDefault(e => e.Name.Equals("customs.pak.xen", StringComparison.OrdinalIgnoreCase));
                if (pakEntry != null)
                {
                    using var cMs = new MemoryStream();
                    using var cStream = pakEntry.Open();
                    cStream.CopyTo(cMs);
                    BackupAndDeployCustomsPak(cMs.ToArray());
                }
            }

            foreach (var entry in archive.Entries)
            {
                if (entry.Name.EndsWith(".gh3song", StringComparison.OrdinalIgnoreCase))
                {
                    using var subMs = new MemoryStream();
                    using var entryStream = entry.Open();
                    entryStream.CopyTo(subMs);
                    InstallSongPackage(subMs.ToArray(), logger, mergeIntoCustomsPak: true);
                    count++;
                }
            }
        }
        else
        {
            InstallSongPackage(archiveBytes, logger, mergeIntoCustomsPak: true);
            count++;
        }

        return count;
    }

    public void BackupAndDeployCustomsPak(byte[] newCustomsPakBytes)
    {
        if (!IsDetected) return;

        if (newCustomsPakBytes.Length < 16 ||
            newCustomsPakBytes[0] != 0xA7 || newCustomsPakBytes[1] != 0xF5 ||
            newCustomsPakBytes[2] != 0x05 || newCustomsPakBytes[3] != 0xC4)
        {
            throw new InvalidDataException("Invalid customs.pak.xen: Corrupted or unauthorized PAK file header.");
        }

        if (File.Exists(CustomsPakPath))
        {
            var backupName = $"customs.pak.xen.bak_{DateTime.UtcNow:yyyyMMdd_HHmmss}";
            File.Copy(CustomsPakPath, Path.Combine(DataPath, backupName), overwrite: true);
        }

        File.WriteAllBytes(CustomsPakPath, newCustomsPakBytes);
    }

    public static string ComputeFingerprint(string title, string artist)
    {
        static string Norm(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var c in (s ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
            }
            return sb.ToString();
        }
        var raw = $"{Norm(title)}|{Norm(artist)}";
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).Substring(0, 16);
    }

    private static (string title, string artist, bool isLocal) ResolveSongMetadata(string songId)
    {
        if (songId.StartsWith("beatit", StringComparison.OrdinalIgnoreCase))
            return ("Beat It", "Michael Jackson", true);
        if (songId.StartsWith("gentlyweeps", StringComparison.OrdinalIgnoreCase))
            return ("While My Guitar Gently Weeps", "Tom Petty, Jeff Lynne, Steve Winwood, Dhani Harrison, & Prince", true);
        if (songId.StartsWith("onogami", StringComparison.OrdinalIgnoreCase))
            return ("ONOGAMI (Dragon Ball Super Beerus OP)", "MEGATERA・ZERO", true);

        var clean = songId;
        var underscoreIdx = clean.IndexOf('_');
        if (underscoreIdx > 0) clean = clean.Substring(0, underscoreIdx);

        return (clean, "Custom Artist", false);
    }

    private static bool IsVanillaSong(string songId)
    {
        string[] vanilla = new[]
        {
            "anarchyintheuk", "avalancha", "barracuda", "beforeiforget", "bellyofashark",
            "blackmagicwoman", "blacksunshine", "bossdevil", "bossslash", "bosstom",
            "bullsonparade", "cantbesaved", "cherubrock", "citiesonflame", "cliffsofdover",
            "closer", "cultofpersonality", "dontholdback", "downndirty", "evenflow",
            "fcpremix", "generationrock", "gothatfar", "helicopter", "hierkommtalex",
            "hitmewithyourbestshot", "holidayincambodia", "imintheband", "impulse", "inlove",
            "knightsofcydonia", "koolthing", "lagrange", "laydown", "mauvaisgarcon",
            "metalheavylady", "minuscelsius", "mississippiqueen", "missmurder", "monsters",
            "mycurse", "mynameisjonas", "nothingformehere", "numberofthebeast", "one",
            "paintitblack", "paranoid", "prayeroftherefugee", "pridenjoy", "radiosong",
            "rainingblood", "reptilia", "rocknrollallnite", "rockulikeahurricane", "ruby",
            "sabotage", "sameoldsonganddance", "schoolsout", "shebangsadrum", "slowride",
            "storyofmylife", "stricken", "suckmykiss", "sunshineofyourlove", "takethislife",
            "talkdirtytome", "themetal", "theseeker", "thewayitends", "threesandsevens",
            "thrufireandflames", "welcometothejungle", "whenyouwereyoung", "synctest",
            "synctestaudioandvisual", "synctestplaytoaudio", "mutetest"
        };

        if (songId.StartsWith("dlc", StringComparison.OrdinalIgnoreCase) && !songId.Contains('_'))
            return true;
        if (songId.StartsWith("tutorial", StringComparison.OrdinalIgnoreCase))
            return true;

        return vanilla.Contains(songId, StringComparer.OrdinalIgnoreCase);
    }

    private static string ComputeSha256(string filePath)
    {
        try
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(filePath);
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash);
        }
        catch
        {
            return "";
        }
    }

    public class DeleteSongResult
    {
        public bool Success { get; set; }
        public List<string> DeletedItems { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
        public string Message { get; set; } = "";
    }

    /// <summary>
    /// Safely removes a custom song and ALL related compiled game files (.pak.xen, .fsb.xen, .dat.xen, previews, scripts),
    /// config registrations, and optionally the ONOGAMI source project folder & stems.
    /// Automatically protects vanilla GH3 songs and backs up career saves first.
    /// </summary>
    public DeleteSongResult DeleteCustomSong(
        CustomSongInfo song,
        bool deleteProjectFolder = true,
        SaveManager? saveManager = null,
        SyncLogger? logger = null)
    {
        var result = new DeleteSongResult();

        if (song == null || string.IsNullOrWhiteSpace(song.SongId))
        {
            result.Message = "Invalid song selection.";
            return result;
        }

        if (IsVanillaSong(song.SongId))
        {
            result.Message = $"Safety block: '{song.SongId}' is a vanilla Guitar Hero III song and cannot be deleted.";
            return result;
        }

        try
        {
            // 1. Auto-backup career saves first
            try
            {
                saveManager?.BackupCurrentSaves($"Before-Delete-{song.SongId}");
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Career save backup note: {ex.Message}");
            }

            string id = song.SongId.Trim();

            // 2. Delete all matching files in DATA/SONGS (and Staging/SONGS)
            foreach (var songsDir in new[] { SongsPath, Path.Combine(StagingPath, "SONGS") })
            {
                if (!Directory.Exists(songsDir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(songsDir))
                    {
                        var name = Path.GetFileName(file);
                        if (name.Equals($"{id}_song.pak.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals($"{id}_song.pab.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals($"{id}_song_scripts.pak.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals($"{id}.pak.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith($"{id}_song.", StringComparison.OrdinalIgnoreCase))
                        {
                            File.Delete(file);
                            result.DeletedItems.Add(file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Error cleaning {songsDir}: {ex.Message}");
                }
            }

            // 3. Delete all matching files in DATA/MUSIC (and Staging/MUSIC)
            foreach (var musicDir in new[] { MusicPath, Path.Combine(StagingPath, "MUSIC") })
            {
                if (!Directory.Exists(musicDir)) continue;
                try
                {
                    foreach (var file in Directory.GetFiles(musicDir))
                    {
                        var name = Path.GetFileName(file);
                        if (name.Equals($"{id}.fsb.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals($"{id}.dat.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals($"{id}_preview.fsb.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.Equals($"{id}_preview.dat.xen", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith($"{id}_", StringComparison.OrdinalIgnoreCase) &&
                            (name.EndsWith(".fsb.xen", StringComparison.OrdinalIgnoreCase) ||
                             name.EndsWith(".dat.xen", StringComparison.OrdinalIgnoreCase)))
                        {
                            File.Delete(file);
                            result.DeletedItems.Add(file);
                        }
                    }
                }
                catch (Exception ex)
                {
                    result.Warnings.Add($"Error cleaning {musicDir}: {ex.Message}");
                }
            }

            // 4. Delete source ONOGAMI project directory & stems if requested
            string? projectDir = !string.IsNullOrWhiteSpace(song.ProjectDirectory)
                ? song.ProjectDirectory
                : FindProjectDirectory(id);

            if (deleteProjectFolder && !string.IsNullOrWhiteSpace(projectDir) && Directory.Exists(projectDir))
            {
                var normProj = Path.GetFullPath(projectDir).TrimEnd('\\', '/');
                var normGame = !string.IsNullOrEmpty(GamePath) ? Path.GetFullPath(GamePath).TrimEnd('\\', '/') : "";
                var normDocs = Path.GetFullPath(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)).TrimEnd('\\', '/');

                // Strict safety check: never delete game root, Documents root, or drive root
                bool isSafeSubfolder =
                    normProj.Length > 10 &&
                    !normProj.Equals(normGame, StringComparison.OrdinalIgnoreCase) &&
                    !normProj.Equals(normDocs, StringComparison.OrdinalIgnoreCase) &&
                    !normProj.EndsWith("\\Custom_Songs", StringComparison.OrdinalIgnoreCase) &&
                    !normProj.EndsWith("\\Guitar Hero Songs", StringComparison.OrdinalIgnoreCase);

                if (isSafeSubfolder)
                {
                    try
                    {
                        Directory.Delete(normProj, recursive: true);
                        result.DeletedItems.Add(normProj + " (entire project folder & stems)");
                    }
                    catch (Exception ex)
                    {
                        result.Warnings.Add($"Could not delete project folder '{normProj}': {ex.Message}");
                    }
                }
                else
                {
                    result.Warnings.Add($"Skipped deleting protected parent folder '{normProj}'.");
                }
            }

            // 5. Strip song cleanly from DATA\customs.pak.xen so no ghost entry remains in-game
            try
            {
                if (CustomsPakService.RemoveSongFromCustomsPak(GamePath, id, logger))
                {
                    result.DeletedItems.Add($"DATA\\customs.pak.xen entry ({id})");
                }
            }
            catch (Exception ex)
            {
                result.Warnings.Add($"Could not strip entry from customs.pak.xen: {ex.Message}");
            }

            // 6. Remove tracking entry from config.json
            AppConfig.UnregisterCreatedSong(id, projectDir);

            result.Success = true;
            result.Message = $"Successfully removed '{song.Title}' ({result.DeletedItems.Count} item(s) deleted).";
            logger?.LogInfo($"[DELETE SONG] {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"Failed to delete song '{song.Title}': {ex.Message}";
            logger?.LogWarn($"[DELETE SONG] {result.Message}");
            return result;
        }
    }

    /// <summary>
    /// Cleans up unnecessary temporary/intermediate conversion MP3s, duplicate compiled .fsb/.pak/.dat files
    /// sitting inside Custom_Songs project folders, redundant duplicate project folders, and excess .bak files.
    /// </summary>
    public (int FilesRemoved, long BytesFreed, List<string> Summary) CleanDuplicateAndTempFiles(SyncLogger? logger = null)
    {
        int removed = 0;
        long bytesFreed = 0;
        var summary = new List<string>();

        void TryDeleteFile(string path, string reason)
        {
            try
            {
                if (!File.Exists(path)) return;
                var len = new FileInfo(path).Length;
                File.Delete(path);
                removed++;
                bytesFreed += len;
                summary.Add($"Removed {Path.GetFileName(path)} ({reason})");
            }
            catch { }
        }

        // 1. Clean intermediate MP3s & duplicate compiled game binaries inside Custom_Songs project folders
        var customSongsDir = Path.Combine(GamePath, "Custom_Songs");
        var dirsToScan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(customSongsDir)) dirsToScan.Add(customSongsDir);
        var configuredSongs = SongCreatorService.ResolveSongsDirectory();
        if (Directory.Exists(configuredSongs)) dirsToScan.Add(configuredSongs);

        var seenByFingerprint = new Dictionary<string, (string Dir, DateTime LastWrite)>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in dirsToScan)
        {
            foreach (var subDir in Directory.GetDirectories(root))
            {
                // Intermediate temp audio files
                foreach (var pat in new[] { "*_conv.mp3", "*_mix.mp3", "temp_*.mp3", "*_guitar.mp3", "*_rhythm.mp3", "*_song.mp3", "*_preview.mp3" })
                {
                    foreach (var f in Directory.GetFiles(subDir, pat))
                        TryDeleteFile(f, "intermediate temp audio");
                }

                // Leftover uncompressed Demucs WAV stems folder (400-500+ MB per song) if song is already compiled in DATA\MUSIC
                var stemsDir = Path.Combine(subDir, "stems");
                if (Directory.Exists(stemsDir))
                {
                    var p = ParseProjectDirectory(subDir);
                    bool isInGame = p != null &&
                        (File.Exists(Path.Combine(MusicPath, $"{p.SongId}.fsb.xen")) ||
                         Directory.GetFiles(MusicPath, "*.fsb.xen").Any() && p.IsCompiled);
                    if (isInGame)
                    {
                        try
                        {
                            long stemBytes = Directory.GetFiles(stemsDir, "*.*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
                            Directory.Delete(stemsDir, recursive: true);
                            removed++;
                            bytesFreed += stemBytes;
                            summary.Add($"Removed uncompressed stems\\ folder in '{Path.GetFileName(subDir)}' ({stemBytes / (1024.0 * 1024.0):F1} MB)");
                        }
                        catch { }
                    }
                }

                // Duplicate compiled game binaries (already deployed in DATA\SONGS and DATA\MUSIC)
                foreach (var pat in new[] { "*_song.pak.xen", "*.fsb.xen", "*.dat.xen", "*.fsb", "*.dat" })
                {
                    foreach (var f in Directory.GetFiles(subDir, pat))
                    {
                        var name = Path.GetFileName(f);
                        bool existsInGame =
                            File.Exists(Path.Combine(SongsPath, name)) ||
                            File.Exists(Path.Combine(MusicPath, name)) ||
                            File.Exists(Path.Combine(MusicPath, name + ".xen"));
                        if (existsInGame)
                        {
                            // Preserve link to compiled_song_id in song.ini before removing duplicate binary
                            if (name.EndsWith("_song.pak.xen", StringComparison.OrdinalIgnoreCase))
                            {
                                var sid = name.Substring(0, name.Length - "_song.pak.xen".Length);
                                var iniPath = Path.Combine(subDir, "song.ini");
                                try
                                {
                                    var lines = File.Exists(iniPath) ? File.ReadAllLines(iniPath).ToList() : new List<string> { "[song]" };
                                    if (!lines.Any(l => l.TrimStart().StartsWith("compiled_song_id", StringComparison.OrdinalIgnoreCase)))
                                    {
                                        lines.Add($"compiled_song_id = {sid}");
                                        File.WriteAllLines(iniPath, lines);
                                    }
                                }
                                catch { }
                            }
                            TryDeleteFile(f, "duplicate compiled game binary");
                        }
                    }
                }

                // Detect redundant duplicate folders for the exact same song (e.g. "Ain't No Sunshine" vs "Aint No Sunshine")
                var parsed = ParseProjectDirectory(subDir);
                if (parsed != null && !string.IsNullOrWhiteSpace(parsed.OnogamiFingerprint))
                {
                    if (seenByFingerprint.TryGetValue(parsed.OnogamiFingerprint, out var prev))
                    {
                        string olderDir = parsed.LastModified >= prev.LastWrite ? prev.Dir : subDir;
                        string newerDir = parsed.LastModified >= prev.LastWrite ? subDir : prev.Dir;
                        seenByFingerprint[parsed.OnogamiFingerprint] = (newerDir, parsed.LastModified >= prev.LastWrite ? parsed.LastModified : prev.LastWrite);

                        try
                        {
                            long dirSize = Directory.GetFiles(olderDir, "*.*", SearchOption.AllDirectories).Sum(x => new FileInfo(x).Length);
                            Directory.Delete(olderDir, recursive: true);
                            removed++;
                            bytesFreed += dirSize;
                            summary.Add($"Removed duplicate folder '{Path.GetFileName(olderDir)}'");
                        }
                        catch { }
                    }
                    else
                    {
                        seenByFingerprint[parsed.OnogamiFingerprint] = (subDir, parsed.LastModified);
                    }
                }
            }
        }

        // 2. Prune excessive customs.pak.xen.bak_* backups in DATA (keep newest 3)
        if (IsDetected && Directory.Exists(DataPath))
        {
            var baks = Directory.GetFiles(DataPath, "customs.pak.xen.bak_*")
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Skip(3)
                .ToList();
            foreach (var oldBak in baks)
                TryDeleteFile(oldBak, "old customs.pak backup");
        }

        logger?.LogInfo($"[CLEANUP] Removed {removed} redundant/temp item(s), freed {bytesFreed / (1024.0 * 1024.0):F1} MB.");
        return (removed, bytesFreed, summary);
    }
}
