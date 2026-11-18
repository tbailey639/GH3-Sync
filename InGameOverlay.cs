using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace GH3_Sync;

/// <summary>
/// Per-pixel alpha transparent click-through Electric Guitar HUD overlay for Guitar Hero III.
/// Displays ONLY the sleek Electric Guitar silhouette in the chosen corner:
/// - Electric Neon Blue strings when P2P connection is active (filling 0->100% during active sync)
/// - Regular metallic steel strings when offline/disconnected
/// </summary>
public class InGameOverlay
{
    private readonly SyncLogger _logger;
    private Thread? _overlayThread;
    private GuitarHudWindow? _window;

    public InGameOverlay(SyncLogger logger)
    {
        _logger = logger;
    }

    public void Start()
    {
        _overlayThread = new Thread(() =>
        {
            try
            {
                _window = new GuitarHudWindow(_logger);
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                _logger.LogError("InGameOverlay crashed", ex);
            }
        })
        {
            IsBackground = true
        };
        _overlayThread.SetApartmentState(ApartmentState.STA);
        _overlayThread.Start();
    }
}

internal sealed class GuitarHudWindow : Window
{
    private readonly SyncLogger _logger;
    private readonly DispatcherTimer _trackTimer;
    private OverlayStatus _status;
    private bool _gh3WasRunning;
    private bool _lastConnectionActive = true;

