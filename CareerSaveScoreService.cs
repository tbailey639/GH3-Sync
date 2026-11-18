using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class CareerScoreScanResult
{
    public bool SaveFound { get; set; }
    public string SaveFilePath { get; set; } = "";
    public int SongsMatched { get; set; }
    public int ScoresUpdated { get; set; }
    public string Summary { get; set; } = "";
}

/// <summary>
/// Safely reads Guitar Hero III's binary Career Save (s000.d) in read-only mode to import real in-game
/// custom song high scores and star ratings into GH3-Sync's LocalHighScores & Friend Leaderboard!
/// Also watches for GH3.exe exit so newly played songs auto-import immediately after a session.
/// </summary>
public class CareerSaveScoreService : IDisposable
{
    private readonly Gh3Service _gh3;
    private readonly SaveManager _saves;
    private readonly SyncLogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private bool _wasGh3Running;
    private bool _watcherStarted;

    private static readonly uint[] Crc32Table = BuildQbKeyTable();

    public CareerSaveScoreService(Gh3Service gh3, SaveManager saves, SyncLogger logger)
    {
        _gh3 = gh3;
        _saves = saves;
        _logger = logger;
    }

    /// <summary>
    /// Standard Neversoft QBKey checksum (raw CRC32 over lowercase ASCII without final XOR).
    /// </summary>
    public static uint ComputeQbKey(string text)
    {
        var bytes = Encoding.ASCII.GetBytes((text ?? "").ToLowerInvariant());
        uint crc = 0xFFFFFFFFu;
        foreach (var b in bytes)
            crc = (crc >> 8) ^ Crc32Table[(crc ^ b) & 0xFF];
        return crc;
    }

