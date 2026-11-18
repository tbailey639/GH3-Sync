using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class UrlAudioImportResult
{
    public bool Success { get; set; }
    public string AudioPath { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Message { get; set; } = "";
}

/// <summary>
/// Optional managed yt-dlp add-on stored in %APPDATA%\GH3-Sync\tools\ytdlp\yt-dlp.exe
/// (preserved across app updates just like Demucs). Downloads audio + title/artist + thumbnail
/// from any yt-dlp supported URL directly into ONOGAMI Song Creator.
/// </summary>
public class YtDlpImporterService
{
    private const string OfficialDownloadUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private readonly SyncLogger _logger;
    private readonly HttpClient _http;

    public static string YtDlpDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "tools", "ytdlp");
    public static string YtDlpExePath => Path.Combine(YtDlpDir, "yt-dlp.exe");

    public YtDlpImporterService(SyncLogger logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"GH3-Sync/{UpdateService.CurrentAppVersion}");
    }

    public bool IsInstalled => File.Exists(YtDlpExePath) && new FileInfo(YtDlpExePath).Length > 100_000;

    public async Task<(bool Ok, string Message)> InstallOrUpdateAsync(Action<string>? onStatus = null, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(YtDlpDir);
            onStatus?.Invoke("Downloading official yt-dlp.exe from GitHub...");
            var bytes = await _http.GetByteArrayAsync(OfficialDownloadUrl, ct);
            await File.WriteAllBytesAsync(YtDlpExePath, bytes, ct);
            _logger.LogInfo($"[YT-DLP] Installed yt-dlp.exe ({bytes.Length / (1024.0 * 1024.0):F1} MB)");
            return (true, $"Installed yt-dlp cleanly ({bytes.Length / (1024.0 * 1024.0):F1} MB).");
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[YT-DLP] Install failed: {ex.Message}");
            return (false, $"yt-dlp install failed: {ex.Message}");
        }
    }

    public (bool Ok, string Message) Uninstall()
    {
        try
        {
            if (Directory.Exists(YtDlpDir)) Directory.Delete(YtDlpDir, true);
            return (true, "Removed optional yt-dlp add-on.");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<UrlAudioImportResult> DownloadAudioFromUrlAsync(
        string url,
        Action<string>? onStatus = null,
        CancellationToken ct = default)
    {
        var res = new UrlAudioImportResult();
        if (string.IsNullOrWhiteSpace(url) || (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            res.Message = "Enter a valid http:// or https:// audio/video URL.";
            return res;
        }

        if (!IsInstalled)
        {
            var (ok, msg) = await InstallOrUpdateAsync(onStatus, ct);
            if (!ok) { res.Message = msg; return res; }
        }

        var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
        var ffDir = !string.IsNullOrEmpty(ff) ? Path.GetDirectoryName(ff) : "";
        var workDir = Path.Combine(Path.GetTempPath(), "GH3-Sync-UrlImport", $"dl_{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            onStatus?.Invoke("Fetching audio & cover thumbnail via yt-dlp...");
            var outTemplate = Path.Combine(workDir, "track.%(ext)s");
            var ffArg = !string.IsNullOrEmpty(ffDir) ? $"--ffmpeg-location \"{ffDir}\" " : "";
            var args = $"{ffArg}--no-playlist -x --audio-format mp3 --audio-quality 0 --write-thumbnail --convert-thumbnails jpg --write-info-json -o \"{outTemplate}\" \"{url.Trim()}\"";

            var psi = new ProcessStartInfo
            {
                FileName = YtDlpExePath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) { res.Message = "Could not launch yt-dlp.exe."; return res; }
            var errTask = proc.StandardError.ReadToEndAsync(ct);
            var outTask = proc.StandardOutput.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            var errText = await errTask;
            _ = await outTask;

            var mp3 = Directory.GetFiles(workDir, "*.mp3").FirstOrDefault();
            if (string.IsNullOrEmpty(mp3) || !File.Exists(mp3))
            {
                res.Message = $"yt-dlp did not produce an MP3: {errText.Split('\n').LastOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim()}";
                return res;
            }

            // Parse title & artist from info.json
            string title = "Imported Track";
            string artist = "Unknown Artist";
            var infoJson = Directory.GetFiles(workDir, "*.info.json").FirstOrDefault();
            if (!string.IsNullOrEmpty(infoJson) && File.Exists(infoJson))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(infoJson));
                    var root = doc.RootElement;
                    var rawTitle = root.TryGetProperty("track", out var tk) && !string.IsNullOrWhiteSpace(tk.GetString())
                        ? tk.GetString()!
                        : (root.TryGetProperty("title", out var tl) ? tl.GetString() ?? "" : "");
                    var rawArtist = root.TryGetProperty("artist", out var ar) && !string.IsNullOrWhiteSpace(ar.GetString())
                        ? ar.GetString()!
                        : (root.TryGetProperty("uploader", out var up) ? up.GetString() ?? "" : "");

                    if (rawTitle.Contains(" - "))
                    {
                        var parts = rawTitle.Split(" - ", 2);
                        artist = parts[0].Trim();
                        title = parts[1].Trim();
                    }
                    else
                    {
                        title = string.IsNullOrWhiteSpace(rawTitle) ? title : rawTitle.Trim();
                        artist = string.IsNullOrWhiteSpace(rawArtist) ? artist : rawArtist.Replace(" - Topic", "", StringComparison.OrdinalIgnoreCase).Trim();
                    }
                }
                catch { }
            }

            var thumb = Directory.GetFiles(workDir, "*.jpg").FirstOrDefault() ?? "";
            res.Success = true;
            res.AudioPath = mp3;
            res.ThumbnailPath = thumb;
            res.Title = title;
            res.Artist = artist;
            res.Message = $"Downloaded '{artist} - {title}' cleanly!";
            _logger.LogInfo($"[YT-DLP] {res.Message}");
            return res;
        }
        catch (Exception ex)
        {
            res.Message = $"URL import failed: {ex.Message}";
            _logger.LogWarn($"[YT-DLP] {res.Message}");
            return res;
        }
    }
}
