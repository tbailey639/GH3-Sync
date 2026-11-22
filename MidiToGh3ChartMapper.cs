using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace GH3_Sync;

public class TranscribedNote
{
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public int MidiPitch { get; set; }
    public float Amplitude { get; set; } = 0.7f;
}

public class ChartBuildStats
{
    public int ExpertNoteCount { get; set; }
    public int BassNoteCount { get; set; }
    public int RepeatedRiffsMatched { get; set; }
    public int ForcedMarkersApplied { get; set; }
    public int ThreeNoteChordsCount { get; set; }
    public int SectionsCount { get; set; }
    public bool UsedConstantStudioGrid { get; set; }
}

internal class NoteEvent
{
    public double StartSec { get; set; }
    public double EndSec { get; set; }
    public int RootPitch { get; set; }
    public int TopPitch { get; set; }
    public int NoteCount { get; set; }
    public float PeakAmplitude { get; set; }
    public int Tick { get; set; }
    public int DurationTicks { get; set; }
}

internal record TempoAnchor(int Tick, double TimeSec, double Bpm);

internal record PhrasePattern(
    int StartBar,
    int[] RelativeSlots,
    int[] IntervalSteps,
    int[] AssignedFrets);

/// <summary>
/// ONOGAMI AI Charting Engine v2 — Handcrafted-grade 5-fret Guitar Hero III chart generator.
/// Features:
/// - Studio constant beat-grid lock vs. median-smoothed live variable tempo map
/// - Strict musical subdivision quantization (16th / 8th-triplet / 32nd shred bursts)
/// - 2-bar Riff &amp; Hook Repetition Memory (consistent muscle-memory fret patterns on repeating riffs)
/// - 4-fret hand-box ergonomics (smooth G-R-Y-B / R-Y-B-O box shifting)
/// - Authentic GH3 forced HOPO &amp; forced strum markers (N 5 0)
/// - Energy-driven 2-note power chords &amp; 3-note chorus downbeat chords
/// - Real bar-aligned audio section markers, Solo detection, Smart Star Power, &amp; Co-Op Bass track
/// </summary>
public static class MidiToGh3ChartMapper
{
    private const int Resolution = 192;
    private const int TicksPerBar = Resolution * 4;       // 768 ticks = 1 measure (4/4)
    private const int TicksPer2Bars = TicksPerBar * 2;    // 1536 ticks = 2-bar riff phrase
    private const int Gh3AutoHopoThreshold = 65;          // GH3 default HOPO threshold at res=192 (~170/480 = 68 ticks)

