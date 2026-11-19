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
using WpfMessageBox = GH3_Sync.Views.GuitarHeroModalDialog;

namespace GH3_Sync.Views;

public partial class HighwayPreviewWindow : Window
{
    private readonly ParsedChartData _chart;
    private readonly string _songTitle;
    private readonly string _songArtist;
    private readonly string? _projectDir;
    private readonly string? _songId;
    private readonly ChartDoctorService? _doctor;
    private readonly ChartStudioToolsService? _studioTools;
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _renderTimer;
    private readonly Stopwatch _fallbackClock = new();

    private List<ChartPreviewNote> _activeNotes = new();
    private List<ChartPreviewNote> _activeBassNotes = new();
    private string _trackMode = "lead"; // "lead" | "bass" | "split"
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

    // Playable Practice State (Universal DualSense Edge / PS / Xbox / Guitar + Star Power + Rock Meter)
    private readonly bool[] _heldFrets = new bool[5];
    private readonly bool[] _prevPadFrets = new bool[5];
    private readonly bool[] _keyFrets = new bool[5];
    private readonly double[] _hitFlashUntil = new double[5];
    private readonly HashSet<ChartPreviewNote> _hitNotes = new();
    private readonly HashSet<ChartPreviewNote> _missedNotes = new();
    private bool _prevPadStrum;
    private bool _prevPadSp;
    private int _score;
    private int _streak;
    private int _maxStreak;
    private int _notesHit;
    private int _notesMissed;
    private double _rockMeterVal = 75.0; // 0..100
    private double _starPowerCharge = 0.0; // 0..100
    private bool _starPowerActive;
    private bool _hasRecordedScoreThisRun;
    private Key[] _boundFretKeys = { Key.A, Key.S, Key.J, Key.K, Key.L };
    private Key _boundStrumKey = Key.Space;

    private static readonly WpfColor[][] PalettePresets =
    {
        // 0: Classic GH3
        new[] { WpfColor.FromRgb(34, 197, 94), WpfColor.FromRgb(239, 68, 68), WpfColor.FromRgb(234, 179, 8), WpfColor.FromRgb(59, 130, 246), WpfColor.FromRgb(249, 115, 22) },
        // 1: Colorblind High-Contrast (Okabe-Ito inspired)
        new[] { WpfColor.FromRgb(0, 158, 115), WpfColor.FromRgb(213, 94, 0), WpfColor.FromRgb(240, 228, 66), WpfColor.FromRgb(86, 180, 233), WpfColor.FromRgb(204, 121, 167) },
        // 2: Synthwave Neon
        new[] { WpfColor.FromRgb(34, 211, 238), WpfColor.FromRgb(236, 72, 153), WpfColor.FromRgb(250, 204, 21), WpfColor.FromRgb(168, 85, 247), WpfColor.FromRgb(251, 146, 60) },
        // 3: Metallic Chrome
        new[] { WpfColor.FromRgb(134, 239, 172), WpfColor.FromRgb(252, 165, 165), WpfColor.FromRgb(253, 224, 71), WpfColor.FromRgb(147, 197, 253), WpfColor.FromRgb(253, 186, 116) }
    };
    private WpfColor[] FretColors = PalettePresets[0];

    public event Action? SongRecompiled;
    public event Action? ScoreRecorded;

