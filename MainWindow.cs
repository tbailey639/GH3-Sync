using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace GH3_Sync;

/// <summary>
/// Native Windows 11 Desktop Application Window for GH3-Sync.
/// Embeds WebView2 with hardware acceleration, system tray minimization,
/// immersive dark mode title bar, and seamless local integration.
/// </summary>
public class MainWindow : Form
{
    private readonly string _dashboardUrl;
    private readonly SyncLogger? _logger;
    private WebView2? _webView;
    private NotifyIcon? _trayIcon;
    private ContextMenuStrip? _trayMenu;
    private bool _isExiting = false;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    public MainWindow(string dashboardUrl, SyncLogger? logger = null)
    {
        _dashboardUrl = dashboardUrl;
        _logger = logger;

        InitializeWindow();
        InitializeWebView();
        InitializeTrayIcon();
    }

    private void InitializeWindow()
    {
        Text = "GH3-Sync | BetterGH3 P2P Companion & ONOGAMI Song Creator";
        Width = 1320;
        Height = 860;
        MinimumSize = new Size(1000, 680);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(17, 24, 39); // Deep Slate Gray matching dashboard

        // Enable Windows 11 Immersive Dark Mode for Title Bar
        try
        {
            int darkMode = 1;
            DwmSetWindowAttribute(Handle, 20, ref darkMode, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
        }
        catch { }

        // Load Icon if available
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "wwwroot", "favicon.ico");
            if (File.Exists(iconPath))
            {
                Icon = new Icon(iconPath);
            }
        }
        catch { }

        FormClosing += MainWindow_FormClosing;
    }

    private async void InitializeWebView()
    {
        try
        {
            _webView = new WebView2
            {
                Dock = DockStyle.Fill,
                DefaultBackgroundColor = Color.FromArgb(17, 24, 39)
            };

            Controls.Add(_webView);

            // Store WebView2 cache and profile safely in AppData
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GH3-Sync", "webview2_profile");
            
            if (!Directory.Exists(userDataFolder))
            {
                Directory.CreateDirectory(userDataFolder);
            }

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await _webView.EnsureCoreWebView2Async(env);

            // Configure browser settings
            _webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            _webView.CoreWebView2.Settings.AreDevToolsEnabled = true;
            _webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            _webView.CoreWebView2.Navigate(_dashboardUrl);
            _logger?.LogInfo($"[DESKTOP APP] WebView2 initialized and navigated to: {_dashboardUrl}");
        }
        catch (Exception ex)
        {
            _logger?.LogWarn($"[DESKTOP APP] WebView2 initialization fallback: {ex.Message}");
            // Fallback display if WebView2 runtime is missing
            var lbl = new Label
            {
                Text = $"GH3-Sync Server Running at: {_dashboardUrl}\n\n(WebView2 Runtime not found. Opening in default browser...)",
                ForeColor = Color.White,
                Font = new Font("Segoe UI", 12),
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleCenter
            };
            Controls.Add(lbl);

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _dashboardUrl,
                    UseShellExecute = true
                });
            }
            catch { }
        }
    }

    private void InitializeTrayIcon()
    {
        try
        {
            _trayMenu = new ContextMenuStrip();
            _trayMenu.Items.Add("Open Dashboard", null, (s, e) => RestoreWindow());
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add("Exit GH3-Sync", null, (s, e) =>
            {
                _isExiting = true;
                Close();
            });

            _trayIcon = new NotifyIcon
            {
                Text = "GH3-Sync | P2P & ONOGAMI Song Creator",
                ContextMenuStrip = _trayMenu,
                Visible = true
            };

            if (Icon != null)
            {
                _trayIcon.Icon = Icon;
            }
            else
            {
                _trayIcon.Icon = SystemIcons.Application;
            }

            _trayIcon.DoubleClick += (s, e) => RestoreWindow();
        }
        catch { }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        BringToFront();
        Activate();
    }

    private void MainWindow_FormClosing(object? sender, FormClosingEventArgs e)
    {
        if (!_isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            // Minimize to tray instead of quitting unexpectedly during live sync
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(2000, "GH3-Sync Running in Background", "GH3-Sync is still active in the system tray. Double-click to reopen.", ToolTipIcon.Info);
        }
        else
        {
            _trayIcon?.Dispose();
        }
    }
}
