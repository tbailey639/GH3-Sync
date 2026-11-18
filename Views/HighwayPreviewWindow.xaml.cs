using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using WpfColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;
using WpfMessageBox = System.Windows.MessageBox;

namespace GH3_Sync.Views;

public partial class HighwayPreviewWindow : Window
{
    private readonly ParsedChartData _chart;
    private readonly string? _projectDir;
    private readonly string? _songId;
    private readonly ChartDoctorService? _doctor;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _fallbackClock = new();

    private List<ChartPreviewNote> _activeNotes = new();
    private readonly Stack<List<ChartPreviewNote>> _editUndoStack = new();
    private bool _hasAudio;
    private bool _isPlaying;
    private bool _isDraggingSlider;
    private double _currentTimeSec;
    private double _speedMultiplier = 1.5;
    private double _practiceSpeedRatio = 1.0;
    private int _offsetShiftMs = 0;

    // Mode: "watch" | "play" | "edit"
    private string _mode = "watch";
    private double? _loopASec;
    private double? _loopBSec;

    // Playable Practice State
    private readonly bool[] _heldFrets = new bool[5];
    private readonly bool[] _keyFrets = new bool[5];
    private readonly double[] _hitFlashUntil = new double[5];
    private readonly HashSet<ChartPreviewNote> _hitNotes = new();
    private readonly HashSet<ChartPreviewNote> _missedNotes = new();
    private ushort _prevXInputBtns;
    private int _score;
    private int _streak;
    private int _maxStreak;
    private int _notesHit;
    private int _notesMissed;
    private bool _hasRecordedScoreThisRun;

    private static readonly WpfColor[] FretColors =
    {
        WpfColor.FromRgb(34, 197, 94),   // 0 Green
        WpfColor.FromRgb(239, 68, 68),   // 1 Red
        WpfColor.FromRgb(234, 179, 8),   // 2 Yellow
        WpfColor.FromRgb(59, 130, 246),  // 3 Blue
        WpfColor.FromRgb(249, 115, 22),  // 4 Orange
    };

    public event Action? SongRecompiled;
    public event Action? ScoreRecorded;

    public HighwayPreviewWindow(
        string title,
        string artist,
        string chartPath,
        string? audioPath,
        string? projectDir = null,
        ChartDoctorService? doctor = null,
        string? songId = null)
    {
        InitializeComponent();

        _projectDir = projectDir;
        _songId = songId;
        _doctor = doctor;
        _chart = ChartDoctorService.ParseChartFile(chartPath);

        SongHeaderTitle.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title}  —  {artist}";
        SaveOffsetBtn.IsEnabled = !string.IsNullOrWhiteSpace(_projectDir) && _doctor != null;
        SaveEditsBtn.IsEnabled = !string.IsNullOrWhiteSpace(_projectDir) && _doctor != null;

