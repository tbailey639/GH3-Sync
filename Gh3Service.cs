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
    public long PakSizeBytes { get; set; }
    public long FsbSizeBytes { get; set; }
    public long TotalSizeBytes => PakSizeBytes + FsbSizeBytes;
    public string PakHash { get; set; } = "";
    public string FsbHash { get; set; } = "";
    public DateTime LastModified { get; set; }
    public bool IsCreatedLocally { get; set; } = false;
    public bool CreatedWithOnogamiCreator { get; set; } = false;
    public string OnogamiFingerprint { get; set; } = "";
    public bool IsDuplicate { get; set; } = false;
    public string DuplicateReason { get; set; } = "";
    public bool IsProject { get; set; } = false;
    public string ProjectDirectory { get; set; } = "";
    public bool IsCompiled { get; set; } = true;
    public string GhprojPath { get; set; } = "";
    public bool IsFavorite { get; set; } = false;
    public bool IsInLobbySetlist { get; set; } = false;
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
            string charter = "";
            bool isOnogami = false;

            // 1. Try reading song.ini
            if (File.Exists(iniFile))
            {
                var lines = File.ReadAllLines(iniFile);
                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("name", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        title = trimmed.Substring(trimmed.IndexOf('=') + 1).Trim();
                    else if (trimmed.StartsWith("title", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        title = trimmed.Substring(trimmed.IndexOf('=') + 1).Trim();
                    else if (trimmed.StartsWith("artist", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        artist = trimmed.Substring(trimmed.IndexOf('=') + 1).Trim();
                    else if (trimmed.StartsWith("charter", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                        charter = trimmed.Substring(trimmed.IndexOf('=') + 1).Trim();
                    else if (trimmed.StartsWith("icon", StringComparison.OrdinalIgnoreCase) && trimmed.Contains('='))
                    {
                        var icon = trimmed.Substring(trimmed.IndexOf('=') + 1).Trim();
                        if (icon.Equals("onogami_creator", StringComparison.OrdinalIgnoreCase)) isOnogami = true;
                    }
                }
            }

            // 2. Try reading .ghproj
            if (ghprojFiles.Length > 0)
            {
                isOnogami = true;
                try
                {
                    var json = File.ReadAllText(ghprojFiles[0]);
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    if (string.IsNullOrEmpty(title) && root.TryGetProperty("title", out var tProp))
                        title = tProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(artist) && root.TryGetProperty("artist", out var aProp))
                        artist = aProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(songId) && root.TryGetProperty("songName", out var sProp))
                        songId = sProp.GetString() ?? "";
                    if (string.IsNullOrEmpty(charter) && root.TryGetProperty("chartAuthor", out var cProp))
                        charter = cProp.GetString() ?? "";
                }
                catch { }
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

            if (charter.Contains("ONOGAMI", StringComparison.OrdinalIgnoreCase) ||
                AppConfig.Current.CreatedSongIds.Any(id => songId.Contains(id, StringComparison.OrdinalIgnoreCase) || id.Contains(songId, StringComparison.OrdinalIgnoreCase)))
            {
                isOnogami = true;
            }

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
            bool hasStandalonePak = Directory.GetFiles(dirPath, "*_song.pak.xen").Length > 0;

            return new CustomSongInfo
            {
                SongId = songId,
                Title = title,
                Artist = artist,
                PakSizeBytes = totalBytes,
                FsbSizeBytes = 0,
                LastModified = lastWrite,
                IsCreatedLocally = true,
                CreatedWithOnogamiCreator = isOnogami,
                OnogamiFingerprint = ComputeFingerprint(title, artist),
                IsProject = true,
                ProjectDirectory = dirPath,
                IsCompiled = hasStandalonePak,
                GhprojPath = ghprojPath
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

    public List<CustomSongInfo> GetInstalledCustomSongs()
    {
        var songs = new List<CustomSongInfo>();
        var seenSongIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

                    var (title, artist, isLocal) = ResolveSongMetadata(songId);
                    bool isOnogami = isLocal || AppConfig.Current.CreatedSongIds.Any(id => songId.Contains(id, StringComparison.OrdinalIgnoreCase));

                    songs.Add(new CustomSongInfo
                    {
                        SongId = songId,
                        Title = title,
                        Artist = artist,
                        PakSizeBytes = pakInfo.Length,
                        FsbSizeBytes = fsbInfo?.Length ?? 0,
                        LastModified = pakInfo.LastWriteTimeUtc,
                        IsCreatedLocally = isOnogami,
                        CreatedWithOnogamiCreator = isOnogami,
                        OnogamiFingerprint = ComputeFingerprint(title, artist),
                        IsProject = false
                    });
                    seenSongIds.Add(songId);
                }
            }
            catch { }
        }

        // 2. Scan created/project songs across all songs directories
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
        foreach (var s in songs)
        {
            s.IsFavorite = favs.Any(f => f.Equals(s.SongId, StringComparison.OrdinalIgnoreCase));
            s.IsInLobbySetlist = lobby.Any(l => l.Equals(s.SongId, StringComparison.OrdinalIgnoreCase));
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
        if (seenSongIds.Contains(projectSong.SongId))
        {
            var existing = songs.FirstOrDefault(s => s.SongId.Equals(projectSong.SongId, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsCreatedLocally = true;
                existing.CreatedWithOnogamiCreator = true;
                existing.ProjectDirectory = projectSong.ProjectDirectory;
                if (!string.IsNullOrEmpty(projectSong.GhprojPath)) existing.GhprojPath = projectSong.GhprojPath;
                if (!string.IsNullOrWhiteSpace(projectSong.Title) && projectSong.Title != projectSong.SongId)
                    existing.Title = projectSong.Title;
                if (!string.IsNullOrWhiteSpace(projectSong.Artist) && projectSong.Artist != "Custom Artist")
                    existing.Artist = projectSong.Artist;
            }
        }
        else
        {
            songs.Add(projectSong);
            seenSongIds.Add(projectSong.SongId);
        }
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

        if (File.Exists(pakFile))
        {
            using var ms = new MemoryStream();
            using (var archive = new ZipArchive(ms, ZipArchiveMode.Create, true))
            {
                archive.CreateEntryFromFile(pakFile, Path.GetFileName(pakFile));
                if (File.Exists(fsbFile)) archive.CreateEntryFromFile(fsbFile, Path.GetFileName(fsbFile));
                if (File.Exists(datFile)) archive.CreateEntryFromFile(datFile, Path.GetFileName(datFile));

                var (title, artist, isLocal) = ResolveSongMetadata(songId);
                var metaJson = JsonSerializer.Serialize(new
                {
                    songId,
                    title,
                    artist,
                    isProject = false,
                    createdWithOnogamiCreator = isLocal,
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
                    isProject = true,
                    createdWithOnogamiCreator = true,
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

    public void InstallSongPackage(byte[] zipBytes, SyncLogger logger)
    {
        using (var testMs = new MemoryStream(zipBytes))
        using (var testArchive = new ZipArchive(testMs, ZipArchiveMode.Read))
        {
            bool isProject = testArchive.Entries.Any(e =>
                e.Name.EndsWith(".ghproj", StringComparison.OrdinalIgnoreCase) ||
                e.Name.Equals("song.ini", StringComparison.OrdinalIgnoreCase));

            if (isProject)
            {
                var targetRoot = SongCreatorService.ResolveSongsDirectory();
                string folderName = "Imported_Song_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");

                var metaEntry = testArchive.GetEntry("metadata.json");
                if (metaEntry != null)
                {
                    try
                    {
                        using var s = metaEntry.Open();
                        using var doc = JsonDocument.Parse(s);
                        var title = doc.RootElement.GetProperty("title").GetString();
                        var artist = doc.RootElement.GetProperty("artist").GetString();
                        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
                        {
                            folderName = $"{artist} - {title}";
                        }
                    }
                    catch { }
                }

                var targetDir = Path.Combine(targetRoot, SanitizeFileName(folderName));
                SecurityService.ExtractProjectZip(zipBytes, targetDir, logger);
                AppConfig.RegisterCreatedSong(folderName, targetDir);
                logger.LogInfo($"[CREATOR] ONOGAMI Song Project installed to: {targetDir}");
                return;
            }
        }

        if (AppConfig.Current.DirectInstallToGame)
        {
            if (!IsDetected) throw new InvalidOperationException("GH3 game directory is not detected.");
            SecurityService.ValidateAndExtractZip(zipBytes, SongsPath, MusicPath, logger);
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
            foreach (var entry in archive.Entries)
            {
                if (entry.Name.EndsWith(".gh3song", StringComparison.OrdinalIgnoreCase))
                {
                    using var subMs = new MemoryStream();
                    using var entryStream = entry.Open();
                    entryStream.CopyTo(subMs);
                    InstallSongPackage(subMs.ToArray(), logger);
                    count++;
                }
                else if (entry.Name.Equals("customs.pak.xen", StringComparison.OrdinalIgnoreCase))
                {
                    using var cMs = new MemoryStream();
                    using var cStream = entry.Open();
                    cStream.CopyTo(cMs);
                    BackupAndDeployCustomsPak(cMs.ToArray());
                }
            }
        }
        else
        {
            SecurityService.ValidateAndExtractZip(archiveBytes, SongsPath, MusicPath, logger);
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
        var raw = $"{title.Trim().ToLowerInvariant()}|{artist.Trim().ToLowerInvariant()}";
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

            // 5. Remove tracking entry from config.json
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
}
