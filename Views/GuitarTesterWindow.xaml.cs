using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using WpfColor = System.Windows.Media.Color;

namespace GH3_Sync.Views;

public partial class GuitarTesterWindow : Window
{
    private readonly DispatcherTimer _pollTimer;
    private readonly bool[] _keyFrets = new bool[5];
    private bool _keyStrumUp;
    private bool _keyStrumDown;
    private int _bindingFretIndex = -1;

    public GuitarTesterWindow()
    {
        InitializeComponent();

        LayoutModeCombo.SelectedIndex = (AppConfig.Current.CustomControllerMode ?? "Auto").ToLowerInvariant() switch
        {
            "gamepad" => 1,
            "guitar" => 2,
            _ => 0
        };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pollTimer.Tick += (_, _) => PollInputs();
        Loaded += (_, _) => _pollTimer.Start();
        Closing += (_, _) => _pollTimer.Stop();
    }

    private void LayoutModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || LayoutModeCombo == null) return;
        AppConfig.Current.CustomControllerMode = LayoutModeCombo.SelectedIndex switch
        {
            1 => "Gamepad",
            2 => "Guitar",
            _ => "Auto"
        };
        AppConfig.Save();
    }

    private void FretBox_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is Border b && b.Tag is string idxStr && int.TryParse(idxStr, out int idx))
        {
            _bindingFretIndex = idx;
            string[] names = { "GREEN", "RED", "YELLOW", "BLUE", "ORANGE" };
            BindHintText.Text = $"  PRESS ANY BUTTON on your controller to bind {names[idx]}...";
            BindHintText.Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 224, 71));
        }
    }

    private void ResetPadBindsBtn_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.CustomPadButtonIndices = new[] { -1, -1, -1, -1, -1 };
        AppConfig.Save();
        _bindingFretIndex = -1;
        BindHintText.Text = "  Custom controller binds cleared (Official GH3 defaults active).";
        BindHintText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A) _keyFrets[0] = true;
        else if (e.Key == Key.S) _keyFrets[1] = true;
        else if (e.Key == Key.J) _keyFrets[2] = true;
        else if (e.Key == Key.K) _keyFrets[3] = true;
        else if (e.Key == Key.L) _keyFrets[4] = true;
        else if (e.Key == Key.Up) _keyStrumUp = true;
        else if (e.Key == Key.Down || e.Key == Key.Enter || e.Key == Key.Space) _keyStrumDown = true;
    }

    private void Window_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.A) _keyFrets[0] = false;
        else if (e.Key == Key.S) _keyFrets[1] = false;
        else if (e.Key == Key.J) _keyFrets[2] = false;
        else if (e.Key == Key.K) _keyFrets[3] = false;
        else if (e.Key == Key.L) _keyFrets[4] = false;
        else if (e.Key == Key.Up) _keyStrumUp = false;
        else if (e.Key == Key.Down || e.Key == Key.Enter || e.Key == Key.Space) _keyStrumDown = false;
    }

    private void PollInputs()
    {
        var snap = UnifiedControllerService.Poll();

        // Capture custom button bind (e.g., DualSense Edge back paddles)
        if (_bindingFretIndex >= 0 && snap.Connected && snap.FirstPressedRawButtonIndex >= 0)
        {
            AppConfig.Current.CustomPadButtonIndices ??= new[] { -1, -1, -1, -1, -1 };
            AppConfig.Current.CustomPadButtonIndices[_bindingFretIndex] = snap.FirstPressedRawButtonIndex;
            AppConfig.Save();
            string[] names = { "GREEN", "RED", "YELLOW", "BLUE", "ORANGE" };
            BindHintText.Text = $"  ✓ Bound {names[_bindingFretIndex]} to Pad Button #{snap.FirstPressedRawButtonIndex + 1}!";
            BindHintText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
            _bindingFretIndex = -1;
        }

        if (snap.Connected)
        {
            ControllerStatusPill.Background = new SolidColorBrush(WpfColor.FromRgb(6, 78, 59));
            ControllerStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
            ControllerStatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
            ControllerStatusText.Text = $"✓ {snap.DeviceName} ({snap.Backend})";

            if (snap.EffectiveGamepadMode)
            {
                FretGLabel.Text = snap.IsPlayStation ? "GREEN (L2)" : "GREEN (LT)";
                FretRLabel.Text = snap.IsPlayStation ? "RED (L1)" : "RED (LB)";
                FretYLabel.Text = snap.IsPlayStation ? "YELLOW (R1)" : "YELLOW (RB)";
                FretBLabel.Text = snap.IsPlayStation ? "BLUE (R2)" : "BLUE (RT)";
                FretOLabel.Text = snap.IsPlayStation ? "ORANGE (✕)" : "ORANGE (A)";
                FretMapCaption.Text = "OFFICIAL GH3 GAMEPAD LAYOUT (Auto-Strum Active — L2 / L1 / R1 / R2 / ✕ or LT / LB / RB / RT / A)";
            }
            else
            {
                FretGLabel.Text = snap.IsPlayStation ? "GREEN (✕)" : "GREEN (A)";
                FretRLabel.Text = snap.IsPlayStation ? "RED (○)" : "RED (B)";
                FretYLabel.Text = snap.IsPlayStation ? "YELLOW (△)" : "YELLOW (Y)";
                FretBLabel.Text = snap.IsPlayStation ? "BLUE (□)" : "BLUE (X)";
                FretOLabel.Text = snap.IsPlayStation ? "ORANGE (L1)" : "ORANGE (LB)";
                FretMapCaption.Text = "OFFICIAL GH3 GUITAR CONTROLLER LAYOUT (Hold Frets + D-Pad Strum Bar)";
            }
        }
        else
        {
            ControllerStatusPill.Background = new SolidColorBrush(WpfColor.FromRgb(30, 41, 59));
            ControllerStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(71, 85, 105));
            ControllerStatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225));
            ControllerStatusText.Text = "Plug in DualSense / Xbox / Guitar (Keyboard Active)";
        }

        bool g = _keyFrets[0] || snap.Frets[0];
        bool r = _keyFrets[1] || snap.Frets[1];
        bool y = _keyFrets[2] || snap.Frets[2];
        bool b = _keyFrets[3] || snap.Frets[3];
        bool o = _keyFrets[4] || snap.Frets[4];

        bool up = _keyStrumUp || snap.StrumUp;
        bool dn = _keyStrumDown || snap.StrumDown;

        SetFretVisual(FretG, FretGDot, g, WpfColor.FromRgb(34, 197, 94), WpfColor.FromRgb(12, 30, 20));
        SetFretVisual(FretR, FretRDot, r, WpfColor.FromRgb(239, 68, 68), WpfColor.FromRgb(36, 13, 17));
        SetFretVisual(FretY, FretYDot, y, WpfColor.FromRgb(234, 179, 8), WpfColor.FromRgb(36, 29, 8));
        SetFretVisual(FretB, FretBDot, b, WpfColor.FromRgb(59, 130, 246), WpfColor.FromRgb(12, 25, 48));
        SetFretVisual(FretO, FretODot, o, WpfColor.FromRgb(249, 115, 22), WpfColor.FromRgb(38, 18, 7));

        StrumUpBox.Background = new SolidColorBrush(up ? WpfColor.FromRgb(249, 115, 22) : WpfColor.FromRgb(20, 23, 34));
        StrumDownBox.Background = new SolidColorBrush(dn ? WpfColor.FromRgb(249, 115, 22) : WpfColor.FromRgb(20, 23, 34));
        BtnStartBox.Background = new SolidColorBrush(snap.StartPressed ? WpfColor.FromRgb(16, 185, 129) : WpfColor.FromRgb(20, 23, 34));
        BtnBackBox.Background = new SolidColorBrush(snap.StarPowerPressed ? WpfColor.FromRgb(14, 165, 233) : WpfColor.FromRgb(20, 23, 34));

        int whammyPct = (int)Math.Round(snap.WhammyNormalized * 100.0);
        int l2Pct = (int)Math.Round(snap.LeftTrigger * 100.0);
        int r2Pct = (int)Math.Round(snap.RightTrigger * 100.0);

        WhammyBarMeter.Value = whammyPct;
        WhammyValueText.Text = $"{whammyPct}%";
        L2Meter.Value = l2Pct;
        L2ValueText.Text = $"{l2Pct}%";
        R2Meter.Value = r2Pct;
        R2ValueText.Text = $"{r2Pct}%";

        RawMaskText.Text = snap.Connected
            ? $"Backend: {snap.Backend}  •  Raw Buttons: 0x{snap.RawButtonsMask:X4}  •  Mode: {(snap.EffectiveGamepadMode ? "GH3 Gamepad (Auto-Strum)" : "GH3 Guitar")}"
            : "Waiting for DualSense Edge, DualSense, DualShock 4, Xbox Controller, or Guitar...";
    }

    private static void SetFretVisual(System.Windows.Controls.Border box, System.Windows.Shapes.Ellipse dot, bool active, WpfColor bright, WpfColor dimBg)
    {
        box.Background = new SolidColorBrush(active ? WpfColor.FromArgb(175, bright.R, bright.G, bright.B) : dimBg);
        box.BorderBrush = new SolidColorBrush(active ? WpfColor.FromRgb(255, 255, 255) : bright);
        dot.Fill = new SolidColorBrush(active ? bright : WpfColor.FromArgb(90, bright.R, bright.G, bright.B));
        dot.Stroke = new SolidColorBrush(active ? WpfColor.FromRgb(255, 255, 255) : bright);
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
