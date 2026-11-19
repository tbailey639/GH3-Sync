using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace GH3_Sync.Views;

public partial class StageHudProgressControl : System.Windows.Controls.UserControl
{
    private readonly Ellipse[] _gems;
    private bool _isAnimating = false;

    public StageHudProgressControl()
    {
        InitializeComponent();
        _gems = new[] { Gem1, Gem2, Gem3, Gem4, Gem5 };
    }

    public void Report(int percent, string headline, string? detail = null)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Report(percent, headline, detail));
            return;
        }

        Visibility = Visibility.Visible;
        ProgressBarControl.Value = Math.Clamp(percent, 0, 100);
        TxtPercent.Text = $"{ProgressBarControl.Value:0}%";

        if (!string.IsNullOrWhiteSpace(headline))
            TxtHeadline.Text = headline.ToUpperInvariant();

        if (detail != null)
            TxtDetail.Text = detail;

        StartGemWave();
    }

    public void Reset()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(Reset);
            return;
        }

        ProgressBarControl.Value = 0;
        TxtPercent.Text = "0%";
        TxtHeadline.Text = "STAGE HUD READY";
        TxtDetail.Text = "Standing by...";
        StopGemWave();
        Visibility = Visibility.Collapsed;
    }

    public void Finish(string message)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => Finish(message));
            return;
        }

        ProgressBarControl.Value = 100;
        TxtPercent.Text = "100%";
        TxtHeadline.Text = "COMPLETE";
        TxtDetail.Text = message;
        StopGemWave();

        // Brief delay before fading or resetting if needed
    }

    public void SetError(string headline, string errorDetail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetError(headline, errorDetail));
            return;
        }

        Visibility = Visibility.Visible;
        TxtHeadline.Text = headline.ToUpperInvariant();
        TxtDetail.Text = errorDetail;
        HudContainer.BorderBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 68, 68));
        HudGlow.Color = System.Windows.Media.Color.FromRgb(239, 68, 68);
        StopGemWave();
    }

    private void StartGemWave()
    {
        if (_isAnimating) return;
        _isAnimating = true;

        for (int i = 0; i < _gems.Length; i++)
        {
            var anim = new DoubleAnimation
            {
                From = 0.2,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(320),
                BeginTime = TimeSpan.FromMilliseconds(i * 90),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            _gems[i].BeginAnimation(OpacityProperty, anim);
        }
    }

    private void StopGemWave()
    {
        _isAnimating = false;
        for (int i = 0; i < _gems.Length; i++)
        {
            _gems[i].BeginAnimation(OpacityProperty, null);
            _gems[i].Opacity = 0.85;
        }
    }
}
