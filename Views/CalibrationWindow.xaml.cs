using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

using WpfColor = System.Windows.Media.Color;

namespace GH3_Sync.Views;

public partial class CalibrationWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(10) };
    private readonly Stopwatch _sw = new();
    private readonly List<double> _measuredOffsetsMs = new();
    private const double BeatIntervalSec = 0.5; // 120 BPM
    private const int TotalBeats = 12;
    private bool _running;
    private int _lastClickedBeat = -1;
    private uint _prevRawMask;
    private bool _prevStrum;
    private double _flashUntilSec = -1;
    public int MeasuredCalibrationMs { get; private set; }

    public CalibrationWindow()
    {
        InitializeComponent();
        MeasuredCalibrationMs = AppConfig.Current.CalibrationOffsetMs;
        CalibResultText.Text = $"Current Saved Calibration: {MeasuredCalibrationMs:+0;-0;0} ms";

        _timer.Tick += Timer_Tick;
        Loaded += (_, _) => RenderBeatTrack(0);
        Closing += (_, _) => _timer.Stop();
    }

    private void StartCalibBtn_Click(object sender, RoutedEventArgs e)
    {
        _measuredOffsetsMs.Clear();
        _lastClickedBeat = -1;
        _running = true;
        ApplyCalibBtn.IsEnabled = false;
        StartCalibBtn.IsEnabled = false;
        CalibStatusText.Text = "Listen & tap SPACE or ANY Controller Fret on each beat! (First 2 beats are count-in)";
        _sw.Restart();
        _timer.Start();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_running || e.IsRepeat) return;
        if (e.Key == Key.Space || e.Key == Key.Enter || e.Key == Key.A || e.Key == Key.S || e.Key == Key.J || e.Key == Key.K || e.Key == Key.L)
        {
            e.Handled = true;
            RecordTap();
        }
    }

    private void RecordTap()
    {
        double nowSec = _sw.Elapsed.TotalSeconds;
        int nearestBeat = (int)Math.Round(nowSec / BeatIntervalSec);
        if (nearestBeat < 2 || nearestBeat > TotalBeats + 1) return; // skip count-in

        double deltaMs = (nowSec - nearestBeat * BeatIntervalSec) * 1000.0;
        if (Math.Abs(deltaMs) <= 220.0)
        {
            _measuredOffsetsMs.Add(deltaMs);
            _flashUntilSec = nowSec + 0.12;
            CalibStatusText.Text = $"Captured {_measuredOffsetsMs.Count} tap(s)  •  Last delta: {deltaMs:+0;-0;0} ms";
        }
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        double nowSec = _sw.Elapsed.TotalSeconds;
        int curBeat = (int)Math.Floor(nowSec / BeatIntervalSec);

        // Poll DualSense Edge / Xbox / Guitar taps too!
        var snap = UnifiedControllerService.Poll();
        if (snap.Connected)
        {
            bool fretEdge = (snap.RawButtonsMask & ~_prevRawMask) != 0;
            bool strumNow = snap.StrumUp || snap.StrumDown;
            if (fretEdge || (strumNow && !_prevStrum))
                RecordTap();
            _prevRawMask = snap.RawButtonsMask;
            _prevStrum = strumNow;
        }

        if (curBeat > _lastClickedBeat && curBeat <= TotalBeats + 1)
        {
            _lastClickedBeat = curBeat;
            AudioWaveformAndSfxService.PlayMetronomeClick();
        }

        RenderBeatTrack(nowSec);

        if (nowSec > (TotalBeats + 2) * BeatIntervalSec)
        {
            _running = false;
            _timer.Stop();
            StartCalibBtn.IsEnabled = true;

            if (_measuredOffsetsMs.Count >= 4)
            {
                var sorted = _measuredOffsetsMs.OrderBy(x => x).ToList();
                double median = sorted[sorted.Count / 2];
                MeasuredCalibrationMs = (int)Math.Clamp(Math.Round(median), -200, 200);
                CalibStatusText.Text = $"✓ Calibration Complete! Recommended Offset: {MeasuredCalibrationMs:+0;-0;0} ms ({_measuredOffsetsMs.Count} samples)";
                CalibResultText.Text = $"Apply to Highway Previewer & BetterGH3 Calibration reference: {MeasuredCalibrationMs:+0;-0;0} ms";
                ApplyCalibBtn.IsEnabled = true;
            }
            else
            {
                CalibStatusText.Text = "Too few taps recorded — click Start Calibration and tap along with the clicks!";
            }
        }
    }

    private void RenderBeatTrack(double nowSec)
    {
        double w = Math.Max(400, CalibCanvas.ActualWidth);
        double h = Math.Max(140, CalibCanvas.ActualHeight);
        CalibCanvas.Children.Clear();

        double targetX = w * 0.25;
        double cy = h * 0.5;

        // Horizontal highway track line
        CalibCanvas.Children.Add(new Line { X1 = 0, Y1 = cy, X2 = w, Y2 = cy, Stroke = new SolidColorBrush(WpfColor.FromRgb(30, 41, 59)), StrokeThickness = 3 });

        // Strike zone circle
        bool flash = _running && nowSec < _flashUntilSec;
        var strikeRing = new Ellipse
        {
            Width = flash ? 48 : 40,
            Height = flash ? 48 : 40,
            Fill = new SolidColorBrush(flash ? WpfColor.FromArgb(140, 34, 197, 94) : WpfColor.FromArgb(40, 249, 115, 22)),
            Stroke = new SolidColorBrush(flash ? WpfColor.FromRgb(34, 197, 94) : WpfColor.FromRgb(249, 115, 22)),
            StrokeThickness = 3
        };
        Canvas.SetLeft(strikeRing, targetX - strikeRing.Width * 0.5);
        Canvas.SetTop(strikeRing, cy - strikeRing.Height * 0.5);
        CalibCanvas.Children.Add(strikeRing);

        if (!_running) return;

        // Scrolling beat gems
        double pxPerSec = 360.0;
        for (int b = 0; b <= TotalBeats + 2; b++)
        {
            double beatSec = b * BeatIntervalSec;
            double x = targetX + (beatSec - nowSec) * pxPerSec;
            if (x < -25 || x > w + 25) continue;

            var gem = new Ellipse
            {
                Width = 24,
                Height = 24,
                Fill = new SolidColorBrush(b < 2 ? WpfColor.FromRgb(100, 116, 139) : WpfColor.FromRgb(56, 189, 248)),
                Stroke = System.Windows.Media.Brushes.White,
                StrokeThickness = 2
            };
            Canvas.SetLeft(gem, x - 12);
            Canvas.SetTop(gem, cy - 12);
            CalibCanvas.Children.Add(gem);
        }
    }

    private void ApplyCalibBtn_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.CalibrationOffsetMs = MeasuredCalibrationMs;
        AppConfig.Save();
        DialogResult = true;
        Close();
    }

    private void ResetCalibBtn_Click(object sender, RoutedEventArgs e)
    {
        MeasuredCalibrationMs = 0;
        AppConfig.Current.CalibrationOffsetMs = 0;
        AppConfig.Save();
        CalibResultText.Text = "Current Saved Calibration: 0 ms (Reset)";
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
