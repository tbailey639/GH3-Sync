using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace GH3_Sync.Views;

public partial class SplashWindow : Window
{
    private readonly Ellipse[] _gems;

    public SplashWindow()
    {
        InitializeComponent();
        _gems = new[] { GemGreen, GemRed, GemYellow, GemBlue, GemOrange };
        Loaded += SplashWindow_Loaded;
    }

    private void SplashWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Smooth rock emblem pop-in animation
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 };
        var sx = new DoubleAnimation(0.72, 1.0, TimeSpan.FromMilliseconds(550)) { EasingFunction = ease };
        var sy = new DoubleAnimation(0.72, 1.0, TimeSpan.FromMilliseconds(550)) { EasingFunction = ease };
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, sx);
        LogoScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, sy);

        // Sequenced 5-fret Star Power highway wave!
        for (int i = 0; i < _gems.Length; i++)
        {
            var pulse = new DoubleAnimation
            {
                From = 0.25,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(360),
                BeginTime = TimeSpan.FromMilliseconds(i * 110),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            _gems[i].BeginAnimation(OpacityProperty, pulse);
        }
    }

    public void UpdateProgress(int percent, string message)
    {
        Dispatcher.Invoke(() =>
        {
            SplashProgress.Value = Math.Clamp(percent, 0, 100);
            StatusText.Text = message;
        });
    }

    public async Task FadeOutAndCloseAsync()
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.Invoke(() =>
        {
            var fade = new DoubleAnimation(1.0, 0.0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) =>
            {
                Close();
                tcs.TrySetResult(true);
            };
            BeginAnimation(OpacityProperty, fade);
        });
        await tcs.Task;
    }
}
