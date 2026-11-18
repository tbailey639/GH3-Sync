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

/// <summary>
/// Musical, ergonomic 5-fret Guitar Hero III chart generator.
/// Supports dynamic variable [SyncTrack] tempo maps (for live recordings & tempo shifts),
/// real pitch-contour fret assignment (same pitch = same fret), power chords, sustains,
/// Chart Feel tuning (Casual / Standard / Shredder), and automatic Star Power & Solos.
/// </summary>
public static class MidiToGh3ChartMapper
{
    private const int Resolution = 192;

    public static string BuildChartFromNotes(
        SongCreationRequest meta,
        List<TranscribedNote> rawNotes,
        double exactBpm,
        double durationSeconds,
        string sourceLabel = "Mad Scientist",
        List<double>? beatTimestamps = null)
    {
        double safeBpm = Math.Clamp(exactBpm > 20 ? exactBpm : (meta.Bpm > 20 ? meta.Bpm : 120.0), 60.0, 240.0);

        // 1. Build dynamic variable [SyncTrack] tempo anchors if beat timestamps are provided
        var tempoAnchors = BuildTempoAnchors(safeBpm, meta.UseVariableTempoMap ? beatTimestamps : null);

        Func<double, int> secondsToTick = sec => ConvertSecondsToTick(sec, tempoAnchors);

        // 2. Clean & group raw transcribed notes into simultaneous strum events
        var allEvents = GroupIntoNoteEvents(rawNotes, secondsToTick);

        double songDuration = durationSeconds > 5
            ? durationSeconds
            : (allEvents.Count > 0 ? allEvents[^1].EndSec + 2.0 : 180.0);

        // Compute robust song guitar pitch range (10th to 90th percentile)
        int lowPitch = 45;
        int highPitch = 72;
        if (allEvents.Count >= 10)
        {
            var sortedPitches = allEvents.Select(e => e.RootPitch).OrderBy(p => p).ToList();
            lowPitch = sortedPitches[(int)(sortedPitches.Count * 0.10)];
            highPitch = sortedPitches[(int)(sortedPitches.Count * 0.90)];
            if (highPitch - lowPitch < 8)
            {
                lowPitch -= 4;
                highPitch += 4;
            }
        }

        // Apply Chart Feel spacing multiplier
        double feelScale = (meta.ChartFeel ?? "Standard").ToLowerInvariant() switch
        {
            "casual" => 1.45,
            "shredder" => 0.72,
            _ => 1.0
        };

        (int soloStartTick, int soloEndTick) = DetectSoloSection(allEvents, secondsToTick, songDuration);

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
        sb.AppendLine("  Player2 = rhythm");
        sb.AppendLine($"  Difficulty = {meta.DiffGuitar}");
        sb.AppendLine($"  PreviewStart = {meta.PreviewStartMs}");
        sb.AppendLine($"  PreviewEnd = {meta.PreviewStartMs + 30000}");
        sb.AppendLine($"  Genre = \"{EscapeChartStr(meta.Genre.ToLowerInvariant())}\"");
        sb.AppendLine("  MediaType = \"cd\"");
        sb.AppendLine("  MusicStream = \"song.mp3\"");
        sb.AppendLine("}");

        // Write SyncTrack (supports both constant BPM and dynamic measure-by-measure tempo map!)
        sb.AppendLine("[SyncTrack]");
        sb.AppendLine("{");
        sb.AppendLine("  0 = TS 4");
        double lastEmittedBpm = -1;
        int lastEmittedTick = -1;
        foreach (var anchor in tempoAnchors)
        {
            if (anchor.Tick == lastEmittedTick) continue;
            if (anchor.Tick == 0 || Math.Abs(anchor.Bpm - lastEmittedBpm) >= 0.4)
            {
                int bpmMicro = (int)Math.Round(anchor.Bpm * 1000.0);
                sb.AppendLine($"  {anchor.Tick} = B {bpmMicro}");
                lastEmittedBpm = anchor.Bpm;
                lastEmittedTick = anchor.Tick;
            }
        }
        sb.AppendLine("}");

        // Dynamic section markers scaled to actual song length
        sb.AppendLine("[Events]");
        sb.AppendLine("{");
        int totalTicks = secondsToTick(songDuration);
        int firstTick = allEvents.Count > 0 ? Math.Max(192, allEvents[0].Tick) : 768;
        sb.AppendLine($"  {firstTick} = E \"section Intro\"");

        int verse1Tick = Math.Max(firstTick + 768, (int)(totalTicks * 0.15));
        int chorus1Tick = Math.Max(verse1Tick + 768, (int)(totalTicks * 0.35));
        int verse2Tick = Math.Max(chorus1Tick + 768, (int)(totalTicks * 0.52));
        int outroTick = Math.Max(verse2Tick + 768, (int)(totalTicks * 0.85));

        sb.AppendLine($"  {verse1Tick} = E \"section Verse 1\"");
        sb.AppendLine($"  {chorus1Tick} = E \"section Chorus\"");
        sb.AppendLine($"  {verse2Tick} = E \"section Verse 2\"");

        if (soloStartTick > 0 && soloEndTick > soloStartTick + 768)
        {
            sb.AppendLine($"  {soloStartTick} = E \"section Guitar Solo\"");
            sb.AppendLine($"  {soloStartTick} = E solo");
            sb.AppendLine($"  {soloEndTick} = E soloend");
        }

        sb.AppendLine($"  {outroTick} = E \"section Outro\"");
        sb.AppendLine("}");

        // Build all 4 Guitar Hero III difficulties
        WriteDifficultyTrack(sb, "ExpertSingle", allEvents, lowPitch, highPitch,
            maxFrets: 5, minSpacingSec: 0.085 * feelScale, allowChords: true, allowSustains: true, ampFloor: 0.12f);

        WriteDifficultyTrack(sb, "HardSingle", allEvents, lowPitch, highPitch,
            maxFrets: 4, minSpacingSec: 0.155 * feelScale, allowChords: true, allowSustains: true, ampFloor: 0.18f);

        WriteDifficultyTrack(sb, "MediumSingle", allEvents, lowPitch, highPitch,
            maxFrets: 4, minSpacingSec: 0.260 * feelScale, allowChords: false, allowSustains: true, ampFloor: 0.25f);

        WriteDifficultyTrack(sb, "EasySingle", allEvents, lowPitch, highPitch,
            maxFrets: 3, minSpacingSec: 0.440 * feelScale, allowChords: false, allowSustains: true, ampFloor: 0.32f);

        return sb.ToString();
    }

