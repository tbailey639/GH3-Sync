using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

using WpfColor = System.Windows.Media.Color;

namespace GH3_Sync.Views;

internal static class XInputNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_GAMEPAD
    {
        public ushort wButtons;
        public byte bLeftTrigger;
        public byte bRightTrigger;
        public short sThumbLX;
        public short sThumbLY;
        public short sThumbRX;
        public short sThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XINPUT_STATE
    {
        public uint dwPacketNumber;
        public XINPUT_GAMEPAD Gamepad;
    }

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint dwUserIndex, out XINPUT_STATE pState);

    [DllImport("xinput1_3.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState13(uint dwUserIndex, out XINPUT_STATE pState);

    public static bool TryGetFirstConnectedState(out uint slot, out XINPUT_STATE state)
    {
        for (uint i = 0; i < 4; i++)
        {
            try
            {
                if (XInputGetState14(i, out state) == 0) { slot = i; return true; }
            }
            catch
            {
                try
                {
                    if (XInputGetState13(i, out state) == 0) { slot = i; return true; }
                }
                catch { break; }
            }
        }
        slot = 0;
        state = default;
        return false;
    }

    // Standard Guitar Hero / Rock Band XInput button bitmasks
    public const ushort DPAD_UP = 0x0001;
    public const ushort DPAD_DOWN = 0x0002;
    public const ushort START = 0x0010;
    public const ushort BACK = 0x0020;
    public const ushort LEFT_SHOULDER = 0x0100;  // Orange fret
    public const ushort A = 0x1000;              // Green fret
    public const ushort B = 0x2000;              // Red fret
    public const ushort X = 0x4000;              // Blue fret
    public const ushort Y = 0x8000;              // Yellow fret
}

public partial class GuitarTesterWindow : Window
{
    private readonly DispatcherTimer _pollTimer;
    private readonly bool[] _keyFrets = new bool[5];
    private bool _keyStrumUp;
    private bool _keyStrumDown;

    public GuitarTesterWindow()
    {
        InitializeComponent();
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _pollTimer.Tick += (_, _) => PollInputs();
        Loaded += (_, _) => _pollTimer.Start();
        Closing += (_, _) => _pollTimer.Stop();
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
        bool connected = XInputNative.TryGetFirstConnectedState(out uint slot, out var state);
        ushort btns = connected ? state.Gamepad.wButtons : (ushort)0;

        if (connected)
        {
            ControllerStatusPill.Background = new SolidColorBrush(WpfColor.FromRgb(6, 78, 59));
            ControllerStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
            ControllerStatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183));
            ControllerStatusText.Text = $"✓ XInput Guitar / Pad Connected (P{slot + 1})";
        }
        else
        {
            ControllerStatusPill.Background = new SolidColorBrush(WpfColor.FromRgb(30, 41, 59));
            ControllerStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(71, 85, 105));
            ControllerStatusText.Foreground = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225));
            ControllerStatusText.Text = "No XInput Guitar Detected (Keyboard Test Active)";
        }

        bool g = _keyFrets[0] || (btns & XInputNative.A) != 0;
        bool r = _keyFrets[1] || (btns & XInputNative.B) != 0;
        bool y = _keyFrets[2] || (btns & XInputNative.Y) != 0;
        bool b = _keyFrets[3] || (btns & XInputNative.X) != 0;
        bool o = _keyFrets[4] || (btns & XInputNative.LEFT_SHOULDER) != 0;

        bool up = _keyStrumUp || (btns & XInputNative.DPAD_UP) != 0;
        bool dn = _keyStrumDown || (btns & XInputNative.DPAD_DOWN) != 0;
        bool start = (btns & XInputNative.START) != 0;
        bool back = (btns & XInputNative.BACK) != 0;

        SetFretVisual(FretG, FretGDot, g, WpfColor.FromRgb(34, 197, 94), WpfColor.FromRgb(12, 30, 20));
        SetFretVisual(FretR, FretRDot, r, WpfColor.FromRgb(239, 68, 68), WpfColor.FromRgb(36, 13, 17));
        SetFretVisual(FretY, FretYDot, y, WpfColor.FromRgb(234, 179, 8), WpfColor.FromRgb(36, 29, 8));
        SetFretVisual(FretB, FretBDot, b, WpfColor.FromRgb(59, 130, 246), WpfColor.FromRgb(12, 25, 48));
        SetFretVisual(FretO, FretODot, o, WpfColor.FromRgb(249, 115, 22), WpfColor.FromRgb(38, 18, 7));

        StrumUpBox.Background = new SolidColorBrush(up ? WpfColor.FromRgb(249, 115, 22) : WpfColor.FromRgb(20, 23, 34));
        StrumDownBox.Background = new SolidColorBrush(dn ? WpfColor.FromRgb(249, 115, 22) : WpfColor.FromRgb(20, 23, 34));
        BtnStartBox.Background = new SolidColorBrush(start ? WpfColor.FromRgb(16, 185, 129) : WpfColor.FromRgb(20, 23, 34));
        BtnBackBox.Background = new SolidColorBrush(back ? WpfColor.FromRgb(14, 165, 233) : WpfColor.FromRgb(20, 23, 34));

        // Whammy (Right Thumb X: -32768 resting -> +32767 full press, or normalized magnitude)
        double whammyPct = connected ? Math.Clamp(((state.Gamepad.sThumbRX + 32768.0) / 65535.0) * 100.0, 0, 100) : 0;
        if (connected && Math.Abs(state.Gamepad.sThumbRX) < 1200) whammyPct = 0; // Standard gamepad centered stick
        double tiltPct = connected ? Math.Clamp((Math.Abs((double)state.Gamepad.sThumbRY) / 32767.0) * 100.0, 0, 100) : 0;

        WhammyBarMeter.Value = whammyPct;
        WhammyValueText.Text = $"{(int)Math.Round(whammyPct)}%";
        TiltMeter.Value = tiltPct;
        TiltValueText.Text = $"{(int)Math.Round(tiltPct)}%";
        RawMaskText.Text = $"Raw XInput Mask: 0x{btns:X4}  •  RX={state.Gamepad.sThumbRX}  •  RY={state.Gamepad.sThumbRY}";
    }

    private static void SetFretVisual(System.Windows.Controls.Border box, System.Windows.Shapes.Ellipse dot, bool active, WpfColor bright, WpfColor dimBg)
    {
        box.Background = new SolidColorBrush(active ? WpfColor.FromArgb(145, bright.R, bright.G, bright.B) : dimBg);
        dot.Fill = new SolidColorBrush(active ? bright : WpfColor.FromArgb(80, bright.R, bright.G, bright.B));
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
