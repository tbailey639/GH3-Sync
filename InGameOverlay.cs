using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GH3_Sync;

public class InGameOverlay
{
    private readonly SyncLogger _logger;
    private Thread? _overlayThread;
    private OverlayForm? _form;

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
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                _form = new OverlayForm(_logger);
                Application.Run(_form);
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

public class OverlayForm : Form
{
    private readonly SyncLogger _logger;
    private readonly System.Windows.Forms.Timer _trackTimer;
    private OverlayStatus _status = new();
    private bool _gh3WasRunning = false;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    public OverlayForm(SyncLogger logger)
    {
        _logger = logger;
        _status = logger.CurrentStatus;
        _logger.StatusChanged += OnStatusChanged;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        Size = new Size(340, 75);
        StartPosition = FormStartPosition.Manual;
        Location = new Point(-2000, -2000); // Offscreen until GH3 is found

        BackColor = Color.FromArgb(12, 14, 20);
        TransparencyKey = Color.Magenta;

        _trackTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _trackTimer.Tick += (s, e) => TrackGh3Window();
        _trackTimer.Start();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TRANSPARENT (0x20) + WS_EX_NOACTIVATE (0x08000000)
            cp.ExStyle |= 0x00000020 | 0x08000000;
            return cp;
        }
    }

    private void OnStatusChanged(OverlayStatus newStatus)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(new Action(() =>
            {
                _status = newStatus;
                Invalidate();
            }));
        }
        catch { }
    }

    private void TrackGh3Window()
    {
        var processes = Process.GetProcessesByName("GH3");
        if (processes.Length == 0)
        {
            if (_gh3WasRunning)
            {
                _logger.LogInfo("GH3.exe closed. Hiding overlay.");
                _gh3WasRunning = false;
            }
            if (Visible) Hide();
            return;
        }

        var proc = processes[0];
        var hWnd = proc.MainWindowHandle;
        if (hWnd == IntPtr.Zero || IsIconic(hWnd))
        {
            if (Visible) Hide();
            return;
        }

        if (!_gh3WasRunning)
        {
            _logger.LogInfo($"GH3.exe detected (PID: {proc.Id}). In-game overlay attached.");
            _gh3WasRunning = true;
        }

        if (GetWindowRect(hWnd, out RECT rect))
        {
            int targetX = rect.Right - Width - 24;
            int targetY = rect.Top + 24;

            if (Location.X != targetX || Location.Y != targetY)
            {
                Location = new Point(targetX, targetY);
            }

            if (!Visible) Show();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);

        // Background Glass Card
        using (var bgBrush = new SolidBrush(Color.FromArgb(235, 18, 22, 32)))
        using (var borderPen = new Pen(GetStateBorderColor(_status.State), 1.5f))
        using (var path = GetRoundedRectangle(bounds, 8))
        {
            g.FillPath(bgBrush, path);
            g.DrawPath(borderPen, path);
        }

        // Header Line: Icon + Plugin Name + Pill
        using var fontHeader = new Font("Segoe UI", 9f, FontStyle.Bold);
        using var fontSub = new Font("Segoe UI", 8.5f, FontStyle.Regular);
        using var brushWhite = new SolidBrush(Color.FromArgb(240, 240, 245));
        using var brushMuted = new SolidBrush(Color.FromArgb(160, 165, 180));

        g.DrawString("🎸 GH3-SYNC PLUGIN", fontHeader, brushWhite, 10, 8);

        // Status Pill
        var pillText = GetPillText(_status.State);
        var pillColor = GetStateBorderColor(_status.State);
        var pillSize = g.MeasureString(pillText, fontSub);
        int pillX = Width - (int)pillSize.Width - 22;
        int pillY = 8;

        using (var pillBg = new SolidBrush(Color.FromArgb(40, pillColor)))
        using (var pillBorder = new Pen(pillColor, 1f))
        {
            var pillRect = new Rectangle(pillX - 6, pillY - 2, (int)pillSize.Width + 12, (int)pillSize.Height + 4);
            using var pillPath = GetRoundedRectangle(pillRect, 4);
            g.FillPath(pillBg, pillPath);
            g.DrawPath(pillBorder, pillPath);
        }
        using (var pillBrush = new SolidBrush(pillColor))
        {
            g.DrawString(pillText, fontSub, pillBrush, pillX, pillY);
        }

        // Detail / Message Line
        g.DrawString(_status.Message, fontSub, brushWhite, 10, 30);

        // Progress Bar or Subtitle
        if (_status.State == SyncState.Syncing && _status.ProgressPercent > 0)
        {
            int pBarX = 10;
            int pBarY = 52;
            int pBarW = Width - 20;
            int pBarH = 12;

            using var pBarBg = new SolidBrush(Color.FromArgb(35, 40, 55));
            g.FillRectangle(pBarBg, pBarX, pBarY, pBarW, pBarH);

            int fillW = Math.Max(4, (int)(pBarW * (_status.ProgressPercent / 100.0f)));
            using var fillBrush = new LinearGradientBrush(
                new Rectangle(pBarX, pBarY, fillW, pBarH),
                Color.FromArgb(249, 115, 22),
                Color.FromArgb(16, 185, 129),
                LinearGradientMode.Horizontal);
            g.FillRectangle(fillBrush, pBarX, pBarY, fillW, pBarH);
        }
        else
        {
            g.DrawString(_status.Detail, fontSub, brushMuted, 10, 50);
        }
    }

    private static Color GetStateBorderColor(SyncState state) => state switch
    {
        SyncState.Syncing => Color.FromArgb(249, 115, 22),
        SyncState.Success => Color.FromArgb(16, 185, 129),
        SyncState.Failed => Color.FromArgb(239, 68, 68),
        _ => Color.FromArgb(70, 80, 105)
    };

    private static string GetPillText(SyncState state) => state switch
    {
        SyncState.Syncing => "SYNCING",
        SyncState.Success => "✓ SUCCESS",
        SyncState.Failed => "✗ FAILED",
        _ => "LOADED"
    };

    private static GraphicsPath GetRoundedRectangle(Rectangle rect, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