    private static List<TempoAnchor> BuildTempoAnchors(double baseBpm, List<double>? beats)
    {
        var anchors = new List<TempoAnchor>();
        if (beats == null || beats.Count < 8)
        {
            anchors.Add(new TempoAnchor(0, 0.0, baseBpm));
            return anchors;
        }

        // First anchor at tick 0 -> time 0 using first beat lead-in
        double firstBeat = beats[0];
        double initialBpm = baseBpm;
        if (firstBeat > 0.15 && firstBeat < 3.0)
        {
            // Align tick 192 (beat 1) to firstBeat if clean, else use baseBpm from 0
            initialBpm = Math.Clamp(60.0 / firstBeat, baseBpm * 0.72, baseBpm * 1.32);
        }
        anchors.Add(new TempoAnchor(0, 0.0, initialBpm));

        int curTick = firstBeat > 0.15 && firstBeat < 3.0 ? Resolution : 0;
        double curTime = firstBeat > 0.15 && firstBeat < 3.0 ? firstBeat : 0.0;

        // Measure-by-measure tempo tracking (every 4 beats = 768 ticks)
        int stepBeats = 4;
        for (int i = 0; i + stepBeats < beats.Count; i += stepBeats)
        {
            double dt = beats[i + stepBeats] - beats[i];
            if (dt <= 0.2) continue;

            double measureBpm = Math.Clamp((stepBeats * 60.0) / dt, baseBpm * 0.78, baseBpm * 1.28);
            anchors.Add(new TempoAnchor(curTick, curTime, measureBpm));

            curTick += stepBeats * Resolution;
            curTime += dt;
        }

        return anchors;
    }

    private static int ConvertSecondsToTick(double sec, List<TempoAnchor> anchors)
    {
        if (sec <= 0 || anchors.Count == 0) return 0;

        // Find last anchor with TimeSec <= sec
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

    private static List<NoteEvent> GroupIntoNoteEvents(List<TranscribedNote> rawNotes, Func<double, int> secondsToTick)
    {
        var events = new List<NoteEvent>();
        if (rawNotes == null || rawNotes.Count == 0) return events;

        var filtered = rawNotes
            .Where(n => n.StartSec >= 0.25 && n.EndSec > n.StartSec + 0.03 && n.MidiPitch >= 28 && n.MidiPitch <= 96)
            .OrderBy(n => n.StartSec)
            .ThenByDescending(n => n.Amplitude)
            .ToList();

        if (filtered.Count == 0) return events;

        const double chordWindowSec = 0.042;
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

            int rawTick = secondsToTick(startSec);
            int snapped32 = ((rawTick + 12) / 24) * 24;
            int finalTick = Math.Abs(rawTick - snapped32) <= 7 ? snapped32 : rawTick;
            if (finalTick < 192) finalTick = 192;

            int endTick = secondsToTick(endSec);
            int durTicks = Math.Max(0, endTick - finalTick);

            if (events.Count > 0 && finalTick <= events[^1].Tick + 12)
            {
                i = j;
                continue;
            }

            events.Add(new NoteEvent
            {
                StartSec = startSec,
                EndSec = endSec,
                RootPitch = distinctPitches[0].MidiPitch,
                TopPitch = distinctPitches[^1].MidiPitch,
                NoteCount = distinctPitches.Count,
                PeakAmplitude = peakAmp,
                Tick = finalTick,
                DurationTicks = durTicks
            });

            i = j;
        }

        return events;
    }