    public static string BuildChartFromNotes(
        SongCreationRequest meta,
        List<TranscribedNote> rawNotes,
        double exactBpm,
        double durationSeconds,
        string sourceLabel = "Mad Scientist",
        List<double>? beatTimestamps = null,
        List<TranscribedNote>? bassNotes = null,
        bool isStudioConstantTempo = true,
        List<double>? measureEnergies = null,
        List<DetectedMusicalSection>? detectedSections = null,
        ChartBuildStats? statsOut = null)
    {
        double safeBpm = Math.Clamp(exactBpm > 20 ? exactBpm : (meta.Bpm > 20 ? meta.Bpm : 120.0), 60.0, 240.0);

        // 1. Build Studio Constant Grid (locked to exact downbeat) OR Smooth Live Variable Tempo Map
        bool useConstant = isStudioConstantTempo || !meta.UseVariableTempoMap;
        var tempoAnchors = BuildTempoAnchors(safeBpm, beatTimestamps, useConstant);
        if (statsOut != null) statsOut.UsedConstantStudioGrid = useConstant;

        Func<double, int> secondsToTick = sec => ConvertSecondsToTick(sec, tempoAnchors);

        // 2. Clean, group, and strictly quantize raw transcribed notes onto musical subdivisions
        var allEvents = GroupAndQuantizeNoteEvents(rawNotes, secondsToTick);

        // Musical Key & Scale Snapping (eliminates microtonal feedback and out-of-scale dissonant notes)
        var detectedScale = DetectMusicalScale(allEvents);
        foreach (var ev in allEvents)
        {
            ev.RootPitch = detectedScale.SnapMidiPitch(ev.RootPitch);
            ev.TopPitch = detectedScale.SnapMidiPitch(ev.TopPitch);
        }

        double songDuration = durationSeconds > 5
            ? durationSeconds
            : (allEvents.Count > 0 ? allEvents[^1].EndSec + 2.0 : 180.0);

        var (lowPitch, highPitch) = ComputeRobustPitchRange(allEvents, 45, 72);

        double feelScale = (meta.ChartFeel ?? "Standard").ToLowerInvariant() switch
        {
            "casual" => 1.40,
            "shredder" => 0.72,
            _ => 1.0
        };

        (int soloStartTick, int soloEndTick) = DetectSoloSection(allEvents, secondsToTick, songDuration);

        bool hasCoopBass = bassNotes != null && bassNotes.Count >= 20;
        var bassEvents = hasCoopBass ? GroupAndQuantizeNoteEvents(bassNotes!, secondsToTick) : new List<NoteEvent>();
        if (bassEvents.Count < 16) hasCoopBass = false;
        else
        {
            foreach (var bev in bassEvents)
            {
                bev.RootPitch = detectedScale.SnapMidiPitch(bev.RootPitch);
                bev.TopPitch = detectedScale.SnapMidiPitch(bev.TopPitch);
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("[Song]");
        sb.AppendLine("{");
        sb.AppendLine($"  Name = \"{EscapeChartStr(meta.Title)}\"");
        sb.AppendLine($"  Artist = \"{EscapeChartStr(meta.Artist)}\"");
        sb.AppendLine($"  Charter = \"{EscapeChartStr(string.IsNullOrWhiteSpace(meta.Charter) ? sourceLabel : meta.Charter)}\"");
        sb.AppendLine($"  Album = \"{EscapeChartStr(meta.Album)}\"");
        sb.AppendLine($"  Year = \", {meta.Year}\"");
        sb.AppendLine("  Offset = 0");
        sb.AppendLine($"  Resolution = {Resolution}");
        sb.AppendLine($"  Player2 = {(hasCoopBass ? "bass" : "rhythm")}");
        sb.AppendLine($"  Difficulty = {meta.DiffGuitar}");
        sb.AppendLine($"  PreviewStart = {meta.PreviewStartMs}");
        sb.AppendLine($"  PreviewEnd = {meta.PreviewStartMs + 30000}");
        sb.AppendLine($"  Genre = \"{EscapeChartStr(meta.Genre.ToLowerInvariant())}\"");
        sb.AppendLine("  MediaType = \"cd\"");
        sb.AppendLine("  MusicStream = \"song.mp3\"");
        sb.AppendLine("}");

        // Write SyncTrack
        sb.AppendLine("[SyncTrack]");
        sb.AppendLine("{");
        sb.AppendLine("  0 = TS 4");
        double lastEmittedBpm = -1;
        int lastEmittedTick = -1;
        foreach (var anchor in tempoAnchors)
        {
            if (anchor.Tick == lastEmittedTick) continue;
            if (anchor.Tick == 0 || Math.Abs(anchor.Bpm - lastEmittedBpm) >= 0.35)
            {
                int bpmMicro = (int)Math.Round(anchor.Bpm * 1000.0);
                sb.AppendLine($"  {anchor.Tick} = B {bpmMicro}");
                lastEmittedBpm = anchor.Bpm;
                lastEmittedTick = anchor.Tick;
            }
        }
        sb.AppendLine("}");

        // Write authentic bar-aligned [Events] section markers & Solo markers
        int sectionsWritten = WriteEventsSection(sb, allEvents, detectedSections, secondsToTick, songDuration, soloStartTick, soloEndTick);
        if (statsOut != null) statsOut.SectionsCount = sectionsWritten;

        // Build all 4 Lead Guitar difficulties (with Riff Memory, Hand-Box Ergonomics, Forced HOPOs/Strums, Energy Chords)
        WriteDifficultyTrack(sb, "ExpertSingle", allEvents, lowPitch, highPitch, measureEnergies,
            soloStartTick, soloEndTick, safeBpm,
            maxFrets: 5, minSpacingSec: 0.052 * feelScale,
            allowChords: true, allowThreeNoteChords: true, allowSustains: true, allowForcedMarkers: true,
            ampFloor: 0.08f, isBassTrack: false, statsOut: statsOut);

        // HardSingle: Full 5 frets (Green through Orange)! Introduces the index-finger slide and moderate density
        WriteDifficultyTrack(sb, "HardSingle", allEvents, lowPitch, highPitch, measureEnergies,
            soloStartTick, soloEndTick, safeBpm,
            maxFrets: 5, minSpacingSec: 0.095 * feelScale,
            allowChords: true, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: true,
            ampFloor: 0.12f, isBassTrack: false, statsOut: null);

        // MediumSingle: Strictly 4 frets (Green, Red, Yellow, Blue). STRICTLY 0% Orange! Hand stays in home position
        WriteDifficultyTrack(sb, "MediumSingle", allEvents, lowPitch, highPitch, measureEnergies,
            soloStartTick, soloEndTick, safeBpm,
            maxFrets: 4, minSpacingSec: 0.220 * feelScale,
            allowChords: true, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: false,
            ampFloor: 0.22f, isBassTrack: false, statsOut: null);

        // EasySingle: Strictly 3 frets (Green, Red, Yellow). Single notes only, relaxed tempo
        WriteDifficultyTrack(sb, "EasySingle", allEvents, lowPitch, highPitch, measureEnergies,
            soloStartTick, soloEndTick, safeBpm,
            maxFrets: 3, minSpacingSec: 0.380 * feelScale,
            allowChords: false, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: false,
            ampFloor: 0.30f, isBassTrack: false, statsOut: null);

        // Build all 4 Co-Op Bass difficulties when isolated bass transcription is available
        if (hasCoopBass)
        {
            var (bassLow, bassHigh) = ComputeRobustPitchRange(bassEvents, 32, 56);
            var bassStats = new ChartBuildStats();
            WriteDifficultyTrack(sb, "ExpertDoubleBass", bassEvents, bassLow, bassHigh, measureEnergies,
                0, 0, safeBpm, maxFrets: 5, minSpacingSec: 0.105 * feelScale,
                allowChords: false, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: true,
                ampFloor: 0.14f, isBassTrack: true, statsOut: bassStats);

            WriteDifficultyTrack(sb, "HardDoubleBass", bassEvents, bassLow, bassHigh, measureEnergies,
                0, 0, safeBpm, maxFrets: 5, minSpacingSec: 0.160 * feelScale,
                allowChords: false, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: false,
                ampFloor: 0.18f, isBassTrack: true, statsOut: null);

            WriteDifficultyTrack(sb, "MediumDoubleBass", bassEvents, bassLow, bassHigh, measureEnergies,
                0, 0, safeBpm, maxFrets: 4, minSpacingSec: 0.260 * feelScale,
                allowChords: false, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: false,
                ampFloor: 0.24f, isBassTrack: true, statsOut: null);

            WriteDifficultyTrack(sb, "EasyDoubleBass", bassEvents, bassLow, bassHigh, measureEnergies,
                0, 0, safeBpm, maxFrets: 3, minSpacingSec: 0.420 * feelScale,
                allowChords: false, allowThreeNoteChords: false, allowSustains: true, allowForcedMarkers: false,
                ampFloor: 0.32f, isBassTrack: true, statsOut: null);

            if (statsOut != null) statsOut.BassNoteCount = bassStats.ExpertNoteCount;
        }

        return sb.ToString();
    }

    private static (int Low, int High) ComputeRobustPitchRange(List<NoteEvent> events, int defLow, int defHigh)
    {
        if (events.Count < 10) return (defLow, defHigh);
        var sorted = events.Select(e => e.RootPitch).OrderBy(p => p).ToList();
        int low = sorted[(int)(sorted.Count * 0.10)];
        int high = sorted[(int)(sorted.Count * 0.90)];
        if (high - low < 8) { low -= 4; high += 4; }
        return (low, high);
    }

    private static List<TempoAnchor> BuildTempoAnchors(double baseBpm, List<double>? beats, bool useStudioConstant)
    {
        var anchors = new List<TempoAnchor>();
        if (beats == null || beats.Count < 8)
        {
            anchors.Add(new TempoAnchor(0, 0.0, baseBpm));
            return anchors;
        }

        double firstBeat = beats[0];
        double secPerBeat = 60.0 / baseBpm;

        // Align downbeat cleanly: pick lead-in beat count (1..4 beats) so firstBeat lands on an exact beat grid line
        if (useStudioConstant)
        {
            if (firstBeat > 0.06 && firstBeat < secPerBeat * 4.5)
            {
                int leadBeats = Math.Clamp((int)Math.Round(firstBeat / secPerBeat), 1, 4);
                double leadBpm = Math.Clamp((leadBeats * 60.0) / firstBeat, 45.0, 260.0);
                anchors.Add(new TempoAnchor(0, 0.0, leadBpm));
                anchors.Add(new TempoAnchor(leadBeats * Resolution, firstBeat, baseBpm));
            }
            else
            {
                anchors.Add(new TempoAnchor(0, 0.0, baseBpm));
            }
            return anchors;
        }

        // Smooth Live Variable Tempo Map (median-filtered 4-bar windows to eliminate jitter)
        double initialBpm = baseBpm;
        int startTick = 0;
        double startTime = 0.0;
        if (firstBeat > 0.12 && firstBeat < 2.8)
        {
            int leadBeats = Math.Clamp((int)Math.Round(firstBeat / secPerBeat), 1, 4);
            initialBpm = Math.Clamp((leadBeats * 60.0) / firstBeat, baseBpm * 0.70, baseBpm * 1.35);
            startTick = leadBeats * Resolution;
            startTime = firstBeat;
        }
        anchors.Add(new TempoAnchor(0, 0.0, initialBpm));

        int stepBeats = 4;
        var rawMeasures = new List<(int Tick, double Time, double Bpm)>();
        int curTick = startTick;
        double curTime = startTime;

        for (int i = 0; i + stepBeats < beats.Count; i += stepBeats)
        {
            double dt = beats[i + stepBeats] - beats[i];
            if (dt <= 0.25) continue;
            double mBpm = Math.Clamp((stepBeats * 60.0) / dt, baseBpm * 0.80, baseBpm * 1.24);
            rawMeasures.Add((curTick, curTime, mBpm));
            curTick += stepBeats * Resolution;
            curTime += dt;
        }

        // 3-measure rolling median smoothing so genuine tempo drifts track smoothly without jitter
        for (int i = 0; i < rawMeasures.Count; i++)
        {
            var window = new List<double> { rawMeasures[i].Bpm };
            if (i > 0) window.Add(rawMeasures[i - 1].Bpm);
            if (i + 1 < rawMeasures.Count) window.Add(rawMeasures[i + 1].Bpm);
            window.Sort();
            double smoothed = window[window.Count / 2];
            anchors.Add(new TempoAnchor(rawMeasures[i].Tick, rawMeasures[i].Time, smoothed));
        }

        return anchors;
    }

    private static int ConvertSecondsToTick(double sec, List<TempoAnchor> anchors)
    {
        if (sec <= 0 || anchors.Count == 0) return 0;

        int idx = 0;
        for (int i = 1; i < anchors.Count; i++)
        {
            if (anchors[i].TimeSec <= sec) idx = i;
            else break;
        }

        var a = anchors[idx];
        double dt = sec - a.TimeSec;
        double ticksPerSec = Resolution * (a.Bpm / 60.0);
        return a.Tick + (int)Math.Round(dt * ticksPerSec);
    }

    private static List<NoteEvent> GroupAndQuantizeNoteEvents(List<TranscribedNote> rawNotes, Func<double, int> secondsToTick)
    {
        var events = new List<NoteEvent>();
        if (rawNotes == null || rawNotes.Count == 0) return events;

        var filtered = rawNotes
            .Where(n => n.StartSec >= 0.20 && n.EndSec > n.StartSec + 0.03 && n.MidiPitch >= 28 && n.MidiPitch <= 96)
            .OrderBy(n => n.StartSec)
            .ThenByDescending(n => n.Amplitude)
            .ToList();

        if (filtered.Count == 0) return events;

        const double chordWindowSec = 0.044;
        var groupedRaw = new List<(double StartSec, double EndSec, int RootPitch, int TopPitch, int NoteCount, float PeakAmp, int RawTick, int RawEndTick)>();

        int i = 0;
        while (i < filtered.Count)
        {
            var group = new List<TranscribedNote> { filtered[i] };
            double anchorStart = filtered[i].StartSec;
            int j = i + 1;
            while (j < filtered.Count && (filtered[j].StartSec - anchorStart) <= chordWindowSec)
            {
                group.Add(filtered[j]);
                j++;
            }

            var distinctPitches = group
                .GroupBy(n => n.MidiPitch)
                .Select(g => g.OrderByDescending(x => x.Amplitude).First())
                .OrderByDescending(n => n.Amplitude)
                .Take(3)
                .OrderBy(n => n.MidiPitch)
                .ToList();

            double startSec = distinctPitches.Min(n => n.StartSec);
            double endSec = distinctPitches.Max(n => n.EndSec);
            float peakAmp = distinctPitches.Max(n => n.Amplitude);

            groupedRaw.Add((startSec, endSec, distinctPitches[0].MidiPitch, distinctPitches[^1].MidiPitch,
                distinctPitches.Count, peakAmp, secondsToTick(startSec), secondsToTick(endSec)));
            i = j;
        }

        // Strict musical grid quantization:
        // Prefer 16th-note grid (48 ticks) and 8th-triplet grid (64 ticks);
        // allow 32nd-note grid (24 ticks) only when inside a rapid solo/shred burst (< 0.115s neighbor spacing).
        for (int k = 0; k < groupedRaw.Count; k++)
        {
            var g = groupedRaw[k];
            bool inFastBurst =
                (k > 0 && (g.StartSec - groupedRaw[k - 1].StartSec) < 0.115) ||
                (k + 1 < groupedRaw.Count && (groupedRaw[k + 1].StartSec - g.StartSec) < 0.115);

            int snap16 = ((g.RawTick + 24) / 48) * 48;
            int snapTriplet = ((g.RawTick + 32) / 64) * 64;
            int snap32 = ((g.RawTick + 12) / 24) * 24;

            int dist16 = Math.Abs(g.RawTick - snap16);
            int distTriplet = Math.Abs(g.RawTick - snapTriplet);
            int dist32 = Math.Abs(g.RawTick - snap32);

            int finalTick;
            if (inFastBurst && dist32 < dist16 - 4 && dist32 < distTriplet - 4)
            {
                finalTick = snap32;
            }
            else if (distTriplet + 5 < dist16)
            {
                finalTick = snapTriplet;
            }
            else
            {
                finalTick = snap16;
            }

            if (finalTick < Resolution) finalTick = Resolution;

            // Prevent overlapping ticks (merge stronger chord if two events quantized onto the exact same slot)
            if (events.Count > 0 && finalTick <= events[^1].Tick)
            {
                if (g.PeakAmp > events[^1].PeakAmplitude)
                {
                    events[^1].RootPitch = Math.Min(events[^1].RootPitch, g.RootPitch);
                    events[^1].TopPitch = Math.Max(events[^1].TopPitch, g.TopPitch);
                    events[^1].NoteCount = Math.Min(3, events[^1].NoteCount + 1);
                    events[^1].PeakAmplitude = g.PeakAmp;
                }
                continue;
            }

            // Quantize duration cleanly to 16th-note increments
            int rawDur = Math.Max(0, g.RawEndTick - finalTick);
            int quantDur = ((rawDur + 24) / 48) * 48;

            events.Add(new NoteEvent
            {
                StartSec = g.StartSec,
                EndSec = g.EndSec,
                RootPitch = g.RootPitch,
                TopPitch = g.TopPitch,
                NoteCount = g.NoteCount,
                PeakAmplitude = g.PeakAmp,
                Tick = finalTick,
                DurationTicks = quantDur
            });
        }

        return events;
    }

    private static int WriteEventsSection(
        StringBuilder sb,
        List<NoteEvent> allEvents,
        List<DetectedMusicalSection>? detectedSections,
        Func<double, int> secondsToTick,
        double songDuration,
        int soloStartTick,
        int soloEndTick)
    {
        sb.AppendLine("[Events]");
        sb.AppendLine("{");

        var entries = new SortedDictionary<int, List<string>>();
        void AddEvt(int tick, string line)
        {
            int t = Math.Max(Resolution, tick);
            if (!entries.TryGetValue(t, out var list))
            {
                list = new List<string>();
                entries[t] = list;
            }
            if (!list.Contains(line)) list.Add(line);
        }

        int sectionCount = 0;
        if (detectedSections != null && detectedSections.Count >= 2)
        {
            int lastBarTick = -TicksPerBar * 4;
            foreach (var sec in detectedSections.OrderBy(s => s.TimeSec))
            {
                int rawTick = secondsToTick(sec.TimeSec);
                int barTick = Math.Max(Resolution, ((rawTick + (TicksPerBar / 2)) / TicksPerBar) * TicksPerBar);
                if (barTick - lastBarTick < TicksPerBar * 2) continue;
                AddEvt(barTick, $"E \"section {EscapeChartStr(sec.Name)}\"");
                lastBarTick = barTick;
                sectionCount++;
            }
        }
        else
        {
            int totalTicks = secondsToTick(songDuration);
            int firstTick = allEvents.Count > 0 ? Math.Max(Resolution, (allEvents[0].Tick / TicksPerBar) * TicksPerBar) : TicksPerBar;
            if (firstTick < Resolution) firstTick = Resolution;
            AddEvt(firstTick, "E \"section Intro\"");

            int verse1Tick = Math.Max(firstTick + TicksPerBar, ((int)(totalTicks * 0.15) / TicksPerBar) * TicksPerBar);
            int chorus1Tick = Math.Max(verse1Tick + TicksPerBar, ((int)(totalTicks * 0.35) / TicksPerBar) * TicksPerBar);
            int verse2Tick = Math.Max(chorus1Tick + TicksPerBar, ((int)(totalTicks * 0.54) / TicksPerBar) * TicksPerBar);
            int outroTick = Math.Max(verse2Tick + TicksPerBar, ((int)(totalTicks * 0.85) / TicksPerBar) * TicksPerBar);

            AddEvt(verse1Tick, "E \"section Verse 1\"");
            AddEvt(chorus1Tick, "E \"section Chorus\"");
            AddEvt(verse2Tick, "E \"section Verse 2\"");
            AddEvt(outroTick, "E \"section Outro\"");
            sectionCount = 5;
        }

        if (soloStartTick > 0 && soloEndTick > soloStartTick + TicksPerBar)
        {
            int sStart = Math.Max(Resolution, ((soloStartTick + 96) / Resolution) * Resolution);
            int sEnd = Math.Max(sStart + TicksPerBar, ((soloEndTick + 96) / Resolution) * Resolution);
            AddEvt(sStart, "E \"section Solo\"");
            AddEvt(sStart, "E solo");
            AddEvt(sEnd, "E soloend");
            sectionCount++;
        }

        foreach (var kv in entries)
        {
            foreach (var ev in kv.Value)
                sb.AppendLine($"  {kv.Key} = {ev}");
        }

        sb.AppendLine("}");
        return sectionCount;
    }

    private static void WriteDifficultyTrack(
        StringBuilder sb,
        string trackName,
        List<NoteEvent> allEvents,
        int lowPitch,
        int highPitch,
        List<double>? measureEnergies,
        int soloStartTick,
        int soloEndTick,
        double safeBpm,
        int maxFrets,
        double minSpacingSec,
        bool allowChords,
        bool allowThreeNoteChords,
        bool allowSustains,
        bool allowForcedMarkers,
        float ampFloor,
        bool isBassTrack,
        ChartBuildStats? statsOut)
    {
        sb.AppendLine($"[{trackName}]");
        sb.AppendLine("{");

        // 1. Filter notes for this difficulty level
        var diffEvents = new List<NoteEvent>();
        double lastKeptSec = -10.0;
        for (int idx = 0; idx < allEvents.Count; idx++)
        {
            var ev = allEvents[idx];
            double gap = ev.StartSec - lastKeptSec;
            if (gap < minSpacingSec) continue;
            if (ev.PeakAmplitude < ampFloor && gap < minSpacingSec * 2.5) continue;
            diffEvents.Add(ev);
            lastKeptSec = ev.StartSec;
        }

        if (diffEvents.Count == 0)
        {
            sb.AppendLine("}");
            return;
        }

        // 2. Assign base 5-fret lane for every note using:
        //    - 4-fret Hand-Box Ergonomics (G-R-Y-B vs R-Y-B-O sliding smoothly with local melody register)
        //    - 2-bar Riff & Hook Repetition Memory (consistent fret patterns on repeating riffs!)
        int[] assignedFrets = new int[diffEvents.Count];
        int pitchSpan = Math.Max(8, highPitch - lowPitch);
        int prevPitch = -1;
        int prevFret = 0;

        // First pass: ergonomic pitch-contour + 4-fret hand-box fret assignment
        for (int i = 0; i < diffEvents.Count; i++)
        {
            var ev = diffEvents[i];

            // Compute local phrase median pitch (8-note window) to choose ergonomic hand box
            int wStart = Math.Max(0, i - 4);
            int wEnd = Math.Min(diffEvents.Count - 1, i + 3);
            var winPitches = new List<int>(8);
            for (int w = wStart; w <= wEnd; w++) winPitches.Add(diffEvents[w].RootPitch);
            winPitches.Sort();
            double localMedian = winPitches[winPitches.Count / 2];
            double localNorm = Math.Clamp((localMedian - lowPitch) / (double)pitchSpan, 0.0, 1.0);

            // Sliding 4-fret hand box on Expert & Hard (5-fret): low box [0..3] vs high box [1..4]
            int boxMin = 0;
            int boxMax = maxFrets - 1;
            if (maxFrets == 5)
            {
                bool highRegister = localNorm >= 0.54;
                boxMin = highRegister ? 1 : 0;
                boxMax = highRegister ? 4 : 3;
            }

            double normPitch = Math.Clamp((ev.RootPitch - lowPitch) / (double)pitchSpan, 0.0, 1.0);
            int absoluteFret = (int)Math.Round(normPitch * (maxFrets - 1));

            int fret;
            if (prevPitch < 0)
            {
                fret = Math.Clamp(absoluteFret, boxMin, boxMax);
            }
            else
            {
                int delta = ev.RootPitch - prevPitch;
                if (delta == 0)
                {
                    fret = prevFret;
                }
                else if (delta > 0)
                {
                    int step = delta <= 2 ? 1 : (delta <= 5 ? 2 : 3);
                    int target = Math.Min(maxFrets - 1, prevFret + step);
                    if (delta < 5 && normPitch < 0.80) target = Math.Min(target, boxMax);
                    
                    // Heavy absolute weighting (65% absolute pitch vs 35% relative step) so low stays Green and high reaches Orange!
                    fret = (int)Math.Round(target * 0.35 + absoluteFret * 0.65);
                    if (fret <= prevFret && prevFret < maxFrets - 1) fret = prevFret + 1;
                }
                else
                {
                    int absDelta = -delta;
                    int step = absDelta <= 2 ? 1 : (absDelta <= 5 ? 2 : 3);
                    int target = Math.Max(0, prevFret - step);
                    if (absDelta < 5 && normPitch > 0.20) target = Math.Max(target, boxMin);
                    
                    fret = (int)Math.Round(target * 0.35 + absoluteFret * 0.65);
                    if (fret >= prevFret && prevFret > 0) fret = prevFret - 1;
                }
            }

            // Vanilla GH3 Root & Peak Anchor Rules:
            // Lowest fundamental notes firmly anchor to Green (0), highest screams hit Orange (maxFrets-1)
            if (maxFrets >= 4 && normPitch <= 0.16) fret = 0;
            else if (maxFrets == 5 && normPitch >= 0.84) fret = 4;

            fret = Math.Clamp(fret, 0, maxFrets - 1);
            assignedFrets[i] = fret;
            prevFret = fret;
            prevPitch = ev.RootPitch;
        }

        // Second pass: 2-Bar Riff & Hook Repetition Memory!
        // Group notes into 2-bar phrases (1536 ticks). When a hook repeats with matching rhythm & melodic contour,
        // lock it to the EXACT same fret pattern as its first appearance!
        int matchedRiffs = ApplyRiffRepetitionMemory(diffEvents, assignedFrets, maxFrets);
        if (statsOut != null) statsOut.RepeatedRiffsMatched += matchedRiffs;

        // 3. Emit notes, chords, sustains, authentic GH3 forced HOPO/strum markers (N 5 0), and Smart Star Power
        double lastStarPowerSec = -20.0;
        int forcedCount = 0;
        int threeChordCount = 0;
        int prevEmittedFret = -1;
        bool prevWasChord = false;

        double GetMeasureEnergy(int tick)
        {
            if (measureEnergies == null || measureEnergies.Count == 0) return 0.55;
            int m = Math.Clamp(tick / TicksPerBar, 0, measureEnergies.Count - 1);
            return measureEnergies[m];
        }

        for (int i = 0; i < diffEvents.Count; i++)
        {
            var ev = diffEvents[i];
            int fret = Math.Clamp(assignedFrets[i], 0, maxFrets - 1);
            double energy = GetMeasureEnergy(ev.Tick);
            bool inSolo = soloStartTick > 0 && ev.Tick >= soloStartTick && ev.Tick <= soloEndTick;
            bool onBarDownbeat = (ev.Tick % TicksPerBar) == 0;

            // Intelligent Musical Sustain Calculation:
            int nextNoteTick = (i + 1 < diffEvents.Count) ? diffEvents[i + 1].Tick : (ev.Tick + TicksPerBar * 2);
            int availableGapTicks = nextNoteTick - ev.Tick;
            int maxSustainHeadroom = Math.Max(0, availableGapTicks - 48); // leave 48-tick (16th note) visual/strike safety gap

            int sustain = 0;
            if (allowSustains && maxSustainHeadroom >= 48)
            {
                // Measure the note's acoustic duration in ticks
                double acousticDurationSec = Math.Max(0.0, ev.EndSec - ev.StartSec);
                int acousticTicks = (int)Math.Round(acousticDurationSec * (Resolution * (safeBpm / 60.0)));

                // A note sustains if:
                // a) It has measurable audio ring-out (>= 0.18s or DurationTicks >= 48)
                // b) It is a chord on a bar downbeat (power chords naturally ring into the measure)
                // c) It sits before a musical rest (gap >= 1 beat) or is a singing solo vibrato note
                bool hasAudioSustain = acousticDurationSec >= 0.18 || ev.DurationTicks >= 48;
                bool isCadenceHold = (onBarDownbeat && allowChords) || (availableGapTicks >= Resolution && acousticDurationSec >= 0.14);
                bool isSoloSingingNote = inSolo && availableGapTicks >= Resolution && ev.PeakAmplitude >= 0.48f;

                if (hasAudioSustain || isCadenceHold || isSoloSingingNote)
                {
                    int intendedTicks = Math.Max(ev.DurationTicks, acousticTicks);

                    if (isCadenceHold || isSoloSingingNote)
                    {
                        intendedTicks = Math.Max(intendedTicks, Math.Min(availableGapTicks / 2, Resolution));
                    }

                    int boundedSustain = Math.Min(intendedTicks, maxSustainHeadroom);

                    // Quantize to 16th-note ticks (48 ticks). 48 ticks is the official minimum visible sustain tail in GH3!
                    if (boundedSustain >= 48)
                    {
                        sustain = (boundedSustain / 48) * 48;
                    }
                }
            }

            int intervalSemitones = ev.TopPitch - ev.RootPitch;
            bool isPolyphonic = ev.NoteCount >= 2 && intervalSemitones >= 2;

            // Keep solos & basslines crisp single notes; unleash chords on driving riffs & high-energy choruses!
            bool isChord = allowChords && !isBassTrack && !inSolo && maxFrets >= 3 &&
                (isPolyphonic || (onBarDownbeat && energy >= 0.75 && ev.PeakAmplitude >= 0.50f));

            bool isThreeNote = allowThreeNoteChords && isChord && maxFrets >= 5 && energy >= 0.76 &&
                ((ev.NoteCount >= 3 && intervalSemitones >= 5) || (onBarDownbeat && energy >= 0.85 && ev.PeakAmplitude >= 0.60f));

            if (isThreeNote)
            {
                // Ergonomic GH3 3-note chords: G+R+Y (0,1,2), R+Y+B (1,2,3), Y+B+O (2,3,4), or G+Y+O (0,2,4)
                int f0, f1, f2;
                if (fret <= 0) { f0 = 0; f1 = 2; f2 = 4; }
                else if (fret == 1) { f0 = 0; f1 = 1; f2 = 2; }
                else if (fret == 2) { f0 = 1; f1 = 2; f2 = 3; }
                else { f0 = 2; f1 = 3; f2 = 4; }

                f0 = Math.Clamp(f0, 0, maxFrets - 1);
                f1 = Math.Clamp(f1, 0, maxFrets - 1);
                f2 = Math.Clamp(f2, 0, maxFrets - 1);

                sb.AppendLine($"  {ev.Tick} = N {f0} {sustain}");
                sb.AppendLine($"  {ev.Tick} = N {f1} {sustain}");
                sb.AppendLine($"  {ev.Tick} = N {f2} {sustain}");
                prevEmittedFret = f0;
                prevWasChord = true;
                threeChordCount++;
            }
            else if (isChord)
            {
                int baseFret = Math.Clamp(Math.Min(fret, maxFrets - 2), 0, Math.Max(0, maxFrets - 2));
                int secondFret = (intervalSemitones >= 5 && baseFret + 2 < maxFrets) ? baseFret + 2 : baseFret + 1;
                secondFret = Math.Clamp(secondFret, 0, maxFrets - 1);

                sb.AppendLine($"  {ev.Tick} = N {baseFret} {sustain}");
                sb.AppendLine($"  {ev.Tick} = N {secondFret} {sustain}");
                prevEmittedFret = baseFret;
                prevWasChord = true;
            }
            else
            {
                sb.AppendLine($"  {ev.Tick} = N {fret} {sustain}");

                // Authentic GH3 N 5 0 (Forced HOPO / Strum Toggle) logic!
                if (allowForcedMarkers && i > 0 && !prevWasChord)
                {
                    int gapFromPrev = ev.Tick - diffEvents[i - 1].Tick;
                    int pitchDelta = Math.Abs(ev.RootPitch - diffEvents[i - 1].RootPitch);
                    bool wouldAutoHopo = gapFromPrev <= Gh3AutoHopoThreshold && fret != prevEmittedFret;

                    // Case A: Force HOPO on smooth legato melodic runs (8th-note spacing = 96 ticks) inside solos or scalar runs!
                    bool nextAlsoFlowing = i + 1 < diffEvents.Count && (diffEvents[i + 1].Tick - ev.Tick) <= 96 && assignedFrets[i + 1] != fret;
                    if (!wouldAutoHopo && gapFromPrev <= 96 && fret != prevEmittedFret && pitchDelta >= 1 && pitchDelta <= 5 && (inSolo || nextAlsoFlowing))
                    {
                        sb.AppendLine($"  {ev.Tick} = N 5 0");
                        forcedCount++;
                    }
                    // Case B: Force Strum on punchy accented bar downbeats that sit <= 65 ticks after a pickup note
                    else if (wouldAutoHopo && onBarDownbeat && !inSolo && ev.PeakAmplitude >= 0.65f)
                    {
                        sb.AppendLine($"  {ev.Tick} = N 5 0");
                        forcedCount++;
                    }
                }

                prevEmittedFret = fret;
                prevWasChord = false;
            }

            // Smart Star Power placement: favor bar boundaries leading into high-energy sections & solos
            double spCooldown = energy >= 0.68 ? 17.0 : 23.0;
            bool goodSpSpot = onBarDownbeat || energy >= 0.68 || inSolo;
            if (goodSpSpot && ev.StartSec - lastStarPowerSec >= spCooldown && i + 6 < diffEvents.Count)
            {
                int spEndIdx = Math.Min(i + 8, diffEvents.Count - 1);
                int spEndTick = diffEvents[spEndIdx].Tick;
                int spLen = Math.Clamp(spEndTick - ev.Tick, 384, 1536);
                sb.AppendLine($"  {ev.Tick} = S 2 {spLen}");
                lastStarPowerSec = ev.StartSec;
            }
        }

        if (statsOut != null)
        {
            statsOut.ExpertNoteCount = diffEvents.Count;
            statsOut.ForcedMarkersApplied += forcedCount;
            statsOut.ThreeNoteChordsCount += threeChordCount;
        }

        sb.AppendLine("}");
    }

    private static int ApplyRiffRepetitionMemory(List<NoteEvent> events, int[] frets, int maxFrets)
    {
        if (events.Count < 16) return 0;

        // Group event indices by 2-bar phrase index (1536 ticks)
        var byPhrase = new SortedDictionary<int, List<int>>();
        for (int i = 0; i < events.Count; i++)
        {
            int pIdx = events[i].Tick / TicksPer2Bars;
            if (!byPhrase.TryGetValue(pIdx, out var list))
            {
                list = new List<int>();
                byPhrase[pIdx] = list;
            }
            list.Add(i);
        }

        var library = new List<PhrasePattern>();
        int matchedCount = 0;

        foreach (var (pIdx, indices) in byPhrase)
        {
            if (indices.Count < 4 || indices.Count > 36) continue;

            int baseTick = pIdx * TicksPer2Bars;
            var slots = new int[indices.Count];
            var intervals = new int[indices.Count];
            for (int k = 0; k < indices.Count; k++)
            {
                int idx = indices[k];
                slots[k] = (events[idx].Tick - baseTick) / 48; // 16th-note slot (0..31)
                if (k == 0)
                {
                    intervals[k] = 0;
                }
                else
                {
                    int dp = events[idx].RootPitch - events[indices[k - 1]].RootPitch;
                    intervals[k] = dp == 0 ? 0 : (dp > 0 ? (dp <= 4 ? 1 : 2) : (dp >= -4 ? -1 : -2));
                }
            }

            // Compare against previously recorded 2-bar riff patterns
            PhrasePattern? bestMatch = null;
            double bestSim = 0.0;

            foreach (var prev in library)
            {
                if (prev.RelativeSlots.Length != slots.Length) continue;

                int slotMatches = 0;
                int contourMatches = 0;
                for (int k = 0; k < slots.Length; k++)
                {
                    if (Math.Abs(prev.RelativeSlots[k] - slots[k]) <= 1) slotMatches++;
                    if (prev.IntervalSteps[k] == intervals[k] ||
                        (Math.Sign(prev.IntervalSteps[k]) == Math.Sign(intervals[k]) && prev.IntervalSteps[k] != 0))
                    {
                        contourMatches++;
                    }
                }

                double rhythmSim = slotMatches / (double)slots.Length;
                double contourSim = contourMatches / (double)slots.Length;
                double combined = rhythmSim * 0.55 + contourSim * 0.45;

                if (rhythmSim >= 0.75 && contourSim >= 0.72 && combined > bestSim)
                {
                    bestSim = combined;
                    bestMatch = prev;
                }
            }

            if (bestMatch != null)
            {
                for (int k = 0; k < indices.Count; k++)
                {
                    frets[indices[k]] = Math.Clamp(bestMatch.AssignedFrets[k], 0, maxFrets - 1);
                }
                matchedCount++;
            }
            else
            {
                var snapFrets = new int[indices.Count];
                for (int k = 0; k < indices.Count; k++) snapFrets[k] = Math.Clamp(frets[indices[k]], 0, maxFrets - 1);
                library.Add(new PhrasePattern(pIdx, slots, intervals, snapFrets));
            }
        }

        return matchedCount;
    }

    private static (int StartTick, int EndTick) DetectSoloSection(List<NoteEvent> events, Func<double, int> secondsToTick, double durationSec)
    {
        if (events.Count < 40 || durationSec < 45) return (0, 0);

        const double windowSec = 12.0;
        double bestScore = 0;
        double bestStart = 0;

        for (double t = durationSec * 0.38; t <= durationSec * 0.82; t += 2.0)
        {
            var inWindow = events.Where(e => e.StartSec >= t && e.StartSec < t + windowSec).ToList();
            if (inWindow.Count < 22) continue;
            double avgPitch = inWindow.Average(e => e.RootPitch);
            double score = inWindow.Count * (1.0 + Math.Max(0, avgPitch - 55) * 0.045);
            if (score > bestScore)
            {
                bestScore = score;
                bestStart = t;
            }
        }

        if (bestScore <= 0) return (0, 0);
        return (secondsToTick(bestStart), secondsToTick(bestStart + windowSec));
    }

    private static string EscapeChartStr(string s) => (s ?? "").Replace("\"", "'");

    public class MusicalScale
    {
        public int RootClass { get; }
        public string Name { get; }
        public HashSet<int> PitchClasses { get; }

        public MusicalScale(int root, string name, int[] intervals)
        {
            RootClass = root;
            Name = name;
            PitchClasses = new HashSet<int>(intervals.Select(i => ((root + i) % 12 + 12) % 12));
        }

        public bool Contains(int midiPitch) => PitchClasses.Contains(((midiPitch % 12) + 12) % 12);

        public int SnapMidiPitch(int midiPitch)
        {
            int pc = ((midiPitch % 12) + 12) % 12;
            if (PitchClasses.Contains(pc)) return midiPitch;

            // Check neighbor semitones (+1 / -1)
            int upPc = (pc + 1) % 12;
            int downPc = (pc + 11) % 12;

            if (PitchClasses.Contains(upPc)) return midiPitch + 1;
            if (PitchClasses.Contains(downPc)) return midiPitch - 1;

            return midiPitch;
        }
    }

    private static MusicalScale DetectMusicalScale(List<NoteEvent> events)
    {
        string[] noteNames = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        var scaleTemplates = new (string TypeName, int[] Intervals)[]
        {
            ("Minor Pentatonic", new[] { 0, 3, 5, 7, 10 }),
            ("Natural Minor",     new[] { 0, 2, 3, 5, 7, 8, 10 }),
            ("Major Pentatonic", new[] { 0, 2, 4, 7, 9 }),
            ("Major",            new[] { 0, 2, 4, 5, 7, 9, 11 }),
            ("Dorian",           new[] { 0, 2, 3, 5, 7, 9, 10 }),
            ("Blues",            new[] { 0, 3, 5, 6, 7, 10 }),
            ("Mixolydian",       new[] { 0, 2, 4, 5, 7, 9, 10 })
        };

        if (events == null || events.Count < 6)
        {
            return new MusicalScale(4, "E Natural Minor", new[] { 0, 2, 3, 5, 7, 8, 10 });
        }

        double[] weights = new double[12];
        foreach (var ev in events)
        {
            int pc = ((ev.RootPitch % 12) + 12) % 12;
            weights[pc] += Math.Max(0.1, ev.PeakAmplitude);
        }

        MusicalScale bestScale = new MusicalScale(4, "E Natural Minor", new[] { 0, 2, 3, 5, 7, 8, 10 });
        double bestScore = -1.0;

        for (int root = 0; root < 12; root++)
        {
            foreach (var (typeName, intervals) in scaleTemplates)
            {
                var candidate = new MusicalScale(root, $"{noteNames[root]} {typeName}", intervals);
                double inScaleWeight = 0;
                double outScaleWeight = 0;

                for (int pc = 0; pc < 12; pc++)
                {
                    if (candidate.PitchClasses.Contains(pc))
                        inScaleWeight += weights[pc];
                    else
                        outScaleWeight += weights[pc];
                }

                double score = inScaleWeight - outScaleWeight * 0.75;
                if (score > bestScore)
                {
                    bestScore = score;
                    bestScale = candidate;
                }
            }
        }

        return bestScale;
    }
}
