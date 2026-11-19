using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GH3_Sync.Views;

public partial class VintageToggleSwitch : System.Windows.Controls.UserControl
{
    public static readonly DependencyProperty IsCheckedProperty =
        DependencyProperty.Register(nameof(IsChecked), typeof(bool), typeof(VintageToggleSwitch),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnIsCheckedChangedStatic));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(VintageToggleSwitch),
            new PropertyMetadata("CHROME TOGGLE", (d, e) => ((VintageToggleSwitch)d).TxtLabel.Text = (string)e.NewValue));

    public event RoutedPropertyChangedEventHandler<bool>? CheckedChanged;

    public VintageToggleSwitch()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisualState(animate: false);
    }

    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    public string Label
    {
        get => (string)GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    private static void OnIsCheckedChangedStatic(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is VintageToggleSwitch sw)
        {
            sw.UpdateVisualState(animate: true);
            sw.CheckedChanged?.Invoke(sw, new RoutedPropertyChangedEventArgs<bool>((bool)e.OldValue, (bool)e.NewValue));
        }
    }

    private void UpdateVisualState(bool animate)
    {
        double targetAngle = IsChecked ? -28.0 : 28.0;

        if (animate)
        {
            var anim = new DoubleAnimation
            {
                To = targetAngle,
                Duration = TimeSpan.FromMilliseconds(120),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            LeverRotation.BeginAnimation(RotateTransform.AngleProperty, anim);
        }
        else
        {
            LeverRotation.Angle = targetAngle;
        }

        if (IsChecked)
        {
            JewelLamp.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(245, 158, 11)); // Amber Gold ON
            JewelLamp.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(251, 191, 36));
            JewelGlow.Opacity = 0.9;
            TxtStatus.Text = "ENGAGED / ON";
            TxtStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(34, 197, 94));
        }
        else
        {
            JewelLamp.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(69, 26, 3)); // Dim OFF
            JewelLamp.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(120, 53, 15));
            JewelGlow.Opacity = 0.0;
            TxtStatus.Text = "DISENGAGED / OFF";
            TxtStatus.Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
        }
    }

    private void OnToggleClick(object sender, MouseButtonEventArgs e)
    {
        IsChecked = !IsChecked;
        try
        {
            // Play authentic procedural mechanical switch click sound
            System.Media.SystemSounds.Asterisk.Play();
        }
        catch { }
    }
}
