using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WpfApplication = System.Windows.Application;
using WpfPoint = System.Windows.Point;
using MediaColor = System.Windows.Media.Color;

namespace GH3_Sync.Views;

public partial class GuitarHeroModalDialog : Window
{
    public MessageBoxResult Result { get; private set; } = MessageBoxResult.None;
    private readonly MessageBoxButton _buttonMode;

    public GuitarHeroModalDialog(string message, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        InitializeComponent();
        _buttonMode = button;

        DialogCaptionText.Text = string.IsNullOrWhiteSpace(caption) ? "GUITAR HERO III" : caption.ToUpperInvariant();
        DialogMessageText.Text = message ?? string.Empty;

        ConfigureVisuals(caption, icon, button);

        Loaded += (_, _) =>
        {
            // Pop-in bounce animation
            var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.3 };
            var anim = new DoubleAnimation(0.85, 1.0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            OuterBorder.RenderTransformOrigin = new WpfPoint(0.5, 0.5);
            var transform = new ScaleTransform(0.85, 0.85);
            OuterBorder.RenderTransform = transform;
            transform.BeginAnimation(ScaleTransform.ScaleXProperty, anim);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, anim);

            // Play SFX
            try
            {
                if (icon == MessageBoxImage.Error || icon == MessageBoxImage.Warning)
                {
                    AudioWaveformAndSfxService.PlayNoteMissClunk();
                }
                else
                {
                    AudioWaveformAndSfxService.PlayStarPowerChime();
                }
            }
            catch { }
        };
    }

    private void ConfigureVisuals(string caption, MessageBoxImage icon, MessageBoxButton button)
    {
        MediaColor themeColor;
        string glyph;

        switch (icon)
        {
            case MessageBoxImage.Error:
                themeColor = MediaColor.FromRgb(239, 68, 68); // Red / Crimson
                glyph = "💥";
                break;
            case MessageBoxImage.Warning:
                themeColor = MediaColor.FromRgb(245, 158, 11); // Amber / Flame
                glyph = "⚠️";
                break;
            case MessageBoxImage.Question:
                themeColor = MediaColor.FromRgb(59, 130, 246); // Electric Blue
                glyph = "🎸";
                break;
            case MessageBoxImage.Information:
            default:
                themeColor = MediaColor.FromRgb(16, 185, 129); // Star Power Green / Teal
                glyph = "⚡";
                break;
        }

        // Apply theme color to border & glow
        var brush = new SolidColorBrush(themeColor);
        OuterBorder.BorderBrush = brush;
        BorderGlow.Color = themeColor;
        IconBadgeBorder.BorderBrush = brush;
        IconGlyphText.Text = glyph;

        // Configure buttons
        switch (button)
        {
            case MessageBoxButton.OK:
                BtnPrimary.Content = "🎸 ROCK ON!";
                BtnPrimary.Visibility = Visibility.Visible;
                BtnSecondary.Visibility = Visibility.Collapsed;
                BtnCancel.Visibility = Visibility.Collapsed;
                break;

            case MessageBoxButton.OKCancel:
                BtnPrimary.Content = "🎸 ROCK ON!";
                BtnPrimary.Visibility = Visibility.Visible;
                BtnSecondary.Visibility = Visibility.Collapsed;
                BtnCancel.Content = "✕ CANCEL";
                BtnCancel.Visibility = Visibility.Visible;
                break;

            case MessageBoxButton.YesNo:
                BtnPrimary.Content = "🎸 YES";
                BtnPrimary.Visibility = Visibility.Visible;
                BtnSecondary.Content = "✕ NO";
                BtnSecondary.Visibility = Visibility.Visible;
                BtnCancel.Visibility = Visibility.Collapsed;
                break;

            case MessageBoxButton.YesNoCancel:
                BtnPrimary.Content = "🎸 YES";
                BtnPrimary.Visibility = Visibility.Visible;
                BtnSecondary.Content = "✕ NO";
                BtnSecondary.Visibility = Visibility.Visible;
                BtnCancel.Content = "CANCEL";
                BtnCancel.Visibility = Visibility.Visible;
                break;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_buttonMode == MessageBoxButton.YesNo)
            Result = MessageBoxResult.No;
        else if (_buttonMode == MessageBoxButton.OK)
            Result = MessageBoxResult.OK;
        else
            Result = MessageBoxResult.Cancel;

        Close();
    }

    private void BtnPrimary_Click(object sender, RoutedEventArgs e)
    {
        try { AudioWaveformAndSfxService.PlayCompilePowerChord(); } catch { }
        Result = (_buttonMode == MessageBoxButton.YesNo || _buttonMode == MessageBoxButton.YesNoCancel)
            ? MessageBoxResult.Yes
            : MessageBoxResult.OK;
        Close();
    }

    private void BtnSecondary_Click(object sender, RoutedEventArgs e)
    {
        try { AudioWaveformAndSfxService.PlayNoteMissClunk(); } catch { }
        Result = MessageBoxResult.No;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        try { AudioWaveformAndSfxService.PlayNoteMissClunk(); } catch { }
        Result = MessageBoxResult.Cancel;
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            BtnPrimary_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseButton_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    // =========================================================================
    // STATIC SHOW OVERLOADS (Drop-in replacement for System.Windows.MessageBox.Show)
    // =========================================================================

    public static MessageBoxResult Show(string messageBoxText)
    {
        return Show(null, messageBoxText, "Guitar Hero III", MessageBoxButton.OK, MessageBoxImage.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption)
    {
        return Show(null, messageBoxText, caption, MessageBoxButton.OK, MessageBoxImage.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button)
    {
        return Show(null, messageBoxText, caption, button, MessageBoxImage.None);
    }

    public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
    {
        return Show(null, messageBoxText, caption, button, icon);
    }

    public static MessageBoxResult Show(Window? owner, string messageBoxText, string caption = "Guitar Hero III",
        MessageBoxButton button = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None)
    {
        if (WpfApplication.Current != null && !WpfApplication.Current.Dispatcher.CheckAccess())
        {
            return WpfApplication.Current.Dispatcher.Invoke(() => Show(owner, messageBoxText, caption, button, icon));
        }

        try
        {
            var dlg = new GuitarHeroModalDialog(messageBoxText, caption, button, icon);
            
            // Set owner cleanly
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
            return dlg.Result;
        }
        catch
        {
            // Fallback in case of XAML or dispatcher constraint
            return System.Windows.MessageBox.Show(messageBoxText, caption, button, icon);
        }
    }
}
