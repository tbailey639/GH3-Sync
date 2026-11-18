using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GH3_Sync;

public class CommunityChartEntry
{
    public string Name { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Album { get; set; } = "";
    public string Genre { get; set; } = "";
    public string Year { get; set; } = "";
    public string Charter { get; set; } = "";
    public string Md5 { get; set; } = "";
    public string PackName { get; set; } = "";
    public int DiffGuitar { get; set; } = -1;
    public int DiffBass { get; set; } = -1;
    public int DiffRhythm { get; set; } = -1;
    public long SongLengthMs { get; set; }
    public int ExpertNoteCount { get; set; }
    public double MaxNps { get; set; }
    public bool HasSoloSections { get; set; }
    public bool HasForcedNotes { get; set; }
    public bool HasFullDiffs { get; set; }
    public string DownloadUrl => string.IsNullOrWhiteSpace(Md5) ? "" : $"https://files.enchor.us/{Md5}.sng";

    public string DurationDisplay
    {
        get
        {
            if (SongLengthMs <= 0) return "--:--";
            var ts = TimeSpan.FromMilliseconds(SongLengthMs);
            return $"{(int)ts.TotalMinutes}:{ts.Seconds:D2}";
        }
    }

    public string BadgesSummary
    {
        get
        {
            var parts = new List<string>();
            if (ExpertNoteCount > 0) parts.Add($"{ExpertNoteCount:N0} notes");
            if (HasFullDiffs) parts.Add("Full Diffs");
            if (HasSoloSections) parts.Add("Solos");
            if (HasForcedNotes) parts.Add("HOPOs");
            if (DiffBass >= 0 || DiffRhythm >= 0) parts.Add("Co-Op Bass/Rhythm");
            if (!string.IsNullOrWhiteSpace(PackName)) parts.Add(PackName);
            return string.Join(" • ", parts);
        }
    }
}

public class CommunityChartSearchResult
{
    public bool Success { get; set; }
    public int TotalFound { get; set; }
    public List<CommunityChartEntry> Charts { get; set; } = new();
    public string Message { get; set; } = "";
}

/// <summary>
/// Searches Chorus Encore's public Clone Hero / Guitar Hero chart index (api.enchor.us),
/// downloads verified .sng packages directly from files.enchor.us, and extracts SNGPKG v1
/// containers natively in pure C# (notes.chart/.mid, song.ini, audio stems, album art).
/// </summary>
public class CommunityChartService
{
    private const string SearchApiUrl = "https://api.enchor.us/search";
    private readonly HttpClient _http;
    private readonly SyncLogger? _logger;

    public CommunityChartService(SyncLogger? logger = null)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        _http.DefaultRequestHeaders.Add("User-Agent", $"GH3-Sync/{UpdateService.CurrentAppVersion}");
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<CommunityChartSearchResult> SearchChartsAsync(
        string query,
        int page = 1,
        CancellationToken ct = default)
    {
        var result = new CommunityChartSearchResult();
        if (string.IsNullOrWhiteSpace(query))
        {
            result.Message = "Enter a song title or artist name to search.";
            return result;
        }

        try
        {
            var payload = new
            {
                search = query.Trim(),
                page = Math.Max(1, page),
                instrument = "guitar",
                difficulty = (string?)null,
                drumType = (string?)null,
                source = "website"
            };

            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(SearchApiUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                result.Message = $"Chorus Encore search returned HTTP {(int)resp.StatusCode}.";
                return result;
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("found", out var fProp) && fProp.TryGetInt32(out var total))
                result.TotalFound = total;

            if (root.TryGetProperty("data", out var dataArr) && dataArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in dataArr.EnumerateArray())
                {
                    var md5 = GetStr(item, "md5");
                    if (string.IsNullOrWhiteSpace(md5)) continue;

                    var entry = new CommunityChartEntry
                    {
                        Name = StripColorTags(GetStr(item, "name")),
                        Artist = StripColorTags(GetStr(item, "artist")),
                        Album = StripColorTags(GetStr(item, "album")),
                        Genre = StripColorTags(GetStr(item, "genre")),
                        Year = GetStr(item, "year"),
                        Charter = StripColorTags(GetStr(item, "charter")),
                        Md5 = md5,
                        PackName = StripColorTags(GetStr(item, "packName")),
                        DiffGuitar = GetInt(item, "diff_guitar", -1),
                        DiffBass = GetInt(item, "diff_bass", -1),
                        DiffRhythm = GetInt(item, "diff_rhythm", -1),
                        SongLengthMs = GetLong(item, "song_length", 0)
                    };

                    if (item.TryGetProperty("notesData", out var nd) && nd.ValueKind == JsonValueKind.Object)
                    {
                        entry.HasSoloSections = GetBool(nd, "hasSoloSections");
                        entry.HasForcedNotes = GetBool(nd, "hasForcedNotes");

                        if (nd.TryGetProperty("noteCounts", out var ncs) && ncs.ValueKind == JsonValueKind.Array)
                        {
                            var guitarDiffs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                            foreach (var nc in ncs.EnumerateArray())
                            {
                                var inst = GetStr(nc, "instrument");
                                var diff = GetStr(nc, "difficulty");
                                if (inst.Equals("guitar", StringComparison.OrdinalIgnoreCase))
                                {
                                    guitarDiffs.Add(diff);
                                    if (diff.Equals("expert", StringComparison.OrdinalIgnoreCase))
                                        entry.ExpertNoteCount = GetInt(nc, "count", 0);
                                }
                            }
                            entry.HasFullDiffs = guitarDiffs.Contains("expert") && guitarDiffs.Contains("hard")
                                              && guitarDiffs.Contains("medium") && guitarDiffs.Contains("easy");
                        }

                        if (nd.TryGetProperty("maxNps", out var npsArr) && npsArr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var np in npsArr.EnumerateArray())
                            {
                                if (GetStr(np, "instrument").Equals("guitar", StringComparison.OrdinalIgnoreCase) &&
                                    GetStr(np, "difficulty").Equals("expert", StringComparison.OrdinalIgnoreCase) &&
                                    np.TryGetProperty("nps", out var npsVal) && npsVal.TryGetDouble(out var d))
                                {
                                    entry.MaxNps = d;
                                }
                            }
                        }
                    }

                    result.Charts.Add(entry);
                }
            }

