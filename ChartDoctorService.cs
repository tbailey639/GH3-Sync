using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace GH3_Sync;

public class ChartPreviewNote
{
    public long Tick { get; set; }
    public double TimeSeconds { get; set; }
    public double EndTimeSeconds { get; set; }
    public int Fret { get; set; } // 0=G, 1=R, 2=Y, 3=B, 4=O, 7=Open
    public long LengthTicks { get; set; }
    public bool IsHopo { get; set; }
    public bool IsTap { get; set; }
    public bool InStarPower { get; set; }
}

public class ChartTempoPoint
{
    public long Tick { get; set; }
    public double Bpm { get; set; }
    public double TimeSeconds { get; set; }
}

public class ChartStarPowerPhrase
{
    public long StartTick { get; set; }
    public long LengthTicks { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
}

public class ChartSectionMarker
{
    public long Tick { get; set; }
    public double TimeSeconds { get; set; }
    public string Name { get; set; } = "";
}

public class ParsedChartData
{
    public string SourcePath { get; set; } = "";
    public int Resolution { get; set; } = 192;
    public double OffsetSeconds { get; set; } = 0.0;
    public List<ChartTempoPoint> Tempos { get; set; } = new();
    public Dictionary<string, List<ChartPreviewNote>> Difficulties { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<ChartStarPowerPhrase> StarPower { get; set; } = new();
    public List<ChartSectionMarker> Sections { get; set; } = new();
    public List<double> BeatTimesSeconds { get; set; } = new();
    public double TotalDurationSeconds { get; set; }

    public double TickToSeconds(long tick)
    {
        if (Tempos.Count == 0)
            return OffsetSeconds + (tick / (double)Math.Max(1, Resolution)) * (60.0 / 120.0);

        ChartTempoPoint active = Tempos[0];
        for (int i = 1; i < Tempos.Count; i++)
        {
            if (Tempos[i].Tick <= tick) active = Tempos[i];
            else break;
        }
        double beats = (tick - active.Tick) / (double)Math.Max(1, Resolution);
        return active.TimeSeconds + beats * (60.0 / Math.Max(20.0, active.Bpm));
    }

    public long SecondsToTick(double sec)
    {
        if (Tempos.Count == 0)
            return Math.Max(0, (long)Math.Round(((sec - OffsetSeconds) * (120.0 / 60.0)) * Math.Max(1, Resolution)));

        ChartTempoPoint active = Tempos[0];
        for (int i = 1; i < Tempos.Count; i++)
        {
            if (Tempos[i].TimeSeconds <= sec) active = Tempos[i];
            else break;
        }
        double beats = (sec - active.TimeSeconds) * (Math.Max(20.0, active.Bpm) / 60.0);
        return Math.Max(0, active.Tick + (long)Math.Round(beats * Math.Max(1, Resolution)));
    }
}

public class ChartDoctorIssue
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "Warning"; // Warning, Error, Info
    public string Description { get; set; } = "";
    public bool AutoFixable { get; set; } = true;
}

public class ChartDoctorReport
{
    public bool CanAnalyze { get; set; }
    public string ChartPath { get; set; } = "";
    public string Summary { get; set; } = "";
    public int ExpertNoteCount { get; set; }
    public int HardNoteCount { get; set; }
    public int MediumNoteCount { get; set; }
    public int EasyNoteCount { get; set; }
    public int OpenNoteCount { get; set; }
    public int StarPowerCount { get; set; }
    public int SectionCount { get; set; }
    public double PeakNps { get; set; }
    public List<ChartDoctorIssue> Issues { get; set; } = new();
}

public class ChartDoctorFixOptions
{
    public bool GenerateMissingDifficulties { get; set; } = true;
    public bool AddMissingStarPower { get; set; } = true;
    public bool AddMissingSections { get; set; } = true;
    public bool ConvertOpenNotesToClassicGh3 { get; set; } = false;
    public int OffsetShiftMs { get; set; } = 0;
    public bool RecompileToGame { get; set; } = true;
}

/// <summary>
/// Parses .chart files for the Interactive Mini Highway Previewer AND provides the "Chart Doctor"
/// diagnostic & auto-repair engine (missing Easy/Medium/Hard diffs, Star Power, Practice sections,
/// open-note conversion, and ±ms audio-chart sync offset shifting).
/// </summary>
public class ChartDoctorService
{
    private readonly HoneycombCompilerService _compiler;
    private readonly SyncLogger _logger;