    private readonly RectangleGeometry _blueStringsClip;
    private readonly Canvas _blueStringsLayer;
    private DropShadowEffect? _blueGlowEffect;
    private readonly DispatcherTimer _pulseTimer;
    private int _pulseTicksRemaining = 0;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    public GuitarHudWindow(SyncLogger logger)
    {
        _logger = logger;
        _status = logger.CurrentStatus;
        _logger.StatusChanged += OnStatusChanged;
        _logger.PeerActivityPulsed += OnPeerActivityPulsed;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = System.Windows.Media.Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        IsHitTestVisible = false;
        ApplyHudSizeAndOpacity();
        Left = -4000;
        Top = -4000;

        (_blueStringsLayer, _blueStringsClip) = BuildGuitarVisual();
        UpdateStringAppearance();

        _pulseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        _pulseTimer.Tick += (_, _) => StepStrumPulse();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
            }
        };

        _trackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _trackTimer.Tick += (_, _) => TrackGh3Window();
        _trackTimer.Start();
    }

    private (Canvas BlueLayer, RectangleGeometry ClipRect) BuildGuitarVisual()
    {
        var bodyGrad = new LinearGradientBrush(
            System.Windows.Media.Color.FromArgb(235, 30, 41, 59),
            System.Windows.Media.Color.FromArgb(240, 2, 6, 23),
            new System.Windows.Point(0, 0), new System.Windows.Point(1, 1));

        var fretGrad = new LinearGradientBrush(
            System.Windows.Media.Color.FromArgb(240, 30, 27, 46),
            System.Windows.Media.Color.FromArgb(240, 15, 23, 42),
            new System.Windows.Point(0, 0), new System.Windows.Point(0, 1));

        var blueGrad = new LinearGradientBrush { StartPoint = new System.Windows.Point(0, 0), EndPoint = new System.Windows.Point(1, 0) };
        blueGrad.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(29, 78, 216), 0.0));
        blueGrad.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(14, 165, 233), 0.45));
        blueGrad.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(56, 189, 248), 0.82));
        blueGrad.GradientStops.Add(new GradientStop(System.Windows.Media.Color.FromRgb(186, 230, 253), 1.0));

        var root = new Canvas { Width = 640, Height = 150, Background = System.Windows.Media.Brushes.Transparent };

        // Double-Cutaway Electric Guitar Silhouette (matches the updater guitar 1:1!)
        root.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 54,18 C 22,18 14,44 14,75 C 14,106 22,132 54,132 C 82,132 100,114 118,116 C 138,118 156,138 182,132 C 202,127 212,112 204,98 C 198,90 202,88 210,88 L 210,62 C 202,62 198,60 206,50 C 216,36 202,16 178,18 C 154,20 138,34 118,34 C 100,34 82,18 54,18 Z"),
            Fill = bodyGrad,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
            StrokeThickness = 2.6,
            Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 10, ShadowDepth = 2, Opacity = 0.75 }
        });

        // Pickguard
        root.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 112,44 C 138,40 166,30 190,44 C 196,54 196,96 188,108 C 166,118 134,112 112,106 Z"),
            Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(22, 32, 51)),
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
            StrokeThickness = 1.2
        });

        // Bridge & Pickups
        void AddRect(double l, double t, double w, double h, System.Windows.Media.Color fill, System.Windows.Media.Color stroke, double rx = 2.5)
        {
            var r = new System.Windows.Shapes.Rectangle
            {
                Width = w, Height = h, RadiusX = rx, RadiusY = rx,
                Fill = new SolidColorBrush(fill),
                Stroke = new SolidColorBrush(stroke),
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(r, l);
            Canvas.SetTop(r, t);
            root.Children.Add(r);
        }

        AddRect(76, 52, 10, 46, System.Windows.Media.Color.FromRgb(71, 85, 105), System.Windows.Media.Color.FromRgb(148, 163, 184));
        AddRect(90, 50, 16, 50, System.Windows.Media.Color.FromRgb(51, 65, 85), System.Windows.Media.Color.FromRgb(148, 163, 184));
        AddRect(118, 50, 22, 50, System.Windows.Media.Color.FromRgb(11, 17, 32), System.Windows.Media.Color.FromRgb(100, 116, 139));
        AddRect(160, 50, 22, 50, System.Windows.Media.Color.FromRgb(11, 17, 32), System.Windows.Media.Color.FromRgb(100, 116, 139));

        // Knobs
        foreach (var (kl, kt) in new[] { (138.0, 112.0), (158.0, 116.0) })
        {
            var knob = new Ellipse
            {
                Width = 12, Height = 12,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(51, 65, 85)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184)),
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(knob, kl);
            Canvas.SetTop(knob, kt);
            root.Children.Add(knob);
        }

        // Fretboard Neck
        var neck = new System.Windows.Shapes.Rectangle
        {
            Width = 322, Height = 40,
            Fill = fretGrad,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
            StrokeThickness = 1.6
        };
        Canvas.SetLeft(neck, 204);
        Canvas.SetTop(neck, 55);
        root.Children.Add(neck);

        // Silver Frets
        var fretBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139));
        foreach (double fx in new[] { 224, 246, 268, 291, 314, 338, 362, 386, 411, 436, 461, 486, 510 })
        {
            root.Children.Add(new Line { X1 = fx, Y1 = 56, X2 = fx, Y2 = 94, Stroke = fretBrush, StrokeThickness = 1.4 });
        }

        // Nut
        AddRect(524, 54, 5, 42, System.Windows.Media.Color.FromRgb(203, 213, 225), System.Windows.Media.Color.FromRgb(203, 213, 225), 1.5);

        // Headstock
        root.Children.Add(new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M 529,56 L 548,46 L 622,42 L 628,58 L 614,76 L 624,96 L 612,106 L 548,100 L 529,94 Z"),
            Fill = bodyGrad,
            Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(71, 85, 105)),
            StrokeThickness = 2.2
        });

        // 6 Tuning Pegs
        foreach (var (pl, pt) in new[] { (552.0, 36.0), (574.0, 34.0), (596.0, 32.0), (552.0, 102.0), (574.0, 104.0), (596.0, 106.0) })
        {
            var peg = new Ellipse
            {
                Width = 10, Height = 10,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(100, 116, 139)),
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(203, 213, 225)),
                StrokeThickness = 1.2
            };
            Canvas.SetLeft(peg, pl);
            Canvas.SetTop(peg, pt);
            root.Children.Add(peg);
        }

        // Regular Metallic Steel Strings (shown when disconnected / offline)
        var steelBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(148, 163, 184));
        double[] stringYs = { 60, 66, 72, 78, 84, 90 };
        double[] steelThick = { 1.6, 1.8, 2.0, 2.2, 2.5, 2.8 };
        for (int i = 0; i < 6; i++)
        {
            root.Children.Add(new Line
            {
                X1 = 92, Y1 = stringYs[i], X2 = 526, Y2 = stringYs[i],
                Stroke = steelBrush, StrokeThickness = steelThick[i]
            });
        }

        // Glowing Electric Blue Strings Layer (shown when P2P connection is active; fills 0->100% during sync!)
        var clipRect = new RectangleGeometry(new Rect(92, 48, 434, 54));
        _blueGlowEffect = new DropShadowEffect { Color = System.Windows.Media.Color.FromRgb(0, 210, 255), BlurRadius = 14, ShadowDepth = 0, Opacity = 0.95 };
        var blueLayer = new Canvas
        {
            Width = 640, Height = 150,
            Clip = clipRect,
            Effect = _blueGlowEffect
        };
        double[] blueThick = { 2.2, 2.4, 2.6, 2.8, 3.0, 3.3 };
        for (int i = 0; i < 6; i++)
        {
            blueLayer.Children.Add(new Line
            {
                X1 = 92, Y1 = stringYs[i], X2 = 526, Y2 = stringYs[i],
                Stroke = blueGrad, StrokeThickness = blueThick[i],
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
        }
        root.Children.Add(blueLayer);

        Content = new Viewbox { Stretch = Stretch.Uniform, Child = root };
        return (blueLayer, clipRect);
    }

    private void OnStatusChanged(OverlayStatus newStatus)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _status = newStatus;
                UpdateStringAppearance();
            });
        }
        catch { }
    }

    private void UpdateStringAppearance()
    {
        bool connectionActive = AppConfig.Current.P2pOnlineEnabled && _status.State != SyncState.Failed;
        _lastConnectionActive = connectionActive;

        if (!connectionActive)
        {
            // Disconnected / Offline: regular silver strings only
            _blueStringsLayer.Visibility = Visibility.Collapsed;
            return;
        }

        _blueStringsLayer.Visibility = Visibility.Visible;
        if (_status.State == SyncState.Syncing && _status.ProgressPercent > 0 && _status.ProgressPercent < 100)
        {
            double fillW = 434.0 * (_status.ProgressPercent / 100.0);
            _blueStringsClip.Rect = new Rect(92, 48, fillW, 54);
        }
        else
        {
            // Active & Connected: all 6 strings fully lit electric blue!
            _blueStringsClip.Rect = new Rect(92, 48, 434, 54);
        }
    }

    private void ApplyHudSizeAndOpacity()
    {
        var (w, h) = (AppConfig.Current.InGameOverlaySize ?? "Medium") switch
        {
            "Small" => (176.0, 46.0),
            "Large" => (280.0, 72.0),
            _ => (224.0, 58.0)
        };
        if (Math.Abs(Width - w) > 0.5) Width = w;
        if (Math.Abs(Height - h) > 0.5) Height = h;
        double targetOpacity = Math.Clamp(AppConfig.Current.InGameOverlayOpacity, 0.35, 1.0);
        if (Math.Abs(Opacity - targetOpacity) > 0.01) Opacity = targetOpacity;
    }

    private void OnPeerActivityPulsed(string detail)
    {
        try
        {
            Dispatcher.BeginInvoke(() =>
            {
                _pulseTicksRemaining = 24;
                if (!_pulseTimer.IsEnabled) _pulseTimer.Start();
            });
        }
        catch { }
    }

    private void StepStrumPulse()
    {
        if (_pulseTicksRemaining <= 0)
        {
            _pulseTimer.Stop();
            if (_blueGlowEffect != null)
            {
                _blueGlowEffect.BlurRadius = 14;
                _blueGlowEffect.Opacity = 0.95;
            }
            UpdateStringAppearance();
            return;
        }

        _pulseTicksRemaining--;
        double phase = Math.Sin(_pulseTicksRemaining * 0.55);
        if (_blueGlowEffect != null)
        {
            _blueGlowEffect.BlurRadius = 16 + Math.Abs(phase) * 14;
            _blueGlowEffect.Opacity = 1.0;
        }
        // Electric sweep across strings during peer strum pulse
        if (_blueStringsLayer.Visibility == Visibility.Visible && _status.State != SyncState.Syncing)
        {
            double sweep = 0.65 + 0.35 * Math.Abs(phase);
            _blueStringsClip.Rect = new Rect(92, 48, 434.0 * sweep, 54);
        }
    }

    private void TrackGh3Window()
    {
        if (!AppConfig.Current.EnableInGameOverlay)
        {
            if (IsVisible) Hide();
            return;
        }

        ApplyHudSizeAndOpacity();

        // Refresh string glow state if P2P Online setting changed dynamically
        bool nowActive = AppConfig.Current.P2pOnlineEnabled && _status.State != SyncState.Failed;
        if (nowActive != _lastConnectionActive) UpdateStringAppearance();

        var processes = Process.GetProcessesByName("GH3");
        if (processes.Length == 0)
        {
            if (_gh3WasRunning)
            {
                _logger.LogInfo("GH3.exe closed. Hiding guitar HUD overlay.");
                _gh3WasRunning = false;
            }
            if (IsVisible) Hide();
            return;
        }

        var proc = processes[0];
        var hWnd = proc.MainWindowHandle;
        if (hWnd == IntPtr.Zero || IsIconic(hWnd))
        {
            if (IsVisible) Hide();
            return;
        }

        if (!_gh3WasRunning)
        {
            _logger.LogInfo($"GH3.exe detected (PID: {proc.Id}). Electric Guitar HUD overlay attached.");
            _gh3WasRunning = true;
        }

        if (GetWindowRect(hWnd, out RECT rect))
        {
            // Account for Windows DPI scaling so position stays exact on 100%/125%/150% displays
            var src = PresentationSource.FromVisual(this);
            double dpiX = src?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
            double dpiY = src?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

            double gLeft = rect.Left / dpiX;
            double gTop = rect.Top / dpiY;
            double gRight = rect.Right / dpiX;
            double gBottom = rect.Bottom / dpiY;

            const double pad = 18.0;
            string corner = AppConfig.Current.InGameOverlayCorner ?? "TopRight";

            double targetX = corner.Contains("Left", StringComparison.OrdinalIgnoreCase)
                ? gLeft + pad
                : gRight - Width - pad;

            double targetY = corner.Contains("Bottom", StringComparison.OrdinalIgnoreCase)
                ? gBottom - Height - pad
                : gTop + pad;

            if (Math.Abs(Left - targetX) > 1.0 || Math.Abs(Top - targetY) > 1.0)
            {
                Left = targetX;
                Top = targetY;
            }

            if (!IsVisible) Show();
        }
    }
}