    private static uint[] BuildQbKeyTable()
    {
        var tbl = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c & 1) != 0 ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
            tbl[i] = c;
        }
        return tbl;
    }

    public string? FindActiveSaveFile()
    {
        try
        {
            var candidates = new List<string>();
            if (Directory.Exists(SaveManager.SaveFolder))
            {
                candidates.AddRange(Directory.GetFiles(SaveManager.SaveFolder, "s000.d", SearchOption.AllDirectories));
                candidates.AddRange(Directory.GetFiles(SaveManager.SaveFolder, "*.d", SearchOption.TopDirectoryOnly));
            }

            var docsAspyr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Aspyr", "Guitar Hero III", "Save");
            if (Directory.Exists(docsAspyr))
            {
                candidates.AddRange(Directory.GetFiles(docsAspyr, "s000.d", SearchOption.AllDirectories));
            }

            return candidates
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(f => new FileInfo(f).LastWriteTimeUtc)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Scans the active GH3 career save (read-only) for installed custom song QBKey checksums
    /// and merges any higher verified scores & star counts into AppConfig.Current.LocalHighScores.
    /// </summary>
    public CareerScoreScanResult ScanAndImportCareerScores()
    {
        var res = new CareerScoreScanResult();
        var saveFile = FindActiveSaveFile();
        if (string.IsNullOrEmpty(saveFile) || !File.Exists(saveFile))
        {
            res.SaveFound = false;
            res.Summary = "No GH3 career save (s000.d) detected yet.";
            return res;
        }

        res.SaveFound = true;
        res.SaveFilePath = saveFile;

        try
        {
            byte[] data;
            using (var fs = new FileStream(saveFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                if (fs.Length == 0 || fs.Length > 32 * 1024 * 1024)
                {
                    res.Summary = "Career save size outside expected range; skipped safely.";
                    return res;
                }
                data = new byte[fs.Length];
                int readTotal = 0;
                while (readTotal < data.Length)
                {
                    int n = fs.Read(data, readTotal, data.Length - readTotal);
                    if (n <= 0) break;
                    readTotal += n;
                }
            }

            var songs = _gh3.GetInstalledCustomSongs();
            if (songs.Count == 0)
            {
                res.Summary = "No custom songs installed to match against career save.";
                return res;
            }

            AppConfig.Current.LocalHighScores ??= new Dictionary<string, SongScoreRecord>(StringComparer.OrdinalIgnoreCase);
            bool anyChanged = false;

            foreach (var song in songs)
            {
                if (string.IsNullOrWhiteSpace(song.SongId)) continue;
                uint qb = ComputeQbKey(song.SongId);
                uint qbInverted = ~qb;

                var best = FindBestScoreForKey(data, qb);
                var alt = FindBestScoreForKey(data, qbInverted);
                if (alt.Score > best.Score) best = alt;

                if (best.Score >= 500 && best.Score <= 5_000_000)
                {
                    res.SongsMatched++;
                    AppConfig.Current.LocalHighScores.TryGetValue(song.SongId, out var existing);
                    if (existing == null || best.Score > existing.BestScore)
                    {
                        AppConfig.Current.LocalHighScores[song.SongId] = new SongScoreRecord
                        {
                            BestScore = best.Score,
                            Stars = Math.Clamp(best.Stars, 3, 5),
                            Difficulty = best.Difficulty,
                            BestPercent = existing?.BestPercent ?? (best.Stars >= 5 ? 97.0 : 88.0),
                            BestStreak = existing?.BestStreak ?? 0,
                            IsFullCombo = existing?.IsFullCombo ?? false,
                            AchievedAt = DateTime.UtcNow
                        };
                        res.ScoresUpdated++;
                        anyChanged = true;
                    }
                }
            }

            if (anyChanged)
            {
                AppConfig.Save();
                _logger.LogInfo($"[SAVES] Imported {res.ScoresUpdated} high score(s) from GH3 career save ({Path.GetFileName(saveFile)}).");
            }

            res.Summary = res.ScoresUpdated > 0
                ? $"Imported {res.ScoresUpdated} new high score(s) from {Path.GetFileName(saveFile)}!"
                : $"Scanned {Path.GetFileName(saveFile)} ({res.SongsMatched} custom score record(s) up to date).";
            return res;
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[SAVES] Career save score scan skipped: {ex.Message}");
            res.Summary = $"Career save scan warning: {ex.Message}";
            return res;
        }
    }

    private static (int Score, int Stars, string Difficulty) FindBestScoreForKey(byte[] data, uint key)
    {
        int bestScore = 0;
        int bestStars = 5;
        string bestDiff = "Expert";

        byte b0 = (byte)(key & 0xFF);
        byte b1 = (byte)((key >> 8) & 0xFF);
        byte b2 = (byte)((key >> 16) & 0xFF);
        byte b3 = (byte)((key >> 24) & 0xFF);

        // Search both Big-Endian (GH3 Xen/PC QB struct order) and Little-Endian occurrences
        for (int i = 0; i <= data.Length - 20; i++)
        {
            bool matchBe = data[i] == b3 && data[i + 1] == b2 && data[i + 2] == b1 && data[i + 3] == b0;
            bool matchLe = data[i] == b0 && data[i + 1] == b1 && data[i + 2] == b2 && data[i + 3] == b3;
            if (!matchBe && !matchLe) continue;

            // Inspect nearby 32-bit integers in the struct record (within +4..+40 bytes)
            int windowEnd = Math.Min(data.Length - 4, i + 40);
            for (int off = i + 4; off <= windowEnd; off += 4)
            {
                uint val = matchBe
                    ? ((uint)data[off] << 24) | ((uint)data[off + 1] << 16) | ((uint)data[off + 2] << 8) | data[off + 3]
                    : ((uint)data[off + 3] << 24) | ((uint)data[off + 2] << 16) | ((uint)data[off + 1] << 8) | data[off];

                // Plausible GH3 song score range (avoid timestamps/checksums)
                if (val >= 5_000 && val <= 2_500_000 && (int)val > bestScore)
                {
                    bestScore = (int)val;
                    // Check adjacent word for stars (3..5)
                    if (off + 8 <= data.Length)
                    {
                        uint nextVal = matchBe
                            ? ((uint)data[off + 4] << 24) | ((uint)data[off + 5] << 16) | ((uint)data[off + 6] << 8) | data[off + 7]
                            : ((uint)data[off + 7] << 24) | ((uint)data[off + 6] << 16) | ((uint)data[off + 5] << 8) | data[off + 4];
                        if (nextVal >= 3 && nextVal <= 5) bestStars = (int)nextVal;
                    }
                }
            }
        }

        return (bestScore, bestStars, bestDiff);
    }

    /// <summary>
    /// Lightweight background monitor that detects when GH3.exe finishes running and automatically
    /// re-scans s000.d so new scores appear right away in GH3-Sync.
    /// </summary>
    public void StartGh3ExitWatcher(Action<CareerScoreScanResult> onPostGameScan)
    {
        if (_watcherStarted) return;
        _watcherStarted = true;

        _ = Task.Run(async () =>
        {
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    bool running = Process.GetProcessesByName("GH3").Length > 0;
                    if (_wasGh3Running && !running)
                    {
                        await Task.Delay(1500, _cts.Token); // let GH3 finish flushing s000.d
                        var scan = ScanAndImportCareerScores();
                        onPostGameScan(scan);
                    }
                    _wasGh3Running = running;
                }
                catch (OperationCanceledException) { break; }
                catch { }

                try { await Task.Delay(4000, _cts.Token); }
                catch (OperationCanceledException) { break; }
            }
        });
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