    public ChartDoctorService(HoneycombCompilerService compiler, SyncLogger logger)
    {
        _compiler = compiler;
        _logger = logger;
    }

    public static string? FindChartFileInProject(string? projectDir)
    {
        if (string.IsNullOrWhiteSpace(projectDir) || !Directory.Exists(projectDir)) return null;
        var chart = Path.Combine(projectDir, "notes.chart");
        if (File.Exists(chart)) return chart;
        return Directory.GetFiles(projectDir, "*.chart").FirstOrDefault();
    }

    public static ParsedChartData ParseChartFile(string chartPath)
    {
        var data = new ParsedChartData { SourcePath = chartPath };
        if (!File.Exists(chartPath)) return data;

        var lines = File.ReadAllLines(chartPath);
        string section = "";
        var rawNotesByDiff = new Dictionary<string, List<(long Tick, int Fret, long Len)>>(StringComparer.OrdinalIgnoreCase);
        var forcedTicksByDiff = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var tapTicksByDiff = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
        var rawSp = new List<(long Tick, long Len)>();

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line == "{" || line == "}") continue;
            if (line.StartsWith("[") && line.EndsWith("]"))
            {
                section = line.Substring(1, line.Length - 2);
                continue;
            }

            if (section.Equals("Song", StringComparison.OrdinalIgnoreCase))
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    var key = line.Substring(0, eq).Trim();
                    var val = line.Substring(eq + 1).Trim().Trim('"');
                    if (key.Equals("Resolution", StringComparison.OrdinalIgnoreCase) && int.TryParse(val, out var res) && res > 0)
                        data.Resolution = res;
                    else if (key.Equals("Offset", StringComparison.OrdinalIgnoreCase) && double.TryParse(val, NumberStyles.Float, CultureInfo.InvariantCulture, out var off))
                        data.OffsetSeconds = off;
                }
            }
            else if (section.Equals("SyncTrack", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"^(\d+)\s*=\s*B\s+(\d+)$", RegexOptions.IgnoreCase);
                if (m.Success && long.TryParse(m.Groups[1].Value, out var tick) && long.TryParse(m.Groups[2].Value, out var milliBpm))
                {
                    data.Tempos.Add(new ChartTempoPoint { Tick = tick, Bpm = Math.Max(20.0, milliBpm / 1000.0) });
                }
            }
            else if (section.Equals("Events", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"^(\d+)\s*=\s*E\s+""(?:section\s+)?([^""]+)""$", RegexOptions.IgnoreCase);
                if (m.Success && long.TryParse(m.Groups[1].Value, out var tick))
                {
                    var name = m.Groups[2].Value.Trim();
                    if (!name.StartsWith("lyric", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("phrase_", StringComparison.OrdinalIgnoreCase))
                        data.Sections.Add(new ChartSectionMarker { Tick = tick, Name = name });
                }
            }
            else if (section.EndsWith("Single", StringComparison.OrdinalIgnoreCase))
            {
                var diffName = section.Substring(0, section.Length - "Single".Length);
                if (!rawNotesByDiff.ContainsKey(diffName))
                {
                    rawNotesByDiff[diffName] = new();
                    forcedTicksByDiff[diffName] = new();
                    tapTicksByDiff[diffName] = new();
                }

                var nm = Regex.Match(line, @"^(\d+)\s*=\s*N\s+(\d+)\s+(\d+)$", RegexOptions.IgnoreCase);
                if (nm.Success &&
                    long.TryParse(nm.Groups[1].Value, out var tick) &&
                    int.TryParse(nm.Groups[2].Value, out var fret) &&
                    long.TryParse(nm.Groups[3].Value, out var len))
                {
                    if (fret == 5) forcedTicksByDiff[diffName].Add(tick);
                    else if (fret == 6) tapTicksByDiff[diffName].Add(tick);
                    else if ((fret >= 0 && fret <= 4) || fret == 7)
                        rawNotesByDiff[diffName].Add((tick, fret, len));
                    continue;
                }

                var sm = Regex.Match(line, @"^(\d+)\s*=\s*S\s+2\s+(\d+)$", RegexOptions.IgnoreCase);
                if (sm.Success && diffName.Equals("Expert", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(sm.Groups[1].Value, out var spTick) &&
                    long.TryParse(sm.Groups[2].Value, out var spLen))
                {
                    rawSp.Add((spTick, spLen));
                }
            }
        }

        // Compute tempo map timestamps
        if (data.Tempos.Count == 0)
            data.Tempos.Add(new ChartTempoPoint { Tick = 0, Bpm = 120.0, TimeSeconds = data.OffsetSeconds });
        else
        {
            data.Tempos = data.Tempos.OrderBy(t => t.Tick).ToList();
            if (data.Tempos[0].Tick > 0)
                data.Tempos.Insert(0, new ChartTempoPoint { Tick = 0, Bpm = data.Tempos[0].Bpm });

            double curSec = data.OffsetSeconds;
            data.Tempos[0].TimeSeconds = curSec;
            for (int i = 1; i < data.Tempos.Count; i++)
            {
                double beats = (data.Tempos[i].Tick - data.Tempos[i - 1].Tick) / (double)Math.Max(1, data.Resolution);
                curSec += beats * (60.0 / Math.Max(20.0, data.Tempos[i - 1].Bpm));
                data.Tempos[i].TimeSeconds = curSec;
            }
        }

        foreach (var (spTick, spLen) in rawSp)
        {
            data.StarPower.Add(new ChartStarPowerPhrase
            {
                StartTick = spTick,
                LengthTicks = spLen,
                StartSec = data.TickToSeconds(spTick),
                EndSec = data.TickToSeconds(spTick + spLen)
            });
        }

        foreach (var sec in data.Sections)
            sec.TimeSeconds = data.TickToSeconds(sec.Tick);

        long maxTick = 0;
        long hopoThresholdTicks = (long)Math.Round(data.Resolution * (65.0 / 192.0)) + 1;

        foreach (var kv in rawNotesByDiff)
        {
            var diff = kv.Key;
            var grouped = kv.Value.GroupBy(n => n.Tick).OrderBy(g => g.Key).ToList();
            var built = new List<ChartPreviewNote>();
            var forcedSet = forcedTicksByDiff[diff];
            var tapSet = tapTicksByDiff[diff];

            long prevTick = -10000;
            int prevFret = -1;
            int prevCount = 0;

            foreach (var g in grouped)
            {
                var tick = g.Key;
                if (tick > maxTick) maxTick = tick;
                var notesAtTick = g.DistinctBy(x => x.Fret).ToList();
                bool isChord = notesAtTick.Count > 1;
                bool isTap = tapSet.Contains(tick);
                bool naturalHopo = !isChord && prevCount == 1 && (tick - prevTick) <= hopoThresholdTicks && notesAtTick[0].Fret != prevFret;
                bool isHopo = forcedSet.Contains(tick) ? !naturalHopo : naturalHopo;
                bool inSp = data.StarPower.Any(sp => tick >= sp.StartTick && tick <= sp.StartTick + sp.LengthTicks);

                double tSec = data.TickToSeconds(tick);
                foreach (var n in notesAtTick)
                {
                    long endTick = tick + n.Len;
                    if (endTick > maxTick) maxTick = endTick;
                    built.Add(new ChartPreviewNote
                    {
                        Tick = tick,
                        TimeSeconds = tSec,
                        EndTimeSeconds = n.Len > 0 ? data.TickToSeconds(endTick) : tSec,
                        Fret = n.Fret,
                        LengthTicks = n.Len,
                        IsHopo = isHopo,
                        IsTap = isTap,
                        InStarPower = inSp
                    });
                }

                prevTick = tick;
                prevFret = notesAtTick[0].Fret;
                prevCount = notesAtTick.Count;
            }

            data.Difficulties[diff] = built;
        }

        data.TotalDurationSeconds = Math.Max(5.0, data.TickToSeconds(maxTick + data.Resolution * 4));
        for (long bt = 0; bt <= maxTick + data.Resolution * 8; bt += data.Resolution)
            data.BeatTimesSeconds.Add(data.TickToSeconds(bt));

        return data;
    }

    public ChartDoctorReport AnalyzeProject(string projectDir)
    {
        var report = new ChartDoctorReport();
        var chartPath = FindChartFileInProject(projectDir);
        if (string.IsNullOrEmpty(chartPath))
        {
            report.CanAnalyze = false;
            report.Summary = "No editable notes.chart found in this song folder (MIDI-only or binary-only song).";
            return report;
        }

        var parsed = ParseChartFile(chartPath);
        report.CanAnalyze = true;
        report.ChartPath = chartPath;
        report.ExpertNoteCount = parsed.Difficulties.TryGetValue("Expert", out var ex) ? ex.Count : 0;
        report.HardNoteCount = parsed.Difficulties.TryGetValue("Hard", out var hd) ? hd.Count : 0;
        report.MediumNoteCount = parsed.Difficulties.TryGetValue("Medium", out var md) ? md.Count : 0;
        report.EasyNoteCount = parsed.Difficulties.TryGetValue("Easy", out var ez) ? ez.Count : 0;
        report.StarPowerCount = parsed.StarPower.Count;
        report.SectionCount = parsed.Sections.Count;
        report.OpenNoteCount = parsed.Difficulties.Values.SelectMany(v => v).Count(n => n.Fret == 7);

        if (ex != null && ex.Count > 0)
        {
            double peak = 0;
            int left = 0;
            for (int right = 0; right < ex.Count; right++)
            {
                while (ex[right].TimeSeconds - ex[left].TimeSeconds > 1.0) left++;
                peak = Math.Max(peak, right - left + 1);
            }
            report.PeakNps = peak;
        }

        var missingDiffs = new List<string>();
        if (report.HardNoteCount == 0 && report.ExpertNoteCount > 0) missingDiffs.Add("Hard");
        if (report.MediumNoteCount == 0 && report.ExpertNoteCount > 0) missingDiffs.Add("Medium");
        if (report.EasyNoteCount == 0 && report.ExpertNoteCount > 0) missingDiffs.Add("Easy");
        if (missingDiffs.Count > 0)
        {
            report.Issues.Add(new ChartDoctorIssue
            {
                Code = "MISSING_DIFFS",
                Severity = "Warning",
                Description = $"Missing lower difficulties: {string.Join(", ", missingDiffs)} (Co-Op or casual players on those levels will see an empty highway)."
            });
        }

        if (report.StarPowerCount == 0 && report.ExpertNoteCount > 20)
        {
            report.Issues.Add(new ChartDoctorIssue
            {
                Code = "NO_STAR_POWER",
                Severity = "Warning",
                Description = "No Star Power phrases (S 2) detected — players cannot earn Star Power."
            });
        }

        if (report.SectionCount < 2 && report.ExpertNoteCount > 20)
        {
            report.Issues.Add(new ChartDoctorIssue
            {
                Code = "NO_SECTIONS",
                Severity = "Info",
                Description = "No Practice Mode sections found in [Events]."
            });
        }

        if (report.OpenNoteCount > 0)
        {
            report.Issues.Add(new ChartDoctorIssue
            {
                Code = "OPEN_NOTES",
                Severity = "Info",
                Description = $"Contains {report.OpenNoteCount} Clone Hero open-strum bar note(s) (N 7). Supported in BetterGH3, or optionally convertible to classic Green notes."
            });
        }

        report.Summary = report.Issues.Count == 0
            ? $"Chart is healthy! Expert: {report.ExpertNoteCount:N0} notes • Peak {report.PeakNps:F0} NPS • {report.StarPowerCount} Star Power phrases • {report.SectionCount} sections."
            : $"Found {report.Issues.Count} item(s) to review/improve (Expert: {report.ExpertNoteCount:N0} notes, Peak {report.PeakNps:F0} NPS).";

        return report;
    }

    public async Task<(bool Ok, string Message)> RepairChartAndRecompileAsync(
        string projectDir,
        ChartDoctorFixOptions opts,
        Action<string>? onProgress = null)
    {
        var chartPath = FindChartFileInProject(projectDir);
        if (string.IsNullOrEmpty(chartPath))
            return (false, "No notes.chart found in this project folder.");

        try
        {
            onProgress?.Invoke("Parsing chart structure...");
            var parsed = ParseChartFile(chartPath);
            var rawLines = File.ReadAllLines(chartPath).ToList();
            var sections = SplitChartSections(rawLines);
            var changes = new List<string>();

            // 1. Optional Sync Offset shift (±ms)
            if (opts.OffsetShiftMs != 0)
            {
                double firstBpm = parsed.Tempos.FirstOrDefault()?.Bpm ?? 120.0;
                long tickDelta = (long)Math.Round((opts.OffsetShiftMs / 1000.0) * (firstBpm / 60.0) * parsed.Resolution);
                if (tickDelta != 0)
                {
                    foreach (var secKey in sections.Keys.ToList())
                    {
                        if (secKey.Equals("Song", StringComparison.OrdinalIgnoreCase) || secKey.Equals("SyncTrack", StringComparison.OrdinalIgnoreCase))
                            continue;
                        sections[secKey] = ShiftSectionTicks(sections[secKey], tickDelta);
                    }
                    changes.Add($"Shifted notes by {opts.OffsetShiftMs:+0;-0;0} ms ({tickDelta:+0;-0;0} ticks)");
                }
            }

            // 2. Optional Classic 5-Fret Open Note conversion (N 7 -> N 0)
            if (opts.ConvertOpenNotesToClassicGh3)
            {
                int converted = 0;
                foreach (var secKey in sections.Keys.ToList())
                {
                    if (!secKey.EndsWith("Single", StringComparison.OrdinalIgnoreCase) && !secKey.EndsWith("DoubleBass", StringComparison.OrdinalIgnoreCase))
                        continue;
                    var updated = new List<string>();
                    foreach (var l in sections[secKey])
                    {
                        var m = Regex.Match(l, @"^(\s*\d+\s*=\s*N\s+)7(\s+\d+\s*)$", RegexOptions.IgnoreCase);
                        if (m.Success) { updated.Add($"{m.Groups[1].Value}0{m.Groups[2].Value}"); converted++; }
                        else updated.Add(l);
                    }
                    sections[secKey] = updated;
                }
                if (converted > 0) changes.Add($"Converted {converted} open note(s) to Green");
            }

            // 3. Auto-generate missing Easy / Medium / Hard diffs from Expert
            if (opts.GenerateMissingDifficulties && parsed.Difficulties.TryGetValue("Expert", out var expertNotes) && expertNotes.Count > 0)
            {
                var expByTick = expertNotes.GroupBy(n => n.Tick).OrderBy(g => g.Key).ToList();
                var res = parsed.Resolution;

                void EnsureDiff(string diffSection, int maxFret, long minGapTicks, int maxChordSize)
                {
                    if (sections.TryGetValue(diffSection, out var existing) && existing.Any(l => l.Contains("= N ")))
                        return;

                    var outLines = new List<string>();
                    long lastTick = -100000;
                    foreach (var g in expByTick)
                    {
                        if (g.Key - lastTick < minGapTicks) continue;
                        var picked = g.Take(maxChordSize).ToList();
                        foreach (var n in picked)
                        {
                            int f = n.Fret == 7 ? 0 : Math.Min(maxFret, n.Fret);
                            outLines.Add($"  {g.Key} = N {f} {n.LengthTicks}");
                        }
                        lastTick = g.Key;
                    }
                    if (outLines.Count > 0)
                    {
                        sections[diffSection] = outLines;
                        changes.Add($"Generated {diffSection} ({outLines.Count} notes)");
                    }
                }

                EnsureDiff("HardSingle", maxFret: 4, minGapTicks: res / 2, maxChordSize: 2);
                EnsureDiff("MediumSingle", maxFret: 3, minGapTicks: res, maxChordSize: 2);
                EnsureDiff("EasySingle", maxFret: 2, minGapTicks: res * 2, maxChordSize: 1);
            }

            // 4. Auto-insert Star Power phrases if missing
            if (opts.AddMissingStarPower && parsed.StarPower.Count == 0 && parsed.Difficulties.TryGetValue("Expert", out var expForSp) && expForSp.Count > 20)
            {
                long firstTick = expForSp.First().Tick;
                long lastTick = expForSp.Last().Tick;
                long barTicks = parsed.Resolution * 4L;
                var spLines = new List<string>();
                for (long t = firstTick + barTicks * 4; t + barTicks * 2 < lastTick; t += barTicks * 12)
                {
                    spLines.Add($"  {t} = S 2 {barTicks * 2}");
                }
                if (spLines.Count > 0)
                {
                    foreach (var diffKey in new[] { "ExpertSingle", "HardSingle", "MediumSingle", "EasySingle" })
                    {
                        if (sections.ContainsKey(diffKey))
                            sections[diffKey] = InsertSortedChartLines(sections[diffKey], spLines);
                    }
                    changes.Add($"Added {spLines.Count} Star Power phrase(s)");
                }
            }

            // 5. Auto-insert Practice Mode sections if missing
            if (opts.AddMissingSections && parsed.Sections.Count < 2 && parsed.Difficulties.TryGetValue("Expert", out var expForSec) && expForSec.Count > 20)
            {
                long lastTick = expForSec.Last().Tick;
                long step = Math.Max(parsed.Resolution * 16L, lastTick / 6);
                string[] labels = { "Intro", "Verse 1", "Chorus 1", "Verse 2", "Guitar Solo", "Chorus 2", "Outro" };
                var secLines = new List<string>();
                int idx = 0;
                for (long t = 0; t < lastTick && idx < labels.Length; t += step, idx++)
                    secLines.Add($"  {t} = E \"section {labels[idx]}\"");

                if (!sections.ContainsKey("Events")) sections["Events"] = new List<string>();
                sections["Events"] = InsertSortedChartLines(sections["Events"], secLines);
                changes.Add($"Added {secLines.Count} Practice sections");
            }

            // Write updated chart back safely (with backup)
            var bakPath = chartPath + ".doctor.bak";
            if (!File.Exists(bakPath)) File.Copy(chartPath, bakPath, overwrite: false);
            File.WriteAllLines(chartPath, RebuildChartLines(sections));

            // Re-convert notes.chart -> notes.mid
            var honeycombDir = HoneycombCompilerService.FindHoneycombDirectory();
            if (!string.IsNullOrEmpty(honeycombDir))
            {
                onProgress?.Invoke("Converting repaired chart to MIDI...");
                HoneycombCompilerService.ConvertChartToMid(chartPath, honeycombDir);
            }

            if (opts.RecompileToGame)
            {
                var ghproj = Directory.GetFiles(projectDir, "*.ghproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(ghproj))
                {
                    onProgress?.Invoke("Recompiling repaired chart into Guitar Hero III...");
                    var comp = await _compiler.CompileSongProjectAsync(ghproj);
                    if (!comp.Success)
                        return (false, $"Chart repaired ({string.Join(", ", changes)}), but recompile failed: {comp.Message}");
                }
            }

            var summary = changes.Count > 0 ? string.Join(" • ", changes) : "Chart re-verified & recompiled cleanly";
            _logger.LogInfo($"[CHART DOCTOR] Repaired '{Path.GetFileName(projectDir)}': {summary}");
            return (true, $"Chart Doctor Complete! {summary}");
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[CHART DOCTOR] Failed: {ex.Message}");
            return (false, $"Chart Doctor error: {ex.Message}");
        }
    }

    private static Dictionary<string, List<string>> SplitChartSections(List<string> lines)
    {
        var dict = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        string current = "";
        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
            {
                current = trimmed.Substring(1, trimmed.Length - 2);
                if (!dict.ContainsKey(current)) dict[current] = new List<string>();
                continue;
            }
            if (trimmed == "{" || trimmed == "}" || string.IsNullOrEmpty(current)) continue;
            dict[current].Add(raw);
        }
        return dict;
    }

    private static List<string> RebuildChartLines(Dictionary<string, List<string>> sections)
    {
        var output = new List<string>();
        foreach (var kv in sections)
        {
            output.Add($"[{kv.Key}]");
            output.Add("{");
            output.AddRange(kv.Value);
            output.Add("}");
        }
        return output;
    }

    private static List<string> ShiftSectionTicks(List<string> lines, long delta)
    {
        var result = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var m = Regex.Match(line, @"^(\s*)(\d+)(\s*=.*)$");
            if (m.Success && long.TryParse(m.Groups[2].Value, out var tick))
            {
                long shifted = Math.Max(0, tick + delta);
                result.Add($"{m.Groups[1].Value}{shifted}{m.Groups[3].Value}");
            }
            else result.Add(line);
        }
        return result;
    }

    /// <summary>
    /// Computes a lightweight Note Intensity Sparkline (normalized 0..1 across N buckets)
    /// and a 1..5 Guitar Hero Difficulty Tier from Expert notes for My Customs song cards.
    /// </summary>
    public static (double[] Buckets, int DifficultyTier, double PeakNps) ComputeIntensitySparkline(string? chartPath, int bucketCount = 18)
    {
        var buckets = new double[Math.Max(4, bucketCount)];
        if (string.IsNullOrWhiteSpace(chartPath) || !File.Exists(chartPath))
            return (buckets, 3, 0);

        try
        {
            var parsed = ParseChartFile(chartPath);
            var notes = parsed.Difficulties.TryGetValue("Expert", out var ex) && ex.Count > 0
                ? ex
                : parsed.Difficulties.Values.FirstOrDefault() ?? new List<ChartPreviewNote>();
            if (notes.Count == 0) return (buckets, 1, 0);

            double maxT = Math.Max(1.0, notes[^1].TimeSeconds);
            var counts = new int[buckets.Length];
            foreach (var n in notes)
            {
                int idx = Math.Clamp((int)((n.TimeSeconds / maxT) * buckets.Length), 0, buckets.Length - 1);
                counts[idx]++;
            }

            int peakBucket = Math.Max(1, counts.Max());
            for (int i = 0; i < buckets.Length; i++)
                buckets[i] = Math.Clamp(counts[i] / (double)peakBucket, 0.08, 1.0);

            double avgNps = notes.Count / Math.Max(10.0, maxT);
            double peakNps = 0;
            int left = 0;
            for (int r = 0; r < notes.Count; r++)
            {
                while (notes[r].TimeSeconds - notes[left].TimeSeconds > 1.0) left++;
                peakNps = Math.Max(peakNps, r - left + 1);
            }

            int tier = (avgNps, peakNps) switch
            {
                ( >= 7.5, _) or (_, >= 14) => 5,
                ( >= 5.5, _) or (_, >= 10) => 4,
                ( >= 3.5, _) or (_, >= 7) => 3,
                ( >= 2.0, _) => 2,
                _ => 1
            };

            return (buckets, tier, peakNps);
        }
        catch
        {
            return (buckets, 3, 0);
        }
    }

    /// <summary>
    /// Saves visually edited notes from the Highway Previewer Quick-Fix Note Editor back into
    /// notes.chart, re-converts notes.mid, and recompiles into Guitar Hero III.
    /// </summary>
    public async Task<(bool Ok, string Message)> SaveEditedDifficultyNotesAsync(
        string projectDir,
        string difficultyName,
        List<ChartPreviewNote> editedNotes,
        bool recompile = true)
    {
        var chartPath = FindChartFileInProject(projectDir);
        if (string.IsNullOrEmpty(chartPath))
            return (false, "No notes.chart found in this project folder.");

        try
        {
            var rawLines = File.ReadAllLines(chartPath).ToList();
            var sections = SplitChartSections(rawLines);
            var targetSection = $"{difficultyName}Single";

            // Preserve existing Star Power (S 2) lines in that difficulty section
            var preservedSp = sections.TryGetValue(targetSection, out var oldLines)
                ? oldLines.Where(l => Regex.IsMatch(l, @"^\s*\d+\s*=\s*S\s+", RegexOptions.IgnoreCase)).ToList()
                : new List<string>();

            var noteLines = new List<string>();
            var grouped = editedNotes.OrderBy(n => n.Tick).ThenBy(n => n.Fret).GroupBy(n => n.Tick);
            foreach (var g in grouped)
            {
                var distinct = g.DistinctBy(x => x.Fret).ToList();
                foreach (var n in distinct)
                    noteLines.Add($"  {Math.Max(0, n.Tick)} = N {n.Fret} {Math.Max(0, n.LengthTicks)}");
                if (distinct.Any(n => n.IsHopo) && distinct.Count == 1)
                    noteLines.Add($"  {Math.Max(0, g.Key)} = N 5 0");
            }

            sections[targetSection] = InsertSortedChartLines(preservedSp, noteLines);

            var bak = chartPath + ".edit.bak";
            if (!File.Exists(bak)) File.Copy(chartPath, bak, overwrite: false);
            File.WriteAllLines(chartPath, RebuildChartLines(sections));

            var hDir = HoneycombCompilerService.FindHoneycombDirectory();
            if (!string.IsNullOrEmpty(hDir))
                HoneycombCompilerService.ConvertChartToMid(chartPath, hDir);

            if (recompile)
            {
                var ghproj = Directory.GetFiles(projectDir, "*.ghproj").FirstOrDefault();
                if (!string.IsNullOrEmpty(ghproj))
                {
                    var comp = await _compiler.CompileSongProjectAsync(ghproj);
                    if (!comp.Success) return (false, $"Chart saved ({editedNotes.Count} notes), but recompile failed: {comp.Message}");
                }
            }

            _logger.LogInfo($"[NOTE EDITOR] Saved {editedNotes.Count} edited {difficultyName} notes for '{Path.GetFileName(projectDir)}'.");
            return (true, $"Saved {editedNotes.Count:N0} {difficultyName} notes & recompiled cleanly into Guitar Hero III!");
        }
        catch (Exception ex)
        {
            return (false, $"Could not save edited notes: {ex.Message}");
        }
    }

    private static List<string> InsertSortedChartLines(List<string> existing, List<string> additions)
    {
        var merged = new List<(long Tick, int Order, string Line)>();
        int ord = 0;
        foreach (var l in existing.Concat(additions))
        {
            var m = Regex.Match(l, @"^\s*(\d+)\s*=");
            long t = m.Success && long.TryParse(m.Groups[1].Value, out var parsed) ? parsed : 0;
            merged.Add((t, ord++, l));
        }
        return merged.OrderBy(x => x.Tick).ThenBy(x => x.Order).Select(x => x.Line).ToList();
    }
}