            // Rank best-quality GH3-compatible charts first (full diffs, solos/HOPOs, non-modchart)
            result.Charts = result.Charts
                .OrderByDescending(c => c.HasFullDiffs ? 1 : 0)
                .ThenByDescending(c => (c.HasSoloSections ? 1 : 0) + (c.HasForcedNotes ? 1 : 0))
                .ThenByDescending(c => c.ExpertNoteCount > 80 ? 1 : 0)
                .Take(20)
                .ToList();

            result.Success = true;
            result.Message = result.Charts.Count > 0
                ? $"Found {result.TotalFound:N0} community chart(s) matching '{query.Trim()}'."
                : $"No community charts found for '{query.Trim()}'. You can auto-generate one with ONOGAMI AI!";
            _logger?.LogInfo($"[COMMUNITY CHARTS] Search '{query.Trim()}' -> {result.TotalFound} found, showing {result.Charts.Count}.");
            return result;
        }
        catch (Exception ex)
        {
            result.Message = $"Community chart search failed: {ex.Message}";
            _logger?.LogWarn($"[COMMUNITY CHARTS] Search failed: {ex.Message}");
            return result;
        }
    }

    public async Task<(bool Success, string ExtractedDir, string Message)> DownloadAndExtractChartAsync(
        CommunityChartEntry chart,
        Action<int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chart.DownloadUrl))
            return (false, "", "Invalid community chart download URL.");

        var workRoot = Path.Combine(Path.GetTempPath(), "GH3-Sync-Community", $"sng_{chart.Md5}_{Guid.NewGuid().ToString("N")[..6]}");
        Directory.CreateDirectory(workRoot);
        var sngPath = Path.Combine(workRoot, $"{chart.Md5}.sng");
        var extractDir = Path.Combine(workRoot, "song");
        Directory.CreateDirectory(extractDir);

        try
        {
            onProgress?.Invoke(5, $"Downloading '{chart.Name}' by {chart.Artist}...");
            _logger?.LogInfo($"[COMMUNITY CHARTS] Downloading {chart.DownloadUrl}");

            using var resp = await _http.GetAsync(chart.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var totalBytes = resp.Content.Headers.ContentLength ?? 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(sngPath, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                var buf = new byte[81920];
                long readTotal = 0;
                int read;
                int lastPct = -1;
                while ((read = await src.ReadAsync(buf, ct)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, read), ct);
                    readTotal += read;
                    if (totalBytes > 0)
                    {
                        int pct = (int)Math.Min(65, 5 + (readTotal * 60L) / totalBytes);
                        if (pct != lastPct)
                        {
                            lastPct = pct;
                            onProgress?.Invoke(pct, $"Downloading chart package... {readTotal / (1024.0 * 1024.0):F1} / {totalBytes / (1024.0 * 1024.0):F1} MB");
                        }
                    }
                }
            }

            onProgress?.Invoke(70, "Unpacking SNG chart & audio assets...");
            ExtractSngFile(sngPath, extractDir, _logger);
            try { File.Delete(sngPath); } catch { }

            onProgress?.Invoke(85, "Community chart package unpacked cleanly!");
            return (true, extractDir, $"Downloaded '{chart.Name}' cleanly.");
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[COMMUNITY CHARTS] Download/extract failed: {ex.Message}");
            return (false, "", $"Could not download community chart: {ex.Message}");
        }
    }

    /// <summary>
    /// Pure C# SNGPKG v1 extractor (Clone Hero / YARG / Chorus Encore .sng container format)
    /// with strict path traversal protection and song.ini reconstruction if metadata is stored in header.
    /// </summary>
    public static void ExtractSngFile(string sngPath, string destDir, SyncLogger? logger = null)
    {
        if (!File.Exists(sngPath))
            throw new FileNotFoundException("SNG package not found.", sngPath);

        Directory.CreateDirectory(destDir);
        string fullDestRoot = Path.GetFullPath(destDir);

        using var fs = new FileStream(sngPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

        var magicBytes = br.ReadBytes(6);
        var magic = Encoding.ASCII.GetString(magicBytes);
        if (magic != "SNGPKG")
            throw new InvalidDataException($"Not a valid SNG package (header '{magic}').");

        uint version = br.ReadUInt32();
        if (version != 1)
            throw new InvalidDataException($"Unsupported SNG package version: {version}");

        byte[] xorMask = br.ReadBytes(16);

        // 1. Metadata section
        ulong metaLen = br.ReadUInt64();
        long metaEndPos = fs.Position + (long)metaLen;
        ulong metaCount = br.ReadUInt64();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (ulong i = 0; i < metaCount; i++)
        {
            int keyLen = br.ReadInt32();
            if (keyLen < 0 || keyLen > 65536) throw new InvalidDataException("Invalid SNG metadata key length.");
            string key = Encoding.UTF8.GetString(br.ReadBytes(keyLen));

            int valLen = br.ReadInt32();
            if (valLen < 0 || valLen > 1024 * 1024) throw new InvalidDataException("Invalid SNG metadata value length.");
            string val = Encoding.UTF8.GetString(br.ReadBytes(valLen));

            metadata[key] = val;
        }
        fs.Position = metaEndPos;

        // 2. File index section
        ulong fileIndexLen = br.ReadUInt64();
        long fileIndexEndPos = fs.Position + (long)fileIndexLen;
        ulong fileCount = br.ReadUInt64();
        var entries = new List<(string Name, ulong Length, ulong Offset)>();

        for (ulong i = 0; i < fileCount; i++)
        {
            byte nameLen = br.ReadByte();
            string fileName = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
            ulong contentsLen = br.ReadUInt64();
            ulong contentsIdx = br.ReadUInt64();
            entries.Add((fileName, contentsLen, contentsIdx));
        }
        fs.Position = fileIndexEndPos;

        // 3. Extract allowed song files cleanly (unmasking XOR stream)
        var allowedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".chart", ".mid", ".ini", ".ogg", ".mp3", ".wav", ".opus", ".flac", ".png", ".jpg", ".jpeg"
        };

        var buf = new byte[65536];
        foreach (var (rawName, len, offset) in entries)
        {
            if (len > 500UL * 1024 * 1024) continue; // Safety cap 500 MB per file

            // Flatten/sanitize relative path to prevent zip-slip/traversal
            var safeName = Path.GetFileName(rawName.Replace('/', Path.DirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(safeName)) continue;

            var ext = Path.GetExtension(safeName);
            if (!allowedExts.Contains(ext)) continue;

            var outPath = Path.GetFullPath(Path.Combine(fullDestRoot, safeName));
            if (!outPath.StartsWith(fullDestRoot, StringComparison.OrdinalIgnoreCase)) continue;

            fs.Position = (long)offset;
            using var outFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);

            ulong remaining = len;
            ulong filePos = 0;
            while (remaining > 0)
            {
                int toRead = (int)Math.Min((ulong)buf.Length, remaining);
                int got = fs.Read(buf, 0, toRead);
                if (got <= 0) break;

                for (int b = 0; b < got; b++)
                {
                    ulong idx = filePos + (ulong)b;
                    byte key = (byte)(xorMask[idx & 0x0FUL] ^ (byte)(idx & 0xFFUL));
                    buf[b] = (byte)(buf[b] ^ key);
                }

                outFs.Write(buf, 0, got);
                filePos += (ulong)got;
                remaining -= (ulong)got;
            }
        }

        // 4. Reconstruct song.ini from SNG header metadata if not already present inside the package
        var iniPath = Path.Combine(fullDestRoot, "song.ini");
        if (!File.Exists(iniPath) && metadata.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[song]");
            foreach (var kv in metadata)
            {
                sb.AppendLine($"{kv.Key} = {kv.Value}");
            }
            File.WriteAllText(iniPath, sb.ToString());
        }

        logger?.LogInfo($"[SNG EXTRACT] Extracted {entries.Count} file(s) from {Path.GetFileName(sngPath)} into {fullDestRoot}");
    }

    private static string GetStr(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int GetInt(JsonElement el, string prop, int fallback)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : fallback;

    private static long GetLong(JsonElement el, string prop, long fallback)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : fallback;

    private static bool GetBool(JsonElement el, string prop)
        => el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static string StripColorTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", "").Trim();
    }
}
