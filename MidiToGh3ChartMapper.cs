using System;
using System.Collections.Generic;
using System.Globalization;
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

/// <summary>
/// Musical, ergonomic 5-fret Guitar Hero III chart generator.
/// Translates real pitch-transcribed guitar notes (from Spotify Basic Pitch AI
/// or FFT/YIN pitch analysis) into authentic .chart files where:
/// - Repeated notes stay on the SAME fret (no ping-pong loops!)
/// - Rising/falling melodies follow real pitch contours across Green..Orange
/// - Polyphonic strums become playable 2-note power chords
/// - Real note lengths produce accurate sustains
/// - Exact millisecond-to-tick alignment prevents tempo drift even on live recordings
/// </summary>
public static class MidiToGh3ChartMapper
{
    private const int Resolution = 192;

    public static string BuildChartFromNotes(
        SongCreationRequest meta,
        List<TranscribedNote> rawNotes,
        double exactBpm,
        double durationSeconds,
        string sourceLabel = "ONOGAMI AI Pitch Engine")
    {
        double safeBpm = Math.Clamp(exactBpm > 20 ? exactBpm : (meta.Bpm > 20 ? meta.Bpm : 120.0), 60.0, 240.0);
        int bpmMicro = (int)Math.Round(safeBpm * 1000.0);
        double ticksPerSec = Resolution * (safeBpm / 60.0);

        // 1. Clean & group raw transcribed notes into simultaneous strum events
        var allEvents = GroupIntoNoteEvents(rawNotes, ticksPerSec);

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

        // Detect highest-density guitar solo window
        (int soloStartTick, int soloEndTick) = DetectSoloSection(allEvents, ticksPerSec, songDuration);

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

        sb.AppendLine("[SyncTrack]");
        sb.AppendLine("{");
        sb.AppendLine("  0 = TS 4");
        sb.AppendLine($"  0 = B {bpmMicro}");
        sb.AppendLine("}");

        // Dynamic section markers scaled to actual song length
        sb.AppendLine("[Events]");
        sb.AppendLine("{");
        int totalTicks = (int)Math.Round(songDuration * ticksPerSec);
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

        // Build all 4 authentic Guitar Hero III difficulties
        WriteDifficultyTrack(sb, "ExpertSingle", allEvents, lowPitch, highPitch,
            maxFrets: 5, minSpacingSec: 0.085, allowChords: true, allowSustains: true, ampFloor: 0.12f, ticksPerSec);

        WriteDifficultyTrack(sb, "HardSingle", allEvents, lowPitch, highPitch,
            maxFrets: 4, minSpacingSec: 0.155, allowChords: true, allowSustains: true, ampFloor: 0.18f, ticksPerSec);

        WriteDifficultyTrack(sb, "MediumSingle", allEvents, lowPitch, highPitch,
            maxFrets: 4, minSpacingSec: 0.260, allowChords: false, allowSustains: true, ampFloor: 0.25f, ticksPerSec);

        WriteDifficultyTrack(sb, "EasySingle", allEvents, lowPitch, highPitch,
            maxFrets: 3, minSpacingSec: 0.440, allowChords: false, allowSustains: true, ampFloor: 0.32f, ticksPerSec);

        return sb.ToString();
    }

