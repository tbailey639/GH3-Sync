using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace GH3_Sync;

public class CustomsPakSongEntry
{
    public string SongId { get; set; } = "";
    public string Checksum { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Year { get; set; } = "";
    public string Charter { get; set; } = "";
    public string Album { get; set; } = "";
}

/// <summary>
/// Native reader, merger, and cleaner for Guitar Hero III / BetterGH3 DATA\customs.pak.xen.
/// Reads real titles/artists/charters for all installed customs, merges imported or synced songs
/// cleanly into an existing customs.pak.xen without overwriting other songs or resetting saves,
/// and strips removed/temporary songs cleanly on deletion or session end.
/// </summary>
public static class CustomsPakService
{
    private static readonly object _pakLock = new();
    private static bool _resolveHooked;
    private static string _loadedHoneycombDir = "";

    private static (Assembly CoreAsm, string HoneycombDir)? LoadCoreAssembly(string? gamePath = null)
    {
        var hDir = HoneycombCompilerService.FindHoneycombDirectory(AppConfig.Current.HoneycombPath, gamePath);
        if (string.IsNullOrWhiteSpace(hDir)) return null;

        var coreDll = Path.Combine(hDir, "GH-Toolkit-Core.dll");
        if (!File.Exists(coreDll)) return null;

        if (!_resolveHooked)
        {
            _resolveHooked = true;
            _loadedHoneycombDir = hDir;
            AppDomain.CurrentDomain.AssemblyResolve += (_, args) =>
            {
                try
                {
                    var name = new AssemblyName(args.Name).Name + ".dll";
                    var p = Path.Combine(_loadedHoneycombDir, name);
                    if (File.Exists(p)) return Assembly.LoadFrom(p);
                }
                catch { }
                return null;
            };
        }

        var asm = Assembly.LoadFrom(coreDll);

        // Ensure GlobalVariables.ExeRootFolder points to a writable directory so DebugReader never fails under Program Files.
        string writableRoot = GetWritableToolkitRoot(hDir);
        var gvType = asm.GetType("GH_Toolkit_Core.Methods.GlobalVariables");
        gvType?.GetProperty("ExeRootFolder")?.SetValue(null, writableRoot);

        return (asm, hDir);
    }

    public static string GetWritableToolkitRoot(string honeycombDir)
    {
        try
        {
            var testDir = Path.Combine(honeycombDir, "QBDebug");
            Directory.CreateDirectory(testDir);
            var probe = Path.Combine(testDir, ".write_test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return honeycombDir;
        }
        catch
        {
            var appWork = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "GH3-Sync", "compiler_work");
            Directory.CreateDirectory(Path.Combine(appWork, "QBDebug"));
            return appWork;
        }
    }

    /// <summary>
    /// Reads all custom song entries from DATA\customs.pak.xen and returns a map keyed by songId (lowercase).
    /// </summary>
    public static Dictionary<string, CustomsPakSongEntry> ReadSongMetadataMap(string gamePath)
    {
        var map = new Dictionary<string, CustomsPakSongEntry>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(gamePath)) return map;

        var pakPath = Path.Combine(gamePath, "DATA", "customs.pak.xen");
        if (!File.Exists(pakPath)) return map;

        lock (_pakLock)
        {
            try
            {
                var loaded = LoadCoreAssembly(gamePath);
                if (loaded == null) return map;

                var (asm, _) = loaded.Value;
                var (_, _, qbText) = DecompileCustomsPak(asm, pakPath);
                if (string.IsNullOrWhiteSpace(qbText)) return map;

                foreach (var entry in ParseSongPropsFromQbText(qbText))
                {
                    if (!string.IsNullOrWhiteSpace(entry.SongId))
                    {
                        map[entry.SongId] = entry;
                    }
                }
            }
            catch { }
        }

        return map;
    }

