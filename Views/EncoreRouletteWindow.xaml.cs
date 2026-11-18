using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

using WpfColor = System.Windows.Media.Color;

namespace GH3_Sync.Views;

public partial class EncoreRouletteWindow : Window
{
    private readonly List<CustomSongInfo> _pool;
    private readonly Action<CustomSongInfo> _onPreview;
    private readonly Action<CustomSongInfo> _onHighway;
    private readonly DispatcherTimer _spinTimer = new();
    private int _ticksRemaining;
    private int _currentIndex;
    private CustomSongInfo? _winner;

    public EncoreRouletteWindow(List<CustomSongInfo> pool, Action<CustomSongInfo> onPreview, Action<CustomSongInfo> onHighway)
    {
        InitializeComponent();
        _pool = pool;
        _onPreview = onPreview;
        _onHighway = onHighway;
        RoulettePoolLabel.Text = $"Drawing from {_pool.Count} custom song(s)";
        _spinTimer.Tick += SpinTimer_Tick;
        Loaded += (_, _) => StartSpin();
        Closing += (_, _) => _spinTimer.Stop();
    }

    private void StartSpin()
    {
        if (_pool.Count == 0) return;
        _winner = null;
        PreviewWinnerBtn.IsEnabled = false;
        HighwayWinnerBtn.IsEnabled = false;
        SpinAgainBtn.IsEnabled = false;
        ReelFrame.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22));

        _currentIndex = Random.Shared.Next(_pool.Count);
        _ticksRemaining = AppConfig.Current.EnableUiAnimations ? 26 : 1;
        _spinTimer.Interval = TimeSpan.FromMilliseconds(45);
        _spinTimer.Start();
    }

    private void SpinTimer_Tick(object? sender, EventArgs e)
    {
        _currentIndex = (_currentIndex + 1) % _pool.Count;
        var prev = _pool[(_currentIndex - 1 + _pool.Count) % _pool.Count];
        var curr = _pool[_currentIndex];
        var next = _pool[(_currentIndex + 1) % _pool.Count];

        PrevSlotText.Text = $"{prev.Title} — {prev.Artist}";
        SelectedTitleText.Text = curr.Title;
        SelectedArtistText.Text = curr.Artist;
        NextSlotText.Text = $"{next.Title} — {next.Artist}";

        _ticksRemaining--;
        if (_ticksRemaining <= 0)
        {
            _spinTimer.Stop();
            _winner = curr;
            ReelFrame.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
            SpinAgainBtn.IsEnabled = true;
            PreviewWinnerBtn.IsEnabled = !string.IsNullOrEmpty(Gh3Service.FindSongAudioPath(curr));
            HighwayWinnerBtn.IsEnabled = !string.IsNullOrEmpty(ChartDoctorService.FindChartFileInProject(curr.ProjectDirectory));
            return;
        }

        // Smooth slot-machine deceleration curve
        if (_ticksRemaining < 12)
            _spinTimer.Interval = TimeSpan.FromMilliseconds(45 + (12 - _ticksRemaining) * 24);
    }

    private void SpinAgainBtn_Click(object sender, RoutedEventArgs e) => StartSpin();
    private void PreviewWinnerBtn_Click(object sender, RoutedEventArgs e) { if (_winner != null) _onPreview(_winner); }
    private void HighwayWinnerBtn_Click(object sender, RoutedEventArgs e) { if (_winner != null) { Close(); _onHighway(_winner); } }
    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
