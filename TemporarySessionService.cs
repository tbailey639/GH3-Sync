using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class TempSessionSong
{
    public string SongId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Charter { get; set; } = "";
    public string Year { get; set; } = "";
    public string Album { get; set; } = "";
    public bool Kept { get; set; } = false;
}

public class TempSessionState
{
    public bool Active { get; set; } = false;
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public string PeerAddress { get; set; } = "";
    public string PreSessionCustomsPakBackup { get; set; } = "";
    public bool AutoCleanOnGameExit { get; set; } = true;
    public List<TempSessionSong> Songs { get; set; } = new();
}

/// <summary>
/// Manages Temporary Lobby Session synced songs: snapshots pre-session customs.pak.xen & saves,
/// allows promoting individual tracks with "Keep Song", and automatically strips temporary files
/// and restores customs.pak.xen when GH3.exe exits or the user clicks "End Session & Clean Up".
/// </summary>
public class TemporarySessionService
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync");
    private static readonly string SessionFilePath = Path.Combine(AppDataDir, "temp_session.json");
    private static readonly string PreSessionPakPath = Path.Combine(AppDataDir, "temp_session_customs.pak.xen");
    private static readonly object _lock = new();

    private readonly Gh3Service _gh3;
    private readonly SaveManager _saveManager;
    private readonly SyncLogger _logger;
    private CancellationTokenSource? _watcherCts;
    private bool _sawGh3Running;

    public event Action<string>? SessionCleanedUp;

    public TempSessionState CurrentState { get; private set; }

    public TemporarySessionService(Gh3Service gh3, SaveManager saveManager, SyncLogger logger)
    {
        _gh3 = gh3;
        _saveManager = saveManager;
        _logger = logger;
        CurrentState = LoadState();
        StartGameExitWatcher();
    }

    public static HashSet<string> GetActiveTempSongIds()
    {
        lock (_lock)
        {
            var st = LoadState();
            if (!st.Active) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            return new HashSet<string>(
                st.Songs.Where(s => !s.Kept).Select(s => s.SongId),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    private static TempSessionState LoadState()
    {
        try
        {
            if (File.Exists(SessionFilePath))
            {
                var st = JsonSerializer.Deserialize<TempSessionState>(File.ReadAllText(SessionFilePath));
                if (st != null) return st;
            }
        }
        catch { }
        return new TempSessionState();
    }

    private void SaveState()
    {
        try
        {
            Directory.CreateDirectory(AppDataDir);
            File.WriteAllText(SessionFilePath, JsonSerializer.Serialize(CurrentState, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void BeginSessionIfNeeded(string peerAddress, bool autoCleanOnGameExit = true)
    {
        lock (_lock)
        {
            if (CurrentState.Active) return;

            Directory.CreateDirectory(AppDataDir);
            string backupPak = "";
            if (_gh3.IsDetected && File.Exists(_gh3.CustomsPakPath))
            {
                File.Copy(_gh3.CustomsPakPath, PreSessionPakPath, overwrite: true);
                backupPak = PreSessionPakPath;
            }

            try
            {
                _saveManager.BackupCurrentSaves("Before-Temp-Lobby-Session");
            }
            catch { }

            CurrentState = new TempSessionState
            {
                Active = true,
                StartedAtUtc = DateTime.UtcNow,
                PeerAddress = peerAddress,
                PreSessionCustomsPakBackup = backupPak,
                AutoCleanOnGameExit = autoCleanOnGameExit,
                Songs = new List<TempSessionSong>()
            };
            SaveState();
            _logger.LogInfo($"[TEMP SESSION] Started Temporary Lobby Session for peer {peerAddress} (pre-session customs.pak.xen backed up).");
        }
    }

    public void RegisterTempSong(CustomSongInfo song)
    {
        lock (_lock)
        {
            if (!CurrentState.Active) return;
            if (CurrentState.Songs.Any(s => s.SongId.Equals(song.SongId, StringComparison.OrdinalIgnoreCase)))
                return;

            CurrentState.Songs.Add(new TempSessionSong
            {
                SongId = song.SongId,
                Title = song.Title,
                Artist = song.Artist,
                Charter = song.Charter,
                Year = song.Year,
                Album = song.Album,
                Kept = false
            });
            SaveState();
        }
    }

    public bool KeepSongPermanently(string songId)
    {
        lock (_lock)
        {
            var s = CurrentState.Songs.FirstOrDefault(x => x.SongId.Equals(songId, StringComparison.OrdinalIgnoreCase));
            if (s == null) return false;
            s.Kept = true;
            SaveState();
            _logger.LogInfo($"[TEMP SESSION] Promoted '{s.Title}' ({songId}) to permanent library.");
            return true;
        }
    }

    public int KeepAllSongsPermanently()
    {
        lock (_lock)
        {
            if (!CurrentState.Active && CurrentState.Songs.Count == 0) return 0;
            int count = 0;
            foreach (var s in CurrentState.Songs)
            {
                if (!s.Kept)
                {
                    s.Kept = true;
                    count++;
                }
            }
            CurrentState.Active = false;
            if (!string.IsNullOrWhiteSpace(CurrentState.PreSessionCustomsPakBackup) && File.Exists(CurrentState.PreSessionCustomsPakBackup))
            {
                try { File.Delete(CurrentState.PreSessionCustomsPakBackup); } catch { }
                CurrentState.PreSessionCustomsPakBackup = "";
            }
            CurrentState.Songs.Clear();
            SaveState();
            _logger.LogInfo($"[TEMP SESSION] Promoted all {count} synced song(s) to permanent library. Temporary session cleared.");
            return count;
        }
    }

    public (int RemovedCount, int KeptCount, string Message) EndSessionAndCleanup()
    {
        lock (_lock)
        {
            if (!CurrentState.Active)
            {
                return (0, 0, "No active temporary lobby session.");
            }

            int removedCount = 0;
            var toRemove = CurrentState.Songs.Where(s => !s.Kept).ToList();
            var toKeep = CurrentState.Songs.Where(s => s.Kept).ToList();

            foreach (var song in toRemove)
            {
                try
                {
                    var pak = Path.Combine(_gh3.SongsPath, $"{song.SongId}_song.pak.xen");
                    var pab = Path.Combine(_gh3.SongsPath, $"{song.SongId}_song.pab.xen");
                    var fsb = Path.Combine(_gh3.MusicPath, $"{song.SongId}.fsb.xen");
                    var dat = Path.Combine(_gh3.MusicPath, $"{song.SongId}.dat.xen");
                    var prevFsb = Path.Combine(_gh3.MusicPath, $"{song.SongId}_preview.fsb.xen");
                    var prevDat = Path.Combine(_gh3.MusicPath, $"{song.SongId}_preview.dat.xen");

                    foreach (var f in new[] { pak, pab, fsb, dat, prevFsb, prevDat })
                    {
                        if (File.Exists(f)) File.Delete(f);
                    }
                    removedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarn($"[TEMP SESSION] Could not remove temp file for {song.SongId}: {ex.Message}");
                }
            }

            // Restore pre-session customs.pak.xen cleanly, then re-merge any songs promoted via "Keep"
            if (!string.IsNullOrWhiteSpace(CurrentState.PreSessionCustomsPakBackup) &&
                File.Exists(CurrentState.PreSessionCustomsPakBackup) &&
                _gh3.IsDetected)
            {
                try
                {
                    File.Copy(CurrentState.PreSessionCustomsPakBackup, _gh3.CustomsPakPath, overwrite: true);
                    foreach (var kept in toKeep)
                    {
                        CustomsPakService.MergeSongIntoCustomsPak(
                            _gh3.GamePath, kept.SongId, kept.Title, kept.Artist, kept.Charter, kept.Year, kept.Album, _logger);
                    }
                    try { File.Delete(CurrentState.PreSessionCustomsPakBackup); } catch { }
                }
                catch (Exception ex)
                {
                    _logger.LogWarn($"[TEMP SESSION] Could not restore pre-session customs.pak.xen: {ex.Message}");
                }
            }
            else
            {
                // Fallback: strip each removed song directly from current customs.pak.xen
                foreach (var song in toRemove)
                {
                    CustomsPakService.RemoveSongFromCustomsPak(_gh3.GamePath, song.SongId, _logger);
                }
            }

            CurrentState = new TempSessionState { Active = false };
            SaveState();

            var msg = $"Temporary lobby session ended: removed {removedCount} temp song(s), kept {toKeep.Count} permanent song(s).";
            _logger.LogInfo($"[TEMP SESSION] {msg}");
            SessionCleanedUp?.Invoke(msg);
            return (removedCount, toKeep.Count, msg);
        }
    }

    private void StartGameExitWatcher()
    {
        _watcherCts = new CancellationTokenSource();
        var ct = _watcherCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(4000, ct);
                    bool running = Process.GetProcessesByName("GH3").Length > 0;
                    if (running)
                    {
                        _sawGh3Running = true;
                    }
                    else if (_sawGh3Running)
                    {
                        _sawGh3Running = false;
                        if (CurrentState.Active && CurrentState.AutoCleanOnGameExit)
                        {
                            _logger.LogInfo("[TEMP SESSION] GH3.exe exited — auto-cleaning temporary lobby songs.");
                            EndSessionAndCleanup();
                        }
                    }
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, ct);
    }

    public void Stop()
    {
        try { _watcherCts?.Cancel(); } catch { }
    }
}