    private static List<NoteEvent> GroupIntoNoteEvents(List<TranscribedNote> rawNotes, double ticksPerSec)
    {
        var events = new List<NoteEvent>();
        if (rawNotes == null || rawNotes.Count == 0) return events;

        // Filter out micro-glitches and out-of-guitar-range noise
        var filtered = rawNotes
            .Where(n => n.StartSec >= 0.25 && n.EndSec > n.StartSec + 0.03 && n.MidiPitch >= 28 && n.MidiPitch <= 96)
            .OrderBy(n => n.StartSec)
            .ThenByDescending(n => n.Amplitude)
            .ToList();

        if (filtered.Count == 0) return events;

        const double chordWindowSec = 0.042; // Notes within 42ms are treated as a single strum/chord
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

            // Pick unique pitches sorted low-to-high, favoring strongest amplitudes
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

            // Convert exact millisecond timestamp to tick (with gentle 1/32nd snap if very close)
            int rawTick = (int)Math.Round(startSec * ticksPerSec);
            int snapped32 = ((rawTick + 12) / 24) * 24;
            int finalTick = Math.Abs(rawTick - snapped32) <= 7 ? snapped32 : rawTick;
            if (finalTick < 192) finalTick = 192;

            int durTicks = (int)Math.Round((endSec - startSec) * ticksPerSec);

            // Avoid colliding ticks
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
        float ampFloor,
        double ticksPerSec)
    {
        sb.AppendLine($"[{trackName}]");
        sb.AppendLine("{");

        // Thin events cleanly according to difficulty spacing & salience
        var diffEvents = new List<NoteEvent>();
        double lastKeptSec = -10.0;

        for (int idx = 0; idx < allEvents.Count; idx++)
        {
            var ev = allEvents[idx];
            double gap = ev.StartSec - lastKeptSec;
            if (gap < minSpacingSec)
            {
                // If this event is significantly stronger than the one we just kept and very close, skip;
                // otherwise enforce clean playable density
                continue;
            }

            // Keep strong notes always; on easier modes filter weak background ghost notes
            if (ev.PeakAmplitude < ampFloor && gap < minSpacingSec * 2.5)
            {
                continue;
            }

            diffEvents.Add(ev);
            lastKeptSec = ev.StartSec;
        }

        // Ergonomic pitch-contour fret assignment
        int prevPitch = -1;
        int prevFret = 0;
        double lastStarPowerSec = -20.0;
        int pitchSpan = Math.Max(8, highPitch - lowPitch);

        for (int i = 0; i < diffEvents.Count; i++)
        {
            var ev = diffEvents[i];

            // Compute absolute target fret from guitar register (0 .. maxFrets-1)
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
                    // EXACT SAME PITCH -> STAY ON EXACT SAME FRET! (Authentic riff chugging!)
                    fret = prevFret;
                }
                else if (delta > 0)
                {
                    // Pitch rose: move up proportionally, anchored toward absolute register
                    int step = delta <= 3 ? 1 : (delta <= 7 ? 2 : 3);
                    int relativeTarget = Math.Min(maxFrets - 1, prevFret + step);
                    fret = (int)Math.Round((relativeTarget * 0.65) + (absoluteFret * 0.35));
                    if (fret <= prevFret && prevFret < maxFrets - 1) fret = prevFret + 1;
                }
                else
                {
                    // Pitch dropped: move down proportionally
                    int absDelta = -delta;
                    int step = absDelta <= 3 ? 1 : (absDelta <= 7 ? 2 : 3);
                    int relativeTarget = Math.Max(0, prevFret - step);
                    fret = (int)Math.Round((relativeTarget * 0.65) + (absoluteFret * 0.35));
                    if (fret >= prevFret && prevFret > 0) fret = prevFret - 1;
                }
            }

            fret = Math.Clamp(fret, 0, maxFrets - 1);

            // Compute true sustain length (clamped safely before next note)
            int sustain = 0;
            if (allowSustains && (ev.EndSec - ev.StartSec) >= 0.32 && ev.DurationTicks >= 96)
            {
                int maxAllowed = ev.DurationTicks;
                if (i + 1 < diffEvents.Count)
                {
                    int gapTicks = diffEvents[i + 1].Tick - ev.Tick;
                    maxAllowed = Math.Min(maxAllowed, gapTicks - 48);
                }
                if (maxAllowed >= 96)
                {
                    sustain = maxAllowed;
                }
            }

            // Polyphonic 2-note chord detection (authentic power chords!)
            int intervalSemitones = ev.TopPitch - ev.RootPitch;
            bool isChord = allowChords && ev.NoteCount >= 2 && intervalSemitones >= 2 && maxFrets >= 3;

            if (isChord)
            {
                int baseFret = Math.Min(fret, maxFrets - 2);
                int secondFret = (intervalSemitones >= 6 && baseFret + 2 < maxFrets)
                    ? baseFret + 2
                    : baseFret + 1;

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

            // Place Star Power phrases on energetic phrases spaced ~20+ seconds apart
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

    private static (int StartTick, int EndTick) DetectSoloSection(List<NoteEvent> events, double ticksPerSec, double durationSec)
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
        return ((int)Math.Round(bestStart * ticksPerSec), (int)Math.Round((bestStart + windowSec) * ticksPerSec));
    }

    private static string EscapeChartStr(string s) => (s ?? "").Replace("\"", "'");
}