    public HighwayPreviewWindow(
        string title,
        string artist,
        string chartPath,
        string? audioPath,
        string? projectDir = null,
        ChartDoctorService? doctor = null,
        string? songId = null,
        ChartStudioToolsService? studioTools = null)
    {
        InitializeComponent();

        _songTitle = title;
        _songArtist = artist;
        _projectDir = projectDir;
        _songId = songId;
        _doctor = doctor;
        _studioTools = studioTools;
        _chart = ChartDoctorService.ParseChartFile(chartPath);

        LoadKeyBindingsAndPalette();
        BuildSoloCutBtn.IsEnabled = !string.IsNullOrWhiteSpace(_projectDir) && _studioTools != null;

        SongHeaderTitle.Text = string.IsNullOrWhiteSpace(artist) ? title : $"{title}  —  {artist}";
        try
        {
            double bpm = _chart.Tempos.FirstOrDefault()?.Bpm ?? 120.0;
            var theme = HighwayThemeService.DetectTheme(_songTitle, _songArtist, bpm);
            if (CoachInsightText != null)
                CoachInsightText.Text = $"🏟️ VENUE: {theme.VenueName} ({theme.Name}) • AI Coach: Ready";
        }
        catch { }
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
                            CreateNoWindow = true,
                            UseShellExecute = false
                        };
                        psi.ArgumentList.Add("-y");
                        psi.ArgumentList.Add("-v");
                        psi.ArgumentList.Add("error");
                        psi.ArgumentList.Add("-i");
                        psi.ArgumentList.Add(audioPath);
                        psi.ArgumentList.Add("-ar");
                        psi.ArgumentList.Add("44100");
                        psi.ArgumentList.Add("-ac");
                        psi.ArgumentList.Add("2");
                        psi.ArgumentList.Add(tmpWav);
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
        var bassInfo = _activeBassNotes.Count > 0 ? $"  •  Co-Op Bass: {_activeBassNotes.Count:N0} notes" : "  •  No Co-Op Bass track";
        SongHeaderMeta.Text = $"{diff} ({_activeNotes.Count:N0} Lead notes{bassInfo})  •  Base BPM: {bpm:F1}  •  {_chart.StarPower.Count} SP phrases" +
                              (_hasAudio ? "" : "  •  [Visual Mode]");
    }

    private void TrackModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || TrackModeCombo == null) return;
        _trackMode = TrackModeCombo.SelectedIndex switch
        {
            1 => "bass",
            2 => "split",
            _ => "lead"
        };
        RenderHighwayFrame();
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

    private void LoadKeyBindingsAndPalette()
    {
        var keys = AppConfig.Current.CustomKeyFrets;
        if (keys != null && keys.Length == 5)
        {
            for (int i = 0; i < 5; i++)
                if (Enum.TryParse<Key>(keys[i], true, out var k)) _boundFretKeys[i] = k;
        }
        if (Enum.TryParse<Key>(AppConfig.Current.CustomKeyStrum ?? "Space", true, out var sk))
            _boundStrumKey = sk;

        int palIdx = (AppConfig.Current.HighwayGemPalette ?? "Classic").ToLowerInvariant() switch
        {
            "colorblind" => 1,
            "synthwave" => 2,
            "metallic" => 3,
            _ => 0
        };
        PaletteCombo.SelectedIndex = palIdx;
        FretColors = PalettePresets[palIdx];
    }

    private void PaletteCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || PaletteCombo == null) return;
        int idx = Math.Clamp(PaletteCombo.SelectedIndex, 0, PalettePresets.Length - 1);
        FretColors = PalettePresets[idx];
        AppConfig.Current.HighwayGemPalette = idx switch { 1 => "Colorblind", 2 => "Synthwave", 3 => "Metallic", _ => "Classic" };
        AppConfig.Save();
        RenderHighwayFrame();
    }

    private void RemapKeysBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Window
        {
            Title = "Custom Keyboard Fret Bindings",
            Width = 390,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(new TextBlock { Text = "Enter 5 Fret Keys + Strum Key (e.g. A S J K L or D1 D2 D3 D4 D5)", FontSize = 11.5, Foreground = System.Windows.Media.Brushes.LightGray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });

        var boxes = new System.Windows.Controls.TextBox[5];
        string[] labels = { "Green Fret:", "Red Fret:", "Yellow Fret:", "Blue Fret:", "Orange Fret:" };
        for (int i = 0; i < 5; i++)
        {
            var dp = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            boxes[i] = new System.Windows.Controls.TextBox { Text = _boundFretKeys[i].ToString(), Width = 90 };
            DockPanel.SetDock(boxes[i], Dock.Right);
            dp.Children.Add(boxes[i]);
            dp.Children.Add(new TextBlock { Text = labels[i], VerticalAlignment = VerticalAlignment.Center });
            sp.Children.Add(dp);
        }
        var strumBox = new System.Windows.Controls.TextBox { Text = _boundStrumKey.ToString(), Width = 90 };
        var sdp = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(strumBox, Dock.Right);
        sdp.Children.Add(strumBox);
        sdp.Children.Add(new TextBlock { Text = "Strum Key:", VerticalAlignment = VerticalAlignment.Center });
        sp.Children.Add(sdp);

        var save = new System.Windows.Controls.Button { Content = "Save Keybinds", Padding = new Thickness(14, 6, 14, 6), Background = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22)), Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        save.Click += (_, _) =>
        {
            var arr = new string[5];
            for (int i = 0; i < 5; i++)
            {
                if (Enum.TryParse<Key>(boxes[i].Text.Trim(), true, out var k)) { _boundFretKeys[i] = k; arr[i] = k.ToString(); }
                else arr[i] = _boundFretKeys[i].ToString();
            }
            if (Enum.TryParse<Key>(strumBox.Text.Trim(), true, out var sk)) _boundStrumKey = sk;
            AppConfig.Current.CustomKeyFrets = arr;
            AppConfig.Current.CustomKeyStrum = _boundStrumKey.ToString();
            AppConfig.Save();
            dlg.Close();
        };
        sp.Children.Add(save);
        dlg.Content = sp;
        dlg.ShowDialog();
    }

    private async void BuildSoloCutBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_projectDir) || _studioTools == null) return;
        double start = _loopASec ?? Math.Max(0, _currentTimeSec - 10.0);
        double end = _loopBSec ?? Math.Min(_chart.TotalDurationSeconds, start + 30.0);
        if (end <= start + 3.0)
        {
            WpfMessageBox.Show("Set Loop A and Loop B first (at least 3 seconds apart) to mark the solo you want to slice into a standalone GH3 song!", "Solo Extractor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        BuildSoloCutBtn.IsEnabled = false;
        BuildSoloCutBtn.Content = "Slicing...";
        StopPlayback();
        try
        {
            var secName = _chart.Sections.LastOrDefault(s => s.TimeSeconds <= start)?.Name ?? "Solo Cut";
            var (ok, msg) = await _studioTools.BuildPlayableSoloCutAsync(_projectDir, _songTitle, _songArtist, secName, start, end, autoCompile: true);
            if (ok) AudioWaveformAndSfxService.PlayCompilePowerChord();
            WpfMessageBox.Show(msg, ok ? "Playable Solo Cut Created!" : "Solo Extractor", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            if (ok) SongRecompiled?.Invoke();
        }
        finally
        {
            BuildSoloCutBtn.IsEnabled = true;
            BuildSoloCutBtn.Content = "✂️ Build Solo Cut";
        }
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
        _rockMeterVal = 75.0;
        _starPowerCharge = 0.0;
        _starPowerActive = false;
        _hasRecordedScoreThisRun = false;
        if (PostSongResultsOverlay != null) PostSongResultsOverlay.Visibility = Visibility.Collapsed;
        UpdatePlayHud();
    }

    private void UpdatePlayHud()
    {
        int baseMult = Math.Min(4, 1 + _streak / 10);
        int mult = _starPowerActive ? baseMult * 2 : baseMult;
        int total = _notesHit + _notesMissed;
        double acc = total > 0 ? (_notesHit * 100.0 / total) : 100.0;
        bool fc = _notesMissed == 0 && _notesHit > 0;
        PlayScoreText.Text = _score.ToString("N0");
        PlayStreakText.Text = _starPowerActive ? $"⚡ STAR POWER! Streak: {_streak} ({mult}x)" : $"Streak: {_streak} ({mult}x)";
        PlayAccuracyText.Text = $"{acc:F1}%{(fc ? "  •  ★ FC" : "")}";

        PlayRockMeterBar.Value = Math.Clamp(_rockMeterVal, 0, 100);
        if (_rockMeterVal >= 66) { PlayRockMeterLabel.Text = "ROCK: GREEN"; PlayRockMeterLabel.Foreground = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94)); PlayRockMeterBar.Foreground = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94)); }
        else if (_rockMeterVal >= 33) { PlayRockMeterLabel.Text = "ROCK: YELLOW"; PlayRockMeterLabel.Foreground = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8)); PlayRockMeterBar.Foreground = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8)); }
        else { PlayRockMeterLabel.Text = "ROCK: RED!"; PlayRockMeterLabel.Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)); PlayRockMeterBar.Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)); }

        PlaySpChargeBar.Value = Math.Clamp(_starPowerCharge, 0, 100);
        PlaySpChargeLabel.Text = _starPowerActive ? "ACTIVE!" : (_starPowerCharge >= 50 ? "READY!" : $"{(int)_starPowerCharge}%");
    }

    private void TryActivateStarPower()
    {
        if (_starPowerActive || _starPowerCharge < 50.0) return;
        _starPowerActive = true;
        AudioWaveformAndSfxService.PlayStarPowerChime();
        UpdatePlayHud();
    }

    private void OnTrackEnded()
    {
        SavePlayScoreIfMeaningful();
        bool showResults = _mode == "play" && (_notesHit + _notesMissed) >= 10;
        if (showResults)
        {
            int total = Math.Max(1, _notesHit + _notesMissed);
            double acc = _notesHit * 100.0 / total;
            bool fc = _notesMissed == 0 && _notesHit >= Math.Max(10, _activeNotes.Count - 2);
            int stars = acc >= 95 ? 5 : acc >= 88 ? 4 : acc >= 75 ? 3 : acc >= 60 ? 2 : 1;
            ResultsHeadlineText.Text = fc ? "👑 FULL COMBO! YOU ROCK!" : "★ YOU ROCK! ★";
            ResultsStarsText.Text = string.Concat(Enumerable.Repeat("★", stars)) + string.Concat(Enumerable.Repeat("☆", 5 - stars));
            ResultsScoreSummary.Text = $"Score: {_score:N0}  •  Accuracy: {acc:F1}%  •  Max Streak: {_maxStreak}";
            ResultsDetailText.Text = $"Notes Hit: {_notesHit:N0} / {total:N0}  •  Missed: {_notesMissed:N0}";
            PostSongResultsOverlay.Visibility = Visibility.Visible;
            if (fc)
            {
                AudioWaveformAndSfxService.PlayFcFanfare();
                AppConfig.Current.LifetimeStats.TotalFullCombos++;
                AppConfig.UnlockTrophy("FirstFullCombo");
                AppConfig.Save();
            }
        }
        StopPlayback(keepResultsVisible: showResults);
    }

    private void ResultsReplayBtn_Click(object sender, RoutedEventArgs e)
    {
        PostSongResultsOverlay.Visibility = Visibility.Collapsed;
        ResetPlayStats();
        _currentTimeSec = _loopASec ?? 0;
        PlayPauseBtn_Click(sender, e);
    }

    private void ResultsCloseBtn_Click(object sender, RoutedEventArgs e) => PostSongResultsOverlay.Visibility = Visibility.Collapsed;

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

    private void SavePlayScoreIfMeaningful()
    {
        if (_hasRecordedScoreThisRun || _mode != "play" || string.IsNullOrWhiteSpace(_songId) || _notesHit < 15) return;
        int total = Math.Max(1, _notesHit + _notesMissed);
        double pct = _notesHit * 100.0 / total;
        bool isFc = _notesMissed == 0 && _notesHit >= Math.Max(15, _activeNotes.Count - 2);
        var diff = DifficultyCombo.SelectedItem?.ToString() ?? "Expert";
        AppConfig.Current.LifetimeStats.TotalNotesHit += _notesHit;
        if (AppConfig.RecordHighScore(_songId, _score, pct, _maxStreak, isFc, diff))
        {
            _hasRecordedScoreThisRun = true;
            ScoreRecorded?.Invoke();
            if (isFc) RockHallOfFameService.UnlockAchievement("FC_MASTER");
            if (_score >= 1_000_000) RockHallOfFameService.UnlockAchievement("ONE_MILLION_CLUB");
        }
    }

    // ==================== PLAYABLE INPUT (KEYBOARD + DUALSENSE EDGE / PS / XBOX / GUITAR) ====================
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (_mode != "play") return;
        int fretHit = -1;
        for (int i = 0; i < 5; i++)
        {
            if (e.Key == _boundFretKeys[i]) { _keyFrets[i] = true; fretHit = i; }
        }
        if (e.Key == Key.RightShift)
        {
            TryActivateStarPower();
            return;
        }
        if (e.Key == _boundStrumKey || e.Key == Key.Enter || e.Key == Key.Up || e.Key == Key.Down)
        {
            e.Handled = true;
            TriggerStrumHitCheck();
            return;
        }

        if (fretHit >= 0 && !e.IsRepeat)
            TryHitSingleFretOrHopo(fretHit);
    }

    private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        for (int i = 0; i < 5; i++)
            if (e.Key == _boundFretKeys[i]) _keyFrets[i] = false;
    }

    private void PollPlayableController()
    {
        var snap = UnifiedControllerService.Poll();

        if (snap.Connected)
        {
            PlayControllerHint.Text = snap.EffectiveGamepadMode
                ? $"🎮 {snap.DeviceName} • {(snap.IsPlayStation ? "L2/L1/R1/R2/✕" : "LT/LB/RB/RT/A")} Auto-Strum"
                : $"🎸 {snap.DeviceName} • Guitar Strum Mode";
        }
        else
        {
            PlayControllerHint.Text = $"⌨️ Keys: {string.Join(" ", _boundFretKeys)} + {_boundStrumKey} • Plug in DualSense/Xbox/Guitar";
        }

        for (int i = 0; i < 5; i++)
            _heldFrets[i] = _keyFrets[i] || (snap.Connected && snap.Frets[i]);

        if (snap.Connected)
        {
            // Official GH3 Gamepad Mode: Auto-Strum on Fret Press edge (works seamlessly on DualSense Edge & Xbox pads!)
            if (snap.EffectiveGamepadMode)
            {
                bool anyFreshPress = false;
                for (int i = 0; i < 5; i++)
                {
                    if (snap.Frets[i] && !_prevPadFrets[i])
                    {
                        anyFreshPress = true;
                        TryHitSingleFretOrHopo(i);
                    }
                }
                // Also support chord auto-strum when 2+ frets are pressed together
                if (anyFreshPress && _heldFrets.Count(x => x) >= 2)
                    TriggerStrumHitCheck();
            }

            // Guitar Strum Bar OR optional D-Pad strum on gamepad
            bool strumNow = snap.StrumUp || snap.StrumDown;
            if (strumNow && !_prevPadStrum)
                TriggerStrumHitCheck();
            _prevPadStrum = strumNow;

            // Star Power Activation (Create / Share / Back / Guitar Tilt)
            if (snap.StarPowerPressed && !_prevPadSp)
                TryActivateStarPower();
            _prevPadSp = snap.StarPowerPressed;

            for (int i = 0; i < 5; i++) _prevPadFrets[i] = snap.Frets[i];
        }

        // Drain active Star Power smoothly (~12 seconds per full bar)
        if (_starPowerActive && _isPlaying)
        {
            _starPowerCharge -= 0.14;
            if (_starPowerCharge <= 0)
            {
                _starPowerCharge = 0;
                _starPowerActive = false;
            }
            UpdatePlayHud();
        }

        // Mark notes that passed well behind the strike line as missed
        double offsetSec = (_offsetShiftMs + AppConfig.Current.CalibrationOffsetMs) / 1000.0;
        foreach (var n in _activeNotes)
        {
            double t = n.TimeSeconds + offsetSec;
            if (t < _currentTimeSec - 0.14 && !_hitNotes.Contains(n) && !_missedNotes.Contains(n))
            {
                _missedNotes.Add(n);
                _notesMissed++;
                _streak = 0;
                _rockMeterVal = Math.Max(0, _rockMeterVal - 3.5);
                UpdatePlayHud();
            }
            if (t > _currentTimeSec + 0.2) break;
        }
    }

    private void TryHitSingleFretOrHopo(int fret)
    {
        if (!_isPlaying) return;
        double offsetSec = (_offsetShiftMs + AppConfig.Current.CalibrationOffsetMs) / 1000.0;
        var hit = _activeNotes.FirstOrDefault(n =>
            !_hitNotes.Contains(n) && !_missedNotes.Contains(n) &&
            n.Fret == fret &&
            Math.Abs((n.TimeSeconds + offsetSec) - _currentTimeSec) <= 0.11);
        if (hit != null) RegisterNoteHit(hit);
    }

    private void TriggerStrumHitCheck()
    {
        if (!_isPlaying) return;
        double offsetSec = (_offsetShiftMs + AppConfig.Current.CalibrationOffsetMs) / 1000.0;
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
        int baseMult = Math.Min(4, 1 + _streak / 10);
        int mult = _starPowerActive ? baseMult * 2 : baseMult;
        _score += 50 * mult;
        _rockMeterVal = Math.Min(100.0, _rockMeterVal + 2.0);
        if (n.InStarPower && !_starPowerActive)
            _starPowerCharge = Math.Min(100.0, _starPowerCharge + 8.5);
        if (n.Fret >= 0 && n.Fret <= 4) _hitFlashUntil[n.Fret] = _currentTimeSec + 0.16;
        else for (int i = 0; i < 5; i++) _hitFlashUntil[i] = _currentTimeSec + 0.16;
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
        _activeBassNotes = _chart.BassDifficulties.TryGetValue(diff, out var bassList) ? bassList
                         : (_chart.BassDifficulties.Values.FirstOrDefault() ?? new List<ChartPreviewNote>());
        ResetPlayStats();
        UpdateHeaderMeta();
        RenderCoachHeatmap();
        RenderHighwayFrame();
    }

    private double _peakSoloStartSec = 0;
    private double _peakSoloEndSec = 0;
    private double _peakSoloNps = 0;

    private void CoachHeatmapCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        RenderCoachHeatmap();
    }

    private void CoachLoopSoloBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_peakSoloEndSec > _peakSoloStartSec)
        {
            _loopASec = _peakSoloStartSec;
            _loopBSec = _peakSoloEndSec;
            UpdateLoopLabel();
            _currentTimeSec = _peakSoloStartSec;
            if (_hasAudio) _player.Position = TimeSpan.FromSeconds(_currentTimeSec);
            SeekSlider.Value = _currentTimeSec;
            RenderHighwayFrame();
        }
    }

    private void RenderCoachHeatmap()
    {
        if (CoachHeatmapCanvas == null || CoachHeatmapCanvas.ActualWidth <= 10) return;
        CoachHeatmapCanvas.Children.Clear();

        double totalDur = _chart.TotalDurationSeconds;
        if (totalDur <= 1.0 || _activeNotes.Count == 0) return;

        double canvasW = CoachHeatmapCanvas.ActualWidth;
        double canvasH = CoachHeatmapCanvas.ActualHeight;
        int buckets = (int)Math.Clamp(canvasW / 4.0, 32, 180);
        double bucketDuration = totalDur / buckets;
        int[] noteCounts = new int[buckets];

        foreach (var n in _activeNotes)
        {
            int b = (int)(n.TimeSeconds / bucketDuration);
            if (b >= 0 && b < buckets) noteCounts[b]++;
        }

        int maxInBucket = 1;
        int peakBucket = 0;
        for (int i = 0; i < buckets; i++)
        {
            if (noteCounts[i] > maxInBucket)
            {
                maxInBucket = noteCounts[i];
                peakBucket = i;
            }
        }

        double peakNps = bucketDuration > 0 ? (maxInBucket / bucketDuration) : 0;
        _peakSoloNps = peakNps;
        _peakSoloStartSec = Math.Max(0, (peakBucket - 1) * bucketDuration);
        _peakSoloEndSec = Math.Min(totalDur, (peakBucket + 3) * bucketDuration);

        if (CoachInsightText != null)
        {
            string startStr = $"{(int)(_peakSoloStartSec / 60)}:{(int)(_peakSoloStartSec % 60):D2}";
            CoachInsightText.Text = $"🤖 AI COACH: Peak shred solo at {startStr} ({peakNps:F1} NPS). Suggested Debounce: 25ms • Recommended: 75% speed.";
        }

        double sliceW = canvasW / buckets;
        for (int i = 0; i < buckets; i++)
        {
            double ratio = (double)noteCounts[i] / maxInBucket;
            var col = ratio switch
            {
                >= 0.75 => System.Windows.Media.Color.FromRgb(239, 68, 68),  // Red (Solo)
                >= 0.45 => System.Windows.Media.Color.FromRgb(249, 115, 22), // Orange (Fast)
                >= 0.20 => System.Windows.Media.Color.FromRgb(234, 179, 8),  // Yellow (Medium)
                _ => System.Windows.Media.Color.FromRgb(34, 197, 94)         // Green (Groove)
            };

            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = Math.Max(1.0, sliceW - 0.5),
                Height = canvasH,
                Fill = new SolidColorBrush(col),
                Opacity = Math.Clamp(0.35 + ratio * 0.65, 0.35, 1.0)
            };
            Canvas.SetLeft(rect, i * sliceW);
            CoachHeatmapCanvas.Children.Add(rect);
        }
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

    private void StopPlayback(bool keepResultsVisible = false)
    {
        _isPlaying = false;
        _currentTimeSec = _loopASec ?? 0;
        if (_hasAudio) { _player.Stop(); _player.Position = TimeSpan.FromSeconds(_currentTimeSec); }
        _fallbackClock.Reset();
        SeekSlider.Value = _currentTimeSec;
        PlayPauseBtn.Content = "▶ Play";
        if (_mode == "play" && !keepResultsVisible) ResetPlayStats();
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

        double offsetSec = (_offsetShiftMs + AppConfig.Current.CalibrationOffsetMs) / 1000.0;
        bool inSpNow = _starPowerActive || _chart.StarPower.Any(sp => (_currentTimeSec - offsetSec) >= sp.StartSec && (_currentTimeSec - offsetSec) <= sp.EndSec);
        StarPowerBadge.Visibility = inSpNow ? Visibility.Visible : Visibility.Collapsed;

        int npsTotal;
        if (_trackMode == "split")
        {
            double laneTopW = Math.Min(w * 0.105, 86.0);
            double laneBotW = Math.Min(w * 0.225, 195.0);
            int leftNps = DrawPerspectiveHighway(w * 0.27, h, laneTopW, laneBotW, _activeNotes, "🎸 LEAD GUITAR", inSpNow, isInteractivePrimary: true);
            int rightNps = DrawPerspectiveHighway(w * 0.73, h, laneTopW, laneBotW, _activeBassNotes,
                _activeBassNotes.Count > 0 ? "🎸 CO-OP BASS / RHYTHM" : "🎸 CO-OP BASS (NO CHART)", inSpNow, isInteractivePrimary: false);
            npsTotal = leftNps + rightNps;
        }
        else if (_trackMode == "bass")
        {
            double topHalfW = Math.Min(w * 0.16, 115.0);
            double botHalfW = Math.Min(w * 0.38, 280.0);
            npsTotal = DrawPerspectiveHighway(w * 0.5, h, topHalfW, botHalfW, _activeBassNotes,
                _activeBassNotes.Count > 0 ? "🎸 CO-OP BASS / RHYTHM" : "🎸 CO-OP BASS (NO BASS TRACK IN CHART)", inSpNow, isInteractivePrimary: true);
        }
        else
        {
            double topHalfW = Math.Min(w * 0.16, 115.0);
            double botHalfW = Math.Min(w * 0.38, 280.0);
            npsTotal = DrawPerspectiveHighway(w * 0.5, h, topHalfW, botHalfW, _activeNotes, null, inSpNow, isInteractivePrimary: true);
        }

        var activeSection = _chart.Sections.LastOrDefault(s => s.TimeSeconds <= (_currentTimeSec - offsetSec))?.Name ?? "Intro";
        var activeBpm = _chart.Tempos.LastOrDefault(t => t.TimeSeconds <= (_currentTimeSec - offsetSec))?.Bpm ?? 120.0;
        SectionPillText.Text = $"Section: {activeSection}";
        LiveStatsText.Text = $"BPM: {activeBpm:F1}  •  NPS: {npsTotal:F1}" + (_trackMode == "split" ? "  •  CO-OP SPLIT" : "");
    }

    private int DrawPerspectiveHighway(
        double cx,
        double h,
        double topHalfW,
        double botHalfW,
        List<ChartPreviewNote> notes,
        string? headerBadge,
        bool inSpNow,
        bool isInteractivePrimary)
    {
        double topY = 24.0;
        double strikeY = h - 54.0;
        double lookaheadSec = 1.65 / Math.Max(0.5, _speedMultiplier);
        double offsetSec = (_offsetShiftMs + AppConfig.Current.CalibrationOffsetMs) / 1000.0;
        double scaleFactor = Math.Clamp(botHalfW / 260.0, 0.58, 1.0);

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
            Stroke = new SolidColorBrush(inSpNow ? WpfColor.FromRgb(56, 189, 248) : (_mode == "edit" && isInteractivePrimary ? WpfColor.FromRgb(129, 140, 248) : WpfColor.FromRgb(71, 85, 105))),
            StrokeThickness = inSpNow || (_mode == "edit" && isInteractivePrimary) ? 2.5 : 2.0
        };
        HighwayCanvas.Children.Add(board);

        if (!string.IsNullOrWhiteSpace(headerBadge))
        {
            var lbl = new TextBlock
            {
                Text = headerBadge,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(251, 146, 60)),
                TextAlignment = TextAlignment.Center,
                Width = topHalfW * 2.4
            };
            Canvas.SetLeft(lbl, cx - topHalfW * 1.2);
            Canvas.SetTop(lbl, 4);
            HighwayCanvas.Children.Add(lbl);
        }

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

        // Strike line & fret targets
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
            bool held = isInteractivePrimary && _mode == "play" && _heldFrets[f];
            bool flash = isInteractivePrimary && _mode == "play" && _currentTimeSec < _hitFlashUntil[f];

            if (flash)
            {
                double bw = 58 * scaleFactor, bh = 30 * scaleFactor;
                var burst = new Ellipse
                {
                    Width = bw, Height = bh,
                    Fill = new SolidColorBrush(WpfColor.FromArgb(115, 255, 255, 255)),
                    Stroke = new SolidColorBrush(FretColors[f]),
                    StrokeThickness = 2.5
                };
                Canvas.SetLeft(burst, lx - bw * 0.5);
                Canvas.SetTop(burst, strikeY - bh * 0.5);
                HighwayCanvas.Children.Add(burst);
            }

            double tw = 42 * scaleFactor, th = 20 * scaleFactor;
            var target = new Ellipse
            {
                Width = tw, Height = th,
                Fill = new SolidColorBrush(held ? FretColors[f] : WpfColor.FromArgb(50, FretColors[f].R, FretColors[f].G, FretColors[f].B)),
                Stroke = new SolidColorBrush(held ? WpfColor.FromRgb(255, 255, 255) : FretColors[f]),
                StrokeThickness = held ? 3.0 : 2.4
            };
            Canvas.SetLeft(target, lx - tw * 0.5);
            Canvas.SetTop(target, strikeY - th * 0.5);
            HighwayCanvas.Children.Add(target);
        }

        // Notes & Sustain Tails
        int notesInLastSec = 0;
        var visibleNotes = new List<ChartPreviewNote>();
        foreach (var n in notes)
        {
            double tStart = n.TimeSeconds + offsetSec;
            double tEnd = n.EndTimeSeconds + offsetSec;
            if (tStart >= _currentTimeSec - 0.5 && tStart <= _currentTimeSec + 0.5) notesInLastSec++;
            if (tEnd < minVisible) continue;
            if (tStart > maxVisible) break;
            if (isInteractivePrimary && _mode == "play" && _hitNotes.Contains(n)) continue;
            visibleNotes.Add(n);
        }

        for (int idx = visibleNotes.Count - 1; idx >= 0; idx--)
        {
            var n = visibleNotes[idx];
            double tStart = n.TimeSeconds + offsetSec;
            double tEnd = n.EndTimeSeconds + offsetSec;
            double normStart = Math.Clamp(1.0 - ((tStart - _currentTimeSec) / lookaheadSec), 0.0, 1.12);
            double yStart = ProjectY(normStart);
            double scale = (0.35 + 0.65 * Math.Pow(normStart, 1.3)) * scaleFactor;

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

        return notesInLastSec;
    }
}
