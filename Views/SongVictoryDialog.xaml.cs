using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;

namespace GH3_Sync.Views;

public partial class SongVictoryDialog : Window
{
    public Action? OnPlayInGh3 { get; set; }
    public Action? OnHighwayPreview { get; set; }
    public Action? OnAudioPreview { get; set; }

    public SongVictoryDialog(
        string title,
        string artist,
        string? album = null,
        string? year = null,
        int bpm = 120,
        int noteCount = 0,
        string? duration = null,
        byte[]? artBytes = null,
        string? artPath = null,
        bool hasEasy = true,
        bool hasMedium = true,
        bool hasHard = true,
        bool hasExpert = true)
    {
        InitializeComponent();

        TxtSongTitle.Text = string.IsNullOrWhiteSpace(title) ? "Custom Song" : title;
        TxtArtist.Text = string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist;
        
        string meta = "";
        if (!string.IsNullOrWhiteSpace(album)) meta += album;
        if (!string.IsNullOrWhiteSpace(year)) meta += (meta.Length > 0 ? " • " : "") + year;
        if (string.IsNullOrWhiteSpace(meta)) meta = "Guitar Hero III Custom";
        TxtAlbumYear.Text = meta;

        TxtBpm.Text = bpm > 0 ? bpm.ToString() : "120";
        TxtNoteCount.Text = noteCount > 0 ? $"{noteCount:N0}" : "—";
        TxtDuration.Text = !string.IsNullOrWhiteSpace(duration) ? duration : "—";

        // Badges opacity based on chart existence
        BadgeEasy.Opacity = hasEasy ? 1.0 : 0.25;
        BadgeMedium.Opacity = hasMedium ? 1.0 : 0.25;
        BadgeHard.Opacity = hasHard ? 1.0 : 0.25;
        BadgeExpert.Opacity = hasExpert ? 1.0 : 0.25;

        // Load album artwork
        try
        {
            if (artBytes != null && artBytes.Length > 0)
            {
                var bmp = new BitmapImage();
                using var ms = new MemoryStream(artBytes);
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                ArtImage.Source = bmp;
            }
            else if (!string.IsNullOrWhiteSpace(artPath) && File.Exists(artPath))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(artPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                ArtImage.Source = bmp;
            }
        }
        catch { }

        Loaded += (_, _) =>
        {
            // Pop-in bounce
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.25 };
            var anim = new DoubleAnimation(0.88, 1.0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
            VictoryCard.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            var transform = new System.Windows.Media.ScaleTransform(0.88, 0.88);
            VictoryCard.RenderTransform = transform;
            transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, anim);
            transform.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, anim);

            // Play FC Fanfare
            try { AudioWaveformAndSfxService.PlayFcFanfare(); } catch { }
        };
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AudioPreview_Click(object sender, RoutedEventArgs e)
    {
        OnAudioPreview?.Invoke();
    }

    private void HighwayPreview_Click(object sender, RoutedEventArgs e)
    {
        OnHighwayPreview?.Invoke();
    }

    private void LaunchGh3_Click(object sender, RoutedEventArgs e)
    {
        Close();
        OnPlayInGh3?.Invoke();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape || e.Key == Key.Enter)
        {
            Close();
            e.Handled = true;
        }
    }

    public static void ShowVictory(
        Window? owner,
        string title,
        string artist,
        string? album = null,
        string? year = null,
        int bpm = 120,
        int noteCount = 0,
        string? duration = null,
        byte[]? artBytes = null,
        string? artPath = null,
        Action? onPlayInGh3 = null,
        Action? onHighwayPreview = null,
        Action? onAudioPreview = null)
    {
        if (WpfApplication.Current != null && !WpfApplication.Current.Dispatcher.CheckAccess())
        {
            WpfApplication.Current.Dispatcher.Invoke(() => ShowVictory(owner, title, artist, album, year, bpm, noteCount, duration, artBytes, artPath, onPlayInGh3, onHighwayPreview, onAudioPreview));
            return;
        }

        try
        {
            var dlg = new SongVictoryDialog(title, artist, album, year, bpm, noteCount, duration, artBytes, artPath);
            dlg.OnPlayInGh3 = onPlayInGh3;
            dlg.OnHighwayPreview = onHighwayPreview;
            dlg.OnAudioPreview = onAudioPreview;

            var targetOwner = owner ?? WpfApplication.Current?.MainWindow;
            if (targetOwner != null && targetOwner.IsVisible && targetOwner.WindowState != WindowState.Minimized)
            {
                dlg.Owner = targetOwner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            else
            {
                dlg.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            }

            dlg.ShowDialog();
        }
        catch
        {
            GuitarHeroModalDialog.Show(owner, $"Song compiled successfully!\n\n{artist} - {title}", "Song Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