    /// <summary>
    /// Cleanly merges (adds or updates) a single song entry inside the user's existing DATA\customs.pak.xen
    /// without touching or overwriting any other custom songs already installed.
    /// </summary>
    public static bool MergeSongIntoCustomsPak(
        string gamePath,
        string songId,
        string title,
        string artist,
        string? charter = null,
        string? year = null,
        string? album = null,
        SyncLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(songId)) return false;

        var pakPath = Path.Combine(gamePath, "DATA", "customs.pak.xen");
        if (!File.Exists(pakPath))
        {
            logger?.LogWarn("[CUSTOMS PAK] DATA\\customs.pak.xen does not exist yet; skipping songlist merge.");
            return false;
        }

        lock (_pakLock)
        {
            try
            {
                var loaded = LoadCoreAssembly(gamePath);
                if (loaded == null) return false;
                var (asm, _) = loaded.Value;

                var crcType = asm.GetType("GH_Toolkit_Core.Checksum.CRC");
                var qbKeyMethod = crcType?.GetMethod("QBKey", new[] { typeof(string), typeof(bool) });
                if (qbKeyMethod == null) return false;

                var cleanSongId = songId.Trim();
                var checksum = qbKeyMethod.Invoke(null, new object[] { cleanSongId, false })?.ToString() ?? "";
                if (string.IsNullOrWhiteSpace(checksum)) return false;

                var (entriesObj, primaryEntry, qbText) = DecompileCustomsPak(asm, pakPath);
                if (entriesObj == null || primaryEntry == null || string.IsNullOrWhiteSpace(qbText))
                    return false;

                // Remove any existing occurrence of this checksum/songId before appending cleanly
                qbText = RemoveSongFromQbText(qbText, checksum, cleanSongId);

                // Clean values for QB syntax
                string escTitle = EscapeQbString(string.IsNullOrWhiteSpace(title) ? cleanSongId : title);
                string escArtist = EscapeQbString(string.IsNullOrWhiteSpace(artist) ? "Custom Artist" : artist);
                string escCharter = EscapeQbString(string.IsNullOrWhiteSpace(charter) ? "GH3-Sync" : charter);
                string cleanYear = ExtractYearDigits(year);
                string escAlbum = EscapeQbString(album ?? "");

                // 1. Append checksum to tier1 songs = [ ... ]
                qbText = InsertIntoFirstArrayBlock(qbText, @"tier1\s*=\s*\{[\s\S]*?songs\s*=\s*\[", checksum, "\t\t\t");

                // 2. Append checksum to download_songlist = [ ... ]
                qbText = InsertIntoFirstArrayBlock(qbText, @"download_songlist\s*=\s*\[", checksum, "\t");

                // 3. Append struct entry to download_songlist_props = { ... }
                var sb = new StringBuilder();
                sb.AppendLine($"\t{checksum} = {{");
                sb.AppendLine($"\t\tchecksum = {checksum}");
                sb.AppendLine($"\t\tname = '{cleanSongId.Replace("'", "")}'");
                sb.AppendLine($"\t\ttitle = \"{escTitle}\"");
                sb.AppendLine($"\t\tartist = \"{escArtist}\"");
                sb.AppendLine($"\t\t0x70a1c521 = \", {cleanYear}\"");
                sb.AppendLine($"\t\tyear = \", {cleanYear}\"");
                sb.AppendLine("\t\tartist_text = $artist_text_by");
                sb.AppendLine("\t\toriginal_artist = 1");
                sb.AppendLine("\t\tversion = gh3");
                sb.AppendLine("\t\tleaderboard = 1");
                sb.AppendLine("\t\tgem_offset = 0");
                sb.AppendLine("\t\tinput_offset = 0");
                sb.AppendLine("\t\tsinger = Male");
                sb.AppendLine("\t\tkeyboard = false");
                sb.AppendLine("\t\tband_playback_volume = 0.0");
                sb.AppendLine("\t\tguitar_playback_volume = 0.0");
                sb.AppendLine("\t\tcountoff = 'HiHat01'");
                sb.AppendLine("\t\trhythm_track = 1");
                sb.AppendLine("\t\thammer_on_measure_scale = 500.0");
                sb.AppendLine($"\t\t0xac77825a = \"{escCharter}\"");
                if (!string.IsNullOrWhiteSpace(escAlbum))
                {
                    sb.AppendLine($"\t\talbum_title = \"{escAlbum}\"");
                }
                sb.AppendLine("\t}");

                qbText = AppendToPropsBlock(qbText, sb.ToString());

                var ok = RecompileAndSaveCustomsPak(asm, pakPath, entriesObj, primaryEntry, qbText, logger);
                if (ok)
                {
                    logger?.LogInfo($"[CUSTOMS PAK] Merged '{title}' ({cleanSongId} / {checksum}) into DATA\\customs.pak.xen cleanly.");
                }
                return ok;
            }
            catch (Exception ex)
            {
                logger?.LogWarn($"[CUSTOMS PAK] Merge failed for {songId}: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// Removes a song's entry from DATA\customs.pak.xen cleanly so deleted or temporary songs
    /// never leave ghost entries in the in-game setlist.
    /// </summary>
    public static bool RemoveSongFromCustomsPak(string gamePath, string songId, SyncLogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || string.IsNullOrWhiteSpace(songId)) return false;

        var pakPath = Path.Combine(gamePath, "DATA", "customs.pak.xen");
        if (!File.Exists(pakPath)) return false;

        lock (_pakLock)
        {
            try
            {
                var loaded = LoadCoreAssembly(gamePath);
                if (loaded == null) return false;
                var (asm, _) = loaded.Value;

                var crcType = asm.GetType("GH_Toolkit_Core.Checksum.CRC");
                var qbKeyMethod = crcType?.GetMethod("QBKey", new[] { typeof(string), typeof(bool) });
                var cleanSongId = songId.Trim();
                var checksum = qbKeyMethod?.Invoke(null, new object[] { cleanSongId, false })?.ToString() ?? "";

                var (entriesObj, primaryEntry, qbText) = DecompileCustomsPak(asm, pakPath);
                if (entriesObj == null || primaryEntry == null || string.IsNullOrWhiteSpace(qbText))
                    return false;

                var updated = RemoveSongFromQbText(qbText, checksum, cleanSongId);
                if (string.Equals(updated, qbText, StringComparison.Ordinal))
                {
                    // Song was not present in customs.pak.xen
                    return true;
                }

                var ok = RecompileAndSaveCustomsPak(asm, pakPath, entriesObj, primaryEntry, updated, logger);
                if (ok)
                {
                    logger?.LogInfo($"[CUSTOMS PAK] Stripped '{cleanSongId}' ({checksum}) from DATA\\customs.pak.xen.");
                }
                return ok;
            }
            catch (Exception ex)
            {
                logger?.LogWarn($"[CUSTOMS PAK] Remove failed for {songId}: {ex.Message}");
                return false;
            }
        }
    }

    private static (object? EntriesObj, object? PrimaryEntry, string QbText) DecompileCustomsPak(Assembly asm, string pakPath)
    {
        var pakType = asm.GetType("GH_Toolkit_Core.PAK.PAK");
        var qbType = asm.GetType("GH_Toolkit_Core.QB.QB");
        if (pakType == null || qbType == null) return (null, null, "");

        var entriesObj = pakType.GetMethod("PakEntriesFromFilepath")?.Invoke(null, new object[] { pakPath });
        if (entriesObj is not IList entryList || entryList.Count == 0) return (null, null, "");

        object? targetEntry = null;
        foreach (var e in entryList)
        {
            var ext = e?.GetType().GetProperty("Extension")?.GetValue(e) as string;
            if (string.Equals(ext, ".qb", StringComparison.OrdinalIgnoreCase))
            {
                targetEntry = e;
                break;
            }
        }
        targetEntry ??= entryList[0];
        if (targetEntry == null) return (null, null, "");

        var data = targetEntry.GetType().GetProperty("EntryData")?.GetValue(targetEntry) as byte[];
        if (data == null || data.Length == 0) return (null, null, "");

        var qbList = qbType.GetMethod("DecompileQb")?.Invoke(null, new object[] { data, "big", "", "GH3", "PC" });
        if (qbList == null) return (null, null, "");

        var txt = qbType.GetMethod("QbToTextString")?.Invoke(null, new object[] { qbList }) as string ?? "";
        return (entriesObj, targetEntry, txt);
    }

    private static bool RecompileAndSaveCustomsPak(
        Assembly asm,
        string pakPath,
        object entriesObj,
        object primaryEntry,
        string qbText,
        SyncLogger? logger)
    {
        var qbType = asm.GetType("GH_Toolkit_Core.QB.QB");
        var pakCompilerType = asm.GetType("GH_Toolkit_Core.PAK.PAK+PakCompiler");
        if (qbType == null || pakCompilerType == null) return false;

        var entryType = primaryEntry.GetType();
        var fullName = entryType.GetProperty("FullName")?.GetValue(primaryEntry) as string ?? "0x2cbadf14.0x41ca13c0.qb";

        var parsedTuple = qbType.GetMethod("ParseQFile")?.Invoke(null, new object[] { qbText, "PC", "GH3" });
        if (parsedTuple == null) return false;

        var parsedList = parsedTuple.GetType().GetField("Item1")?.GetValue(parsedTuple);
        if (parsedList == null) return false;

        var newQbBytes = qbType.GetMethod("CompileQbFile")?.Invoke(null, new object[] { parsedList, fullName, "GH3", "PC" }) as byte[];
        if (newQbBytes == null || newQbBytes.Length < 16) return false;

        entryType.GetProperty("EntryData")?.SetValue(primaryEntry, newQbBytes);
        entryType.GetProperty("FileSize")?.SetValue(primaryEntry, (uint)newQbBytes.Length);

        var compiler = Activator.CreateInstance(pakCompilerType, new object[] { "GH3", "PC", "", true, false });
        if (compiler == null) return false;

        var compiledTuple = pakCompilerType.GetMethod("CompilePakEntries")?.Invoke(compiler, new object[] { entriesObj });
        if (compiledTuple == null) return false;

        var pakBytes = compiledTuple.GetType().GetField("Item1")?.GetValue(compiledTuple) as byte[];
        if (pakBytes == null || pakBytes.Length < 32) return false;

        // Validate GH3 PC PAK magic header (A7 F5 05 C4)
        if (pakBytes[0] != 0xA7 || pakBytes[1] != 0xF5 || pakBytes[2] != 0x05 || pakBytes[3] != 0xC4)
        {
            logger?.LogWarn("[CUSTOMS PAK] Compiled PAK failed magic header check; aborting write.");
            return false;
        }

        // Write atomically via temp file + backup
        var tmpPath = pakPath + ".tmp";
        File.WriteAllBytes(tmpPath, pakBytes);
        var backupPath = pakPath + ".gh3sync_last.bak";
        if (File.Exists(pakPath))
        {
            File.Copy(pakPath, backupPath, overwrite: true);
        }
        File.Move(tmpPath, pakPath, overwrite: true);
        return true;
    }

    private static List<CustomsPakSongEntry> ParseSongPropsFromQbText(string qbText)
    {
        var results = new List<CustomsPakSongEntry>();
        int propsIdx = qbText.IndexOf("download_songlist_props", StringComparison.OrdinalIgnoreCase);
        if (propsIdx < 0) return results;

        var propsSection = qbText.Substring(propsIdx);
        var blockRegex = new Regex(@"(?m)^\t([A-Za-z0-9_x]+)\s*=\s*\{([\s\S]*?)^\t\}", RegexOptions.Multiline);
        foreach (Match m in blockRegex.Matches(propsSection))
        {
            var key = m.Groups[1].Value.Trim();
            var body = m.Groups[2].Value;

            string ExtractQuoted(string field)
            {
                var fm = Regex.Match(body, $@"(?m)^\t\t{Regex.Escape(field)}\s*=\s*""([^""]*)""");
                return fm.Success ? fm.Groups[1].Value.Trim() : "";
            }
            string ExtractSingleQuoted(string field)
            {
                var fm = Regex.Match(body, $@"(?m)^\t\t{Regex.Escape(field)}\s*=\s*'([^']*)'");
                return fm.Success ? fm.Groups[1].Value.Trim() : "";
            }

            var name = ExtractSingleQuoted("name");
            var title = ExtractQuoted("title");
            var artist = ExtractQuoted("artist");
            var year = ExtractQuoted("year").TrimStart(',', ' ');
            var charter = ExtractQuoted("0xac77825a");
            var album = ExtractQuoted("album_title");

            if (!string.IsNullOrWhiteSpace(name))
            {
                results.Add(new CustomsPakSongEntry
                {
                    SongId = name,
                    Checksum = key,
                    Title = title,
                    Artist = artist,
                    Year = year,
                    Charter = charter,
                    Album = album
                });
            }
        }

        return results;
    }

    private static string RemoveSongFromQbText(string qbText, string checksum, string songId)
    {
        // Also find any existing checksum whose block has name = '<songId>' (in case casing differs)
        var keysToRemove = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(checksum)) keysToRemove.Add(checksum);

        foreach (var entry in ParseSongPropsFromQbText(qbText))
        {
            if (entry.SongId.Equals(songId, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(entry.Checksum))
            {
                keysToRemove.Add(entry.Checksum);
            }
        }

        foreach (var key in keysToRemove)
        {
            // Remove struct block in download_songlist_props
            var structPattern = $@"(?m)^\t{Regex.Escape(key)}\s*=\s*\{{[\s\S]*?^\t\}}\r?\n?";
            qbText = Regex.Replace(qbText, structPattern, "");

            // Remove array element line in tier1.songs & download_songlist
            var arrayItemPattern = $@"(?m)^\t+{Regex.Escape(key)}\r?\n";
            qbText = Regex.Replace(qbText, arrayItemPattern, "");
        }

        return qbText;
    }

    private static string InsertIntoFirstArrayBlock(string qbText, string openPattern, string item, string indent)
    {
        var m = Regex.Match(qbText, openPattern);
        if (!m.Success) return qbText;

        int closeIdx = qbText.IndexOf(']', m.Index + m.Length);
        if (closeIdx < 0) return qbText;

        var existingBlock = qbText.Substring(m.Index + m.Length, closeIdx - (m.Index + m.Length));
        if (Regex.IsMatch(existingBlock, $@"(?m)^\s*{Regex.Escape(item)}\s*$"))
            return qbText;

        // Find the start of the line holding ']' so indentation stays pristine
        int insertPos = closeIdx;
        while (insertPos > 0 && (qbText[insertPos - 1] == '\t' || qbText[insertPos - 1] == ' '))
            insertPos--;

        var newline = qbText.Contains("\r\n") ? "\r\n" : "\n";
        return qbText.Insert(insertPos, $"{indent}{item}{newline}");
    }

    private static string AppendToPropsBlock(string qbText, string entryBlock)
    {
        int propsIdx = qbText.IndexOf("download_songlist_props", StringComparison.OrdinalIgnoreCase);
        if (propsIdx < 0) return qbText;

        int lastClose = qbText.LastIndexOf('}');
        if (lastClose <= propsIdx) return qbText;

        return qbText.Insert(lastClose, entryBlock);
    }

    private static string EscapeQbString(string input)
    {
        return input.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ").Trim();
    }

    private static string ExtractYearDigits(string? year)
    {
        if (string.IsNullOrWhiteSpace(year)) return DateTime.Now.Year.ToString();
        var m = Regex.Match(year, @"\d{4}");
        return m.Success ? m.Value : DateTime.Now.Year.ToString();
    }
}
