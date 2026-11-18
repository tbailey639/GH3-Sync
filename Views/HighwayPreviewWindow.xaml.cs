using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfMessageBox = System.Windows.MessageBox;

namespace GH3_Sync.Views;

public partial class HighwayPreviewWindow : Window
{
    private readonly ParsedChartData _chart;
    private readonly string? _projectDir;
    private readonly ChartDoctorService? _doctor;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _fallbackClock = new();

    private List<ChartPreviewNote> _activeNotes = new();
    private bool _hasAudio;
    private bool _isPlaying;
    private bool _isDraggingSlider;
    private double _currentTimeSec;
    private double _speedMultiplier = 1.5;
    private int _offsetShiftMs = 0;

    private static readonly WpfColor[] FretColors =
    {
        WpfColor.FromRgb(34, 197, 94),   // 0 Green
        WpfColor.FromRgb(239, 68, 68),   // 1 Red
        WpfColor.FromRgb(234, 179, 8),   // 2 Yellow
        WpfColor.FromRgb(59, 130, 246),  // 3 Blue
        WpfColor.FromRgb(249, 115, 22),  // 4 Orange
    };

    public event Action? SongRecompiled;

    public HighwayPreviewWindow(
        string title,
        string artist,
        string chartPath,
        string? audioPath,
        string? projectDir = null,
        ChartDoctorService? doctor = null)
    {
        InitializeComponent();

        _projectDir = projectDir;
        _doctor = doctor;
        _chart = ChartDoctorService.ParseChartFile(chartPath);

        SongHeaderTitle.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title}  —  {artist}";
        SaveOffsetBtn.IsEnabled = !string.IsNullOrWhiteSpace(_projectDir) && _doctor != null;

        // Populate available difficulties (prefer Expert first)
        string[] order = { "Expert", "Hard", "Medium", "Easy" };
        foreach (var d in order)
        {
            if (_chart.Difficulties.TryGetValue(d, out var notes) && notes.Count > 0)
                DifficultyCombo.Items.Add(d);
        }
        if (DifficultyCombo.Items.Count == 0)
            DifficultyCombo.Items.Add("Expert");
        DifficultyCombo.SelectedIndex = 0;