        string[] order = { "Expert", "Hard", "Medium", "Easy" };
        foreach (var d in order)
        {
            if (_chart.Difficulties.TryGetValue(d, out var notes) && notes.Count > 0)
                DifficultyCombo.Items.Add(d);
        }
        if (DifficultyCombo.Items.Count == 0)
            DifficultyCombo.Items.Add("Expert");
        DifficultyCombo.SelectedIndex = 0;

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
                _player.MediaEnded += (_, _) => OnTrackEnded();
                _hasAudio = true;
            }
            catch { _hasAudio = false; }
        }

        SeekSlider.Maximum = Math.Max(10, _chart.TotalDurationSeconds);
        UpdateHeaderMeta();

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _renderTimer.Tick += RenderTimer_Tick;
        Loaded += (_, _) => { RenderHighwayFrame(); _renderTimer.Start(); };
        Closing += (_, _) =>
        {
            _renderTimer.Stop();
            SavePlayScoreIfMeaningful();
            try { _player.Stop(); _player.Close(); } catch { }
        };
    }

    private void UpdateHeaderMeta()
    {
        if (DifficultyCombo == null || _chart == null) return;
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        double bpm = _chart.Tempos.FirstOrDefault()?.Bpm ?? 120.0;
        SongHeaderMeta.Text = $"{diff} ({_activeNotes.Count:N0} notes)  •  Base BPM: {bpm:F1}  •  {_chart.StarPower.Count} Star Power phrases  •  {_chart.Sections.Count} sections" +
                              (_hasAudio ? "" : "  •  [Visual Chart Mode — No Audio]");
    }

    private void ModeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string m) return;
        _mode = m;

        ModeWatchBtn.Background = new SolidColorBrush(m == "watch" ? WpfColor.FromRgb(249, 115, 22) : WpfColor.FromRgb(30, 36, 54));
        ModePlayBtn.Background = new SolidColorBrush(m == "play" ? WpfColor.FromRgb(16, 185, 129) : WpfColor.FromRgb(30, 36, 54));
        ModeEditBtn.Background = new SolidColorBrush(m == "edit" ? WpfColor.FromRgb(99, 102, 241) : WpfColor.FromRgb(30, 36, 54));

        PlayScoreHud.Visibility = m == "play" ? Visibility.Visible : Visibility.Collapsed;
        EditorHintPill.Visibility = m == "edit" ? Visibility.Visible : Visibility.Collapsed;
        EditorActionPanel.Visibility = m == "edit" ? Visibility.Visible : Visibility.Collapsed;

        if (m == "play") ResetPlayStats();
        RenderHighwayFrame();
    }

    private void ResetPlayStats()
    {
        _hitNotes.Clear();
        _missedNotes.Clear();
        _score = 0;
        _streak = 0;
        _maxStreak = 0;
        _notesHit = 0;
        _notesMissed = 0;
        _hasRecordedScoreThisRun = false;
        UpdatePlayHud();
    }

    private void UpdatePlayHud()
    {
        int mult = Math.Min(4, 1 + _streak / 10);
        int total = _notesHit + _notesMissed;
        double acc = total > 0 ? (_notesHit * 100.0 / total) : 100.0;
        bool fc = _notesMissed == 0 && _notesHit > 0;
        PlayScoreText.Text = _score.ToString("N0");
        PlayStreakText.Text = $"Streak: {_streak} ({mult}x)";
        PlayAccuracyText.Text = $"{acc:F1}%{(fc ? "  •  ★ FC" : "")}";
    }

    private void SavePlayScoreIfMeaningful()
    {
        if (_hasRecordedScoreThisRun || _mode != "play" || string.IsNullOrWhiteSpace(_songId) || _notesHit < 15) return;
        int total = Math.Max(1, _notesHit + _notesMissed);
        double pct = _notesHit * 100.0 / total;
        bool isFc = _notesMissed == 0 && _notesHit >= Math.Max(15, _activeNotes.Count - 2);
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        if (AppConfig.RecordHighScore(_songId, _score, pct, _maxStreak, isFc, diff))
        {
            _hasRecordedScoreThisRun = true;
            ScoreRecorded?.Invoke();
        }
    }

    private void OnTrackEnded()
    {
        SavePlayScoreIfMeaningful();
        StopPlayback();
    }

    // ==================== A->B SOLO LOOPER & PRACTICE SPEED ====================
    private void SetLoopABtn_Click(object sender, RoutedEventArgs e)
    {
        _loopASec = _currentTimeSec;
        if (_loopBSec.HasValue && _loopBSec <= _loopASec + 0.5) _loopBSec = null;
        UpdateLoopLabel();
    }

    private void SetLoopBBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_loopASec.HasValue) _loopASec = Math.Max(0, _currentTimeSec - 5.0);
        if (_currentTimeSec <= _loopASec.Value + 0.5) return;
        _loopBSec = _currentTimeSec;
        UpdateLoopLabel();
    }

    private void ClearLoopBtn_Click(object sender, RoutedEventArgs e)
    {
        _loopASec = null;
        _loopBSec = null;
        UpdateLoopLabel();
    }

    private void UpdateLoopLabel()
    {
        if (!_loopASec.HasValue && !_loopBSec.HasValue)
        {
            LoopStatusText.Text = "";
            ClearLoopBtn.Visibility = Visibility.Collapsed;
            return;
        }
        ClearLoopBtn.Visibility = Visibility.Visible;
        string a = _loopASec.HasValue ? $"{(int)(_loopASec.Value / 60)}:{(int)(_loopASec.Value % 60):D2}" : "--";
        string b = _loopBSec.HasValue ? $"{(int)(_loopBSec.Value / 60)}:{(int)(_loopBSec.Value % 60):D2}" : "--";
        LoopStatusText.Text = $"Loop {a} → {b}";
    }

    private void PracticeRateCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PracticeRateCombo == null) return;
        _practiceSpeedRatio = PracticeRateCombo.SelectedIndex switch
        {
            0 => 0.50,
            1 => 0.75,
            2 => 0.90,
            _ => 1.00
        };
        if (_hasAudio)
        {
            try { _player.SpeedRatio = _practiceSpeedRatio; } catch { }
        }
    }

    // ==================== PLAYABLE INPUT (KEYBOARD + XINPUT GUITAR) ====================
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_mode != "play") return;
        int fretHit = -1;
        if (e.Key == Key.A) { _keyFrets[0] = true; fretHit = 0; }
        else if (e.Key == Key.S) { _keyFrets[1] = true; fretHit = 1; }
        else if (e.Key == Key.J) { _keyFrets[2] = true; fretHit = 2; }
        else if (e.Key == Key.K) { _keyFrets[3] = true; fretHit = 3; }
        else if (e.Key == Key.L) { _keyFrets[4] = true; fretHit = 4; }
        else if (e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.Up || e.Key == Key.Down)
        {
            e.Handled = true;
            TriggerStrumHitCheck();
            return;
        }

        // Allow convenient tap-to-hit on keyboard if playing fast
        if (fretHit >= 0 && !e.IsRepeat)
            TryHitSingleFretOrHopo(fretHit);
    }

    private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A) _keyFrets[0] = false;
        else if (e.Key == Key.S) _keyFrets[1] = false;
        else if (e.Key == Key.J) _keyFrets[2] = false;
        else if (e.Key == Key.K) _keyFrets[3] = false;
        else if (e.Key == Key.L) _keyFrets[4] = false;
    }

    private void PollPlayableController()
    {
        ushort btns = 0;
        if (XInputNative.TryGetFirstConnectedState(out _, out var state))
            btns = state.Gamepad.wButtons;

        _heldFrets[0] = _keyFrets[0] || (btns & XInputNative.A) != 0;
        _heldFrets[1] = _keyFrets[1] || (btns & XInputNative.B) != 0;
        _heldFrets[2] = _keyFrets[2] || (btns & XInputNative.Y) != 0;
        _heldFrets[3] = _keyFrets[3] || (btns & XInputNative.X) != 0;
        _heldFrets[4] = _keyFrets[4] || (btns & XInputNative.LEFT_SHOULDER) != 0;

        bool strumPressed = ((btns & (XInputNative.DPAD_UP | XInputNative.DPAD_DOWN)) != 0) &&
                            ((_prevXInputBtns & (XInputNative.DPAD_UP | XInputNative.DPAD_DOWN)) == 0);
        if (strumPressed) TriggerStrumHitCheck();
        _prevXInputBtns = btns;

        // Mark notes that passed well behind the strike line as missed
        double offsetSec = _offsetShiftMs / 1000.0;
        foreach (var n in _activeNotes)
        {
            double t = n.TimeSeconds + offsetSec;
            if (t < _currentTimeSec - 0.14 && !_hitNotes.Contains(n) && !_missedNotes.Contains(n))
            {
                _missedNotes.Add(n);
                _notesMissed++;
                _streak = 0;
                UpdatePlayHud();
            }
            if (t > _currentTimeSec + 0.2) break;
        }
    }

    private void TryHitSingleFretOrHopo(int fret)
    {
        if (!_isPlaying) return;
        double offsetSec = _offsetShiftMs / 1000.0;
        var hit = _activeNotes.FirstOrDefault(n =>
            !_hitNotes.Contains(n) && !_missedNotes.Contains(n) &&
            n.Fret == fret &&
            Math.Abs((n.TimeSeconds + offsetSec) - _currentTimeSec) <= 0.10);
        if (hit != null) RegisterNoteHit(hit);
    }

    private void TriggerStrumHitCheck()
    {
        if (!_isPlaying) return;
        double offsetSec = _offsetShiftMs / 1000.0;
        var windowNotes = _activeNotes.Where(n =>
            !_hitNotes.Contains(n) && !_missedNotes.Contains(n) &&
            Math.Abs((n.TimeSeconds + offsetSec) - _currentTimeSec) <= 0.11).ToList();

        if (windowNotes.Count == 0) return;

        bool anyHit = false;
        foreach (var n in windowNotes)
        {
            bool fretMatches = n.Fret == 7
                ? !_heldFrets.Any(x => x)
                : (n.Fret >= 0 && n.Fret <= 4 && _heldFrets[n.Fret]);
            if (fretMatches)
            {
                RegisterNoteHit(n);
                anyHit = true;
            }
        }

        if (!anyHit)
        {
            _streak = 0;
            UpdatePlayHud();
        }
    }

    private void RegisterNoteHit(ChartPreviewNote n)
    {
        _hitNotes.Add(n);
        _notesHit++;
        _streak++;
        if (_streak > _maxStreak) _maxStreak = _streak;
        int mult = Math.Min(4, 1 + _streak / 10);
        _score += 50 * mult;
        if (n.Fret >= 0 && n.Fret <= 4) _hitFlashUntil[n.Fret] = _currentTimeSec + 0.14;
        else for (int i = 0; i < 5; i++) _hitFlashUntil[i] = _currentTimeSec + 0.14;
        UpdatePlayHud();
    }

    // ==================== VISUAL QUICK-FIX NOTE EDITOR ====================
    private void HighwayCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != "edit") return;
        var pos = e.GetPosition(HighwayCanvas);
        if (!TryMapCanvasPointToLaneAndTime(pos, out int fret, out double targetSec)) return;

        PushUndoSnapshot();
        double offsetSec = _offsetShiftMs / 1000.0;
        double rawSec = Math.Max(0, targetSec - offsetSec);

        // If clicking near an existing note in that lane, remove it; otherwise snap to 1/16th beat & add!
        var existing = _activeNotes.FirstOrDefault(n => n.Fret == fret && Math.Abs(n.TimeSeconds - rawSec) <= 0.09);
        if (existing != null)
        {
            _activeNotes.Remove(existing);
        }
        else
        {
            long rawTick = _chart.SecondsToTick(rawSec);
            long step = Math.Max(1, _chart.Resolution / 4); // 16th-note snap
            long snappedTick = (long)Math.Round(rawTick / (double)step) * step;
            double snappedSec = _chart.TickToSeconds(snappedTick);
            _activeNotes.Add(new ChartPreviewNote
            {
                Tick = snappedTick,
                TimeSeconds = snappedSec,
                EndTimeSeconds = snappedSec,
                Fret = fret,
                LengthTicks = 0
            });
            _activeNotes = _activeNotes.OrderBy(n => n.Tick).ThenBy(n => n.Fret).ToList();
        }

        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        _chart.Difficulties[diff] = _activeNotes;
        UpdateHeaderMeta();
        RenderHighwayFrame();
    }

    private void HighwayCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_mode != "edit") return;
        var pos = e.GetPosition(HighwayCanvas);
        if (!TryMapCanvasPointToLaneAndTime(pos, out int fret, out double targetSec)) return;

        double offsetSec = _offsetShiftMs / 1000.0;
        double rawSec = Math.Max(0, targetSec - offsetSec);
        var existing = _activeNotes.FirstOrDefault(n => n.Fret == fret && Math.Abs(n.TimeSeconds - rawSec) <= 0.11);
        if (existing != null)
        {
            PushUndoSnapshot();
            existing.IsHopo = !existing.IsHopo;
            RenderHighwayFrame();
        }
    }

    private void PushUndoSnapshot()
    {
        var clone = _activeNotes.Select(n => new ChartPreviewNote
        {
            Tick = n.Tick, TimeSeconds = n.TimeSeconds, EndTimeSeconds = n.EndTimeSeconds,
            Fret = n.Fret, LengthTicks = n.LengthTicks, IsHopo = n.IsHopo, IsTap = n.IsTap, InStarPower = n.InStarPower
        }).ToList();
        _editUndoStack.Push(clone);
    }

    private void UndoEditBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_editUndoStack.Count == 0) return;
        _activeNotes = _editUndoStack.Pop();
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        _chart.Difficulties[diff] = _activeNotes;
        UpdateHeaderMeta();
        RenderHighwayFrame();
    }

    private async void SaveEditsBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_projectDir) || _doctor == null) return;
        SaveEditsBtn.IsEnabled = false;
        SaveEditsBtn.Content = "Saving...";
        StopPlayback();
        try
        {
            var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
            var (ok, msg) = await _doctor.SaveEditedDifficultyNotesAsync(_projectDir, diff, _activeNotes, recompile: true);
            WpfMessageBox.Show(msg, ok ? "Chart Saved" : "Save Error", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok) SongRecompiled?.Invoke();
        }
        finally
        {
            SaveEditsBtn.IsEnabled = true;
            SaveEditsBtn.Content = "💾 Save Chart & Recompile";
        }
    }

    private bool TryMapCanvasPointToLaneAndTime(WpfPoint pt, out int fret, out double timeSec)
    {
        fret = 0;
        timeSec = _currentTimeSec;
        double w = HighwayCanvas.ActualWidth;
        double h = HighwayCanvas.ActualHeight;
        if (w < 100 || h < 100) return false;

        double cx = w * 0.5;
        double topY = 24.0;
        double strikeY = h - 54.0;
        if (pt.Y < topY || pt.Y > strikeY + 25) return false;

        double yRatio = Math.Clamp((pt.Y - topY) / Math.Max(1.0, strikeY - topY), 0.0, 1.0);
        double norm = Math.Pow(yRatio, 1.0 / 1.45);
        double topHalfW = Math.Min(w * 0.16, 115.0);
        double botHalfW = Math.Min(w * 0.38, 280.0);
        double hw = topHalfW + (botHalfW - topHalfW) * Math.Pow(norm, 1.45);

        if (Math.Abs(pt.X - cx) > hw * 1.05) return false;

        double lanePos = ((pt.X - cx) / Math.Max(1.0, hw)) * 2.5 + 2.0;
        fret = Math.Clamp((int)Math.Round(lanePos), 0, 4);
        double lookaheadSec = 1.65 / Math.Max(0.5, _speedMultiplier);
        timeSec = _currentTimeSec + (1.0 - norm) * lookaheadSec;
        return true;
    }

    // ==================== STANDARD CONTROLS ====================
    private void DifficultyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_chart == null) return;
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        _activeNotes = _chart.Difficulties.TryGetValue(diff, out var list) ? list : new List<ChartPreviewNote>();
        ResetPlayStats();
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
                try { _player.SpeedRatio = _practiceSpeedRatio; } catch { }
                _player.Play();
            }
            _fallbackClock.Restart();
            PlayPauseBtn.Content = "⏸ Pause";
        }
    }

    private void StopBtn_Click(object sender, RoutedEventArgs e)
    {
        SavePlayScoreIfMeaningful();
        StopPlayback();
    }

    private void StopPlayback()
    {
        _isPlaying = false;
        _currentTimeSec = _loopASec ?? 0;
        if (_hasAudio) { _player.Stop(); _player.Position = TimeSpan.FromSeconds(_currentTimeSec); }
        _fallbackClock.Reset();
        SeekSlider.Value = _currentTimeSec;
        PlayPauseBtn.Content = "▶ Play";
        if (_mode == "play") ResetPlayStats();
        RenderHighwayFrame();
    }

    private void SeekSlider_MouseDown(object sender, MouseButtonEventArgs e) => _isDraggingSlider = true;

    private void SeekSlider_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _isDraggingSlider = false;
        _currentTimeSec = SeekSlider.Value;
        if (_hasAudio) _player.Position = TimeSpan.FromSeconds(_currentTimeSec);
        if (_isPlaying) _fallbackClock.Restart();
        if (_mode == "play") { _hitNotes.Clear(); _missedNotes.Clear(); }
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
                _currentTimeSec += _fallbackClock.Elapsed.TotalSeconds * _practiceSpeedRatio;
                _fallbackClock.Restart();
                if (_currentTimeSec > _chart.TotalDurationSeconds) OnTrackEnded();
            }

            // A->B Section Loop
            if (_loopASec.HasValue && _loopBSec.HasValue && _currentTimeSec >= _loopBSec.Value)
            {
                _currentTimeSec = _loopASec.Value;
                if (_hasAudio) _player.Position = TimeSpan.FromSeconds(_currentTimeSec);
                if (_mode == "play") { _hitNotes.Clear(); _missedNotes.Clear(); }
            }

            if (!_isDraggingSlider) SeekSlider.Value = Math.Min(SeekSlider.Maximum, _currentTimeSec);
            if (_mode == "play") PollPlayableController();
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
            Stroke = new SolidColorBrush(inSpNow ? WpfColor.FromRgb(56, 189, 248) : (_mode == "edit" ? WpfColor.FromRgb(129, 140, 248) : WpfColor.FromRgb(71, 85, 105))),
            StrokeThickness = inSpNow || _mode == "edit" ? 2.5 : 2.0
        };
        HighwayCanvas.Children.Add(board);

        double ProjectY(double norm) => topY + (strikeY - topY) * Math.Pow(norm, 1.45);
        double HalfWidthAt(double norm) => topHalfW + (botHalfW - topHalfW) * Math.Pow(norm, 1.45);
        double LaneX(int fret, double norm) => cx + ((fret - 2.0) / 2.5) * HalfWidthAt(norm);

        // Lane strings
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

        // Beat gridlines
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

        // Strike line fret targets (light up when held or hit in Playable Practice Mode!)
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
            bool held = _mode == "play" && _heldFrets[f];
            bool flash = _mode == "play" && _currentTimeSec < _hitFlashUntil[f];

            if (flash)
            {
                var burst = new Ellipse
                {
                    Width = 58, Height = 30,
                    Fill = new SolidColorBrush(WpfColor.FromArgb(115, 255, 255, 255)),
                    Stroke = new SolidColorBrush(FretColors[f]),
                    StrokeThickness = 2.5
                };
                Canvas.SetLeft(burst, lx - 29);
                Canvas.SetTop(burst, strikeY - 15);
                HighwayCanvas.Children.Add(burst);
            }

            var target = new Ellipse
            {
                Width = 42, Height = 20,
                Fill = new SolidColorBrush(held ? FretColors[f] : WpfColor.FromArgb(50, FretColors[f].R, FretColors[f].G, FretColors[f].B)),
                Stroke = new SolidColorBrush(held ? WpfColor.FromRgb(255, 255, 255) : FretColors[f]),
                StrokeThickness = held ? 3.0 : 2.6
            };
            Canvas.SetLeft(target, lx - 21);
            Canvas.SetTop(target, strikeY - 10);
            HighwayCanvas.Children.Add(target);
        }

        // Notes & Sustain Tails
        int notesInLastSec = 0;
        var visibleNotes = new List<ChartPreviewNote>();
        foreach (var n in _activeNotes)
        {
            double tStart = n.TimeSeconds + offsetSec;
            double tEnd = n.EndTimeSeconds + offsetSec;
            if (tStart >= _currentTimeSec - 0.5 && tStart <= _currentTimeSec + 0.5) notesInLastSec++;
            if (tEnd < minVisible) continue;
            if (tStart > maxVisible) break;
            if (_mode == "play" && _hitNotes.Contains(n)) continue;
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

        var activeSection = _chart.Sections.LastOrDefault(s => s.TimeSeconds <= (_currentTimeSec - offsetSec))?.Name ?? "Intro";
        var activeBpm = _chart.Tempos.LastOrDefault(t => t.TimeSeconds <= (_currentTimeSec - offsetSec))?.Bpm ?? 120.0;
        SectionPillText.Text = $"Section: {activeSection}";
        LiveStatsText.Text = $"BPM: {activeBpm:F1}  •  NPS: {notesInLastSec:F1}";
    }
}