    private static void WriteDifficultyTrack(
        StringBuilder sb,
        string trackName,
        List<NoteEvent> allEvents,
        int lowPitch,
        int highPitch,
        int maxFrets,
        double minSpacingSec,
        bool allowChords,
        bool allowSustains,
        float ampFloor)
    {
        sb.AppendLine($"[{trackName}]");
        sb.AppendLine("{");

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

        int prevPitch = -1;
        int prevFret = 0;
        double lastStarPowerSec = -20.0;
        int pitchSpan = Math.Max(8, highPitch - lowPitch);

        for (int i = 0; i < diffEvents.Count; i++)
        {
            var ev = diffEvents[i];
            double normPitch = Math.Clamp((ev.RootPitch - lowPitch) / (double)pitchSpan, 0.0, 1.0);
            int absoluteFret = (int)Math.Round(normPitch * (maxFrets - 1));

            int fret;
            if (prevPitch < 0)
            {
                fret = absoluteFret;
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
                    int step = delta <= 3 ? 1 : (delta <= 7 ? 2 : 3);
                    int relativeTarget = Math.Min(maxFrets - 1, prevFret + step);
                    fret = (int)Math.Round((relativeTarget * 0.65) + (absoluteFret * 0.35));
                    if (fret <= prevFret && prevFret < maxFrets - 1) fret = prevFret + 1;
                }
                else
                {
                    int absDelta = -delta;
                    int step = absDelta <= 3 ? 1 : (absDelta <= 7 ? 2 : 3);
                    int relativeTarget = Math.Max(0, prevFret - step);
                    fret = (int)Math.Round((relativeTarget * 0.65) + (absoluteFret * 0.35));
                    if (fret >= prevFret && prevFret > 0) fret = prevFret - 1;
                }
            }

            fret = Math.Clamp(fret, 0, maxFrets - 1);

            int sustain = 0;
            if (allowSustains && (ev.EndSec - ev.StartSec) >= 0.32 && ev.DurationTicks >= 96)
            {
                int maxAllowed = ev.DurationTicks;
                if (i + 1 < diffEvents.Count)
                {
                    int gapTicks = diffEvents[i + 1].Tick - ev.Tick;
                    maxAllowed = Math.Min(maxAllowed, gapTicks - 48);
                }
                if (maxAllowed >= 96) sustain = maxAllowed;
            }

            int intervalSemitones = ev.TopPitch - ev.RootPitch;
            bool isChord = allowChords && ev.NoteCount >= 2 && intervalSemitones >= 2 && maxFrets >= 3;

            if (isChord)
            {
                int baseFret = Math.Min(fret, maxFrets - 2);
                int secondFret = (intervalSemitones >= 6 && baseFret + 2 < maxFrets) ? baseFret + 2 : baseFret + 1;

                sb.AppendLine($"  {ev.Tick} = N {baseFret} {sustain}");
                sb.AppendLine($"  {ev.Tick} = N {secondFret} {sustain}");
                prevFret = baseFret;
            }
            else
            {
                sb.AppendLine($"  {ev.Tick} = N {fret} {sustain}");
                prevFret = fret;
            }

            prevPitch = ev.RootPitch;

            if (ev.StartSec - lastStarPowerSec >= 20.0 && i + 6 < diffEvents.Count)
            {
                int spEndTick = diffEvents[Math.Min(i + 8, diffEvents.Count - 1)].Tick;
                int spLen = Math.Clamp(spEndTick - ev.Tick, 384, 1536);
                sb.AppendLine($"  {ev.Tick} = S 2 {spLen}");
                lastStarPowerSec = ev.StartSec;
            }
        }

        sb.AppendLine("}");
    }

    private static (int StartTick, int EndTick) DetectSoloSection(List<NoteEvent> events, Func<double, int> secondsToTick, double durationSec)
    {
        if (events.Count < 40 || durationSec < 45) return (0, 0);

        const double windowSec = 12.0;
        double bestScore = 0;
        double bestStart = 0;

        for (double t = durationSec * 0.40; t <= durationSec * 0.80; t += 2.0)
        {
            var inWindow = events.Where(e => e.StartSec >= t && e.StartSec < t + windowSec).ToList();
            if (inWindow.Count < 20) continue;
            double avgPitch = inWindow.Average(e => e.RootPitch);
            double score = inWindow.Count * (1.0 + Math.Max(0, avgPitch - 55) * 0.04);
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
}