        // Load audio if present (auto-convert OGG to quick temp WAV via FFmpeg if needed)
        if (!string.IsNullOrWhiteSpace(audioPath) && File.Exists(audioPath))
        {
            try
            {
                string playable = audioPath;
                if (audioPath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || audioPath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
                {
                    var ff = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
                    if (!string.IsNullOrEmpty(ff) && File.Exists(ff))
                    {
                        var tmpWav = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"gh3_hw_{Guid.NewGuid():N}.wav");
                        var psi = new ProcessStartInfo
                        {
                            FileName = ff,
                            Arguments = $"-y -v error -i \"{audioPath}\" -ar 44100 -ac 2 \"{tmpWav}\"",
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        using var p = Process.Start(psi);
                        p?.WaitForExit(10000);
                        if (File.Exists(tmpWav)) playable = tmpWav;
                    }
                }
                _player.Open(new Uri(playable));
                _player.MediaEnded += (_, _) => StopPlayback();
                _hasAudio = true;
            }
            catch { _hasAudio = false; }
        }

        SeekSlider.Maximum = Math.Max(10, _chart.TotalDurationSeconds);
        UpdateHeaderMeta();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) }; // ~60 FPS
        _renderTimer.Tick += RenderTimer_Tick;
        Loaded += (_, _) => { RenderHighwayFrame(); _renderTimer.Start(); };
        Closing += (_, _) =>
        {
            _renderTimer.Stop();
            try { _player.Stop(); _player.Close(); } catch { }
        };
    }

    private void UpdateHeaderMeta()
    {
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        double bpm = _chart.Tempos.FirstOrDefault()?.Bpm ?? 120.0;
        SongHeaderMeta.Text = $"{diff} ({_activeNotes.Count:N0} notes)  •  Base BPM: {bpm:F1}  •  {_chart.StarPower.Count} Star Power phrases  •  {_chart.Sections.Count} sections" +
                              (_hasAudio ? "" : "  •  [Visual Chart Mode — No Audio]");
    }

    private void DifficultyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_chart == null) return;
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        _activeNotes = _chart.Difficulties.TryGetValue(diff, out var list) ? list : new List<ChartPreviewNote>();
        UpdateHeaderMeta();
        RenderHighwayFrame();
    }

    private void SpeedCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || SpeedCombo == null) return;
        _speedMultiplier = SpeedCombo.SelectedIndex switch
        {
            0 => 1.0,
            1 => 1.25,
            3 => 2.0,
            4 => 2.5,
            _ => 1.5
        };
        RenderHighwayFrame();
    }

    private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            _isPlaying = false;
            if (_hasAudio) _player.Pause();
            _fallbackClock.Stop();
            PlayPauseBtn.Content = "▶ Play";
        }
        else
        {
            _isPlaying = true;
            if (_hasAudio)
            {
                _player.Position = TimeSpan.FromSeconds(_currentTimeSec);
                _player.Play();
            }
            _fallbackClock.Restart();
            PlayPauseBtn.Content = "⏸ Pause";
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        _isPlaying = false;
        _currentTimeSec = 0;
        if (_hasAudio) { _player.Stop(); _player.Position = TimeSpan.Zero; }
        _fallbackClock.Reset();
        SeekSlider.Value = 0;
        PlayPauseBtn.Content = "▶ Play";
        RenderHighwayFrame();
    }

    private void SeekSlider_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) => _isDraggingSlider = true;

    private void SeekSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        _currentTimeSec = SeekSlider.Value;
        if (_hasAudio) _player.Position = TimeSpan.FromSeconds(_currentTimeSec);
        if (_isPlaying) _fallbackClock.Restart();
        RenderHighwayFrame();
    }

    private void OffsetSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _offsetShiftMs = (int)Math.Round(OffsetSlider.Value);
        OffsetValueText.Text = _offsetShiftMs == 0
            ? "0 ms (No Shift)"
            : $"{_offsetShiftMs:+0;-0;0} ms ({(_offsetShiftMs > 0 ? "Notes Later" : "Notes Earlier")})";
        RenderHighwayFrame();
    }

    private void ResetOffsetBtn_Click(object sender, RoutedEventArgs e) => OffsetSlider.Value = 0;

    private async void SaveOffsetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_projectDir) || _doctor == null) return;
        if (_offsetShiftMs == 0)
        {
            WpfMessageBox.Show("Move the Audio-Chart Sync Offset slider first to shift note timing.", "Sync Offset", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SaveOffsetBtn.IsEnabled = false;
        SaveOffsetBtn.Content = "Recompiling...";
        StopPlayback();
        try
        {
            var opts = new ChartDoctorFixOptions
            {
                GenerateMissingDifficulties = false,
                AddMissingStarPower = false,
                AddMissingSections = false,
                ConvertOpenNotesToClassicGh3 = false,
                OffsetShiftMs = _offsetShiftMs,
                RecompileToGame = true
            };
            var (ok, msg) = await _doctor.RepairChartAndRecompileAsync(_projectDir, opts);
            WpfMessageBox.Show(msg, ok ? "Sync Offset Applied" : "Sync Offset Error", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok)
            {
                OffsetSlider.Value = 0;
                SongRecompiled?.Invoke();
            }
        }
        finally
        {
            SaveOffsetBtn.IsEnabled = true;
            SaveOffsetBtn.Content = "Save Offset & Recompile";
        }
    }

    private void HighwayCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => RenderHighwayFrame();

    private void RenderTimer_Tick(object? sender, EventArgs e)
    {
        if (_isPlaying)
        {
            if (_hasAudio && _player.NaturalDuration.HasTimeSpan)
            {
                _currentTimeSec = _player.Position.TotalSeconds;
                double dur = Math.Max(_chart.TotalDurationSeconds, _player.NaturalDuration.TimeSpan.TotalSeconds);
                if (Math.Abs(SeekSlider.Maximum - dur) > 1.0) SeekSlider.Maximum = dur;
            }
            else
            {
                _currentTimeSec += _fallbackClock.Elapsed.TotalSeconds;
                _fallbackClock.Restart();
                if (_currentTimeSec > _chart.TotalDurationSeconds) StopPlayback();
            }

            if (!_isDraggingSlider) SeekSlider.Value = Math.Min(SeekSlider.Maximum, _currentTimeSec);
        }

        var curTs = TimeSpan.FromSeconds(Math.Max(0, _currentTimeSec));
        var totTs = TimeSpan.FromSeconds(Math.Max(1, SeekSlider.Maximum));
        TimeLabel.Text = $"{(int)curTs.TotalMinutes}:{curTs.Seconds:D2} / {(int)totTs.TotalMinutes}:{totTs.Seconds:D2}";

        RenderHighwayFrame();
    }

    private void RenderHighwayFrame()
    {
        if (HighwayCanvas == null || _chart == null) return;
        double w = HighwayCanvas.ActualWidth;
        double h = HighwayCanvas.ActualHeight;
        if (w < 100 || h < 100) return;

        HighwayCanvas.Children.Clear();

        double cx = w * 0.5;
        double topY = 24.0;
        double strikeY = h - 54.0;
        double topHalfW = Math.Min(w * 0.16, 115.0);
        double botHalfW = Math.Min(w * 0.38, 280.0);
        double lookaheadSec = 1.65 / Math.Max(0.5, _speedMultiplier);
        double offsetSec = _offsetShiftMs / 1000.0;

        bool inSpNow = _chart.StarPower.Any(sp => (_currentTimeSec - offsetSec) >= sp.StartSec && (_currentTimeSec - offsetSec) <= sp.EndSec);
        StarPowerBadge.Visibility = inSpNow ? Visibility.Visible : Visibility.Collapsed;

        // Perspective fretboard polygon
        var board = new Polygon
        {
            Points = new PointCollection
            {
                new WpfPoint(cx - topHalfW, topY),
                new WpfPoint(cx + topHalfW, topY),
                new WpfPoint(cx + botHalfW, h),
                new WpfPoint(cx - botHalfW, h)
            },
            Fill = new LinearGradientBrush(
                inSpNow ? WpfColor.FromRgb(8, 36, 62) : WpfColor.FromRgb(14, 18, 30),
                inSpNow ? WpfColor.FromRgb(12, 52, 92) : WpfColor.FromRgb(22, 27, 44),
                90),
            Stroke = new SolidColorBrush(inSpNow ? WpfColor.FromRgb(56, 189, 248) : WpfColor.FromRgb(71, 85, 105)),
            StrokeThickness = inSpNow ? 2.5 : 2.0
        };
        HighwayCanvas.Children.Add(board);

        // Perspective helper: t = 0 at horizon, t = 1 at strike line
        double ProjectY(double norm) => topY + (strikeY - topY) * Math.Pow(norm, 1.45);
        double HalfWidthAt(double norm) => topHalfW + (botHalfW - topHalfW) * Math.Pow(norm, 1.45);
        double LaneX(int fret, double norm)
        {
            double hw = HalfWidthAt(norm);
            double laneFraction = (fret - 2.0) / 2.5; // -0.8, -0.4, 0, +0.4, +0.8
            return cx + laneFraction * hw;
        }

        // Lane divider strings
        for (int i = 0; i < 5; i++)
        {
            HighwayCanvas.Children.Add(new Line
            {
                X1 = LaneX(i, 0.0), Y1 = topY,
                X2 = LaneX(i, 1.12), Y2 = h,
                Stroke = new SolidColorBrush(WpfColor.FromArgb(70, 148, 163, 184)),
                StrokeThickness = 1.2
            });
        }

        // Beat gridlines scrolling down
        double minVisible = _currentTimeSec - 0.12;
        double maxVisible = _currentTimeSec + lookaheadSec;
        for (int bi = 0; bi < _chart.BeatTimesSeconds.Count; bi++)
        {
            double bt = _chart.BeatTimesSeconds[bi] + offsetSec;
            if (bt < minVisible) continue;
            if (bt > maxVisible) break;
            double norm = 1.0 - ((bt - _currentTimeSec) / lookaheadSec);
            if (norm < 0 || norm > 1.1) continue;
            double y = ProjectY(norm);
            double hw = HalfWidthAt(norm);
            bool isMeasure = bi % 4 == 0;
            HighwayCanvas.Children.Add(new Line
            {
                X1 = cx - hw, Y1 = y,
                X2 = cx + hw, Y2 = y,
                Stroke = new SolidColorBrush(isMeasure ? WpfColor.FromArgb(145, 226, 232, 240) : WpfColor.FromArgb(55, 148, 163, 184)),
                StrokeThickness = isMeasure ? 2.0 : 1.0
            });
        }

        // Strike line fret targets (G R Y B O)
        double strikeHw = HalfWidthAt(1.0);
        HighwayCanvas.Children.Add(new Line
        {
            X1 = cx - strikeHw, Y1 = strikeY,
            X2 = cx + strikeHw, Y2 = strikeY,
            Stroke = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
            StrokeThickness = 2.5
        });

        for (int f = 0; f < 5; f++)
        {
            double lx = LaneX(f, 1.0);
            var target = new Ellipse
            {
                Width = 42, Height = 20,
                Fill = new SolidColorBrush(WpfColor.FromArgb(50, FretColors[f].R, FretColors[f].G, FretColors[f].B)),
                Stroke = new SolidColorBrush(FretColors[f]),
                StrokeThickness = 2.6
            };
            Canvas.SetLeft(target, lx - 21);
            Canvas.SetTop(target, strikeY - 10);
            HighwayCanvas.Children.Add(target);
        }

        // Draw Sustain Tails + Notes (back-to-front so closer notes overlap properly)
        int notesInLastSec = 0;
        var visibleNotes = new List<ChartPreviewNote>();
        foreach (var n in _activeNotes)
        {
            double tStart = n.TimeSeconds + offsetSec;
            double tEnd = n.EndTimeSeconds + offsetSec;
            if (tStart >= _currentTimeSec - 0.5 && tStart <= _currentTimeSec + 0.5) notesInLastSec++;
            if (tEnd < minVisible) continue;
            if (tStart > maxVisible) break;
            visibleNotes.Add(n);
        }

        for (int idx = visibleNotes.Count - 1; idx >= 0; idx--)
        {
            var n = visibleNotes[idx];
            double tStart = n.TimeSeconds + offsetSec;
            double tEnd = n.EndTimeSeconds + offsetSec;
            double normStart = Math.Clamp(1.0 - ((tStart - _currentTimeSec) / lookaheadSec), 0.0, 1.12);
            double yStart = ProjectY(normStart);
            double scale = 0.35 + 0.65 * Math.Pow(normStart, 1.3);

            // Purple Open Note Bar (Fret 7)
            if (n.Fret == 7)
            {
                double hw = HalfWidthAt(normStart) * 0.92;
                double barH = Math.Max(5, 13 * scale);
                var openBar = new System.Windows.Shapes.Rectangle
                {
                    Width = hw * 2, Height = barH,
                    RadiusX = barH * 0.5, RadiusY = barH * 0.5,
                    Fill = new SolidColorBrush(n.InStarPower ? WpfColor.FromRgb(56, 189, 248) : WpfColor.FromRgb(192, 38, 211)),
                    Stroke = System.Windows.Media.Brushes.White,
                    StrokeThickness = 1.6
                };
                Canvas.SetLeft(openBar, cx - hw);
                Canvas.SetTop(openBar, yStart - barH * 0.5);
                HighwayCanvas.Children.Add(openBar);
                continue;
            }

            int fret = Math.Clamp(n.Fret, 0, 4);
            var gemColor = n.InStarPower ? WpfColor.FromRgb(56, 189, 248) : FretColors[fret];

            // Sustain Tail
            if (tEnd > tStart + 0.08)
            {
                double normEnd = Math.Clamp(1.0 - ((tEnd - _currentTimeSec) / lookaheadSec), 0.0, 1.05);
                double yEnd = ProjectY(normEnd);
                HighwayCanvas.Children.Add(new Line
                {
                    X1 = LaneX(fret, normStart), Y1 = yStart,
                    X2 = LaneX(fret, normEnd), Y2 = yEnd,
                    Stroke = new SolidColorBrush(WpfColor.FromArgb(200, gemColor.R, gemColor.G, gemColor.B)),
                    StrokeThickness = Math.Max(3.0, 9.0 * scale),
                    StrokeStartLineCap = PenLineCap.Round,
                    StrokeEndLineCap = PenLineCap.Round
                });
            }

            if (tStart < _currentTimeSec - 0.06) continue;

            // Note Gem
            double nx = LaneX(fret, normStart);
            double gemW = 40.0 * scale;
            double gemH = 19.0 * scale;
            var gem = new Ellipse
            {
                Width = gemW, Height = gemH,
                Fill = new SolidColorBrush(gemColor),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = n.IsHopo ? 2.4 : 1.4
            };
            Canvas.SetLeft(gem, nx - gemW * 0.5);
            Canvas.SetTop(gem, yStart - gemH * 0.5);
            HighwayCanvas.Children.Add(gem);

            // Bright white HOPO / Tap center cap
            if (n.IsHopo || n.IsTap)
            {
                double capW = gemW * 0.52;
                double capH = gemH * 0.52;
                var cap = new Ellipse
                {
                    Width = capW, Height = capH,
                    Fill = n.IsTap ? new SolidColorBrush(WpfColor.FromRgb(186, 230, 253)) : System.Windows.Media.Brushes.White
                };
                Canvas.SetLeft(cap, nx - capW * 0.5);
                Canvas.SetTop(cap, yStart - capH * 0.5);
                HighwayCanvas.Children.Add(cap);
            }
        }

        // Update live Section & BPM/NPS HUD
        var activeSection = _chart.Sections.LastOrDefault(s => s.TimeSeconds <= (_currentTimeSec - offsetSec))?.Name ?? "Intro";
        var activeBpm = _chart.Tempos.LastOrDefault(t => t.TimeSeconds <= (_currentTimeSec - offsetSec))?.Bpm ?? 120.0;
        SectionPillText.Text = $"Section: {activeSection}";
        LiveStatsText.Text = $"BPM: {activeBpm:F1}  •  NPS: {notesInLastSec:F1}";
    }
}
