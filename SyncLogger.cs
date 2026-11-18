using System;
using System.IO;
using System.Text;

namespace GH3_Sync;

public enum SyncState
{
    Ready,
    Syncing,
    Success,
    Failed
}

public class OverlayStatus
{
    public SyncState State { get; set; } = SyncState.Ready;
    public string Message { get; set; } = "GH3-Sync Ready";
    public string Detail { get; set; } = "3 Customs Loaded";
    public int ProgressPercent { get; set; } = 0;
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

public class SyncLogger
{
    private static readonly object _lock = new();
    private readonly List<string> _logPaths = new();
    public OverlayStatus CurrentStatus { get; private set; } = new();

    public event Action<OverlayStatus>? StatusChanged;
    public event Action<string>? PeerActivityPulsed;

    public void PulsePeerActivity(string detail = "Peer Sync Activity")
    {
        try { PeerActivityPulsed?.Invoke(detail); } catch { }
    }

    public SyncLogger(string? gamePath = null)
    {
        // 1. Always-writable AppData debug log (safe when installed in C:\Program Files\GH3-Sync\)
        var appDataLogDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync");
        try { Directory.CreateDirectory(appDataLogDir); } catch { }
        _logPaths.Add(Path.Combine(appDataLogDir, "debug.log"));

        // 2. Game folder debug log if detected
        if (!string.IsNullOrEmpty(gamePath) && Directory.Exists(gamePath))
        {
            _logPaths.Add(Path.Combine(gamePath, "gh3_sync_debug.log"));
        }

        LogInfo("========================================================");
        LogInfo("🎸 GH3-Sync Engine & Logger Initialized");
        LogInfo($"Time: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        LogInfo($"OS: {Environment.OSVersion} (x64: {Environment.Is64BitOperatingSystem})");
        LogInfo($"Runtime: .NET {Environment.Version}");
        LogInfo("========================================================");
    }

    public void SetStatus(SyncState state, string message, string detail = "", int percent = 0)
    {
        CurrentStatus = new OverlayStatus
        {
            State = state,
            Message = message,
            Detail = detail,
            ProgressPercent = percent,
            Timestamp = DateTime.UtcNow
        };

        try
        {
            StatusChanged?.Invoke(CurrentStatus);
        }
        catch { }

        switch (state)
        {
            case SyncState.Syncing:
                LogInfo($"[TRANSFER] {message} - {detail} ({percent}%)");
                break;
            case SyncState.Success:
                LogInfo($"[SUCCESS] {message} - {detail}");
                break;
            case SyncState.Failed:
                LogError($"[FAILED] {message} - {detail}");
                break;
            default:
                LogDebug($"[STATUS] {message} - {detail}");
                break;
        }
    }

    public void LogInfo(string message) => WriteEntry("INFO", message);
    public void LogDebug(string message) => WriteEntry("DEBUG", message);
    public void LogWarn(string message) => WriteEntry("WARN", message);
    public void LogError(string message, Exception? ex = null)
    {
        var sb = new StringBuilder(message);
        if (ex != null)
        {
            sb.AppendLine();
            sb.AppendLine($"Exception: {ex.GetType().FullName}: {ex.Message}");
            sb.AppendLine($"Stack Trace:\n{ex.StackTrace}");
        }
        WriteEntry("ERROR", sb.ToString());
    }

    private void WriteEntry(string level, string message)
    {
        var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
        var line = $"[{timestamp}] [{level}] {message}";

        lock (_lock)
        {
            Console.WriteLine(line);
            foreach (var path in _logPaths)
            {
                try
                {
                    File.AppendAllText(path, line + Environment.NewLine);
                }
                catch { }
            }
        }
    }

    public string ReadRecentLogs(int maxLines = 100)
    {
        lock (_lock)
        {
            var primary = _logPaths.FirstOrDefault(File.Exists);
            if (primary == null) return "No log file found.";
            try
            {
                var lines = File.ReadAllLines(primary);
                var takeCount = Math.Min(lines.Length, maxLines);
                var start = lines.Length - takeCount;
                return string.Join(Environment.NewLine, lines.Skip(start));
            }
            catch (Exception ex)
            {
                return $"Could not read log file: {ex.Message}";
            }
        }
    }
}
