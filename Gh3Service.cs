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
}

public class LibraryManifest
{
    public string MachineName { get; set; } = Environment.MachineName;
    public DateTime GeneratedAt { get; set; } = DateTime.UtcNow;
    public string CustomsPakHash { get; set; } = "";
    public long CustomsPakSize { get; set; }
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

    public List<CustomSongInfo> GetInstalledCustomSongs()
    {
        var songs = new List<CustomSongInfo>();
        if (!IsDetected || !Directory.Exists(SongsPath)) return songs;

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
                OnogamiFingerprint = ComputeFingerprint(title, artist)
            });
        }

        return songs;
    }

    public LibraryManifest GenerateManifest()
    {
        var manifest = new LibraryManifest();
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

            manifest.Songs.Add(song);
        }

        return manifest;
    }

    public byte[] PackageSong(string songId)
    {
        var pakFile = Path.Combine(SongsPath, $"{songId}_song.pak.xen");
        var fsbFile = Path.Combine(MusicPath, $"{songId}.fsb.xen");
        var datFile = Path.Combine(MusicPath, $"{songId}.dat.xen");

        if (!File.Exists(pakFile))
            throw new FileNotFoundException($"Song PAK file not found: {pakFile}");

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

    public void InstallSongPackage(byte[] zipBytes, SyncLogger logger)
    {
        if (AppConfig.Current.DirectInstallToGame)
        {
            if (!IsDetected) throw new InvalidOperationException("GH3 game directory is not detected.");
            SecurityService.ValidateAndExtractZip(zipBytes, SongsPath, MusicPath, logger);
        }
        else
        {
            // Staging folder
            if (!Directory.Exists(StagingPath)) Directory.CreateDirectory(StagingPath);
            var stagingSongs = Path.Combine(StagingPath, "SONGS");
            var stagingMusic = Path.Combine(StagingPath, "MUSIC");
            Directory.CreateDirectory(stagingSongs);
            Directory.CreateDirectory(stagingMusic);
            SecurityService.ValidateAndExtractZip(zipBytes, stagingSongs, stagingMusic, logger);
            logger.LogInfo($"[STAGING] Song installed to staging folder: {StagingPath}");
        }
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
}
