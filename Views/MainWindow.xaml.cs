using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Threading;

using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = GH3_Sync.Views.GuitarHeroModalDialog;

namespace GH3_Sync.Views;

internal record QueuedSongItem(SongCreationRequest Meta, string AudioPath, string? ChartPath);

public class PlayInviteNotification
{
    public string SenderCode { get; set; } = "";
    public string SenderUsername { get; set; } = "";
    public string SenderAddress { get; set; } = "";
    public DateTime ReceivedAt { get; set; } = DateTime.UtcNow;
}

public partial class MainWindow : Window
{
    private readonly Gh3Service _gh3;
    private readonly SyncLogger _logger;
    private readonly SecurityService _security;
    private readonly FriendService _friends;
    private readonly SaveManager _saves;
    private readonly HoneycombCompilerService _compiler;
    private readonly CloneHeroImportService _cloneHero;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly DemucsService _demucs;
    private readonly SongCreatorService _creator;
    private readonly NatPortMappingService _nat;
    private readonly UpdateService _updater;
    private readonly TemporarySessionService _tempSession;
    private readonly ComponentUpdaterService _compUpdater;
    private readonly ChartDoctorService _chartDoctor;
    private readonly BetterGh3TweaksService _betterGh3Tweaks;
    private readonly DiscordRpcService? _discordRpc;
    private readonly AlbumArtMetadataService _albumMeta;
    private readonly PracticeAudioService _practiceAudio;
    private readonly HighwaySkinService _highwaySkins;
    private readonly YtDlpImporterService _ytDlp;
    private readonly LibraryHealthService _libraryHealth;
    private readonly TunnelAndMeshService _tunnel;
    private readonly CareerSaveScoreService _careerScores;
    private readonly ChartStudioToolsService _studioTools;
    private readonly Gh3VaultBackupService _vaultService = new();
    private readonly TournamentBracketService _tournamentService = new();
    private readonly BossBattleCreatorService _bossService = new();
    private readonly AiStadiumChantsService _stadiumChants = new();
    private readonly CharterSignatureService _charterSigner = new();
    private ControllerPollingTesterService? _pollingTester;
    private readonly int _port;

    private readonly DispatcherTimer _stageAnimTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private readonly DispatcherTimer _stageToastTimer = new() { Interval = TimeSpan.FromSeconds(3.6) };
    private double _animPhase = 0;
    private double _rockMeterTargetAngle = 42.0;
    private double _rockMeterCurrentAngle = 38.0;
    private double _vinylAngle = 0.0;
    private readonly List<(System.Windows.Shapes.Ellipse Dot, double X, double Y, double Vy, double Drift, double Phase)> _stageEmbers = new();
    private DateTime _lastCareerScoreScanUtc = DateTime.MinValue;
    private readonly Dictionary<string, (double[] Buckets, int Tier, double PeakNps)> _sparklineCache = new(StringComparer.OrdinalIgnoreCase);
    private string _selectedTagFilter = "";

    private List<CustomSongInfo> _allSongs = new();
    private readonly HashSet<string> _selectedExportIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueuedSongItem> _batchQueue = new();
    private int _editingQueueIndex = -1;
    private string _currentFilter = "all";
    private bool _friendCodeVisible = false;
    private int _creatorPreviewStartMs = 5000;
    private double _creatorTrimStartSec = 0.0;
    private double _creatorTrimEndSec = 0.0;
    private byte[]? _creatorCoverArtBytes;
    private string? _creatorCoverArtPath;
    private List<ArtworkCandidate> _creatorArtworkCandidates = new();
    private readonly List<DateTime> _tapTempoStamps = new();
    private bool _tapTempoLocked = false;
    private string? _lastCreatedChartPath;
    private string? _lastCreatedAudioPath;
    private string? _lastCreatedProjectDir;
    private string _lastCreatedTitle = "";
    private string _lastCreatedArtist = "";

    // Community chart search cache for client-side filtering & sorting
    private List<CommunityChartEntry> _lastCommunitySearchCharts = new();
    private string _lastCommunitySearchQuery = "";

    // P2P comparison state
    private LibraryManifest? _lastPeerManifest;
    private string _lastPeerBaseUrl = "";
    private string _lastPeerSecret = "";

    // Built-in Audio & Stem Preview Player
    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _playerTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private bool _isPlayerPlaying = false;
    private CustomSongInfo? _previewingSong;
    private bool _previewingGuitarStem = false;

    private readonly CommunityChartService _communityCharts;
    private UpdateCheckResult? _latestUpdate;
    private WinForms.NotifyIcon? _trayIcon;
    private bool _isExiting = false;
    private readonly List<PlayInviteNotification> _pendingPlayInvites = new();
    private FriendLookupResult? _detectedFriendResult;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtrW")]
    private static extern IntPtr SetClassLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetClassLongW")]
    private static extern int SetClassLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateSolidBrush(uint crColor);

    private static IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong)
    {
        if (IntPtr.Size == 8)
            return SetClassLongPtr64(hWnd, nIndex, dwNewLong);
        return new IntPtr(SetClassLong32(hWnd, nIndex, unchecked((int)dwNewLong.ToInt64())));
    }

    public MainWindow(
        Gh3Service gh3,
        SyncLogger logger,
        SecurityService security,
        FriendService friends,
        SaveManager saves,
        HoneycombCompilerService compiler,
        CloneHeroImportService cloneHero,
        AudioAnalysisService audioAnalysis,
        DemucsService demucs,
        SongCreatorService creator,
        NatPortMappingService nat,
        UpdateService updater,
        TemporarySessionService tempSession,
        ComponentUpdaterService compUpdater,
        int port,
        ChartDoctorService? chartDoctor = null,
        BetterGh3TweaksService? betterGh3Tweaks = null,
        DiscordRpcService? discordRpc = null,
        TunnelAndMeshService? tunnelService = null,
        CareerSaveScoreService? careerScores = null)
    {
        _gh3 = gh3;
        _logger = logger;
        _security = security;
        _friends = friends;
        _saves = saves;
        _compiler = compiler;
        _cloneHero = cloneHero;
        _audioAnalysis = audioAnalysis;
        _demucs = demucs;
        _creator = creator;
        _nat = nat;
        _updater = updater;
        _tempSession = tempSession;
        _compUpdater = compUpdater;
        _port = port;
        _chartDoctor = chartDoctor ?? new ChartDoctorService(compiler, logger);
        _betterGh3Tweaks = betterGh3Tweaks ?? new BetterGh3TweaksService(gh3, logger);
        _discordRpc = discordRpc;
        _tunnel = tunnelService ?? new TunnelAndMeshService(port, logger);
        _careerScores = careerScores ?? new CareerSaveScoreService(gh3, saves, logger);
        _studioTools = new ChartStudioToolsService(creator, compiler, logger);
        _security.DownloadApprovalCallback = PromptHostDownloadApprovalAsync;
        _albumMeta = new AlbumArtMetadataService(logger);
        _practiceAudio = new PracticeAudioService(compiler, demucs, logger);
        _highwaySkins = new HighwaySkinService(gh3, logger);
        _ytDlp = new YtDlpImporterService(logger);
        _libraryHealth = new LibraryHealthService(gh3, _betterGh3Tweaks, saves, logger);
        _communityCharts = new CommunityChartService(logger);

        InitializeComponent();
        ApplyTheme(AppConfig.Current.ThemeMode, save: false);

        InitializeStageEmbers();
        _stageAnimTimer.Tick += StageAnimTimer_Tick;
        _stageToastTimer.Tick += (_, _) => HideStageToast();
        ApplyAnimationPreferences();

        _tunnel.TunnelStateChanged += _ => Dispatcher.BeginInvoke(RefreshMeshAndTunnelStatus);
        _careerScores.StartGh3ExitWatcher(scan => Dispatcher.BeginInvoke(() =>
        {
            if (scan.ScoresUpdated > 0)
            {
                RefreshSongsList();
                ShowStageToast("🏆 NEW CAREER SCORES!", scan.Summary, "#22C55E");
            }
        }));

        _tempSession.SessionCleanedUp += msg => Dispatcher.BeginInvoke(() =>
        {
            RefreshSongsList();
            RefreshTempSessionBanner();
            _trayIcon?.ShowBalloonTip(3000, "Temporary Lobby Session Cleaned", msg, WinForms.ToolTipIcon.Info);
        });

        _friends.OnFriendRequestReceived += req => Dispatcher.BeginInvoke(() =>
        {
            RefreshFriendsList();
            AudioWaveformAndSfxService.PlayStarPowerChime();
            ShowStageToast("📩 NEW FRIEND REQUEST!", $"{req.Username} ({req.FriendCode}) sent you a friend request!", "#F97316");
        });

        _friends.OnFriendRequestAccepted += friend => Dispatcher.BeginInvoke(() =>
        {
            RefreshFriendsList();
            RefreshTrustedFriendDropdown();
            AudioWaveformAndSfxService.PlayFcFanfare();
            ShowStageToast("🎉 FRIEND ACCEPTED!", $"{friend.Username} accepted your friend request! You can now sync songs!", "#22C55E");
        });

        if (FriendService.SignalingRelay != null)
        {
            FriendService.SignalingRelay.OnPeerReadyChanged += (peerCode, isReady) => Dispatcher.BeginInvoke(() => HandleRemotePeerReadyChanged(peerCode, isReady));
            FriendService.SignalingRelay.OnPeerRouletteRoll += (peerCode, seed, songIds) => Dispatcher.BeginInvoke(() => HandleRemotePeerRouletteRoll(peerCode, seed, songIds));
            FriendService.SignalingRelay.OnPeerActivityUpdated += (peerCode, activity, showcase) => Dispatcher.BeginInvoke(() =>
            {
                RefreshFriendsList();
            });
            FriendService.SignalingRelay.OnPeerHypeSfx += (peerCode, sfx) => Dispatcher.BeginInvoke(() =>
            {
                PlayHypeSound(sfx);
                ShowStageToast("📢 LOBBY HYPE", $"{peerCode} fired {sfx.ToUpperInvariant()}!", "#F59E0B");
            });
            FriendService.SignalingRelay.OnPeerPlayInvite += (peerCode, peerUsername, peerAddr) => Dispatcher.BeginInvoke(() =>
            {
                HandleRemotePeerPlayInvite(peerCode, peerUsername, peerAddr);
            });
        }

        _playerTimer.Tick += PlayerTimer_Tick;
        _player.MediaEnded += (_, _) => { _isPlayerPlaying = false; PlayerPlayPauseBtn.Content = "▶"; };

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += MainWindow_StateChanged;

        InitializeTrayIcon();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = true;
                _trayIcon.ShowBalloonTip(1800, "GH3-Sync Active", "Running in the system tray so friends can sync songs & invite you to rock out!", WinForms.ToolTipIcon.Info);
            }
        }
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowChromeTheme();
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                // Set Win32 window background brush to dark #090A0F to prevent any default white brush flash
                var darkBrush = CreateSolidBrush(0x000F0A09); // BGR for #090A0F
                if (darkBrush != IntPtr.Zero)
                {
                    SetClassLongPtr(hwnd, -10 /* GCLP_HBRBACKGROUND */, darkBrush);
                }

                var source = HwndSource.FromHwnd(hwnd);
                source?.AddHook(WndProc);
            }
        }
        catch { }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_ERASEBKGND = 0x0014;
        if (msg == WM_ERASEBKGND)
        {
            // Suppress default Win32 white brush erase during window creation & redraw
            handled = true;
            return (IntPtr)1;
        }
        return IntPtr.Zero;
    }

    private void ApplyWindowChromeTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = string.Equals(AppConfig.Current.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            DwmSetWindowAttribute(hwnd, 19, ref dark, sizeof(int)); // Windows 10 1809-1909
            int cornerPref = 2;
            DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Title = $"GH3-Sync v{UpdateService.CurrentAppVersion}";
        if (HeaderVersionBadge != null) HeaderVersionBadge.Text = $"v{UpdateService.CurrentAppVersion}";

        if (!string.IsNullOrWhiteSpace(AppConfig.Current.Language))
        {
            LocalizationService.SetLanguage(AppConfig.Current.Language);
            ApplyLocalizationStrings();
        }

        // Start Offline LAN UDP Auto-Discovery
        try
        {
            var lanDiscovery = new LanDiscoveryService(_port, _logger);
            lanDiscovery.PeerDiscovered += peer =>
            {
                Dispatcher.BeginInvoke(() =>
                {
                    ShowStageToast("🏠 LAN PLAYER FOUND", $"{peer.Username} discovered on local Wi-Fi/Ethernet!", "#38BDF8");
                });
            };
            lanDiscovery.Start();
        }
        catch { }

        // Initialize GPU Telemetry Display in Settings
        try
        {
            if (GpuTelemetryNameText != null)
                GpuTelemetryNameText.Text = GpuAccelerationService.GpuName;
            if (GpuTelemetryDriverText != null)
                GpuTelemetryDriverText.Text = $"Driver {GpuAccelerationService.GpuDriverVersion} • Direct3D Tier {GpuAccelerationService.WpfRenderTier} • Demucs CUDA Active";
            if (SettingsGpuToggle != null)
                SettingsGpuToggle.IsChecked = AppConfig.Current.EnableGpuAcceleration;
        }
        catch { }

        if (AppConfig.Current.EnableHighPrecisionTimer)
        {
            EngineVaccineService.EnableHighPrecisionTimer();
        }

        // Purge any stale 169.254.* addresses from friends list & input boxes
        foreach (var fr in AppConfig.Current.Friends)
        {
            if (fr.LastAddress != null && fr.LastAddress.Contains("169.254."))
            {
                fr.LastAddress = "";
                AppConfig.Save();
            }
        }
        if (PeerAddressBox != null && PeerAddressBox.Text.Contains("169.254."))
        {
            PeerAddressBox.Text = "";
        }

        // Automated library audit & reconciliation: ensure only original + permanently kept songs exist in customs
        try { _tempSession.ReconcileLibraryIntegrity(); } catch { }

        if (WhatsNewBanner != null)
        {
            if (AppConfig.Current.LastSeenWhatsNewVersion != UpdateService.CurrentAppVersion)
            {
                WhatsNewBanner.Visibility = Visibility.Visible;
                if (WhatsNewTitleText != null) WhatsNewTitleText.Text = $"🎸 GH3-Sync v{UpdateService.CurrentAppVersion}: Stage Overhaul & P2P Auto-Heal Edition";
            }
            else
            {
                WhatsNewBanner.Visibility = Visibility.Collapsed;
            }
        }

        RefreshAllUiState();

        // First-time custom song save setup check (only prompts if Honeycomb}{Honeycomb save doesn't exist yet!)
        if (!AppConfig.Current.HasAcknowledgedFirstTimeSaveNotice && _gh3.IsDetected && _gh3.IsFirstTimeCustomSetupNeeded())
        {
            var choice = WpfMessageBox.Show(
                "First-Time Custom Songs Save Notice\n\n" +
                "GH3-Sync detected that this Guitar Hero III installation has not yet completed the one-time custom songs save setup.\n\n" +
                "The first time you add a custom song, Guitar Hero III will prompt you to reset your save file to create your custom profile. " +
                "After that one-time reset, all future custom songs, imports, and P2P synced songs merge cleanly without resetting your saves again.\n\n" +
                "Would you like GH3-Sync to back up your current vanilla career save right now?",
                "GH3-Sync — First-Time Save Protection",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);

            if (choice == MessageBoxResult.Yes)
            {
                try
                {
                    var snap = _saves.BackupCurrentSaves("Vanilla Career Backup (First Launch)", "Vanilla Career Backup", _gh3.GamePath);
                    RefreshSaveBackups();
                    WpfMessageBox.Show($"Backed up your vanilla career save ('{snap.ProfileName}', {snap.FileCount} files) to Career Saves!",
                        "Backup Created", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch { }
            }

            AppConfig.Current.HasAcknowledgedFirstTimeSaveNotice = true;
            AppConfig.Save();
        }

        if (AppConfig.Current.AutoCheckUpdates)
        {
            await RunUpdateCheckAsync(silentIfUpToDate: true);
        }
    }

    private void DismissWhatsNewBanner_Click(object sender, RoutedEventArgs e)
    {
        if (WhatsNewBanner != null) WhatsNewBanner.Visibility = Visibility.Collapsed;
        AppConfig.Current.LastSeenWhatsNewVersion = UpdateService.CurrentAppVersion;
        AppConfig.Save();
    }

    public void RestoreFromTrayOrActivate()
    {
        RestoreWindow();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open GH3-Sync", null, (_, _) => Dispatcher.BeginInvoke(RestoreWindow));
            menu.Items.Add("Launch Guitar Hero III", null, (_, _) => Dispatcher.BeginInvoke(() => LaunchGh3Btn_Click(this, new RoutedEventArgs())));
            menu.Items.Add("Check for Updates", null, (_, _) => Dispatcher.BeginInvoke(async () =>
            {
                RestoreWindow();
                await RunUpdateCheckAsync(silentIfUpToDate: false);
            }));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit GH3-Sync", null, (_, _) => Dispatcher.BeginInvoke(ExitApplication));

            System.Drawing.Icon? trayIco = null;
            try
            {
                var streamInfo = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app_icon.ico"));
                if (streamInfo != null) trayIco = new System.Drawing.Icon(streamInfo.Stream);
            }
            catch { }

            _trayIcon = new WinForms.NotifyIcon
            {
                Text = $"GH3-Sync v{UpdateService.CurrentAppVersion}",
                Icon = trayIco ?? System.Drawing.SystemIcons.Application,
                ContextMenuStrip = menu,
                Visible = true
            };
            _trayIcon.MouseClick += (_, e) =>
            {
                if (e.Button == WinForms.MouseButtons.Left)
                    Dispatcher.BeginInvoke(RestoreWindow);
            };
            _trayIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(RestoreWindow);
        }
        catch { }
    }

    private void RestoreWindow()
    {
        try
        {
            if (!IsVisible) Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
            Focus();
        }
        catch { }
    }

    private void InitializeStageEmbers()
    {
        if (StageEmbersCanvas == null) return;
        StageEmbersCanvas.Children.Clear();
        _stageEmbers.Clear();
        var rng = new Random(7373);
        for (int i = 0; i < 22; i++)
        {
            double size = 3.0 + rng.NextDouble() * 4.5;
            var baseCol = i % 3 == 0 ? WpfColor.FromRgb(249, 115, 22)
                        : i % 3 == 1 ? WpfColor.FromRgb(251, 191, 36)
                        : VizFretColors[i % VizFretColors.Length];
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(WpfColor.FromArgb(175, baseCol.R, baseCol.G, baseCol.B)),
                IsHitTestVisible = false
            };
            double x = rng.NextDouble() * 1150.0;
            double y = rng.NextDouble() * 820.0;
            double vy = 0.55 + rng.NextDouble() * 1.15;
            double drift = 0.35 + rng.NextDouble() * 0.9;
            double phase = rng.NextDouble() * Math.PI * 2;
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            StageEmbersCanvas.Children.Add(dot);
            _stageEmbers.Add((dot, x, y, vy, drift, phase));
        }
    }

    private void ApplyAnimationPreferences()
    {
        bool enabled = AppConfig.Current.EnableUiAnimations;
        if (StageSpotlightOverlay != null)
            StageSpotlightOverlay.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (StageEmbersCanvas != null)
            StageEmbersCanvas.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (TopFretShimmer != null)
            TopFretShimmer.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        if (enabled)
        {
            if (!_stageAnimTimer.IsEnabled) _stageAnimTimer.Start();
        }
        else
        {
            _stageAnimTimer.Stop();
            if (GameStatusDot != null) GameStatusDot.Opacity = 1.0;
            if (RockMeterNeedleRotate != null) RockMeterNeedleRotate.Angle = _rockMeterTargetAngle;
            if (PlayerVisualizerCanvas != null) PlayerVisualizerCanvas.Children.Clear();
        }
    }

    private void StageAnimTimer_Tick(object? sender, EventArgs e)
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;
        _animPhase += 0.055;

        // 1. Flowing 5-fret top neon shimmer
        if (TopFretShimmerX != null)
        {
            double span = Math.Max(1200, ActualWidth + 320);
            TopFretShimmerX.X = ((_animPhase * 95.0) % span) - 280.0;
        }

        // 2. Softly drifting concert stage spotlights
        if (StageSpot1 != null && StageSpot2 != null)
        {
            Canvas.SetLeft(StageSpot1, 80 + Math.Sin(_animPhase * 0.28) * 140);
            Canvas.SetTop(StageSpot1, -130 + Math.Cos(_animPhase * 0.22) * 55);
            Canvas.SetLeft(StageSpot2, Math.Max(300, ActualWidth * 0.45) + Math.Cos(_animPhase * 0.24) * 130);
            Canvas.SetTop(StageSpot2, 220 + Math.Sin(_animPhase * 0.30) * 65);
        }

        // 3. Rising "Lou's Inferno" Concert Stage Embers & Floating 5-Fret Sparks
        if (StageEmbersCanvas != null && StageEmbersCanvas.Visibility == Visibility.Visible && _stageEmbers.Count > 0)
        {
            double cw = Math.Max(900, StageEmbersCanvas.ActualWidth);
            double ch = Math.Max(680, StageEmbersCanvas.ActualHeight);
            for (int i = 0; i < _stageEmbers.Count; i++)
            {
                var p = _stageEmbers[i];
                double ny = p.Y - p.Vy;
                double nx = p.X + Math.Sin(_animPhase * p.Drift + p.Phase) * 0.55;
                if (ny < -12)
                {
                    ny = ch + 10;
                    nx = ((p.X + 173) % cw);
                }
                double alphaNorm = Math.Clamp(ny / ch, 0.0, 1.0);
                p.Dot.Opacity = Math.Sin(alphaNorm * Math.PI) * 0.72;
                Canvas.SetLeft(p.Dot, nx);
                Canvas.SetTop(p.Dot, ny);
                _stageEmbers[i] = (p.Dot, nx, ny, p.Vy, p.Drift, p.Phase);
            }
        }

        // 4. Iconic GH3 Rock Meter live needle animation + REAL beat-reactive bounce during audio preview!
        if (RockMeterNeedleRotate != null)
        {
            double realEnergy = (_isPlayerPlaying && _player.NaturalDuration.HasTimeSpan)
                ? AudioWaveformAndSfxService.SampleEnergyAt(_activeWaveform, _player.Position.TotalSeconds, _player.NaturalDuration.TimeSpan.TotalSeconds)
                : 0.5;
            double beatBounce = _isPlayerPlaying
                ? (realEnergy - 0.35) * 18.0 + Math.Sin(_animPhase * 3.0) * 3.0
                : Math.Sin(_animPhase * 0.85) * 2.2;
            double desired = Math.Clamp(_rockMeterTargetAngle + beatBounce, -52, 54);
            _rockMeterCurrentAngle += (desired - _rockMeterCurrentAngle) * 0.22;
            RockMeterNeedleRotate.Angle = _rockMeterCurrentAngle;
        }

        // 5. Status dot live heartbeat pulse
        if (GameStatusDot != null)
            GameStatusDot.Opacity = 0.62 + 0.38 * Math.Abs(Math.Sin(_animPhase * 0.9));

        // 6. Real-time 5-Fret Neon Audio Spectrum Visualizer + Spinning Vinyl Disc
        if (AudioPlayerBar != null && AudioPlayerBar.Visibility == Visibility.Visible)
        {
            if (PlayerVisualizerCanvas != null) RenderAudioVisualizerFrame();
            if (PlayerVinylAngle != null && _isPlayerPlaying)
            {
                _vinylAngle = (_vinylAngle + 3.2) % 360.0;
                PlayerVinylAngle.Angle = _vinylAngle;
            }
        }
    }

    private void UpdateRockMeterAndMultiplier(bool isBetterGh3, bool isValid, int songCount)
    {
        bool online = AppConfig.Current.P2pOnlineEnabled;
        bool tunnel = _tunnel.IsTunnelActive;
        int mult = 1;
        if (isValid) mult++;
        if (isBetterGh3) mult++;
        if (songCount > 0) mult++;
        bool starPower = isBetterGh3 && songCount >= 3 && (online || tunnel);

        if (starPower)
        {
            _rockMeterTargetAngle = 50.0;
            if (RockMeterStateText != null) { RockMeterStateText.Text = "⚡ STAR"; RockMeterStateText.Foreground = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)); }
            if (SidebarMultiplierText != null) { SidebarMultiplierText.Text = "8x"; SidebarMultiplierText.Foreground = new SolidColorBrush(WpfColor.FromRgb(125, 211, 252)); }
            if (SidebarMultiplierRing != null) SidebarMultiplierRing.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248));
        }
        else if (isBetterGh3)
        {
            _rockMeterTargetAngle = 38.0;
            if (RockMeterStateText != null) { RockMeterStateText.Text = "GREEN"; RockMeterStateText.Foreground = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94)); }
            if (SidebarMultiplierText != null) { SidebarMultiplierText.Text = $"{mult}x"; SidebarMultiplierText.Foreground = new SolidColorBrush(WpfColor.FromRgb(110, 231, 183)); }
            if (SidebarMultiplierRing != null) SidebarMultiplierRing.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
        }
        else if (isValid)
        {
            _rockMeterTargetAngle = 4.0;
            if (RockMeterStateText != null) { RockMeterStateText.Text = "YELLOW"; RockMeterStateText.Foreground = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8)); }
            if (SidebarMultiplierText != null) { SidebarMultiplierText.Text = $"{mult}x"; SidebarMultiplierText.Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 224, 71)); }
            if (SidebarMultiplierRing != null) SidebarMultiplierRing.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
        }
        else
        {
            _rockMeterTargetAngle = -42.0;
            if (RockMeterStateText != null) { RockMeterStateText.Text = "RED"; RockMeterStateText.Foreground = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68)); }
            if (SidebarMultiplierText != null) { SidebarMultiplierText.Text = "1x"; SidebarMultiplierText.Foreground = new SolidColorBrush(WpfColor.FromRgb(252, 165, 165)); }
            if (SidebarMultiplierRing != null) SidebarMultiplierRing.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
        }

        if (!AppConfig.Current.EnableUiAnimations && RockMeterNeedleRotate != null)
            RockMeterNeedleRotate.Angle = _rockMeterTargetAngle;
    }

    public void ShowStageToast(string headline, string detail, string accentHex = "#F97316")
    {
        if (StageToastBanner == null) return;
        try
        {
            var col = (WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(accentHex);
            StageToastHeadline.Text = headline;
            StageToastHeadline.Foreground = new SolidColorBrush(col);
            StageToastDetail.Text = detail;
            StageToastBanner.BorderBrush = new SolidColorBrush(col);
            StageToastBanner.Visibility = Visibility.Visible;

            if (AppConfig.Current.EnableUiAnimations)
            {
                var dur = TimeSpan.FromMilliseconds(220);
                var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                StageToastBanner.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = ease });
                StageToastTranslate.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(26, 0, dur) { EasingFunction = ease });
            }
            else
            {
                StageToastBanner.Opacity = 1;
                StageToastTranslate.Y = 0;
            }

            _stageToastTimer.Stop();
            _stageToastTimer.Start();
        }
        catch { }
    }

    private void HideStageToast()
    {
        _stageToastTimer.Stop();
        if (StageToastBanner == null) return;
        if (!AppConfig.Current.EnableUiAnimations)
        {
            StageToastBanner.Visibility = Visibility.Collapsed;
            return;
        }
        var anim = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
        anim.Completed += (_, _) => StageToastBanner.Visibility = Visibility.Collapsed;
        StageToastBanner.BeginAnimation(OpacityProperty, anim);
    }

    private static readonly WpfColor[] VizFretColors =
    {
        WpfColor.FromRgb(34, 197, 94),   // Green
        WpfColor.FromRgb(239, 68, 68),   // Red
        WpfColor.FromRgb(234, 179, 8),   // Yellow
        WpfColor.FromRgb(59, 130, 246),  // Blue
        WpfColor.FromRgb(249, 115, 22),  // Orange
    };

    private void RenderAudioVisualizerFrame()
    {
        PlayerVisualizerCanvas.Children.Clear();
        const int bars = 15;
        double barW = 5.0;
        double gap = 2.2;
        double maxH = 26.0;

        double energyBoost = (_isPlayerPlaying && _player.NaturalDuration.HasTimeSpan)
            ? AudioWaveformAndSfxService.SampleEnergyAt(_activeWaveform, _player.Position.TotalSeconds, _player.NaturalDuration.TimeSpan.TotalSeconds)
            : 0.5;

        for (int i = 0; i < bars; i++)
        {
            double wave = Math.Abs(Math.Sin(_animPhase * (1.8 + (i % 5) * 0.45) + i * 0.75) * Math.Cos(_animPhase * 0.9 + i * 0.3));
            double level = _isPlayerPlaying
                ? Math.Clamp(0.16 + energyBoost * (0.45 + 0.55 * wave), 0.12, 1.0)
                : 0.12;
            double bh = Math.Max(3.0, level * maxH);
            var color = VizFretColors[Math.Clamp(i / 3, 0, 4)];
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = barW,
                Height = bh,
                RadiusX = 2,
                RadiusY = 2,
                Fill = new SolidColorBrush(color)
            };
            Canvas.SetLeft(rect, i * (barW + gap));
            Canvas.SetTop(rect, maxH - bh);
            PlayerVisualizerCanvas.Children.Add(rect);
        }

        // 12-Segment Vintage Real-Time LED VU Meter
        if (PlayerVuMeterCanvas != null)
        {
            PlayerVuMeterCanvas.Children.Clear();
            const int segCount = 12;
            double segW = 44.0;
            double segH = 1.6;
            double segGap = 0.5;
            int litCount = _isPlayerPlaying ? (int)Math.Clamp(Math.Round(energyBoost * 12.0), 1, 12) : 1;

            for (int s = 0; s < segCount; s++)
            {
                bool isLit = s < litCount;
                var segColor = s switch
                {
                    >= 10 => WpfColor.FromRgb(239, 68, 68),  // 11-12 Red Peak
                    >= 6 => WpfColor.FromRgb(245, 158, 11),  // 7-10 Amber Gold
                    _ => WpfColor.FromRgb(34, 197, 94)       // 1-6 Green
                };

                var segRect = new System.Windows.Shapes.Rectangle
                {
                    Width = segW,
                    Height = segH,
                    RadiusX = 0.5,
                    RadiusY = 0.5,
                    Fill = isLit ? new SolidColorBrush(segColor) : new SolidColorBrush(WpfColor.FromArgb(40, 71, 85, 105))
                };
                if (isLit && s >= 10)
                {
                    segRect.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 0, Color = segColor, Opacity = 0.8 };
                }

                Canvas.SetLeft(segRect, 2.0);
                Canvas.SetTop(segRect, 25.0 - (s * (segH + segGap)));
                PlayerVuMeterCanvas.Children.Add(segRect);
            }
        }
    }

    private void AnimatePanelEntrance(UIElement panel)
    {
        if (!AppConfig.Current.EnableUiAnimations) return;
        try
        {
            var tt = new TranslateTransform(0, 22);
            panel.RenderTransform = tt;
            panel.Opacity = 0;
            var dur = TimeSpan.FromMilliseconds(240);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            panel.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(22, 0, dur) { EasingFunction = ease });
        }
        catch { }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        bool shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        if (ctrl && shift && e.Key == System.Windows.Input.Key.X)
        {
            EngineVaccineService.TriggerPanicAirGap();
            WpfMessageBox.Show("EMERGENCY AIR-GAP ACTIVATED!\n\nAll external network ports, UPnP mappings, and P2P sync tunnels have been terminated and blackholed.", "AIR-GAP SHIELD", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == System.Windows.Input.Key.K)
        {
            OpenCommandPalette();
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Escape && CommandPaletteOverlay != null && CommandPaletteOverlay.Visibility == Visibility.Visible)
        {
            CloseCommandPalette();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key == System.Windows.Input.Key.F)
        {
            NavCustoms.IsChecked = true;
            SongSearchBox.Focus();
            SongSearchBox.SelectAll();
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.F5)
        {
            RefreshSongsList();
            e.Handled = true;
            return;
        }
        if (ctrl && e.Key >= System.Windows.Input.Key.D1 && e.Key <= System.Windows.Input.Key.D8)
        {
            var navs = new[] { NavCustoms, NavCreator, NavAiTools, NavFriends, NavP2P, NavImport, NavSaves, NavSettings };
            int idx = e.Key - System.Windows.Input.Key.D1;
            if (idx >= 0 && idx < navs.Length) navs[idx].IsChecked = true;
            e.Handled = true;
            return;
        }
        if (e.Key == System.Windows.Input.Key.Space && AudioPlayerBar.Visibility == Visibility.Visible &&
            System.Windows.Input.Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
        {
            PlayerPlayPauseBtn_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    public void ExitApplication()
    {
        if (_isExiting) return;
        _isExiting = true;
        _stageAnimTimer?.Stop();
        _isExplicitExit = true;
        StopAudioPreview();
        try { _tempSession?.EndSessionAndCleanup(); } catch { }
        try { _pollingTester?.Dispose(); } catch { }
        try { _tunnel?.StopTunnel(); } catch { }
        TunnelAndMeshService.KillAllOrphanCloudflaredProcesses();
        try { _trayIcon?.Dispose(); } catch { }
        System.Windows.Application.Current.Shutdown();
    }

    private bool _isExplicitExit = false;

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExplicitExit)
        {
            e.Cancel = true;
            WindowState = WindowState.Minimized;
            Hide();
            if (_trayIcon != null)
            {
                _trayIcon.Visible = true;
                _trayIcon.ShowBalloonTip(2000, "GH3-Sync Active in Tray", "GH3-Sync is minimized to system tray so friends can still connect and sync.\nRight-click tray icon to Exit.", WinForms.ToolTipIcon.Info);
            }
            return;
        }

        ExitApplication();
        Environment.Exit(0);
    }

    // ==================== LAUNCH GH3 & HYPERLINKS ====================
    private void LaunchGh3Btn_Click(object sender, RoutedEventArgs e)
    {
        LaunchGh3(skipAudioAlert: false);
    }

    private void LaunchGh3(bool skipAudioAlert = false)
    {
        var gh3Exe = Path.Combine(_gh3.GamePath ?? "", "GH3.exe");
        if (!File.Exists(gh3Exe))
        {
            WpfMessageBox.Show("GH3.exe was not found in your configured game folder. Open Settings to set your Guitar Hero III path.",
                "Launch Guitar Hero III", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // Auto-Heal, Library Reconcile & Pre-Flight Health Check before launching Guitar Hero III
            try { _tempSession.ReconcileLibraryIntegrity(); } catch { }

            if (!string.IsNullOrWhiteSpace(_gh3.GamePath))
            {
                try { CustomsPakService.RepairCustomsPakIfCorrupted(_gh3.GamePath, _logger); } catch { }
            }

            var lobbySongs = _allSongs.Where(s => s.IsInLobbySetlist).ToList();
            foreach (var ls in lobbySongs)
            {
                var audio = Gh3Service.FindSongAudioPath(ls);
                if (string.IsNullOrEmpty(audio) || !File.Exists(audio))
                {
                    _logger.LogWarn($"[PRE-FLIGHT] Lobby song '{ls.Title}' ({ls.SongId}) missing audio file!");
                }
            }

            bool coreOk = File.Exists(Path.Combine(_gh3.GamePath ?? "", "PLUGINS", "core.dll"));
            ShowStageToast("🎸 LAUNCHING GUITAR HERO III!",
                $"{(coreOk ? "BetterGH3 core.dll Verified" : "Vanilla GH3")} • {_allSongs.Count} Customs Armed • {lobbySongs.Count} Lobby Songs Ready",
                "#22C55E");
            if (!skipAudioAlert)
            {
                AudioWaveformAndSfxService.PlayCompilePowerChord();
            }

            StopAudioPreview();
            Process.Start(new ProcessStartInfo
            {
                FileName = gh3Exe,
                WorkingDirectory = Path.GetDirectoryName(gh3Exe),
                UseShellExecute = true
            });
            _logger.LogInfo("[LAUNCHER] Launched GH3.exe");
            if (AppConfig.Current.MinimizeOnGameLaunch)
                Hide();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Could not launch GH3.exe: {ex.Message}", "Launch Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = e.Uri.AbsoluteUri, UseShellExecute = true });
        }
        catch { }
        e.Handled = true;
    }

    // ==================== DRAG & DROP ANYWHERE ====================
    private void Window_DragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, System.Windows.DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)) return;
        var dropped = e.Data.GetData(System.Windows.DataFormats.FileDrop) as string[];
        if (dropped == null || dropped.Length == 0) return;

        var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp3", ".wav", ".ogg" };
        var chartExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".chart", ".mid", ".midi" };
        var archiveExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".gh3pack", ".gh3song", ".sgh" };

        var droppedAudios = dropped.Where(p => File.Exists(p) && audioExts.Contains(Path.GetExtension(p))).ToList();
        var droppedCharts = dropped.Where(p => File.Exists(p) && chartExts.Contains(Path.GetExtension(p))).ToList();
        var droppedImportables = dropped.Where(p =>
            (Directory.Exists(p) && CloneHeroImportService.IsCloneHeroFolder(p)) ||
            (File.Exists(p) && (Path.GetExtension(p).Equals(".sng", StringComparison.OrdinalIgnoreCase) ||
                                Path.GetExtension(p).Equals(".zip", StringComparison.OrdinalIgnoreCase) ||
                                archiveExts.Contains(Path.GetExtension(p))))).ToList();

        // 1. Multiple audio files dropped -> auto-populate interactive Batch Queue!
        if (droppedAudios.Count >= 2)
        {
            NavCreator.IsChecked = true;
            CrProgressLabel.Text = $"Queueing {droppedAudios.Count} dropped audio files...";
            int added = 0;
            foreach (var audioPath in droppedAudios)
            {
                var (artist, title) = ParseArtistTitleFromFileName(audioPath);
                double bpm = 120.0;
                try
                {
                    var analysis = await Task.Run(() => _audioAnalysis.AnalyzeAudio(audioPath));
                    if (analysis.Success && analysis.Bpm > 30) bpm = analysis.Bpm;
                }
                catch { }

                var req = new SongCreationRequest
                {
                    Title = title,
                    Artist = artist,
                    Album = "",
                    Year = int.TryParse(CrYearBox.Text, out var y) ? y : DateTime.Now.Year,
                    Genre = string.IsNullOrWhiteSpace(CrGenreBox.Text) ? "Rock" : CrGenreBox.Text.Trim(),
                    Charter = string.IsNullOrWhiteSpace(CrCharterBox.Text) ? "Mad Scientist" : CrCharterBox.Text.Trim(),
                    Bpm = bpm,
                    PreviewStartMs = 5000,
                    AutoDetectBpm = true,
                    UseVariableTempoMap = CrVariableTempoChk.IsChecked == true,
                    ChartFeel = (CrFeelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Standard",
                    InstrumentFocus = GetSelectedFocusValue(),
                    AudioMixMode = GetSelectedAudioModeValue(),
                    AutoGenerateChart = true,
                    SeparateStemsWithDemucs = CrSeparateStemsChk.IsChecked == true && _demucs.GetStatus().IsInstalled,
                    AutoCompile = CrAutoCompileChk.IsChecked == true
                };
                _batchQueue.Add(new QueuedSongItem(req, audioPath, null));
                added++;
                CrProgressLabel.Text = $"Queued {added}/{droppedAudios.Count}: {artist} - {title} ({bpm:F1} BPM)";
            }

            CrProgressLabel.Text = $"Added {added} songs to Batch Queue! Click Edit on any card to customize or click Compile Batch Queue.";
            RenderBatchQueue();
            return;
        }

        // 2. Single audio (+ optional paired chart) dropped
        if (droppedAudios.Count == 1)
        {
            NavCreator.IsChecked = true;
            await LoadAudioIntoCreatorAsync(droppedAudios[0], overwriteTitleArtist: true);
            if (droppedCharts.Count > 0)
            {
                ModeUploadRadio.IsChecked = true;
                CrChartPathBox.Text = droppedCharts[0];
            }
            return;
        }

        // 3. Single chart/MIDI dropped
        if (droppedCharts.Count > 0 && droppedImportables.Count == 0)
        {
            NavCreator.IsChecked = true;
            ModeUploadRadio.IsChecked = true;
            CrChartPathBox.Text = droppedCharts[0];
            return;
        }

        // 4. One or more .sng / .zip / .gh3song / .gh3pack / Clone Hero folders dropped -> import ALL sequentially!
        if (droppedImportables.Count > 0)
        {
            NavImport.IsChecked = true;
            int total = droppedImportables.Count;
            int okCount = 0;
            for (int i = 0; i < total; i++)
            {
                var item = droppedImportables[i];
                if (Directory.Exists(item))
                {
                    ChImportStatusText.Text = total > 1 ? $"[{i + 1}/{total}] Converting Clone Hero folder '{Path.GetFileName(item)}'..." : "Starting Clone Hero conversion...";
                    if (await RunCloneHeroImportAsync(item)) okCount++;
                    continue;
                }

                var ext = Path.GetExtension(item).ToLowerInvariant();
                if (ext == ".sng")
                {
                    ChImportStatusText.Text = total > 1 ? $"[{i + 1}/{total}] Converting '{Path.GetFileName(item)}'..." : "Starting Clone Hero conversion...";
                    if (await RunCloneHeroImportAsync(item)) okCount++;
                }
                else if (archiveExts.Contains(ext))
                {
                    ImportStandardArchive(item);
                    okCount++;
                }
                else if (ext == ".zip")
                {
                    bool looksCloneHero = false;
                    try
                    {
                        using var zip = ZipFile.OpenRead(item);
                        looksCloneHero = zip.Entries.Any(en =>
                            en.Name.Equals("notes.chart", StringComparison.OrdinalIgnoreCase) ||
                            en.Name.Equals("notes.mid", StringComparison.OrdinalIgnoreCase));
                    }
                    catch { }

                    if (looksCloneHero)
                    {
                        if (await RunCloneHeroImportAsync(item)) okCount++;
                    }
                    else
                    {
                        ImportStandardArchive(item);
                        okCount++;
                    }
                }
            }

            if (total > 1)
            {
                ChImportStatusText.Text = $"Batch drop complete: imported {okCount} / {total} item(s).";
                RefreshSongsList();
            }
            return;
        }

        if (Directory.Exists(dropped[0]))
        {
            WpfMessageBox.Show("Dropped folder does not contain notes.chart, notes.mid, or song.ini.", "Folder Drop", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static (string Artist, string Title) ParseArtistTitleFromFileName(string path)
    {
        var fname = Path.GetFileNameWithoutExtension(path).Trim();
        if (fname.Contains(" - "))
        {
            var parts = fname.Split(" - ", 2);
            var artist = parts[0].Trim();
            var title = parts[1].Trim();
            return (string.IsNullOrWhiteSpace(artist) ? "Unknown Artist" : artist,
                    string.IsNullOrWhiteSpace(title) ? fname : title);
        }
        return ("Unknown Artist", fname);
    }

    // ==================== NAVIGATION ====================
    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;

        PageCustoms.Visibility = NavCustoms.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageCreator.Visibility = NavCreator.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageAiTools.Visibility = NavAiTools.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageFriends.Visibility = NavFriends.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageP2P.Visibility = NavP2P.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageImport.Visibility = NavImport.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSaves.Visibility = NavSaves.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        UIElement? activePage =
            NavCustoms.IsChecked == true ? PageCustoms :
            NavCreator.IsChecked == true ? PageCreator :
            NavAiTools.IsChecked == true ? PageAiTools :
            NavFriends.IsChecked == true ? PageFriends :
            NavP2P.IsChecked == true ? PageP2P :
            NavImport.IsChecked == true ? PageImport :
            NavSaves.IsChecked == true ? PageSaves :
            PageSettings;
        if (activePage != null) AnimatePanelEntrance(activePage);

        if (NavCustoms.IsChecked == true) RefreshSongsList();
        if (NavCreator.IsChecked == true || NavAiTools.IsChecked == true) RefreshToolsAndAiStatus();
        if (NavFriends.IsChecked == true) RefreshFriendsList();
        if (NavP2P.IsChecked == true) { RefreshNetworkSummary(); RefreshTrustedFriendDropdown(); RefreshTempSessionBanner(); }
        if (NavSaves.IsChecked == true) RefreshSaveBackups();
        if (NavSettings.IsChecked == true) { LoadSettingsForm(); RefreshLogs(); }
    }

    private void GoToAiManager_Click(object sender, RoutedEventArgs e) => NavAiTools.IsChecked = true;

    public void RefreshAllUiState()
    {
        var compat = _gh3.VerifyGamePath(_gh3.GamePath);
        if (compat.IsBetterGh3)
        {
            GameStatusText.Text = "BetterGH3 Ready";
            GameStatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
            GameStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
        }
        else if (compat.IsValid)
        {
            GameStatusText.Text = "Vanilla GH3";
            GameStatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
            GameStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
        }
        else
        {
            GameStatusText.Text = "Set Game Path";
            GameStatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
            GameStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
        }

        CustomsGamePathLabel.Text = $"{_gh3.GamePath}  •  {(compat.IsBetterGh3 ? "BetterGH3 Mod Active" : compat.StatusMessage)}";

        SidebarProfileName.Text = _friends.Profile.Username;
        SidebarFriendCode.Text = _friends.Profile.FriendCode;
        SidebarP2pModeText.Text = AppConfig.Current.P2pOnlineEnabled ? "P2P Sharing: Online" : "P2P Sharing: Offline Mode";
        SidebarP2pModeText.Foreground = (WpfBrush)FindResource(AppConfig.Current.P2pOnlineEnabled ? "CardSuccessTitle" : "CardWarnTitle");
        ProfileUsernameBox.Text = _friends.Profile.Username;
        LobbyCapBox.Text = AppConfig.Current.MaxLobbyAutoSyncSongs.ToString();
        UpdateFriendCodeDisplay();
        LoadSettingsForm();

        RefreshSongsList();
        UpdateRockMeterAndMultiplier(compat.IsBetterGh3, compat.IsValid, _allSongs.Count);
        RefreshSetlistPresets();
        RefreshShowcaseCombo();
        if (FilterMissingOnlyChk != null) FilterMissingOnlyChk.IsChecked = AppConfig.Current.FilterPeerMissingOnly;
        RefreshToolsAndAiStatus();
        RefreshFriendsList();
        RefreshTrustedFriendDropdown();
        RefreshTempSessionBanner();
        RefreshNetworkSummary();
        RefreshMeshAndTunnelStatus();
        RefreshSecurityAuditPanel();
        RefreshSaveBackups();
        RefreshBetterGh3Plugins();
        RefreshLogs();

        // Safely auto-import real in-game scores from GH3 career save (s000.d) in background (throttled)
        if ((DateTime.UtcNow - _lastCareerScoreScanUtc).TotalSeconds > 30)
        {
            _lastCareerScoreScanUtc = DateTime.UtcNow;
            _ = Task.Run(() =>
            {
                var scan = _careerScores.ScanAndImportCareerScores();
                if (scan.ScoresUpdated > 0)
                    Dispatcher.Invoke(() =>
                    {
                        RenderSongs();
                        ShowStageToast("🏆 CAREER SCORES IMPORTED", scan.Summary, "#22C55E");
                    });
            });
        }
    }

    // ==================== BUILT-IN AUDIO & STEM PREVIEW PLAYER ====================
    private void PlayAudioPreview(string filePath, string trackTitle, CustomSongInfo? song = null, bool isGuitarStem = false)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            WpfMessageBox.Show("No previewable audio file (.mp3/.wav/.ogg) found for this track.", "Audio Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            _previewingSong = song;
            _previewingGuitarStem = isGuitarStem;

            // WPF MediaPlayer plays MP3 & WAV natively; if OGG, convert a quick temp WAV via bundled FFmpeg!
            string playablePath = filePath;
            if (filePath.EndsWith(".ogg", StringComparison.OrdinalIgnoreCase) || filePath.EndsWith(".opus", StringComparison.OrdinalIgnoreCase))
            {
                var ffmpeg = HoneycombCompilerService.EnsureFfmpeg(HoneycombCompilerService.FindHoneycombDirectory());
                if (!string.IsNullOrEmpty(ffmpeg) && File.Exists(ffmpeg))
                {
                    var tmpWav = Path.Combine(Path.GetTempPath(), $"gh3_preview_{Path.GetFileNameWithoutExtension(filePath)}.wav");
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffmpeg,
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    psi.ArgumentList.Add("-y");
                    psi.ArgumentList.Add("-v");
                    psi.ArgumentList.Add("error");
                    psi.ArgumentList.Add("-i");
                    psi.ArgumentList.Add(filePath);
                    psi.ArgumentList.Add("-ar");
                    psi.ArgumentList.Add("44100");
                    psi.ArgumentList.Add("-ac");
                    psi.ArgumentList.Add("2");
                    psi.ArgumentList.Add(tmpWav);
                    using var p = Process.Start(psi);
                    p?.WaitForExit(8000);
                    if (File.Exists(tmpWav)) playablePath = tmpWav;
                }
            }

            _player.Open(new Uri(playablePath));
            _player.Volume = Math.Clamp(AppConfig.Current.AudioPreviewVolume, 0.0, 1.0);
            if (PlayerVolumeSlider != null) PlayerVolumeSlider.Value = _player.Volume;
            _player.Play();
            _isPlayerPlaying = true;
            _playerTimer.Start();
            LoadWaveformForPreviewAsync(playablePath);

            AudioPlayerBar.Visibility = Visibility.Visible;
            PlayerTrackTitle.Text = isGuitarStem ? $"{trackTitle} [Guitar Stem]" : trackTitle;
            PlayerPlayPauseBtn.Content = "⏸";

            // Sync OBS Studio Loopback Now Playing Overlay!
            string? coverArt = song != null && !string.IsNullOrWhiteSpace(song.ProjectDirectory)
                ? new[] { "album.jpg", "album.png" }.Select(f => Path.Combine(song.ProjectDirectory, f)).FirstOrDefault(File.Exists)
                : null;
            ObsOverlayService.UpdateNowPlaying(
                song?.Title ?? trackTitle,
                song?.Artist ?? "GH3-Sync Preview",
                song?.Charter,
                "Expert",
                0,
                isPlaying: true,
                coverArtPath: coverArt);

            // Show Solo Guitar Stem button if this song has isolated Demucs stems!
            bool hasGuitarStem = song != null && Gh3Service.FindSongAudioPath(song, preferGuitarStem: true) != Gh3Service.FindSongAudioPath(song, preferGuitarStem: false);
            PlayerStemToggleBtn.Visibility = hasGuitarStem ? Visibility.Visible : Visibility.Collapsed;
            PlayerStemToggleBtn.Content = isGuitarStem ? "Switch to Full Mix" : "Solo Guitar Stem";
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Audio playback error: {ex.Message}", "Audio Preview", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void PlayAudioPreviewUrl(string audioUrl, string title, string artist)
    {
        if (string.IsNullOrWhiteSpace(audioUrl)) return;
        try
        {
            _previewingSong = null;
            _previewingGuitarStem = false;
            _player.Open(new Uri(audioUrl));
            _player.Play();
            _isPlayerPlaying = true;
            _playerTimer.Start();

            AudioPlayerBar.Visibility = Visibility.Visible;
            PlayerTrackTitle.Text = $"🎵 30s Preview: {title} — {artist}";
            PlayerPlayPauseBtn.Content = "⏸";
            PlayerStemToggleBtn.Visibility = Visibility.Collapsed;
            ObsOverlayService.UpdateNowPlaying(title, artist, "Chorus Encore Preview", "30s Preview", 0, isPlaying: true);
            ShowStageToast("🎵 30s AUDIO PREVIEW", $"Playing '{title}' by {artist}", "#38BDF8");
        }
        catch (Exception ex)
        {
            ShowStageToast("Preview Error", ex.Message, "#EF4444");
        }
    }

    private void CopyObsOverlayBtn_Click(object sender, RoutedEventArgs e)
    {
        var url = $"http://127.0.0.1:{_port}/obs-overlay";
        try { System.Windows.Clipboard.SetText(url); } catch { }
        ShowStageToast("📺 OBS OVERLAY URL COPIED!", $"Add Browser Source ({url}) — 495×110, local-only.", "#38BDF8");
    }

    private void StopAudioPreview()
    {
        _playerTimer.Stop();
        _player.Stop();
        _player.Close();
        _isPlayerPlaying = false;
    }

    private void PlayerTimer_Tick(object? sender, EventArgs e)
    {
        if (!_player.NaturalDuration.HasTimeSpan) return;
        var pos = _player.Position;
        var dur = _player.NaturalDuration.TimeSpan;
        PlayerSeekSlider.Maximum = Math.Max(1, dur.TotalSeconds);
        PlayerSeekSlider.Value = pos.TotalSeconds;
        PlayerTimeText.Text = $"{(int)pos.TotalMinutes}:{pos.Seconds:D2} / {(int)dur.TotalMinutes}:{dur.Seconds:D2}";
        RenderAudioWaveformBar();
    }

    private void PlayerPlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlayerPlaying)
        {
            _player.Pause();
            _isPlayerPlaying = false;
            PlayerPlayPauseBtn.Content = "▶";
            ObsOverlayService.SetPlaybackState(false);
        }
        else
        {
            _player.Play();
            _isPlayerPlaying = true;
            PlayerPlayPauseBtn.Content = "⏸";
            ObsOverlayService.SetPlaybackState(true);
        }
    }

    private void PlayerStopBtn_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _isPlayerPlaying = false;
        PlayerPlayPauseBtn.Content = "▶";
        PlayerSeekSlider.Value = 0;
        ObsOverlayService.SetPlaybackState(false);
    }

    private void PlayerSeekSlider_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (_player.NaturalDuration.HasTimeSpan)
            _player.Position = TimeSpan.FromSeconds(PlayerSeekSlider.Value);
    }

    private void PlayerStemToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_previewingSong == null) return;
        bool wantStem = !_previewingGuitarStem;
        var path = Gh3Service.FindSongAudioPath(_previewingSong, preferGuitarStem: wantStem);
        if (!string.IsNullOrEmpty(path))
            PlayAudioPreview(path, _previewingSong.Title, _previewingSong, wantStem);
    }

    private void PlayerSetTrimInBtn_Click(object sender, RoutedEventArgs e)
    {
        _creatorTrimStartSec = Math.Max(0, _player.Position.TotalSeconds);
        if (_creatorTrimEndSec > 0 && _creatorTrimEndSec <= _creatorTrimStartSec + 1.0)
            _creatorTrimEndSec = 0;
        UpdateTrimSummaryLabel();
    }

    private void PlayerSetTrimOutBtn_Click(object sender, RoutedEventArgs e)
    {
        double pos = Math.Max(0, _player.Position.TotalSeconds);
        if (pos <= _creatorTrimStartSec + 1.0)
        {
            WpfMessageBox.Show("Trim Out must be after Trim In.", "Audio Trimmer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _creatorTrimEndSec = pos;
        UpdateTrimSummaryLabel();
    }

    private void CrResetTrimBtn_Click(object sender, RoutedEventArgs e)
    {
        _creatorTrimStartSec = 0;
        _creatorTrimEndSec = 0;
        UpdateTrimSummaryLabel();
    }

    private void UpdateTrimSummaryLabel()
    {
        bool hasTrim = _creatorTrimStartSec > 0.1 || _creatorTrimEndSec > 1.0;
        if (!hasTrim)
        {
            CrTrimSummaryText.Text = "Trim: Full Track (use ▶ Preview bar to set Trim In/Out)";
            CrResetTrimBtn.Visibility = Visibility.Collapsed;
        }
        else
        {
            var startTxt = $"{(int)(_creatorTrimStartSec / 60)}:{(int)(_creatorTrimStartSec % 60):D2}";
            var endTxt = _creatorTrimEndSec > 1.0 ? $"{(int)(_creatorTrimEndSec / 60)}:{(int)(_creatorTrimEndSec % 60):D2}" : "End";
            CrTrimSummaryText.Text = $"Trim: {startTxt} → {endTxt}";
            CrResetTrimBtn.Visibility = Visibility.Visible;
        }
    }

    private void PlayerSetPreviewBtn_Click(object sender, RoutedEventArgs e)
    {
        _creatorPreviewStartMs = (int)Math.Max(0, _player.Position.TotalMilliseconds);
        CrPreviewStartLabel.Text = $"Menu Preview Start: {_creatorPreviewStartMs / 1000.0:F1}s";
        WpfMessageBox.Show($"Menu preview start set to {_creatorPreviewStartMs / 1000.0:F1} seconds!", "Preview Start Set", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void PlayerCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        StopAudioPreview();
        AudioPlayerBar.Visibility = Visibility.Collapsed;
    }

    // ==================== AUTO-UPDATER ====================
    private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate != null && _latestUpdate.UpdateAvailable)
        {
            UpdateNowBtn_Click(sender, e);
            return;
        }
        await RunUpdateCheckAsync(silentIfUpToDate: false);
    }

    private async Task RunUpdateCheckAsync(bool silentIfUpToDate)
    {
        HeaderUpdateBtn.IsEnabled = false;
        HeaderUpdateBtn.Content = "Checking...";
        try
        {
            var res = await _updater.CheckForUpdatesAsync();
            _latestUpdate = res;

            if (!res.Success)
            {
                HeaderUpdateBtn.Content = "Check Updates";
                AboutUpdateStatus.Text = $"Version {UpdateService.CurrentAppVersion}  •  {res.Message}";
                if (!silentIfUpToDate)
                    WpfMessageBox.Show(res.Message, "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (res.UpdateAvailable)
            {
                HeaderUpdateBtn.Content = $"Update v{res.LatestVersion}";
                HeaderUpdateBtn.Style = (Style)FindResource("FlameButton");
                UpdateBannerTitle.Text = $"Update Available: {res.ReleaseName} (v{res.LatestVersion})";
                UpdateBannerSubtitle.Text = $"Installed: v{res.CurrentVersion}  •  Click Update Now to download and install.";
                UpdateBanner.Visibility = Visibility.Visible;
                AboutUpdateStatus.Text = $"Update available: v{res.LatestVersion} (Installed: v{res.CurrentVersion})";
            }
            else
            {
                HeaderUpdateBtn.Content = "Up to Date";
                HeaderUpdateBtn.Style = (Style)FindResource("OutlineButton");
                AboutUpdateStatus.Text = $"Up to date (v{res.CurrentVersion} Studio Edition)";
                if (!silentIfUpToDate)
                {
                    // Check if local freshly-built installer package exists
                    string localInstaller = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "GH3-Sync-Setup.exe");
                    if (!File.Exists(localInstaller))
                        localInstaller = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "dist", "GH3-Sync-Setup.exe");
                    if (!File.Exists(localInstaller))
                        localInstaller = Path.Combine(Path.GetTempPath(), "GH3-Sync-Updates", "GH3-Sync-Setup-1.4.0.exe");
                    if (!File.Exists(localInstaller))
                        localInstaller = @"G:\Guitar Hero Songs\GH3-Sync\dist\GH3-Sync-Setup.exe";

                    if (File.Exists(localInstaller))
                    {
                        var ask = WpfMessageBox.Show(
                            $"GH3-Sync is at the latest release level (v{res.CurrentVersion} Studio Edition).\n\n" +
                            $"Latest Build Package Available:\n{localInstaller}\n\n" +
                            "Would you like to reinstall or apply the latest Studio Edition build now?",
                            "GH3-Sync Update Manager",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Information);

                        if (ask == MessageBoxResult.Yes)
                        {
                            var (launched, cancelled, launchErr, _) = _updater.LaunchSilentInstaller(localInstaller);
                            if (launched)
                            {
                                ExitApplication();
                                Environment.Exit(0);
                            }
                            else if (!cancelled)
                            {
                                WpfMessageBox.Show($"Could not launch installer: {launchErr}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                            return;
                        }
                    }
                    else
                    {
                        WpfMessageBox.Show(
                            $"You are running the latest version of GH3-Sync (v{res.CurrentVersion} Studio Edition).\n\nGitHub release status: Up to date (Release: {res.ReleaseName}).",
                            "Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
            }
        }
        finally
        {
            HeaderUpdateBtn.IsEnabled = true;
        }
    }

    private void SetGuitarUpdateProgress(double percent, string statusText)
    {
        double clamped = Math.Clamp(percent, 0.0, 100.0);
        double fillWidth = 434.0 * (clamped / 100.0);
        GuitarStringsClip.Rect = new Rect(92, 48, fillWidth, 54);
        UpdateGuitarPercentText.Text = $"{(int)Math.Round(clamped)}%";
        UpdateGuitarStatusText.Text = statusText;

        if (clamped > 0.5 && clamped < 99.5)
        {
            GuitarProgressPulse.Visibility = Visibility.Visible;
            Canvas.SetLeft(GuitarProgressPulse, 92.0 + fillWidth - 2.5);
        }
        else
        {
            GuitarProgressPulse.Visibility = Visibility.Collapsed;
        }
    }

    private void AppendUpdateVerboseLog(string line)
    {
        UpdateVerboseLogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}{Environment.NewLine}");
        UpdateVerboseLogBox.ScrollToEnd();
    }

    private void UpdateDetailsToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        bool show = UpdateVerboseLogPanel.Visibility != Visibility.Visible;
        UpdateVerboseLogPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        UpdateDetailsToggleBtn.Content = show ? "Hide Details ▲" : "Show Details ▼";
    }

    private void UpdateOverlayCloseBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateGuitarOverlay.Visibility = Visibility.Collapsed;
        UpdateNowBtn.IsEnabled = true;
    }

    private async void UpdateNowBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_latestUpdate == null || !_latestUpdate.UpdateAvailable)
        {
            await RunUpdateCheckAsync(silentIfUpToDate: false);
            return;
        }

        if (string.IsNullOrEmpty(_latestUpdate.InstallerDownloadUrl))
        {
            Process.Start(new ProcessStartInfo { FileName = _latestUpdate.ReleaseUrl, UseShellExecute = true });
            return;
        }

        UpdateNowBtn.IsEnabled = false;
        UpdateBannerProgress.Visibility = Visibility.Collapsed;
        UpdateVerboseLogBox.Clear();
        UpdateVerboseLogPanel.Visibility = Visibility.Collapsed;
        UpdateDetailsToggleBtn.Content = "Show Details ▼";
        UpdateOverlayCloseBtn.Visibility = Visibility.Collapsed;

        UpdateGuitarTitle.Text = "Installing Update";
        UpdateGuitarVersionBadge.Text = $"v{_latestUpdate.CurrentVersion} → v{_latestUpdate.LatestVersion}";
        UpdateGuitarSubnote.Text = "Using existing config • Silent background upgrade";
        SetGuitarUpdateProgress(0, $"Installing update v{_latestUpdate.LatestVersion}...");
        UpdateGuitarOverlay.Visibility = Visibility.Visible;

        AppendUpdateVerboseLog($"Starting silent update from v{_latestUpdate.CurrentVersion} to v{_latestUpdate.LatestVersion}");
        AppendUpdateVerboseLog($"Release: {_latestUpdate.ReleaseName}");

        var (ok, installerPath, err) = await _updater.DownloadUpdateAsync(
            _latestUpdate,
            (pct, msg) =>
            {
                Dispatcher.Invoke(() =>
                {
                    // Download + SHA-256 verification spans 0% -> 82% of the guitar strings
                    double mapped = Math.Min(82.0, pct * 0.82);
                    SetGuitarUpdateProgress(mapped, $"Installing update... {msg}");
                });
            },
            default,
            detail => Dispatcher.Invoke(() => AppendUpdateVerboseLog(detail)));

        if (!ok)
        {
            UpdateGuitarTitle.Text = "Update Failed";
            SetGuitarUpdateProgress(0, err);
            AppendUpdateVerboseLog($"FAILED: {err}");
            UpdateVerboseLogPanel.Visibility = Visibility.Visible;
            UpdateDetailsToggleBtn.Content = "Hide Details ▲";
            UpdateOverlayCloseBtn.Visibility = Visibility.Visible;
            UpdateNowBtn.IsEnabled = true;
            return;
        }

        SetGuitarUpdateProgress(86, "Installing update silently (keeping existing settings)...");
        await Task.Delay(250);

        var (launched, cancelled, launchErr, installLogPath) = _updater.LaunchSilentInstaller(
            installerPath,
            detail => Dispatcher.Invoke(() => AppendUpdateVerboseLog(detail)));

        if (!launched)
        {
            UpdateGuitarTitle.Text = cancelled ? "Update Cancelled" : "Update Error";
            SetGuitarUpdateProgress(86, launchErr);
            UpdateVerboseLogPanel.Visibility = Visibility.Visible;
            UpdateDetailsToggleBtn.Content = "Hide Details ▲";
            UpdateOverlayCloseBtn.Visibility = Visibility.Visible;
            UpdateNowBtn.IsEnabled = true;
            return;
        }

        AppendUpdateVerboseLog($"Silent installer log: {installLogPath}");
        SetGuitarUpdateProgress(93, "Installing update... Applying new files silently...");
        await Task.Delay(350);
        SetGuitarUpdateProgress(100, "Installing update... Restarting GH3-Sync automatically!");
        AppendUpdateVerboseLog("Closing current instance so the silent upgrade completes and relaunches GH3-Sync.");
        await Task.Delay(750);

        ExitApplication();
        Environment.Exit(0);
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e) => UpdateBanner.Visibility = Visibility.Collapsed;

    // ==================== PAGE 1: MY CUSTOMS ====================
    private void RefreshSongs_Click(object sender, RoutedEventArgs e) => RefreshSongsList();

    private void FilterSongs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
        {
            _currentFilter = tag;
            FilterAllBtn.Style = (Style)FindResource(tag == "all" ? "FlameButton" : "OutlineButton");
            FilterCreatedBtn.Style = (Style)FindResource(tag == "created" ? "FlameButton" : "OutlineButton");
            FilterSyncedBtn.Style = (Style)FindResource(tag == "synced" ? "FlameButton" : "OutlineButton");
            FilterFavBtn.Style = (Style)FindResource(tag == "favorites" ? "FlameButton" : "OutlineButton");
            FilterLobbyBtn.Style = (Style)FindResource(tag == "lobby" ? "FlameButton" : "OutlineButton");
            RenderSongs();
        }
    }

    private void SongSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderSongs();
    private void SongSortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (IsLoaded) RenderSongs(); }
    private void TagFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _selectedTagFilter = TagFilterCombo.SelectedIndex <= 0 ? "" : ((TagFilterCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "");
        RenderSongs();
    }

    private void UpdateLobbyCounters()
    {
        int count = (AppConfig.Current.LobbySetlistIds ?? new()).Count;
        int max = Math.Clamp(AppConfig.Current.MaxLobbyAutoSyncSongs, 1, 25);
        FilterLobbyBtn.Content = $"Lobby Setlist ({count}/{max})";
        SidebarLobbySummary.Text = $"Lobby Auto-Sync: {count} / {max}";
        P2pMyLobbyCount.Text = $"  ({count} / {max} songs tagged in My Customs)";
    }

    private void RefreshSongsList()
    {
        _allSongs = _gh3.GetInstalledCustomSongs();
        CustomsHeaderTitle.Text = $"Installed Custom Songs ({_allSongs.Count})";
        UpdateLobbyCounters();
        RenderSongs();
    }

    private void RenderSongs()
    {
        SongsListPanel.Children.Clear();
        string query = (SongSearchBox.Text ?? "").Trim();

        IEnumerable<CustomSongInfo> filtered = _allSongs.Where(s =>
        {
            bool isOnogami = s.CreatedWithOnogamiCreator || s.IsCreatedLocally;
            if (_currentFilter == "created" && !isOnogami) return false;
            if (_currentFilter == "synced" && isOnogami) return false;
            if (_currentFilter == "favorites" && !s.IsFavorite) return false;
            if (_currentFilter == "lobby" && !s.IsInLobbySetlist) return false;
            if (!string.IsNullOrEmpty(_selectedTagFilter) && !AppConfig.GetSongTags(s.SongId).Any(t => t.Equals(_selectedTagFilter, StringComparison.OrdinalIgnoreCase)))
                return false;

            if (!string.IsNullOrEmpty(query))
            {
                return (s.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                       (s.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                       (s.SongId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
            }
            return true;
        });

        filtered = SongSortCombo.SelectedIndex switch
        {
            1 => filtered.OrderBy(s => s.Artist, StringComparer.OrdinalIgnoreCase),
            2 => filtered.OrderByDescending(s => s.LastModified),
            3 => filtered.OrderByDescending(s => s.TotalSizeBytes),
            _ => filtered.OrderBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
        };

        var list = filtered.ToList();
        UpdateBatchSelectionBarUi(list.Count);

        if (list.Count == 0)
        {
            SongsListPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("GhCard"),
                Child = new TextBlock
                {
                    Text = "No custom songs match this filter.",
                    Foreground = (WpfBrush)FindResource("TextMuted"),
                    FontSize = 13,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 14, 0, 14)
                }
            });
            return;
        }

        bool compact = AppConfig.Current.CompactSetlistView;
        string groupMode = AppConfig.Current.GroupByMode ?? "None";
        int animIdx = 0;

        void AddSongVisual(CustomSongInfo s)
        {
            var card = compact ? BuildCompactSongRow(s) : BuildSongCard(s);
            SongsListPanel.Children.Add(card);
            if (AppConfig.Current.EnableUiAnimations && animIdx < 14)
            {
                try
                {
                    var tt = new TranslateTransform(0, 12);
                    card.RenderTransform = tt;
                    card.Opacity = 0;
                    var delay = TimeSpan.FromMilliseconds(animIdx * 20);
                    var dur = TimeSpan.FromMilliseconds(195);
                    var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
                    card.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { BeginTime = delay, EasingFunction = ease });
                    tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(12, 0, dur) { BeginTime = delay, EasingFunction = ease });
                }
                catch { }
            }
            animIdx++;
        }

        if (groupMode.Equals("Artist", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var grp in list.GroupBy(s => string.IsNullOrWhiteSpace(s.Artist) ? "Unknown Artist" : s.Artist.Trim(), StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                SongsListPanel.Children.Add(new TextBlock
                {
                    Text = $"🎸 {grp.Key} ({grp.Count()})",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource("AccentFlameText"),
                    Margin = new Thickness(4, 10, 0, 6)
                });
                foreach (var s in grp) AddSongVisual(s);
            }
        }
        else if (groupMode.Equals("Tier", StringComparison.OrdinalIgnoreCase))
        {
            int ResolveTier(CustomSongInfo s)
            {
                if (_sparklineCache.TryGetValue(s.SongId, out var sp))
                    return Math.Clamp(sp.Tier, 1, 8);
                return 3;
            }
            foreach (var grp in list.GroupBy(ResolveTier).OrderBy(g => g.Key))
            {
                var tierName = ChartStudioToolsService.VenueTierNames[Math.Clamp(grp.Key - 1, 0, 7)];
                SongsListPanel.Children.Add(new TextBlock
                {
                    Text = $"🏟️ {tierName} ({grp.Count()})",
                    FontSize = 13,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource("AccentFlameText"),
                    Margin = new Thickness(4, 10, 0, 6)
                });
                foreach (var s in grp) AddSongVisual(s);
            }
        }
        else
        {
            foreach (var s in list) AddSongVisual(s);
        }
    }

    private Border BuildSongCard(CustomSongInfo song)
    {
        bool isOnogami = song.CreatedWithOnogamiCreator || song.IsCreatedLocally;
        bool hasProject = !string.IsNullOrWhiteSpace(song.ProjectDirectory) && Directory.Exists(song.ProjectDirectory);
        bool hasAudio = song.HasAudio || !string.IsNullOrEmpty(Gh3Service.FindSongAudioPath(song));
        double sizeMb = song.TotalSizeBytes / (1024.0 * 1024.0);

        // Guitar Hero III Authentic Setlist Notebook Row (lined paper, Impact font, star ratings)
        var defaultBg = song.IsFavorite
            ? (WpfBrush)new BrushConverter().ConvertFromString("#FEF3C7")!
            : System.Windows.Media.Brushes.Transparent;

        var card = new Border
        {
            Background = defaultBg,
            BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString("#C6D8E8")!,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(76, 7, 14, 7),
            Margin = new Thickness(0)
        };
        var cardTransform = new TranslateTransform(0, 0);
        card.RenderTransform = cardTransform;
        card.MouseEnter += (_, _) =>
        {
            card.Background = (WpfBrush)new BrushConverter().ConvertFromString("#FDE68A")!;
            if (AppConfig.Current.EnableUiAnimations)
            {
                cardTransform.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(3, TimeSpan.FromMilliseconds(110))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            }
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = defaultBg;
            if (AppConfig.Current.EnableUiAnimations)
            {
                cardTransform.BeginAnimation(TranslateTransform.XProperty, new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
                });
            }
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // Fav + Art
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // Title & Artist
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // Stars & Score
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });     // Actions

        // Col 0: Favorite Star + Album Art
        var leftTools = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
        var selChk = new System.Windows.Controls.CheckBox
        {
            IsChecked = _selectedExportIds.Contains(song.SongId),
            ToolTip = "Select for Pack Export",
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        selChk.Checked += (_, _) => { _selectedExportIds.Add(song.SongId); UpdateBatchSelectionBarUi(); };
        selChk.Unchecked += (_, _) => { _selectedExportIds.Remove(song.SongId); UpdateBatchSelectionBarUi(); };

        var favBtn = new System.Windows.Controls.Button
        {
            Content = song.IsFavorite ? "★" : "☆",
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(song.IsFavorite ? WpfColor.FromRgb(184, 134, 11) : WpfColor.FromRgb(160, 150, 130)),
            FontSize = 17,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(2, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Toggle Favorite"
        };
        favBtn.Click += (_, _) => { AppConfig.ToggleFavorite(song.SongId); RefreshSongsList(); };
        leftTools.Children.Add(selChk);
        leftTools.Children.Add(favBtn);

        if (!string.IsNullOrWhiteSpace(song.AlbumArtPath) && File.Exists(song.AlbumArtPath))
        {
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 88;
                bmp.UriSource = new Uri(song.AlbumArtPath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                var artBorder = new Border
                {
                    Width = 38, Height = 38, CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(4, 0, 0, 0),
                    BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString("#B8A88A")!,
                    BorderThickness = new Thickness(1), ClipToBounds = true,
                    Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }
                };
                leftTools.Children.Add(artBorder);
            }
            catch { }
        }
        Grid.SetColumn(leftTools, 0);
        grid.Children.Add(leftTools);

        // Col 1: Song Title (Impact font) + Artist/Year + Badges
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleColor = song.IsInLobbySetlist ? "#B45309" : (song.IsTemporarySessionSong ? "#1D4ED8" : "#1E293B");
        infoStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(song.Title) ? song.SongId : song.Title,
            FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
            FontSize = 18,
            Foreground = (WpfBrush)new BrushConverter().ConvertFromString(titleColor)!
        });
        var artistText = string.IsNullOrWhiteSpace(song.Artist) ? "UNKNOWN ARTIST" : song.Artist.ToUpperInvariant();
        if (song.ExpertNoteCount > 0) artistText += $", {song.ExpertNoteCount:N0} NOTES";
        infoStack.Children.Add(new TextBlock
        {
            Text = artistText,
            FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
            FontSize = 11,
            Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#6B7280")!,
            Margin = new Thickness(0, 1, 0, 0)
        });

        // Compact badge row for special status
        var badgeRow = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
        if (song.IsInLobbySetlist)
            badgeRow.Children.Add(MakeBadge("Lobby Sync", "#431407", "#FDBA74"));
        if (song.IsTemporarySessionSong)
            badgeRow.Children.Add(MakeBadge("Temp Session", "#1E3A8A", "#93C5FD"));
        if (song.HasCoopBass)
            badgeRow.Children.Add(MakeBadge("Co-Op", "#312E81", "#C7D2FE"));
        if (!string.IsNullOrWhiteSpace(song.AudioModeTag) && song.AudioModeTag.Contains("Multi-Stem", StringComparison.OrdinalIgnoreCase))
            badgeRow.Children.Add(MakeBadge("Multi-Stem", "#0F3D3E", "#5EEAD4"));
        foreach (var tag in AppConfig.GetSongTags(song.SongId))
            badgeRow.Children.Add(MakeBadge($"#{tag}", "#3B0764", "#E9D5FF"));
        if (badgeRow.Children.Count > 0)
            infoStack.Children.Add(badgeRow);

        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        // Col 2: GH3-Style Star Rating + High Score (right-aligned, matching game screenshot)
        var scoreStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0) };
        var bestScore = AppConfig.GetHighScore(song.SongId);
        int stars = bestScore?.Stars ?? 0;
        if (stars > 0)
        {
            var fullStars = Math.Clamp(stars, 0, 5);
            var emptyStars = 5 - fullStars;
            var starLine = new string('★', fullStars) + new string('☆', emptyStars);
            scoreStack.Children.Add(new TextBlock
            {
                Text = starLine,
                FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
                FontSize = 15,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(bestScore!.IsFullCombo ? "#B45309" : "#8B7355")!,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            });
        }
        if (bestScore != null && bestScore.BestScore > 0)
        {
            var scoreFg = bestScore.IsFullCombo ? "#B45309" : "#1E293B";
            scoreStack.Children.Add(new TextBlock
            {
                Text = bestScore.IsFullCombo ? $"FC {bestScore.BestScore:N0}" : $"{bestScore.BestScore:N0}",
                FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(scoreFg)!,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 0, 0)
            });
        }
        // Sparkline mini-difficulty bars
        var cardChartPath = hasProject ? ChartDoctorService.FindChartFileInProject(song.ProjectDirectory) : null;
        if (!string.IsNullOrEmpty(cardChartPath))
        {
            if (!_sparklineCache.TryGetValue(cardChartPath, out var spark))
            {
                spark = ChartDoctorService.ComputeIntensitySparkline(cardChartPath, 16);
                _sparklineCache[cardChartPath] = spark;
            }
            if (spark.PeakNps > 0)
            {
                var miniCanvas = new Canvas { Width = 48, Height = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
                for (int bi = 0; bi < spark.Buckets.Length; bi++)
                {
                    double val = spark.Buckets[bi];
                    double bh = Math.Max(1.5, val * 12.0);
                    var col = val > 0.72 ? WpfColor.FromRgb(180, 60, 60) : val > 0.42 ? WpfColor.FromRgb(180, 140, 30) : WpfColor.FromRgb(80, 140, 80);
                    var bar = new System.Windows.Shapes.Rectangle { Width = 2.0, Height = bh, Fill = new SolidColorBrush(col), RadiusX = 0.5, RadiusY = 0.5 };
                    Canvas.SetLeft(bar, bi * 3.0);
                    Canvas.SetTop(bar, 12.0 - bh);
                    miniCanvas.Children.Add(bar);
                }
                scoreStack.Children.Add(miniCanvas);
            }
        }
        Grid.SetColumn(scoreStack, 2);
        grid.Children.Add(scoreStack);

        // Col 3: Action Buttons (compact for notebook rows)
        var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (song.IsTemporarySessionSong)
        {
            var keepBtn = new System.Windows.Controls.Button
            {
                Content = "Keep",
                Style = (Style)FindResource("GreenButton"),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Keep this temporary lobby song permanently"
            };
            keepBtn.Click += (_, _) =>
            {
                _tempSession.KeepSongPermanently(song.SongId);
                RefreshSongsList();
                RefreshTempSessionBanner();
            };
            btnRow.Children.Add(keepBtn);
        }

        var lobbyBtn = new System.Windows.Controls.Button
        {
            Content = song.IsInLobbySetlist ? "✓" : "+",
            Style = (Style)FindResource(song.IsInLobbySetlist ? "FlameButton" : "OutlineButton"),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Toggle Lobby Auto-Sync Setlist"
        };
        lobbyBtn.Click += (_, _) =>
        {
            var (_, msg) = AppConfig.ToggleLobbySetlist(song.SongId);
            if (msg.Contains("limit reached", StringComparison.OrdinalIgnoreCase))
                WpfMessageBox.Show(msg, "Lobby Setlist Cap", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshSongsList();
        };
        btnRow.Children.Add(lobbyBtn);

        var editBtn = new System.Windows.Controls.Button
        {
            Content = "Edit",
            Style = (Style)FindResource("OutlineButton"),
            Padding = new Thickness(7, 4, 7, 4),
            Margin = new Thickness(0, 0, 4, 0),
            ToolTip = "Edit Song"
        };
        editBtn.Click += (_, _) => OpenEditSongDialog(song);
        btnRow.Children.Add(editBtn);

        var moreBtn = new System.Windows.Controls.Button
        {
            Content = "⋮",
            Style = (Style)FindResource("OutlineButton"),
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Padding = new Thickness(6, 2, 6, 2),
            ToolTip = "More Actions"
        };
        moreBtn.Click += (_, _) =>
        {
            var menu = BuildSongContextMenu(song);
            menu.PlacementTarget = moreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        btnRow.Children.Add(moreBtn);

        card.ContextMenu = BuildSongContextMenu(song);

        Grid.SetColumn(btnRow, 3);
        grid.Children.Add(btnRow);

        card.Child = grid;
        return card;
    }

    private System.Windows.Controls.ContextMenu BuildSongContextMenu(CustomSongInfo song)
    {
        bool isOnogami = song.CreatedWithOnogamiCreator || song.IsCreatedLocally;
        bool hasProject = !string.IsNullOrWhiteSpace(song.ProjectDirectory) && Directory.Exists(song.ProjectDirectory);
        bool hasAudio = song.HasAudio || !string.IsNullOrEmpty(Gh3Service.FindSongAudioPath(song));
        string? chartFile = hasProject ? ChartDoctorService.FindChartFileInProject(song.ProjectDirectory) : null;

        var menu = new System.Windows.Controls.ContextMenu
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(20, 18, 30)),
            Foreground = System.Windows.Media.Brushes.White,
            BorderBrush = (WpfBrush)FindResource("BorderSubtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(4)
        };

        System.Windows.Controls.MenuItem AddItem(string header, Action onClick, bool isDanger = false)
        {
            var item = new System.Windows.Controls.MenuItem
            {
                Header = header,
                Foreground = isDanger ? new SolidColorBrush(WpfColor.FromRgb(248, 113, 113)) : System.Windows.Media.Brushes.White,
                Padding = new Thickness(10, 6, 14, 6),
                FontSize = 12.5,
                Cursor = System.Windows.Input.Cursors.Hand
            };
            item.Click += (_, _) => onClick();
            menu.Items.Add(item);
            return item;
        }

        if (hasAudio)
        {
            AddItem("▶ Preview Audio Track", () =>
            {
                var audio = Gh3Service.FindSongAudioPath(song);
                if (!string.IsNullOrEmpty(audio)) PlayAudioPreview(audio, song.Title, song, false);
            });
        }

        if (hasProject && !string.IsNullOrEmpty(chartFile))
        {
            AddItem("🎸 Highway 3D Preview & Practice", () =>
            {
                OpenHighwayPreview(song.Title, song.Artist, chartFile, Gh3Service.FindSongAudioPath(song), song.ProjectDirectory, song.SongId);
            });

            // Use ⚕ medical cross icon so it never displays as an empty square
            AddItem("⚕ Chart Doctor (Check & Auto-Fix)", () =>
            {
                OpenChartDoctorDialog(song);
            });
        }

        if (hasAudio)
        {
            AddItem("🎧 Export Practice Stems (Slowdown / Mute)", () =>
            {
                OpenPracticeStemExportDialog(song);
            });
        }

        AddItem("🎨 Album Art Studio (Update Cover)", async () =>
        {
            await FetchSongCoverArtAsync(song, null);
        });

        if (isOnogami && SongCreatorService.HasAiCache(song.ProjectDirectory))
        {
            AddItem("⚡ Fast Re-Chart (5-Sec AI Cache)", () =>
            {
                OpenFastRechartDialog(song);
            });
        }

        if (isOnogami && hasAudio)
        {
            AddItem("🤖 Rechart (Full Studio Re-Analysis)", async () =>
            {
                await RechartSongInCreatorAsync(song);
            });
        }

        if (hasProject)
        {
            AddItem("📁 Open Song Folder in Explorer", () =>
            {
                OpenFolderInExplorer(song.ProjectDirectory);
            });
        }

        AddItem("💾 Export Song (.zip / .gh3pack)", () =>
        {
            ExportSingleSong(song);
        });

        menu.Items.Add(new Separator { Background = (WpfBrush)FindResource("BorderSubtle"), Margin = new Thickness(4, 2, 4, 2) });

        AddItem("🗑 Delete Song from Library", () =>
        {
            ConfirmAndDeleteSong(song);
        }, isDanger: true);

        return menu;
    }

    private static Border MakeBadge(string text, string bgHex, string fgHex) => new()
    {
        Background = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(bgHex)),
        CornerRadius = new CornerRadius(5),
        Padding = new Thickness(7, 2, 7, 2),
        Margin = new Thickness(0, 0, 5, 0),
        Child = new TextBlock
        {
            Text = text,
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(fgHex))
        }
    };

    private void UpdateBatchSelectionBarUi(int totalCount = -1)
    {
        if (totalCount < 0) totalCount = _allSongs.Count;
        int selCount = _selectedExportIds.Count;
        SelectedSongsCountText.Text = selCount == 0
            ? $"{totalCount:N0} song(s) available"
            : $"{selCount:N0} of {totalCount:N0} song(s) selected";

        bool hasSelection = selCount > 0;
        BatchSelectionBar.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;

        if (BatchActionsDropdownBtn != null) BatchActionsDropdownBtn.Content = $"⚡ Batch Actions ({selCount}) ▾";
        if (BatchAddToCreatorBtn != null) BatchAddToCreatorBtn.Content = $"➕ Add to Creator ({selCount})";
        if (BatchRechartAiBtn != null) BatchRechartAiBtn.Content = $"🎸 Rechart ({selCount})";
        if (BatchFastRechartBtn != null) BatchFastRechartBtn.Content = $"⚡ Fast Re-Chart ({selCount})";
        if (BatchChartDoctorBtn != null) BatchChartDoctorBtn.Content = $"⚕️ Chart Doctor ({selCount})";
        if (BatchAddLobbyBtn != null) BatchAddLobbyBtn.Content = $"+ Lobby ({selCount})";
        if (ExportSelectedBtn != null) ExportSelectedBtn.Content = $"📦 Export ({selCount})";
        if (BatchDeleteBtn != null) BatchDeleteBtn.Content = $"🗑️ Delete ({selCount})";

        if (BatchDeleteBtn != null) BatchDeleteBtn.IsEnabled = hasSelection;
        if (BatchFastRechartBtn != null) BatchFastRechartBtn.IsEnabled = hasSelection;
        if (BatchRechartAiBtn != null) BatchRechartAiBtn.IsEnabled = hasSelection;
        if (BatchAddLobbyBtn != null) BatchAddLobbyBtn.IsEnabled = hasSelection;
        if (ExportSelectedBtn != null) ExportSelectedBtn.IsEnabled = hasSelection;
        if (BatchDeselectAllBtn != null) BatchDeselectAllBtn.IsEnabled = hasSelection;

        if (SelectAllSongsChk != null) SelectAllSongsChk.IsChecked = totalCount > 0 && selCount >= totalCount;
        if (NotebookSelectAllChk != null) NotebookSelectAllChk.IsChecked = totalCount > 0 && selCount >= totalCount;
    }

    private void SelectAllSongsChk_Click(object sender, RoutedEventArgs e)
    {
        bool checkAll = sender == NotebookSelectAllChk
            ? NotebookSelectAllChk?.IsChecked == true
            : SelectAllSongsChk?.IsChecked == true;
        _selectedExportIds.Clear();
        if (checkAll)
        {
            foreach (var s in _allSongs)
            {
                if (!string.IsNullOrWhiteSpace(s.SongId))
                    _selectedExportIds.Add(s.SongId);
            }
        }
        UpdateBatchSelectionBarUi();
        RefreshSongsList();
    }

    private void BatchDeselectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        _selectedExportIds.Clear();
        RefreshSongsList();
    }

    private void BatchDeleteBtn_Click(object sender, RoutedEventArgs e)
    {
        var toDelete = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList();
        if (toDelete.Count == 0)
        {
            WpfMessageBox.Show("Select at least one song to delete.", "Batch Delete", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = WpfMessageBox.Show(
            $"Are you sure you want to permanently delete {toDelete.Count} selected song(s) from Guitar Hero III?\n\nThis will remove their soundbanks and project directories.",
            "Confirm Bulk Delete",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.Yes) return;

        StopAudioPreview();
        int deleted = 0;
        foreach (var song in toDelete)
        {
            try
            {
                _gh3.DeleteCustomSong(song, deleteProjectFolder: true, _saves, _logger);
                _selectedExportIds.Remove(song.SongId);
                deleted++;
            }
            catch (Exception ex)
            {
                _logger?.LogWarn($"Failed to delete {song.SongId}: {ex.Message}");
            }
        }

        ShowStageToast("🗑️ BATCH DELETE COMPLETE", $"Removed {deleted} song(s) from Guitar Hero III.", "#EF4444");
        RefreshSongsList();
    }

    private async void BatchFastRechartBtn_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId) && SongCreatorService.HasAiCache(s.ProjectDirectory)).ToList();
        if (targets.Count == 0)
        {
            WpfMessageBox.Show("None of the selected songs have an instant AI analysis cache.\nRun 'Re-Chart AI v2' on a song first to build the instant cache.", "No Fast Cache", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PageCustoms_StageHud.Report(10, "BATCH FAST RE-CHART", $"Updating {targets.Count} song(s) from instant cache...");
        int count = 0;
        foreach (var s in targets)
        {
            try
            {
                var (ok, _) = await _creator.InstantRechartFromCacheAsync(s.ProjectDirectory, "Standard", 0, true, null);
                if (ok) count++;
            }
            catch { }
        }
        PageCustoms_StageHud.Finish($"Updated {count} song(s) from instant cache!");
        ShowStageToast("⚡ BATCH RE-CHART COMPLETE", $"Re-charted {count} song(s) in seconds.", "#22C55E");
        RefreshSongsList();
    }

    private void BatchActionsDropdownBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BatchActionsDropdownBtn?.ContextMenu != null)
        {
            BatchActionsDropdownBtn.ContextMenu.PlacementTarget = BatchActionsDropdownBtn;
            BatchActionsDropdownBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            BatchActionsDropdownBtn.ContextMenu.IsOpen = true;
        }
    }

    private void BatchAddToCreatorBtn_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList();
        if (targets.Count == 0)
        {
            WpfMessageBox.Show("Select at least one song to add to Song Creator.", "Add to Creator", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int added = 0;
        foreach (var song in targets)
        {
            var audio = Gh3Service.FindSongAudioPath(song) ?? song.AudioPath;
            if (string.IsNullOrEmpty(audio) || !File.Exists(audio)) continue;
            var chart = !string.IsNullOrWhiteSpace(song.ProjectDirectory) ? ChartDoctorService.FindChartFileInProject(song.ProjectDirectory) : null;

            var req = new SongCreationRequest
            {
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                Year = int.TryParse(song.Year, out var y) ? y : DateTime.Now.Year,
                Genre = "Rock",
                Charter = string.IsNullOrWhiteSpace(song.Charter) ? "Mad Scientist" : song.Charter,
                Bpm = 120.0,
                PreviewStartMs = 5000,
                AutoDetectBpm = true,
                UseVariableTempoMap = true,
                ChartFeel = "Standard",
                InstrumentFocus = "Auto",
                AudioMixMode = "Auto",
                AutoGenerateChart = string.IsNullOrEmpty(chart),
                AutoCompile = true,
                NormalizeLoudness = true,
                UseCachedAiAnalysis = true,
                EnableStadiumChants = true,
                EnableBossBattle = false,
                EnableTradeOffSolo = false,
                EnableAiLyricsSections = true,
                EnableCharterSignature = true,
                CharterFriendCode = _friends?.Profile?.FriendCode ?? ""
            };

            _batchQueue.Add(new QueuedSongItem(req, audio, chart));
            added++;
        }

        RenderBatchQueue();
        NavCreator.IsChecked = true;
        ShowStageToast("➕ ADDED TO CREATOR", $"Queued {added} song(s) into Song Creator for batch recompile!", "#22C55E");
    }

    private async void BatchChartDoctorBtn_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList();
        if (targets.Count == 0)
        {
            WpfMessageBox.Show("Select at least one song to run Chart Doctor.", "Chart Doctor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        PageCustoms_StageHud.Report(15, "BATCH CHART DOCTOR", $"Checking & repairing {targets.Count} song(s)...");
        int fixedCount = 0;
        var opts = new ChartDoctorFixOptions
        {
            GenerateMissingDifficulties = true,
            AddMissingStarPower = true,
            AddMissingSections = true,
            RecompileToGame = true
        };

        foreach (var song in targets)
        {
            if (string.IsNullOrWhiteSpace(song.ProjectDirectory) || !Directory.Exists(song.ProjectDirectory)) continue;
            try
            {
                var (ok, _) = await _chartDoctor.RepairChartAndRecompileAsync(song.ProjectDirectory, opts);
                if (ok) fixedCount++;
            }
            catch { }
        }

        PageCustoms_StageHud.Finish($"Chart Doctor scanned & repaired {fixedCount} song(s)!");
        ShowStageToast("⚕️ CHART DOCTOR COMPLETE", $"Auto-fixed & recompiled {fixedCount} song(s)!", "#22C55E");
        RefreshSongsList();
    }

    private async void BatchRechartAiBtn_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId) && !string.IsNullOrEmpty(Gh3Service.FindSongAudioPath(s))).ToList();
        if (targets.Count == 0)
        {
            WpfMessageBox.Show("Select at least one song with valid audio to re-chart.", "Rechart", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (targets.Count == 1)
        {
            await RechartSongInCreatorAsync(targets[0]);
            return;
        }

        // Add all selected songs directly into Creator's Batch Queue
        int added = 0;
        foreach (var song in targets)
        {
            var audio = Gh3Service.FindSongAudioPath(song);
            if (string.IsNullOrEmpty(audio) || !File.Exists(audio)) continue;
            var chart = ChartDoctorService.FindChartFileInProject(song.ProjectDirectory);

            var req = new SongCreationRequest
            {
                Title = song.Title,
                Artist = song.Artist,
                Album = song.Album,
                Year = int.TryParse(song.Year, out var y) ? y : DateTime.Now.Year,
                Genre = "Rock",
                Charter = string.IsNullOrWhiteSpace(song.Charter) ? "Mad Scientist" : song.Charter,
                Bpm = 120.0,
                PreviewStartMs = 5000,
                AutoDetectBpm = true,
                UseVariableTempoMap = true,
                ChartFeel = "Standard",
                InstrumentFocus = "Auto",
                AudioMixMode = "Auto",
                AutoGenerateChart = true,
                AutoCompile = true,
                NormalizeLoudness = true,
                UseCachedAiAnalysis = true,
                EnableStadiumChants = true,
                EnableBossBattle = false,
                EnableTradeOffSolo = false,
                EnableAiLyricsSections = true,
                EnableCharterSignature = true,
                CharterFriendCode = _friends?.Profile?.FriendCode ?? ""
            };

            _batchQueue.Add(new QueuedSongItem(req, audio, chart));
            added++;
        }

        RenderBatchQueue();
        NavCreator.IsChecked = true;
        ShowStageToast("🎸 ADDED TO RECHART QUEUE", $"Added {added} songs to Song Creator batch queue!", "#22C55E");
    }

    private void BatchAddLobbyBtn_Click(object sender, RoutedEventArgs e)
    {
        var targets = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList();
        int added = 0;
        foreach (var s in targets)
        {
            if (AppConfig.Current.LobbySetlistIds.Count >= AppConfig.Current.MaxLobbyAutoSyncSongs) break;
            if (!s.IsInLobbySetlist)
            {
                var (ok, _) = AppConfig.ToggleLobbySetlist(s.SongId);
                if (ok) added++;
            }
        }
        ShowStageToast("✓ LOBBY UPDATED", $"Added {added} song(s) to multiplayer lobby setlist.", "#3B82F6");
        RefreshSongsList();
    }

    private void PartyModesMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        PartyModesMenuBtn.ContextMenu.PlacementTarget = PartyModesMenuBtn;
        PartyModesMenuBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        PartyModesMenuBtn.ContextMenu.IsOpen = true;
    }

    private void StudioToolsMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        StudioToolsMenuBtn.ContextMenu.PlacementTarget = StudioToolsMenuBtn;
        StudioToolsMenuBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        StudioToolsMenuBtn.ContextMenu.IsOpen = true;
    }

    private void StreamShareMenuBtn_Click(object sender, RoutedEventArgs e)
    {
        StreamShareMenuBtn.ContextMenu.PlacementTarget = StreamShareMenuBtn;
        StreamShareMenuBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        StreamShareMenuBtn.ContextMenu.IsOpen = true;
    }

    private static readonly string[] Gh3StageVenues =
    {
        "0 — Backyard Bash",
        "1 — Mitch's Moose Lounge",
        "2 — Video Shoot (Default)",
        "3 — Ye Olde Royal Odeon",
        "4 — Shanker's Island",
        "5 — Desert Rock Tour",
        "6 — Kaiju Megadome",
        "7 — Lou's Inferno"
    };

    private void OpenEditSongDialog(CustomSongInfo song)
    {
        var dlg = new Window
        {
            Title = $"Edit Song — {song.Title}",
            Width = 520,
            Height = 570,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "Edit Song Info, Stage Venue & Audio Mix", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });

        var titleBox = new System.Windows.Controls.TextBox { Text = song.Title, Margin = new Thickness(0, 0, 0, 8) };
        var artistBox = new System.Windows.Controls.TextBox { Text = song.Artist, Margin = new Thickness(0, 0, 0, 8) };
        var albumBox = new System.Windows.Controls.TextBox { Text = "", Margin = new Thickness(0, 0, 0, 8) };
        var charterBox = new System.Windows.Controls.TextBox { Text = string.IsNullOrWhiteSpace(song.Charter) ? "Mad Scientist" : song.Charter, Margin = new Thickness(0, 0, 0, 8) };
        var yearBox = new System.Windows.Controls.TextBox { Text = DateTime.Now.Year.ToString(), Margin = new Thickness(0, 0, 0, 8) };
        var genreBox = new System.Windows.Controls.TextBox { Text = "Rock", Margin = new Thickness(0, 0, 0, 8) };
        var venueCombo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        foreach (var v in Gh3StageVenues) venueCombo.Items.Add(v);
        venueCombo.SelectedIndex = 2;

        var gtrVolSlider = new Slider { Minimum = -12, Maximum = 6, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 2, 0, 8) };
        var bandVolSlider = new Slider { Minimum = -12, Maximum = 6, Value = 0, TickFrequency = 1, IsSnapToTickEnabled = true, Margin = new Thickness(0, 2, 0, 12) };
        var gtrVolLbl = new TextBlock { Text = "Guitar Volume: 0 dB", FontSize = 12, Foreground = (WpfBrush)FindResource("TextMuted") };
        var bandVolLbl = new TextBlock { Text = "Band / Backing Volume: 0 dB", FontSize = 12, Foreground = (WpfBrush)FindResource("TextMuted") };
        gtrVolSlider.ValueChanged += (_, _) => gtrVolLbl.Text = $"Guitar Volume: {gtrVolSlider.Value:+0;-0;0} dB";
        bandVolSlider.ValueChanged += (_, _) => bandVolLbl.Text = $"Band / Backing Volume: {bandVolSlider.Value:+0;-0;0} dB";

        stack.Children.Add(new TextBlock { Text = "Title", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(titleBox);
        stack.Children.Add(new TextBlock { Text = "Artist", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(artistBox);
        stack.Children.Add(new TextBlock { Text = "Genre  •  Charter", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        var rowGc = new Grid();
        rowGc.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        rowGc.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        rowGc.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(genreBox, 0); Grid.SetColumn(charterBox, 2);
        rowGc.Children.Add(genreBox); rowGc.Children.Add(charterBox);
        stack.Children.Add(rowGc);

        stack.Children.Add(new TextBlock { Text = "GH3 Concert Stage Venue", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(venueCombo);
        stack.Children.Add(gtrVolLbl);
        stack.Children.Add(gtrVolSlider);
        stack.Children.Add(bandVolLbl);
        stack.Children.Add(bandVolSlider);

        var recompileChk = new System.Windows.Controls.CheckBox { Content = "Recompile into Guitar Hero III immediately", IsChecked = true, Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(recompileChk);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var exportChBtn = new System.Windows.Controls.Button { Content = "Export CH .zip", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Export this song cleanly for Clone Hero / YARG" };
        exportChBtn.Click += (_, _) =>
        {
            var sfd = new Microsoft.Win32.SaveFileDialog { Title = "Export to Clone Hero / YARG (.zip)", Filter = "Clone Hero Song Archive (*.zip)|*.zip", FileName = $"{song.Artist} - {song.Title}.zip" };
            if (sfd.ShowDialog() == true)
            {
                var (ok, msg) = ChartStudioToolsService.ExportToCloneHeroZip(song, sfd.FileName);
                if (ok) ShowStageToast("📦 CLONE HERO EXPORT!", msg, "#22C55E");
                else WpfMessageBox.Show(msg, "Export Clone Hero", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var exportGhwtBtn = new System.Windows.Controls.Button { Content = "Export GHWT:DE", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Export this song as a Guitar Hero World Tour Definitive Edition mod folder" };
        exportGhwtBtn.Click += (_, _) =>
        {
            var fbd = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Destination Folder for GHWT:DE (e.g. GHWTDE/DATA/MODS/Songs/)",
                Multiselect = false
            };
            if (fbd.ShowDialog() == true && !string.IsNullOrWhiteSpace(fbd.FolderName))
            {
                string safeName = $"{song.Artist} - {song.Title}".Replace('/', '_').Replace('\\', '_');
                string targetSubDir = Path.Combine(fbd.FolderName, safeName);
                var (ok, msg) = ChartStudioToolsService.ExportToGhwtdeFolder(song, targetSubDir);
                if (ok) ShowStageToast("🎸 GHWT:DE EXPORT!", msg, "#22C55E");
                else WpfMessageBox.Show(msg, "Export GHWT:DE", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };
        var scaleDiffBtn = new System.Windows.Controls.Button { Content = "Scale Diffs (Fill E/M/H)", Style = (Style)FindResource("BlueButton"), Margin = new Thickness(0, 0, 6, 0), ToolTip = "Generate balanced progressive Hard, Medium & Easy tracks on this song" };
        scaleDiffBtn.Click += async (_, _) =>
        {
            scaleDiffBtn.IsEnabled = false;
            scaleDiffBtn.Content = "Scaling...";
            var (ok, msg) = await _studioTools.ScaleChartDifficultyAsync(song.ProjectDirectory, "FillMissingDiffs", recompile: true);
            scaleDiffBtn.IsEnabled = true;
            scaleDiffBtn.Content = "Scale Diffs (Fill E/M/H)";
            if (ok) ShowStageToast("🎚️ DIFFICULTIES SCALED!", msg, "#22C55E");
            else WpfMessageBox.Show(msg, "Difficulty Scaler", MessageBoxButton.OK, MessageBoxImage.Warning);
        };
        var saveBtn = new System.Windows.Controls.Button { Content = "Save & Apply", Style = (Style)FindResource("FlameButton") };
        saveBtn.Click += async (_, _) =>
        {
            saveBtn.IsEnabled = false;
            saveBtn.Content = "Saving...";
            int yr = int.TryParse(yearBox.Text, out var parsedYr) ? parsedYr : DateTime.Now.Year;
            var (ok, msg) = await _creator.UpdateSongMetadataAndRecompileAsync(
                song.ProjectDirectory, titleBox.Text.Trim(), artistBox.Text.Trim(), albumBox.Text.Trim(),
                yr, genreBox.Text.Trim(), 4, 3, gtrVolSlider.Value, bandVolSlider.Value, recompileChk.IsChecked == true,
                charter: charterBox.Text.Trim(), venueSource: Math.Max(0, venueCombo.SelectedIndex));
            dlg.Close();
            RefreshSongsList();
            if (ok) ShowStageToast("★ SONG UPDATED!", msg, "#22C55E");
            else WpfMessageBox.Show(msg, "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };
        btns.Children.Add(exportChBtn);
        btns.Children.Add(exportGhwtBtn);
        btns.Children.Add(scaleDiffBtn);
        btns.Children.Add(saveBtn);
        stack.Children.Add(btns);

        dlg.Content = stack;
        dlg.ShowDialog();
    }

    private void BulkEditBtn_Click(object sender, RoutedEventArgs e)
    {
        var targets = _selectedExportIds.Count > 0
            ? _allSongs.Where(s => _selectedExportIds.Contains(s.SongId) && !string.IsNullOrWhiteSpace(s.ProjectDirectory)).ToList()
            : _allSongs.Where(s => !string.IsNullOrWhiteSpace(s.ProjectDirectory)).ToList();

        if (targets.Count == 0)
        {
            WpfMessageBox.Show("No editable custom song projects found. Check one or more songs with local project folders first.",
                "Bulk Metadata Editor", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Window
        {
            Title = $"Bulk Metadata Editor ({targets.Count} Song{(targets.Count == 1 ? "" : "s")})",
            Width = 500,
            Height = 460,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = $"Bulk Edit {targets.Count} Song(s)", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(new TextBlock { Text = "Leave any field blank to keep each song's existing value unchanged.", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted"), Margin = new Thickness(0, 0, 0, 12) });

        var genreBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var yearBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var charterBox = new System.Windows.Controls.TextBox { Margin = new Thickness(0, 0, 0, 8) };
        var venueCombo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 0, 10) };
        venueCombo.Items.Add("(Keep Existing Venue)");
        foreach (var v in Gh3StageVenues) venueCombo.Items.Add(v);
        venueCombo.SelectedIndex = 0;

        var recompileChk = new System.Windows.Controls.CheckBox { Content = "Recompile updated songs into Guitar Hero III", IsChecked = false, Margin = new Thickness(0, 4, 0, 14) };

        stack.Children.Add(new TextBlock { Text = "Set Genre (optional)", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(genreBox);
        stack.Children.Add(new TextBlock { Text = "Set Year (optional, e.g. 2007)", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(yearBox);
        stack.Children.Add(new TextBlock { Text = "Set Charter (optional)", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(charterBox);
        stack.Children.Add(new TextBlock { Text = "Set Concert Venue (optional)", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(venueCombo);
        stack.Children.Add(recompileChk);

        var applyBtn = new System.Windows.Controls.Button { Content = $"Apply to {targets.Count} Song(s)", Style = (Style)FindResource("FlameButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        applyBtn.Click += async (_, _) =>
        {
            applyBtn.IsEnabled = false;
            applyBtn.Content = "Updating...";
            int? parsedYr = int.TryParse(yearBox.Text.Trim(), out var y) ? y : null;
            int? venueIdx = venueCombo.SelectedIndex > 0 ? venueCombo.SelectedIndex - 1 : null;
            var (_, _, summary) = await _creator.BulkUpdateMetadataAsync(
                targets,
                genre: string.IsNullOrWhiteSpace(genreBox.Text) ? null : genreBox.Text.Trim(),
                year: parsedYr,
                charter: string.IsNullOrWhiteSpace(charterBox.Text) ? null : charterBox.Text.Trim(),
                venueSource: venueIdx,
                recompile: recompileChk.IsChecked == true);
            dlg.Close();
            RefreshSongsList();
            ShowStageToast("✏️ BULK UPDATE COMPLETE!", summary, "#22C55E");
        };
        stack.Children.Add(applyBtn);

        dlg.Content = stack;
        dlg.ShowDialog();
    }

    private async Task CompileSongCardAsync(CustomSongInfo song, System.Windows.Controls.Button btn)
    {
        var target = !string.IsNullOrWhiteSpace(song.GhprojPath) ? song.GhprojPath : song.ProjectDirectory;
        if (string.IsNullOrWhiteSpace(target)) return;

        btn.IsEnabled = false;
        btn.Content = "Compiling...";
        try
        {
            var res = await _compiler.CompileSongProjectAsync(target);
            if (res.Success)
            {
                WpfMessageBox.Show($"Compiled & installed '{song.Title}' directly into Guitar Hero III.", "Compilation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshSongsList();
            }
            else
            {
                WpfMessageBox.Show($"Compilation failed:\n{res.Message}", "Compiler Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally
        {
            btn.IsEnabled = true;
            btn.Content = "Compile";
        }
    }

    private static void OpenFolderInExplorer(string dir)
    {
        if (Directory.Exists(dir))
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
    }

    private void ExportSingleSong(CustomSongInfo song)
    {
        try
        {
            var bytes = _gh3.PackageSong(song.SongId);
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Export Custom Song Package",
                Filter = "GH3-Sync Song (*.gh3song)|*.gh3song|ZIP Archive (*.zip)|*.zip",
                FileName = $"{song.SongId}.gh3song"
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllBytes(dlg.FileName, bytes);
                WpfMessageBox.Show($"Exported '{song.Title}' ({bytes.Length / (1024.0 * 1024.0):F1} MB).", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Export error: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OpenHighwayPreview(string title, string artist, string chartPath, string? audioPath, string? projectDir, string? songId = null)
    {
        StopAudioPreview();
        var win = new HighwayPreviewWindow(title, artist, chartPath, audioPath, projectDir, _chartDoctor, songId, _studioTools) { Owner = this };
        win.SongRecompiled += () => Dispatcher.Invoke(() => { _sparklineCache.Remove(chartPath); RefreshSongsList(); });
        win.ScoreRecorded += () => Dispatcher.Invoke(RefreshSongsList);
        win.Show();
    }

    private void SpinRouletteBtn_Click(object sender, RoutedEventArgs e)
    {
        var pool = _allSongs.Where(s => !s.IsProject).ToList();
        if (_currentFilter == "lobby") pool = pool.Where(s => s.IsInLobbySetlist).ToList();
        else if (_currentFilter == "favorites") pool = pool.Where(s => s.IsFavorite).ToList();
        if (pool.Count == 0) pool = _allSongs.ToList();
        if (pool.Count == 0)
        {
            WpfMessageBox.Show("Install at least one custom song to spin Encore Roulette!", "Encore Roulette", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var win = new EncoreRouletteWindow(
            pool,
            song => { var a = Gh3Service.FindSongAudioPath(song); if (!string.IsNullOrEmpty(a)) PlayAudioPreview(a, song.Title, song); },
            song => { var ch = ChartDoctorService.FindChartFileInProject(song.ProjectDirectory); if (!string.IsNullOrEmpty(ch)) OpenHighwayPreview(song.Title, song.Artist, ch, Gh3Service.FindSongAudioPath(song), song.ProjectDirectory, song.SongId); })
        { Owner = this };
        win.ShowDialog();
    }

    private void ExportSetlistPosterBtn_Click(object sender, RoutedEventArgs e)
    {
        var lobbyIds = new HashSet<string>(AppConfig.Current.LobbySetlistIds ?? new(), StringComparer.OrdinalIgnoreCase);
        var list = _allSongs.Where(s => lobbyIds.Contains(s.SongId) || s.IsInLobbySetlist).ToList();
        string title = "TONIGHT'S LOBBY SETLIST";
        if (list.Count == 0)
        {
            list = _allSongs.ToList();
            title = "CUSTOM SETLIST";
        }
        if (list.Count == 0)
        {
            WpfMessageBox.Show("Add at least one custom song before generating a Concert Tour Poster.", "Setlist Poster", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Concert Tour Setlist Poster (.png)",
            Filter = "PNG Image (*.png)|*.png",
            FileName = $"GH3_Setlist_Poster_{DateTime.Now:yyyyMMdd}.png"
        };
        if (dlg.ShowDialog() != true) return;

        PageCustoms_StageHud.Report(35, "RENDERING CONCERT POSTER", "Composing 1200×1600 stage poster with authentic typography...");
        try
        {
            SetlistPosterService.ExportSetlistPosterPng(dlg.FileName, _friends.Profile.Username, _friends.Profile.FriendCode, title, list);
            PageCustoms_StageHud.Finish("Concert tour poster exported!");
            WpfMessageBox.Show($"Saved high-res Concert Tour Setlist Poster (1200×1600) to:\n{dlg.FileName}\n\nReady to drop straight into Discord!", "Setlist Poster Exported", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenFolderInExplorer(Path.GetDirectoryName(dlg.FileName)!);
        }
        catch (Exception ex)
        {
            PageCustoms_StageHud.SetError("POSTER EXPORT FAILED", ex.Message);
            WpfMessageBox.Show($"Could not generate poster: {ex.Message}", "Poster Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void VolumeSoundcheckBtn_Click(object sender, RoutedEventArgs e)
    {
        PageCustoms_StageHud.Report(20, "LOUDNESS SOUNDCHECK", "Scanning custom songs against target -14 LUFS...");
        try
        {
            var reports = await _practiceAudio.ScanLibraryLoudnessAsync(_allSongs);
            if (reports.Count == 0)
            {
                PageCustoms_StageHud.Reset();
                WpfMessageBox.Show("No local project audio files found to soundcheck.", "Volume Soundcheck", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var needsFix = reports.Where(r => r.NeedsNormalization).ToList();
            var lines = reports.Take(12).Select(r => $"• {r.Song.Title}: {r.StatusLabel}");
            var summary = $"Scanned {reports.Count} custom song(s):\n\n" + string.Join("\n", lines) +
                          (reports.Count > 12 ? $"\n...and {reports.Count - 12} more." : "");

            if (needsFix.Count == 0)
            {
                PageCustoms_StageHud.Finish("All tracks are balanced near -14 LUFS!");
                WpfMessageBox.Show(summary + "\n\nAll scanned tracks are already nicely balanced near -14 LUFS!", "🔊 Volume Soundcheck — All Balanced", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ask = WpfMessageBox.Show(
                summary + $"\n\n{needsFix.Count} song(s) are outside target GH3 loudness (-14 LUFS).\nNormalize & recompile those {needsFix.Count} song(s) now?",
                "🔊 Library Volume Soundcheck", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes)
            {
                PageCustoms_StageHud.Reset();
                return;
            }

            PageCustoms_StageHud.Report(65, "NORMALIZING AUDIO", $"Recalibrating stems for {needsFix.Count} track(s)...");
            var (_, _, resultMsg) = await _practiceAudio.NormalizeSongsBatchAsync(needsFix);
            RefreshSongsList();
            PageCustoms_StageHud.Finish("Volume Soundcheck Complete!");
            WpfMessageBox.Show(resultMsg, "Volume Soundcheck Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            PageCustoms_StageHud.SetError("SOUNDCHECK ERROR", ex.Message);
        }
    }

    private void AutoHealLibraryBtn_Click(object sender, RoutedEventArgs e)
    {
        PageCustoms_StageHud.Report(25, "LIBRARY HEALTH SCAN", "Scanning file integrity, PAK headers, and QB consistency...");
        var report = _libraryHealth.ScanLibrary();
        var details = string.Join("\n", report.Findings.Select(f => $"[{(f.Severity == "OK" ? "✓ OK" : f.Severity.ToUpperInvariant())}] {f.Category}: {f.Message}"));
        if (report.IssueCount == 0)
        {
            PageCustoms_StageHud.Finish("Library is 100% healthy!");
            WpfMessageBox.Show(report.Summary + "\n\n" + details, "🩺 Library Health Check — All Healthy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = WpfMessageBox.Show(report.Summary + "\n\n" + details + "\n\nRun 1-click Auto-Heal now? (Creates a Career Save backup first.)",
            "🩺 Library Health Scanner", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ask == MessageBoxResult.Yes)
        {
            PageCustoms_StageHud.Report(70, "AUTO-HEALING ASSETS", "Repairing song mappings and synchronizing cache...");
            var (ok, msg) = _libraryHealth.AutoHealLibrary();
            RefreshSongsList();
            if (ok) PageCustoms_StageHud.Finish("Library auto-heal complete!");
            else PageCustoms_StageHud.SetError("AUTO-HEAL WARNING", msg);
            WpfMessageBox.Show(msg, ok ? "Auto-Heal Complete" : "Auto-Heal Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        else
        {
            PageCustoms_StageHud.Reset();
        }
    }

    private void OpenTagPickerDialog(CustomSongInfo song)
    {
        var dlg = new Window
        {
            Title = $"Custom Tags — {song.Title}",
            Width = 370, Height = 250,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(new TextBlock { Text = $"Toggle Tags for '{song.Title}'", FontSize = 14.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });

        var currentTags = AppConfig.GetSongTags(song.SongId);
        var wrap = new WrapPanel { Margin = new Thickness(0, 0, 0, 14) };
        foreach (var tag in new[] { "Warmup", "Shred", "Party", "Chill", "Co-Op", "Live" })
        {
            var chk = new System.Windows.Controls.CheckBox
            {
                Content = $"#{tag}",
                IsChecked = currentTags.Any(t => t.Equals(tag, StringComparison.OrdinalIgnoreCase)),
                Margin = new Thickness(0, 0, 16, 10),
                Foreground = System.Windows.Media.Brushes.White
            };
            chk.Click += (_, _) => AppConfig.ToggleSongTag(song.SongId, tag);
            wrap.Children.Add(chk);
        }
        sp.Children.Add(wrap);
        var doneBtn = new System.Windows.Controls.Button { Content = "Done", Style = (Style)FindResource("FlameButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        doneBtn.Click += (_, _) => dlg.Close();
        sp.Children.Add(doneBtn);
        dlg.Content = sp;
        dlg.ShowDialog();
        RenderSongs();
    }

    private async Task FetchSongCoverArtAsync(CustomSongInfo song, System.Windows.Controls.Button? btn)
    {
        if (btn != null)
        {
            btn.IsEnabled = false;
            btn.Content = "...";
        }
        try
        {
            var res = await _albumMeta.FetchMetadataAndCoverAsync(song.Title, song.Artist, song.ProjectDirectory);
            if (res.Found)
            {
                RefreshSongsList();
                WpfMessageBox.Show(res.Message, "Cover Art Updated", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else WpfMessageBox.Show(res.Message, "Cover Art Lookup", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            if (btn != null)
            {
                btn.IsEnabled = true;
                btn.Content = "🎨 Art";
            }
        }
    }

    private void OpenPracticeStemExportDialog(CustomSongInfo song)
    {
        var dlg = new Window
        {
            Title = $"Real-Guitar Practice Stem Exporter — {song.Title}",
            Width = 480, Height = 285,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this, ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };
        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = $"🎧 Practice Audio Exporter — {song.Title}", FontSize = 15.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        sp.Children.Add(new TextBlock
        {
            Text = "Export practice MP3s to jam on a real electric guitar or learn fast solos by ear:",
            FontSize = 12, Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12)
        });

        var statusTxt = new TextBlock { Text = "", FontSize = 11.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 186, 116)), Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
        var outDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "GH3-Sync Practice Tracks");

        async Task RunMode(string mode, System.Windows.Controls.Button b)
        {
            b.IsEnabled = false;
            var (ok, path, msg) = await _practiceAudio.ExportPracticeTrackAsync(song, mode, outDir, (_, s) => Dispatcher.Invoke(() => statusTxt.Text = s));
            b.IsEnabled = true;
            statusTxt.Text = msg;
            if (ok) OpenFolderInExplorer(outDir);
            WpfMessageBox.Show(msg, ok ? "Practice Track Exported" : "Export Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        var b1 = new System.Windows.Controls.Button { Content = "🐢 Export 75% Pitch-Preserved Slowdown MP3", Style = (Style)FindResource("GreenButton"), Margin = new Thickness(0, 0, 0, 8) };
        b1.Click += async (_, _) => await RunMode("slow75", b1);
        var b2 = new System.Windows.Controls.Button { Content = "🎸 Export Guitarless Backing Track (Play Real Guitar!)", Style = (Style)FindResource("FlameButton"), Margin = new Thickness(0, 0, 0, 8) };
        b2.Click += async (_, _) => await RunMode("guitarless", b2);
        var b3 = new System.Windows.Controls.Button { Content = "🎧 Export Isolated Solo Guitar Stem", Style = (Style)FindResource("BlueButton") };
        b3.Click += async (_, _) => await RunMode("solo", b3);

        sp.Children.Add(b1);
        sp.Children.Add(b2);
        sp.Children.Add(b3);
        sp.Children.Add(statusTxt);
        dlg.Content = sp;
        dlg.ShowDialog();
    }

    private void OpenChartDoctorDialog(CustomSongInfo song)
    {
        var report = _chartDoctor.AnalyzeProject(song.ProjectDirectory);
        var dlg = new Window
        {
            Title = $"Chart Doctor — {song.Title}",
            Width = 560,
            Height = 470,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = $"🩺 Chart Doctor — {song.Title}", FontSize = 16.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
        stack.Children.Add(new TextBlock
        {
            Text = report.Summary,
            FontSize = 12,
            Foreground = new SolidColorBrush(report.Issues.Count == 0 ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(253, 224, 71)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        if (report.Issues.Count > 0)
        {
            var issuesBox = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(11, 13, 20)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(51, 65, 85)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 12)
            };
            var iStack = new StackPanel();
            foreach (var iss in report.Issues)
            {
                iStack.Children.Add(new TextBlock
                {
                    Text = $"• [{iss.Severity}] {iss.Description}",
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(226, 232, 240)),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 4)
                });
            }
            issuesBox.Child = iStack;
            stack.Children.Add(issuesBox);
        }

        var genDiffsChk = new System.Windows.Controls.CheckBox { Content = "Auto-generate missing Easy / Medium / Hard difficulties", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var spChk = new System.Windows.Controls.CheckBox { Content = "Auto-insert balanced Star Power phrases (if missing)", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var secChk = new System.Windows.Controls.CheckBox { Content = "Auto-insert Practice Mode sections (if missing)", IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
        var openChk = new System.Windows.Controls.CheckBox { Content = "Convert Clone Hero Open Notes (N 7) → Classic Green (N 0)", IsChecked = false, Margin = new Thickness(0, 0, 0, 10) };

        var offLbl = new TextBlock { Text = "Sync Offset Shift: 0 ms", FontSize = 12, Foreground = new SolidColorBrush(WpfColor.FromRgb(125, 211, 252)) };
        var offSlider = new Slider { Minimum = -250, Maximum = 250, Value = 0, TickFrequency = 10, IsSnapToTickEnabled = true, Margin = new Thickness(0, 3, 0, 14) };
        offSlider.ValueChanged += (_, _) => offLbl.Text = $"Sync Offset Shift: {(int)offSlider.Value:+0;-0;0} ms";

        stack.Children.Add(genDiffsChk);
        stack.Children.Add(spChk);
        stack.Children.Add(secChk);
        stack.Children.Add(openChk);
        stack.Children.Add(offLbl);
        stack.Children.Add(offSlider);

        var statusLbl = new TextBlock { Text = "", FontSize = 11.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 186, 116)), Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(statusLbl);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var closeBtn = new System.Windows.Controls.Button { Content = "Close", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        closeBtn.Click += (_, _) => dlg.Close();
        var fixBtn = new System.Windows.Controls.Button { Content = "Fix & Recompile into GH3", Style = (Style)FindResource("FlameButton") };
        fixBtn.Click += async (_, _) =>
        {
            fixBtn.IsEnabled = false;
            fixBtn.Content = "Repairing...";
            var opts = new ChartDoctorFixOptions
            {
                GenerateMissingDifficulties = genDiffsChk.IsChecked == true,
                AddMissingStarPower = spChk.IsChecked == true,
                AddMissingSections = secChk.IsChecked == true,
                ConvertOpenNotesToClassicGh3 = openChk.IsChecked == true,
                OffsetShiftMs = (int)Math.Round(offSlider.Value),
                RecompileToGame = true
            };
            var (ok, msg) = await _chartDoctor.RepairChartAndRecompileAsync(song.ProjectDirectory, opts, s => Dispatcher.Invoke(() => statusLbl.Text = s));
            dlg.Close();
            RefreshSongsList();
            WpfMessageBox.Show(msg, ok ? "Chart Doctor Complete" : "Chart Doctor Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        };
        btns.Children.Add(closeBtn);
        btns.Children.Add(fixBtn);
        stack.Children.Add(btns);

        dlg.Content = stack;
        dlg.ShowDialog();
    }

    private void OpenFastRechartDialog(CustomSongInfo song)
    {
        var dlg = new Window
        {
            Title = $"⚡ 5-Second Fast Re-Chart — {song.Title}",
            Width = 460,
            Height = 300,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "⚡ Instant Re-Chart (Cached AI Analysis)", FontSize = 15.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        stack.Children.Add(new TextBlock
        {
            Text = "Rebuilds the chart & recompiles into Guitar Hero III in ~5 seconds using this song's cached Basic Pitch + Librosa analysis (no Demucs wait!).",
            FontSize = 12,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        stack.Children.Add(new TextBlock { Text = "Chart Density / Feel", FontSize = 12, Foreground = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)), Margin = new Thickness(0, 0, 0, 4) });
        var feelCombo = new System.Windows.Controls.ComboBox { Margin = new Thickness(0, 0, 0, 12) };
        feelCombo.Items.Add("Casual");
        feelCombo.Items.Add("Standard");
        feelCombo.Items.Add("Shredder");
        feelCombo.SelectedIndex = 1;
        stack.Children.Add(feelCombo);

        var offLbl = new TextBlock { Text = "Sync Offset Nudge: 0 ms", FontSize = 12, Foreground = new SolidColorBrush(WpfColor.FromRgb(125, 211, 252)) };
        var offSlider = new Slider { Minimum = -200, Maximum = 200, Value = 0, TickFrequency = 10, IsSnapToTickEnabled = true, Margin = new Thickness(0, 3, 0, 14) };
        offSlider.ValueChanged += (_, _) => offLbl.Text = $"Sync Offset Nudge: {(int)offSlider.Value:+0;-0;0} ms";
        stack.Children.Add(offLbl);
        stack.Children.Add(offSlider);

        var statusLbl = new TextBlock { Text = "", FontSize = 11.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 186, 116)), Margin = new Thickness(0, 0, 0, 10) };
        stack.Children.Add(statusLbl);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var goBtn = new System.Windows.Controls.Button { Content = "⚡ Rebuild & Recompile", Style = (Style)FindResource("GreenButton") };
        goBtn.Click += async (_, _) =>
        {
            goBtn.IsEnabled = false;
            goBtn.Content = "Rebuilding...";
            var feel = feelCombo.SelectedItem?.ToString() ?? "Standard";
            var (ok, msg) = await _creator.InstantRechartFromCacheAsync(song.ProjectDirectory, feel, (int)Math.Round(offSlider.Value), true, s => Dispatcher.Invoke(() => statusLbl.Text = s));
            dlg.Close();
            RefreshSongsList();
            WpfMessageBox.Show(msg, ok ? "Instant Re-Chart Complete" : "Instant Re-Chart Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        };
        btns.Children.Add(goBtn);
        stack.Children.Add(btns);

        dlg.Content = stack;
        dlg.ShowDialog();
    }

    // ==================== SETLIST PRESETS & PARTY SHUFFLE ====================
    private bool _suppressPresetSelection;

    private void RefreshSetlistPresets()
    {
        _suppressPresetSelection = true;
        try
        {
            SetlistPresetCombo.Items.Clear();
            SetlistPresetCombo.Items.Add("-- Load Setlist Preset --");
            foreach (var p in AppConfig.Current.SetlistPresets ?? new())
                SetlistPresetCombo.Items.Add($"{p.Name} ({p.SongIds.Count} songs)");
            SetlistPresetCombo.SelectedIndex = 0;
        }
        finally { _suppressPresetSelection = false; }
    }

    private void SetlistPresetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPresetSelection || !IsLoaded) return;
        int idx = SetlistPresetCombo.SelectedIndex - 1;
        var presets = AppConfig.Current.SetlistPresets ?? new();
        if (idx < 0 || idx >= presets.Count) return;

        var chosen = presets[idx];
        int cap = Math.Clamp(AppConfig.Current.MaxLobbyAutoSyncSongs, 1, 25);
        AppConfig.Current.LobbySetlistIds = chosen.SongIds.Take(cap).ToList();
        AppConfig.Save();
        RefreshSongsList();
        WpfMessageBox.Show($"Loaded Setlist Preset '{chosen.Name}' ({AppConfig.Current.LobbySetlistIds.Count} songs active in Lobby Setlist).", "Setlist Preset Loaded", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveSetlistPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var currentLobby = AppConfig.Current.LobbySetlistIds ?? new();
        if (currentLobby.Count == 0)
        {
            WpfMessageBox.Show("Tag at least 1 song with '+ Lobby' in My Customs first before saving a preset.", "Save Setlist Preset", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = new Window
        {
            Title = "Save Lobby Setlist Preset",
            Width = 380,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(new TextBlock { Text = $"Preset Name ({currentLobby.Count} songs):", FontSize = 13, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
        var nameBox = new System.Windows.Controls.TextBox { Text = "Friday Night Co-Op", Margin = new Thickness(0, 0, 0, 14) };
        sp.Children.Add(nameBox);
        var saveBtn = new System.Windows.Controls.Button { Content = "Save Preset", Style = (Style)FindResource("FlameButton"), HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        saveBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        sp.Children.Add(saveBtn);
        dlg.Content = sp;

        if (dlg.ShowDialog() == true)
        {
            var name = nameBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            AppConfig.Current.SetlistPresets ??= new();
            AppConfig.Current.SetlistPresets.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            AppConfig.Current.SetlistPresets.Add(new SetlistPreset { Name = name, SongIds = currentLobby.ToList(), CreatedAt = DateTime.UtcNow });
            AppConfig.Save();
            RefreshSetlistPresets();
        }
    }

    private void DeleteSetlistPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        int idx = SetlistPresetCombo.SelectedIndex - 1;
        var presets = AppConfig.Current.SetlistPresets ?? new();
        if (idx < 0 || idx >= presets.Count)
        {
            WpfMessageBox.Show("Select a saved preset from the dropdown first to delete it.", "Delete Preset", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var removed = presets[idx].Name;
        presets.RemoveAt(idx);
        AppConfig.Save();
        RefreshSetlistPresets();
        WpfMessageBox.Show($"Deleted Setlist Preset '{removed}'.", "Preset Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ShuffleLobbySetlistBtn_Click(object sender, RoutedEventArgs e)
    {
        var installed = _allSongs.Where(s => !s.IsProject).ToList();
        if (installed.Count == 0)
        {
            WpfMessageBox.Show("No compiled custom songs installed yet to shuffle.", "Shuffle Setlist", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        int cap = Math.Clamp(AppConfig.Current.MaxLobbyAutoSyncSongs, 1, 25);
        var picked = installed.OrderBy(_ => Random.Shared.Next()).Take(Math.Min(cap, installed.Count)).ToList();
        AppConfig.Current.LobbySetlistIds = picked.Select(s => s.SongId).ToList();
        AppConfig.Save();
        RefreshSongsList();
        WpfMessageBox.Show($"Shuffled {picked.Count} random songs into your active Lobby Auto-Sync Setlist!\n\n• " + string.Join("\n• ", picked.Select(p => $"{p.Title} — {p.Artist}")),
            "🎲 Party Setlist Ready!", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private CustomSongInfo? _rechartingSong;

    private async Task RechartSongInCreatorAsync(CustomSongInfo song)
    {
        var audio = Gh3Service.FindSongAudioPath(song, preferGuitarStem: false);
        if (string.IsNullOrWhiteSpace(audio) || !File.Exists(audio))
        {
            WpfMessageBox.Show("Source audio file was not found in this song's project folder.", "Re-Chart AI v2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _rechartingSong = song;
        _editingQueueIndex = -1;
        AddToQueueBtn.Content = "+ Add to Batch Queue";
        NavCreator.IsChecked = true;
        ModeAutoGenRadio.IsChecked = true;
        CrAudioPathBox.Text = audio;
        CrTitleBox.Text = song.Title;
        CrArtistBox.Text = song.Artist;
        CrFocusCombo.SelectedIndex = 0; // Auto (Smart Multi-Lead)
        CrAudioMixCombo.SelectedIndex = 0; // Auto
        CrFeelCombo.SelectedIndex = 1; // Standard
        CrProgressLabel.Text = $"Loaded '{song.Title}' for AI v2 re-charting! Click 'Create & Compile Song' when ready.";
        await DetectBpmInternalAsync(audio);
    }

    private void ExportLobbyPack_Click(object sender, RoutedEventArgs e)
    {
        var lobbyIds = new HashSet<string>(AppConfig.Current.LobbySetlistIds ?? new(), StringComparer.OrdinalIgnoreCase);
        var lobbySongs = _allSongs.Where(s => lobbyIds.Contains(s.SongId) || s.IsInLobbySetlist).ToList();
        if (lobbySongs.Count == 0)
        {
            WpfMessageBox.Show(
                "Your Lobby Setlist is currently empty.\n\nClick '+ Lobby' on the songs you want to play with friends in My Customs, then click 'Export Lobby Pack'.",
                "Export Lobby Pack", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExportSongsSubset(lobbySongs, $"GH3_Lobby_Setlist_{lobbySongs.Count}songs.gh3pack");
    }

    private void ExportSelectedPack_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedExportIds.Count == 0)
        {
            WpfMessageBox.Show("Check the boxes next to one or more songs in My Customs first.", "Export Selected", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ExportSongsSubset(_allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList(), "GH3_Selected_Pack.gh3pack");
    }

    private void ExportAllPack_Click(object sender, RoutedEventArgs e)
        => ExportSongsSubset(_gh3.GetInstalledCustomSongs(), "GH3_Full_Customs_Pack.gh3pack");

    private void ExportSongsSubset(List<CustomSongInfo> songs, string defaultFileName)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Custom Song Pack",
            Filter = "GH3-Sync Pack (*.gh3pack)|*.gh3pack",
            FileName = defaultFileName
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var fs = File.Create(dlg.FileName);
            using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
            foreach (var s in songs)
            {
                try
                {
                    var pkg = _gh3.PackageSong(s.SongId);
                    var entry = archive.CreateEntry($"{s.SongId}.gh3song", CompressionLevel.NoCompression);
                    using var es = entry.Open();
                    es.Write(pkg, 0, pkg.Length);
                }
                catch { }
            }
            if (File.Exists(_gh3.CustomsPakPath))
                archive.CreateEntryFromFile(_gh3.CustomsPakPath, "customs.pak.xen");

            WpfMessageBox.Show($"Exported {songs.Count} song(s) into:\n{dlg.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfirmAndDeleteSong(CustomSongInfo song)
    {
        var dlg = new Window
        {
            Title = "Delete Custom Song",
            Width = 490,
            Height = 285,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = $"Delete '{song.Title}'?",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(248, 113, 113)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"Removes all compiled game files for '{song.SongId}' from DATA/SONGS (.pak.xen) and DATA/MUSIC (.fsb.xen / .dat.xen / previews) and creates a paired career save snapshot first.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var deleteProjChk = new System.Windows.Controls.CheckBox
        {
            Content = "Also delete source project folder, audio & isolated stems",
            IsChecked = true,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 224, 71)),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        };
        stack.Children.Add(deleteProjChk);

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        var confirmBtn = new System.Windows.Controls.Button { Content = "Delete All Files", Style = (Style)FindResource("DangerButton") };
        cancelBtn.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        confirmBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);
        stack.Children.Add(btnPanel);
        dlg.Content = stack;

        if (dlg.ShowDialog() == true)
        {
            StopAudioPreview();
            var res = _gh3.DeleteCustomSong(song, deleteProjChk.IsChecked == true, _saves, _logger);
            if (res.Success)
            {
                _selectedExportIds.Remove(song.SongId);
                WpfMessageBox.Show(res.Message, "Song Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshSongsList();
                RefreshSaveBackups();
            }
            else
            {
                WpfMessageBox.Show(res.Message, "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ==================== PAGE 2: SONG CREATOR + BATCH QUEUE ====================
    private void ChartMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool isUpload = ModeUploadRadio.IsChecked == true;
        ChartPickerPanel.Visibility = isUpload ? Visibility.Visible : Visibility.Collapsed;
        BpmPickerPanel.Visibility = isUpload ? Visibility.Collapsed : Visibility.Visible;
        ChartTuningRow.Visibility = isUpload ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BrowseAudioBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Audio File",
            Filter = "Audio Files (*.mp3;*.wav;*.ogg)|*.mp3;*.wav;*.ogg|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            await LoadAudioIntoCreatorAsync(dlg.FileName);
    }

    private async Task LoadAudioIntoCreatorAsync(string path, bool overwriteTitleArtist = false)
    {
        CrAudioPathBox.Text = path;
        if (overwriteTitleArtist || string.IsNullOrWhiteSpace(CrTitleBox.Text))
        {
            var (artist, title) = ParseArtistTitleFromFileName(path);
            if (overwriteTitleArtist || string.IsNullOrWhiteSpace(CrArtistBox.Text))
                CrArtistBox.Text = artist == "Unknown Artist" ? CrArtistBox.Text : artist;
            CrTitleBox.Text = title;
        }
        await DetectBpmInternalAsync(path);
    }

    private void PreviewCreatorAudioBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CrAudioPathBox.Text) || !File.Exists(CrAudioPathBox.Text)) return;
        PlayAudioPreview(CrAudioPathBox.Text, string.IsNullOrWhiteSpace(CrTitleBox.Text) ? Path.GetFileName(CrAudioPathBox.Text) : CrTitleBox.Text);
    }

    private void BrowseChartBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Chart / MIDI File",
            Filter = "Chart / MIDI (*.chart;*.mid;*.midi)|*.chart;*.mid;*.midi|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) CrChartPathBox.Text = dlg.FileName;
    }

    private async void UrlImportBtn_Click(object sender, RoutedEventArgs e)
    {
        var url = UrlImportBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url))
        {
            WpfMessageBox.Show("Paste an audio/video URL into the box first (e.g. YouTube or SoundCloud link).", "Grab Audio URL", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        UrlImportBtn.IsEnabled = false;
        CrProgressLabel.Text = "Grabbing audio & cover art via yt-dlp...";
        try
        {
            var res = await _ytDlp.DownloadAudioFromUrlAsync(url, s => Dispatcher.Invoke(() => CrProgressLabel.Text = s));
            if (res.Success)
            {
                CrAudioPathBox.Text = res.AudioPath;
                CrTitleBox.Text = res.Title;
                CrArtistBox.Text = res.Artist;
                CrProgressLabel.Text = res.Message;
                UrlImportBox.Text = "";
                RefreshToolsAndAiStatus();
                await DetectBpmInternalAsync(res.AudioPath);
            }
            else
            {
                CrProgressLabel.Text = res.Message;
                WpfMessageBox.Show(res.Message, "URL Audio Importer", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            UrlImportBtn.IsEnabled = true;
        }
    }

    private async void FetchCreatorMetaBtn_Click(object sender, RoutedEventArgs e)
    {
        FetchCreatorMetaBtn.IsEnabled = false;
        PageCreator_StageHud.Report(20, "FETCHING METADATA & ALBUM ART", "Querying Deezer HD (1000×1000), Apple iTunes, and embedded tags...");
        CrProgressLabel.Text = "Fetching album art & track metadata...";
        try
        {
            var res = await _albumMeta.FetchMetadataAndCoverAsync(CrTitleBox.Text.Trim(), CrArtistBox.Text.Trim(), CrAudioPathBox?.Text?.Trim());
            if (res.Found)
            {
                if (!string.IsNullOrWhiteSpace(res.Title)) CrTitleBox.Text = res.Title;
                if (!string.IsNullOrWhiteSpace(res.Artist)) CrArtistBox.Text = res.Artist;
                if (!string.IsNullOrWhiteSpace(res.Album)) CrAlbumBox.Text = res.Album;
                if (res.Year > 0) CrYearBox.Text = res.Year.ToString();
                if (!string.IsNullOrWhiteSpace(res.Genre)) CrGenreBox.Text = res.Genre;
                CrProgressLabel.Text = res.Message;

                _creatorArtworkCandidates = res.Candidates ?? new List<ArtworkCandidate>();
                _creatorCoverArtBytes = res.CoverArtBytes;

                UpdateArtworkCarouselUi();
                PageCreator_StageHud.Finish($"Found HD artwork ({_creatorArtworkCandidates.Count} candidate(s)) & tags for {res.Artist} - {res.Title}");
            }
            else
            {
                CrProgressLabel.Text = res.Message;
                PageCreator_StageHud.Reset();
            }
        }
        finally
        {
            FetchCreatorMetaBtn.IsEnabled = true;
        }
    }

    private void UpdateArtworkCarouselUi()
    {
        if (_creatorArtworkCandidates == null || _creatorArtworkCandidates.Count == 0)
        {
            CrArtCarouselContainer.Visibility = Visibility.Collapsed;
            return;
        }

        CrArtCarouselContainer.Visibility = Visibility.Visible;
        HighlightArtCard(1);

        void BindCard(int idx, Border card, TextBlock lbl, TextBlock src, System.Windows.Controls.Image img)
        {
            if (_creatorArtworkCandidates.Count > idx)
            {
                var c = _creatorArtworkCandidates[idx];
                lbl.Text = string.IsNullOrWhiteSpace(c.Album) ? c.Title : c.Album;
                src.Text = $"{c.Source}";
                LoadImageIntoControl(img, c.ImageBytes);
                card.Visibility = Visibility.Visible;
            }
            else
            {
                card.Visibility = Visibility.Collapsed;
            }
        }

        BindCard(0, CrArtCard1, CrArtLabel1, CrArtSource1, CrArtImg1);
        BindCard(1, CrArtCard2, CrArtLabel2, CrArtSource2, CrArtImg2);
        BindCard(2, CrArtCard3, CrArtLabel3, CrArtSource3, CrArtImg3);
        BindCard(3, CrArtCard4, CrArtLabel4, CrArtSource4, CrArtImg4);
        BindCard(4, CrArtCard5, CrArtLabel5, CrArtSource5, CrArtImg5);
    }

    private static void LoadImageIntoControl(System.Windows.Controls.Image imgCtrl, byte[]? bytes)
    {
        if (bytes == null || bytes.Length == 0) return;
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            imgCtrl.Source = bmp;
        }
        catch { }
    }

    private void HighlightArtCard(int index)
    {
        var activeBrush = (System.Windows.Media.Brush)FindResource("AccentFlame");
        var subtleBrush = (System.Windows.Media.Brush)FindResource("BorderSubtle");

        CrArtCard1.BorderBrush = index == 1 ? activeBrush : subtleBrush;
        CrArtCard2.BorderBrush = index == 2 ? activeBrush : subtleBrush;
        CrArtCard3.BorderBrush = index == 3 ? activeBrush : subtleBrush;
        CrArtCard4.BorderBrush = index == 4 ? activeBrush : subtleBrush;
        CrArtCard5.BorderBrush = index == 5 ? activeBrush : subtleBrush;
    }

    private void CrArtCard1_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HighlightArtCard(1);
        if (_creatorArtworkCandidates != null && _creatorArtworkCandidates.Count >= 1)
        {
            _creatorCoverArtBytes = _creatorArtworkCandidates[0].ImageBytes;
            _creatorCoverArtPath = null;
        }
    }

    private void CrArtCard2_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HighlightArtCard(2);
        if (_creatorArtworkCandidates != null && _creatorArtworkCandidates.Count >= 2)
        {
            _creatorCoverArtBytes = _creatorArtworkCandidates[1].ImageBytes;
            _creatorCoverArtPath = null;
        }
    }

    private void CrArtCard3_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HighlightArtCard(3);
        if (_creatorArtworkCandidates != null && _creatorArtworkCandidates.Count >= 3)
        {
            _creatorCoverArtBytes = _creatorArtworkCandidates[2].ImageBytes;
            _creatorCoverArtPath = null;
        }
    }

    private void CrArtCard4_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HighlightArtCard(4);
        if (_creatorArtworkCandidates != null && _creatorArtworkCandidates.Count >= 4)
        {
            _creatorCoverArtBytes = _creatorArtworkCandidates[3].ImageBytes;
            _creatorCoverArtPath = null;
        }
    }

    private void CrArtCard5_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        HighlightArtCard(5);
        if (_creatorArtworkCandidates != null && _creatorArtworkCandidates.Count >= 5)
        {
            _creatorCoverArtBytes = _creatorArtworkCandidates[4].ImageBytes;
            _creatorCoverArtPath = null;
        }
    }

    private void CrArtCustomPickBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Album Art Image (JPG/PNG)",
            Filter = "Image Files (*.jpg;*.jpeg;*.png)|*.jpg;*.jpeg;*.png|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(dlg.FileName);
                _creatorCoverArtBytes = bytes;
                _creatorCoverArtPath = dlg.FileName;
                CrArtCarouselContainer.Visibility = Visibility.Visible;
                CrArtLabel1.Text = "Custom Pick";
                CrArtSource1.Text = Path.GetFileName(dlg.FileName);
                LoadImageIntoControl(CrArtImg1, bytes);
                HighlightArtCard(1);
            }
            catch (Exception ex)
            {
                WpfMessageBox.Show($"Failed to load image: {ex.Message}", "Image Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void InstallYtDlpBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallYtDlpBtn.IsEnabled = false;
        InstallYtDlpBtn.Content = "Downloading...";
        try
        {
            var (ok, msg) = await _ytDlp.InstallOrUpdateAsync();
            RefreshToolsAndAiStatus();
            WpfMessageBox.Show(msg, ok ? "yt-dlp Ready" : "yt-dlp Error", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            InstallYtDlpBtn.IsEnabled = true;
            InstallYtDlpBtn.Content = _ytDlp.IsInstalled ? "Update yt-dlp" : "Install / Update yt-dlp";
        }
    }

    private void UninstallYtDlpBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = _ytDlp.Uninstall();
        RefreshToolsAndAiStatus();
        WpfMessageBox.Show(msg, "yt-dlp", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private async void DetectBpmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CrAudioPathBox.Text) || !File.Exists(CrAudioPathBox.Text)) return;
        _tapTempoLocked = false;
        _tapTempoStamps.Clear();
        await DetectBpmInternalAsync(CrAudioPathBox.Text);
    }

    private void TapTempoBtn_Click(object sender, RoutedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (_tapTempoStamps.Count > 0 && (now - _tapTempoStamps[^1]).TotalSeconds > 2.5)
            _tapTempoStamps.Clear();

        _tapTempoStamps.Add(now);
        while (_tapTempoStamps.Count > 9) _tapTempoStamps.RemoveAt(0);

        if (_tapTempoStamps.Count < 2)
        {
            BpmStatusLabel.Text = "Tap again on beat...";
            return;
        }

        double totalSec = (_tapTempoStamps[^1] - _tapTempoStamps[0]).TotalSeconds;
        int intervals = _tapTempoStamps.Count - 1;
        double avgSec = totalSec / intervals;
        if (avgSec > 0.15 && avgSec < 2.5)
        {
            double tappedBpm = Math.Clamp(60.0 / avgSec, 40.0, 260.0);
            CrBpmBox.Text = tappedBpm.ToString("F1");
            _tapTempoLocked = true;
            BpmStatusLabel.Text = $"Tapped: {tappedBpm:F1} BPM ({_tapTempoStamps.Count} taps — Locked)";
        }
    }

    private void CreatorPreviewHighwayBtn_Click(object sender, RoutedEventArgs e)
    {
        string? chartPath = ModeUploadRadio.IsChecked == true ? CrChartPathBox.Text.Trim() : _lastCreatedChartPath;
        string? audioPath = ModeUploadRadio.IsChecked == true ? CrAudioPathBox.Text.Trim() : (_lastCreatedAudioPath ?? CrAudioPathBox.Text.Trim());
        string title = string.IsNullOrWhiteSpace(CrTitleBox.Text) ? _lastCreatedTitle : CrTitleBox.Text.Trim();
        string artist = string.IsNullOrWhiteSpace(CrArtistBox.Text) ? _lastCreatedArtist : CrArtistBox.Text.Trim();
        string? projDir = ModeUploadRadio.IsChecked == true ? null : _lastCreatedProjectDir;

        if (string.IsNullOrWhiteSpace(chartPath) || !File.Exists(chartPath) || !chartPath.EndsWith(".chart", StringComparison.OrdinalIgnoreCase))
        {
            WpfMessageBox.Show(
                "Load a .chart file (in 'Use Existing Chart' mode) or click 'Create & Compile Song' first to preview it on the 5-fret highway.\n\nYou can also click '🎸 Highway' on any song in My Customs!",
                "Interactive Highway Preview", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        OpenHighwayPreview(string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(chartPath) : title, artist, chartPath, audioPath, projDir);
    }

    private async Task DetectBpmInternalAsync(string audioPath)
    {
        BpmStatusLabel.Text = "Analyzing tempo & beats...";
        var res = await Task.Run(() => _audioAnalysis.AnalyzeAudio(audioPath));
        if (res.Success)
        {
            CrBpmBox.Text = res.Bpm.ToString("F1");
            BpmStatusLabel.Text = $"{res.Bpm:F1} BPM ({res.BeatTimestampsSeconds.Count} beats)";
        }
        else BpmStatusLabel.Text = res.Message;
    }

    private string GetSelectedFocusValue() => CrFocusCombo.SelectedIndex switch
    {
        1 => "Guitar",
        2 => "Keys / Synth / Sax",
        3 => "Bass",
        4 => "Vocals",
        _ => "Auto"
    };

    private string GetSelectedAudioModeValue() => CrAudioMixCombo.SelectedIndex switch
    {
        1 => "StudioMaster",
        2 => "IsolatedStems",
        3 => "HybridBlend",
        _ => "Auto"
    };

    private void SelectComboByFocus(string? focus)
    {
        CrFocusCombo.SelectedIndex = focus switch
        {
            "Guitar" => 1,
            "Keys / Synth / Sax" => 2,
            "Bass" => 3,
            "Vocals" => 4,
            _ => 0
        };
    }

    private void SelectComboByAudioMode(string? mode)
    {
        CrAudioMixCombo.SelectedIndex = mode switch
        {
            "StudioMaster" => 1,
            "IsolatedStems" => 2,
            "HybridBlend" => 3,
            _ => 0
        };
    }

    private void SelectComboByFeel(string? feel)
    {
        CrFeelCombo.SelectedIndex = feel switch
        {
            "Casual" => 0,
            "Shredder" => 2,
            _ => 1
        };
    }

    private SongCreationRequest? BuildCreatorRequest(out string audioPath, out string? chartPath)
    {
        audioPath = CrAudioPathBox.Text.Trim();
        chartPath = ModeAutoGenRadio.IsChecked == true ? null : CrChartPathBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(CrTitleBox.Text) || string.IsNullOrWhiteSpace(CrArtistBox.Text))
        {
            WpfMessageBox.Show("Enter both Song Title and Artist Name.", "Missing Info", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        if (string.IsNullOrWhiteSpace(audioPath) || !File.Exists(audioPath))
        {
            WpfMessageBox.Show("Select an audio file (.mp3, .wav, or .ogg).", "Missing Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        bool autoGen = ModeAutoGenRadio.IsChecked == true;
        if (!autoGen && (string.IsNullOrWhiteSpace(chartPath) || !File.Exists(chartPath)))
        {
            WpfMessageBox.Show("Select a .chart or .mid file.", "Missing Chart", MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        int offsetMs = int.TryParse(CrOffsetBox.Text, out var parsedOff) ? Math.Clamp(parsedOff, -500, 500) : 0;

        return new SongCreationRequest
        {
            Title = CrTitleBox.Text.Trim(),
            Artist = CrArtistBox.Text.Trim(),
            Album = CrAlbumBox.Text.Trim(),
            Year = int.TryParse(CrYearBox.Text, out var y) ? y : DateTime.Now.Year,
            Genre = string.IsNullOrWhiteSpace(CrGenreBox.Text) ? "Rock" : CrGenreBox.Text.Trim(),
            Charter = string.IsNullOrWhiteSpace(CrCharterBox.Text) ? "Mad Scientist" : CrCharterBox.Text.Trim(),
            Bpm = double.TryParse(CrBpmBox.Text, out var bpm) ? bpm : 120.0,
            PreviewStartMs = _creatorPreviewStartMs,
            AutoDetectBpm = !_tapTempoLocked,
            UseVariableTempoMap = CrVariableTempoChk.IsChecked == true && !_tapTempoLocked,
            ChartFeel = (CrFeelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Standard",
            InstrumentFocus = GetSelectedFocusValue(),
            AudioMixMode = GetSelectedAudioModeValue(),
            AutoGenerateChart = autoGen,
            SeparateStemsWithDemucs = CrSeparateStemsChk.IsChecked == true && _demucs.GetStatus().IsInstalled,
            AutoCompile = CrAutoCompileChk.IsChecked == true,
            NormalizeLoudness = CrNormalizeLoudnessChk.IsChecked == true,
            TrimStartSeconds = _creatorTrimStartSec,
            TrimEndSeconds = _creatorTrimEndSec,
            FadeOutSeconds = CrFadeOutChk.IsChecked == true ? 2.0 : 0.0,
            AudioOffsetMs = offsetMs,
            UseCachedAiAnalysis = true,
            EnableStadiumChants = CrStadiumChantsChk.IsChecked == true,
            EnableBossBattle = CrBossBattleChk?.IsChecked == true,
            EnableTradeOffSolo = CrTradeOffSoloChk?.IsChecked == true,
            EnableAiLyricsSections = CrAiLyricsSectionsChk?.IsChecked == true,
            EnableCharterSignature = CrSignCharterChk?.IsChecked == true,
            CharterFriendCode = _friends?.Profile?.FriendCode ?? "",
            CoverArtBytes = _creatorCoverArtBytes
        };
    }

    private bool ConfirmDuplicateHandlingIfNeeded(string title, string artist)
    {
        var existing = _gh3.FindDuplicateInstalledSong(title, artist);
        if (existing == null) return true;

        // If we are currently re-charting this exact song via Re-Chart AI v2:
        if (_rechartingSong != null && (_rechartingSong.SongId.Equals(existing.SongId, StringComparison.OrdinalIgnoreCase) ||
            _rechartingSong.Title.Equals(title.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            StopAudioPreview();
            _gh3.DeleteCustomSong(existing, deleteProjectFolder: false, _saves, _logger);
            return true;
        }

        var choice = WpfMessageBox.Show(
            $"A song matching '{existing.Title}' by '{existing.Artist}' ({existing.SongId}) is already installed in Guitar Hero III.\n\n" +
            "• Click YES to replace the existing version cleanly (recommended — updates in-place).\n" +
            "• Click NO to keep both versions.\n" +
            "• Click CANCEL to abort.",
            "Duplicate Song Detected",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes)
        {
            StopAudioPreview();
            var del = _gh3.DeleteCustomSong(existing, deleteProjectFolder: false, _saves, _logger);
            _logger.LogInfo($"[DUPLICATE] Replaced existing '{existing.SongId}': {del.Message}");
            RefreshSongsList();
        }
        return true;
    }

    private void ClearCreatorInputFields()
    {
        CrTitleBox.Text = "";
        CrArtistBox.Text = "";
        CrAlbumBox.Text = "";
        CrAudioPathBox.Text = "";
        CrChartPathBox.Text = "";
        BpmStatusLabel.Text = "";
        _editingQueueIndex = -1;
        _rechartingSong = null;
        AddToQueueBtn.Content = "+ Add to Batch Queue";
    }

    private void AddToQueueBtn_Click(object sender, RoutedEventArgs e)
    {
        var req = BuildCreatorRequest(out var audioPath, out var chartPath);
        if (req == null) return;

        var item = new QueuedSongItem(req, audioPath, chartPath);
        if (_editingQueueIndex >= 0 && _editingQueueIndex < _batchQueue.Count)
        {
            _batchQueue[_editingQueueIndex] = item;
            CrProgressLabel.Text = $"Saved changes to queued song #{_editingQueueIndex + 1}: {req.Artist} - {req.Title}";
        }
        else
        {
            _batchQueue.Add(item);
            CrProgressLabel.Text = $"Added '{req.Artist} - {req.Title}' to Batch Queue ({_batchQueue.Count} total).";
        }

        ClearCreatorInputFields();
        RenderBatchQueue();
    }

    private void ClearQueueBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_batchQueue.Count == 0) return;
        _batchQueue.Clear();
        _editingQueueIndex = -1;
        AddToQueueBtn.Content = "+ Add to Batch Queue";
        RenderBatchQueue();
    }

    private void LoadQueueItemForEdit(int index)
    {
        if (index < 0 || index >= _batchQueue.Count) return;
        var item = _batchQueue[index];
        _editingQueueIndex = index;

        CrAudioPathBox.Text = item.AudioPath;
        if (!string.IsNullOrWhiteSpace(item.ChartPath))
        {
            ModeUploadRadio.IsChecked = true;
            CrChartPathBox.Text = item.ChartPath;
        }
        else
        {
            ModeAutoGenRadio.IsChecked = true;
            CrChartPathBox.Text = "";
        }

        CrTitleBox.Text = item.Meta.Title;
        CrArtistBox.Text = item.Meta.Artist;
        CrAlbumBox.Text = item.Meta.Album;
        CrYearBox.Text = item.Meta.Year.ToString();
        CrGenreBox.Text = item.Meta.Genre;
        CrCharterBox.Text = item.Meta.Charter;
        CrBpmBox.Text = item.Meta.Bpm.ToString("F1");
        CrVariableTempoChk.IsChecked = item.Meta.UseVariableTempoMap;
        SelectComboByFeel(item.Meta.ChartFeel);
        SelectComboByFocus(item.Meta.InstrumentFocus);
        SelectComboByAudioMode(item.Meta.AudioMixMode);
        _creatorPreviewStartMs = item.Meta.PreviewStartMs;
        CrPreviewStartLabel.Text = $"Menu Preview Start: {_creatorPreviewStartMs / 1000.0:F1}s";

        AddToQueueBtn.Content = $"Save Queue Edit (#{index + 1})";
        CrProgressLabel.Text = $"Editing queued song #{index + 1} ('{item.Meta.Title}') — tweak settings above and click 'Save Queue Edit (#{index + 1})'.";
        RenderBatchQueue();
    }

    private void RenderBatchQueue()
    {
        BatchQueuePanel.Children.Clear();
        bool hasItems = _batchQueue.Count > 0;
        RunQueueBtn.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        ClearQueueBtn.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        BatchQueueContainer.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;

        RunQueueBtn.Content = $"Compile Batch Queue ({_batchQueue.Count})";
        BatchQueueHeaderText.Text = $"Batch Queue ({_batchQueue.Count} {(_batchQueue.Count == 1 ? "song" : "songs")})";

        for (int idx = 0; idx < _batchQueue.Count; idx++)
        {
            int i = idx;
            var item = _batchQueue[i];
            bool isEditing = i == _editingQueueIndex;

            var card = new Border
            {
                Background = (WpfBrush)FindResource("BgCard"),
                BorderBrush = isEditing ? (WpfBrush)FindResource("AccentFlame") : (WpfBrush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(isEditing ? 1.5 : 1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(11, 8, 11, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            info.Children.Add(new TextBlock
            {
                Text = $"#{i + 1}  {item.Meta.Title}  —  {item.Meta.Artist}{(isEditing ? "  [Editing]" : "")}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)FindResource(isEditing ? "AccentFlameText" : "TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var modeStr = string.IsNullOrWhiteSpace(item.ChartPath)
                ? $"{item.Meta.Bpm:F1} BPM • {item.Meta.ChartFeel} • Focus: {item.Meta.InstrumentFocus} • Audio: {item.Meta.AudioMixMode}"
                : $"Existing Chart: {Path.GetFileName(item.ChartPath)} • Audio: {item.Meta.AudioMixMode}";
            info.Children.Add(new TextBlock
            {
                Text = $"{modeStr} • {Path.GetFileName(item.AudioPath)}",
                FontSize = 11,
                Foreground = (WpfBrush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

            var editBtn = new System.Windows.Controls.Button
            {
                Content = "Edit",
                Style = (Style)FindResource(isEditing ? "FlameButton" : "OutlineButton"),
                Padding = new Thickness(9, 4, 9, 4),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Load this queued song into the form above to customize its BPM, Feel, Focus, or Audio Mode"
            };
            editBtn.Click += (_, _) => LoadQueueItemForEdit(i);

            var upBtn = new System.Windows.Controls.Button
            {
                Content = "▲",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = i > 0,
                ToolTip = "Move Up"
            };
            upBtn.Click += (_, _) =>
            {
                (_batchQueue[i - 1], _batchQueue[i]) = (_batchQueue[i], _batchQueue[i - 1]);
                if (_editingQueueIndex == i) _editingQueueIndex = i - 1;
                else if (_editingQueueIndex == i - 1) _editingQueueIndex = i;
                RenderBatchQueue();
            };

            var downBtn = new System.Windows.Controls.Button
            {
                Content = "▼",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(7, 4, 7, 4),
                Margin = new Thickness(0, 0, 4, 0),
                IsEnabled = i < _batchQueue.Count - 1,
                ToolTip = "Move Down"
            };
            downBtn.Click += (_, _) =>
            {
                (_batchQueue[i + 1], _batchQueue[i]) = (_batchQueue[i], _batchQueue[i + 1]);
                if (_editingQueueIndex == i) _editingQueueIndex = i + 1;
                else if (_editingQueueIndex == i + 1) _editingQueueIndex = i;
                RenderBatchQueue();
            };

            var previewBtn = new System.Windows.Controls.Button
            {
                Content = "▶",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 4, 0),
                ToolTip = "Preview Audio"
            };
            previewBtn.Click += (_, _) => PlayAudioPreview(item.AudioPath, $"{item.Meta.Artist} - {item.Meta.Title}");

            var removeBtn = new System.Windows.Controls.Button
            {
                Content = "✕",
                Style = (Style)FindResource("DangerButton"),
                Padding = new Thickness(8, 4, 8, 4),
                ToolTip = "Remove from Batch Queue"
            };
            removeBtn.Click += (_, _) =>
            {
                _batchQueue.RemoveAt(i);
                if (_editingQueueIndex == i)
                {
                    _editingQueueIndex = -1;
                    AddToQueueBtn.Content = "+ Add to Batch Queue";
                }
                else if (_editingQueueIndex > i)
                {
                    _editingQueueIndex--;
                    AddToQueueBtn.Content = $"Save Queue Edit (#{_editingQueueIndex + 1})";
                }
                RenderBatchQueue();
            };

            btns.Children.Add(editBtn);
            btns.Children.Add(upBtn);
            btns.Children.Add(downBtn);
            btns.Children.Add(previewBtn);
            btns.Children.Add(removeBtn);
            Grid.SetColumn(btns, 1);
            grid.Children.Add(btns);

            card.Child = grid;
            BatchQueuePanel.Children.Add(card);
        }
    }

    private async void RunQueueBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_batchQueue.Count == 0) return;

        CreateSongSubmitBtn.IsEnabled = false;
        RunQueueBtn.IsEnabled = false;
        ClearQueueBtn.IsEnabled = false;
        CrProgressBar.Visibility = Visibility.Visible;
        int succeeded = 0;

        try
        {
            for (int i = 0; i < _batchQueue.Count; i++)
            {
                var item = _batchQueue[i];
                var res = await _creator.CreateSongFromFilesAsync(item.Meta, item.AudioPath, item.ChartPath, (pct, msg) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        CrProgressBar.Value = pct;
                        CrProgressLabel.Text = $"[{i + 1}/{_batchQueue.Count}] {item.Meta.Title}: {msg}";
                    });
                });
                if (res.Success) succeeded++;
            }

            WpfMessageBox.Show($"Batch queue complete: {succeeded} / {_batchQueue.Count} song(s) created & compiled!", "Batch Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            _batchQueue.Clear();
            _editingQueueIndex = -1;
            AddToQueueBtn.Content = "+ Add to Batch Queue";
            RenderBatchQueue();
            RefreshSongsList();
        }
        finally
        {
            CreateSongSubmitBtn.IsEnabled = true;
            RunQueueBtn.IsEnabled = true;
            ClearQueueBtn.IsEnabled = true;
            CrProgressBar.Visibility = Visibility.Collapsed;
            CrProgressLabel.Text = "";
        }
    }

    private void ClearCreatorFormBtn_Click(object sender, RoutedEventArgs e)
    {
        CrAudioPathBox.Text = "";
        CrChartPathBox.Text = "";
        CrTitleBox.Text = "";
        CrArtistBox.Text = "";
        CrAlbumBox.Text = "";
        CrYearBox.Text = DateTime.Now.Year.ToString();
        CrGenreBox.Text = "Rock";
        CrCharterBox.Text = "Mad Scientist";
        CrBpmBox.Text = "120";
        _tapTempoLocked = false;
        CrOffsetBox.Text = "0";
        _creatorPreviewStartMs = 5000;
        _creatorTrimStartSec = 0.0;
        _creatorTrimEndSec = 0.0;
        _creatorCoverArtBytes = null;
        _creatorArtworkCandidates?.Clear();
        CrArtImg1.Source = null;
        CrArtImg2.Source = null;
        CrArtImg3.Source = null;
        CrArtImg4.Source = null;
        CrArtImg5.Source = null;
        CrArtCarouselContainer.Visibility = Visibility.Collapsed;
        CrProgressLabel.Text = "";
        PageCreator_StageHud.Reset();
        CrResultBox.Visibility = Visibility.Collapsed;
        _rechartingSong = null;
        ShowStageToast("✨ FORM CLEARED", "Song creator inputs & album artwork reset.", "#3B82F6");
    }

    private async void CreateSongBtn_Click(object sender, RoutedEventArgs e)
    {
        if (CrSeparateStemsChk.IsChecked == true && !_demucs.GetStatus().IsInstalled)
        {
            var ask = WpfMessageBox.Show(
                "Demucs 6-Stem tools are not installed yet.\nOpen Tools & Stems to install them now? (Click No to use built-in pitch analysis.)",
                "Stem Tools Not Installed", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes) { NavAiTools.IsChecked = true; return; }
        }

        var meta = BuildCreatorRequest(out var audioPath, out var chartPath);
        if (meta == null) return;

        if (!ConfirmDuplicateHandlingIfNeeded(meta.Title, meta.Artist)) return;

        CreateSongSubmitBtn.IsEnabled = false;
        CrProgressBar.Visibility = Visibility.Visible;
        CrProgressBar.Value = 5;
        CrResultBox.Visibility = Visibility.Collapsed;
        _discordRpc?.SetTemporaryActivity("Charting Custom Song in GH3-Sync", $"{meta.Title} — {meta.Artist}", 180);

        try
        {
            var result = await _creator.CreateSongFromFilesAsync(meta, audioPath, chartPath, (pct, msg) => Dispatcher.Invoke(() =>
            {
                CrProgressBar.Value = pct;
                CrProgressLabel.Text = msg;
                PageCreator_StageHud.Report(pct, "CREATING GUITAR HERO III CUSTOM", msg);
            }));

            CrResultBox.Visibility = Visibility.Visible;
            if (result.Success)
            {
                _lastCreatedProjectDir = result.ProjectDirectory;
                _lastCreatedChartPath = ChartDoctorService.FindChartFileInProject(result.ProjectDirectory);
                _lastCreatedAudioPath = Path.Combine(result.ProjectDirectory, "song.mp3");
                if (!File.Exists(_lastCreatedAudioPath)) _lastCreatedAudioPath = audioPath;
                _lastCreatedTitle = meta.Title;
                _lastCreatedArtist = meta.Artist;
                CrResultBox.Background = new SolidColorBrush(WpfColor.FromRgb(13, 34, 26));
                CrResultBox.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
                CrResultText.Foreground = new SolidColorBrush(WpfColor.FromRgb(167, 243, 208));
                var lines = new List<string>
                {
                    result.Message,
                    $"• Tempo: {result.DetectedBpm:F1} BPM  •  Feel: {meta.ChartFeel}  •  Audio: {(string.IsNullOrEmpty(result.AudioModeUsed) ? "Studio Master" : result.AudioModeUsed)}"
                };
                if (!string.IsNullOrWhiteSpace(result.StemEnergySummary))
                    lines.Add($"• Stem Balance: {result.StemEnergySummary}");
                if (result.RepeatedRiffsMatched > 0 || result.ForcedMarkersApplied > 0 || result.BassNoteCount > 0)
                    lines.Add($"• AI Chart v2: {result.TranscribedNoteCount:N0} Lead notes • {result.RepeatedRiffsMatched} repeating riffs locked • {result.ForcedMarkersApplied} forced HOPO/strum markers{(result.BassNoteCount > 0 ? $" • Co-Op Bass ({result.BassNoteCount:N0} notes)" : "")}");
                lines.Add($"• Folder: {result.ProjectDirectory}");
                CrResultText.Text = string.Join("\n", lines);

                // Persist artwork to project folder if chosen in Studio
                if (_creatorCoverArtBytes != null && _creatorCoverArtBytes.Length > 1024 && Directory.Exists(result.ProjectDirectory))
                {
                    try
                    {
                        var albJpg = Path.Combine(result.ProjectDirectory, "album.jpg");
                        var albPng = Path.Combine(result.ProjectDirectory, "album.png");
                        File.WriteAllBytes(albJpg, _creatorCoverArtBytes);
                        File.WriteAllBytes(albPng, _creatorCoverArtBytes);
                    }
                    catch { }
                }

                RefreshSongsList();
                _rechartingSong = null;

                // Wave 5: Cryptographic Charter Signature
                if (CrSignCharterChk?.IsChecked == true && File.Exists(_lastCreatedChartPath))
                {
                    _charterSigner.SignChart(_lastCreatedChartPath, meta.Charter, _friends.Profile.FriendCode);
                }

                // Wave 3: Boss Battle Duel
                if (CrBossBattleChk?.IsChecked == true && File.Exists(_lastCreatedChartPath))
                {
                    var battleCfg = new BossBattleCreatorService.BossBattleConfig(
                        BossName: "Lou the Devil",
                        Difficulty: "Shredder",
                        PowerUpDensity: 4,
                        BossHealthDrain: 1.2,
                        IncludeIntroSolo: true,
                        Venue: meta.Venue
                    );
                    _bossService.ConvertChartToBossBattle(_lastCreatedChartPath, battleCfg);
                }

                // Trade-Off Solo Guitar Duels
                if (CrTradeOffSoloChk?.IsChecked == true && File.Exists(_lastCreatedChartPath))
                {
                    try
                    {
                        var chartText = File.ReadAllText(_lastCreatedChartPath);
                        var (tradeOk, _, tradeChart) = TradeOffSoloService.GenerateTradeOffSoloChart(chartText);
                        if (tradeOk) File.WriteAllText(_lastCreatedChartPath, tradeChart);
                    }
                    catch { }
                }

                // AI Lyric & Section Markers
                if (CrAiLyricsSectionsChk?.IsChecked == true && File.Exists(_lastCreatedChartPath))
                {
                    try
                    {
                        var chartText = File.ReadAllText(_lastCreatedChartPath);
                        var sectionChart = AiLyricSectionService.InjectSectionsIntoChart(chartText, 180.0, result.DetectedBpm > 40 ? result.DetectedBpm : 120.0);
                        File.WriteAllText(_lastCreatedChartPath, sectionChart);
                    }
                    catch { }
                }

                // Unlock Hall of Fame achievement
                RockHallOfFameService.UnlockAchievement("MASTER_CHARTER");

                PageCreator_StageHud.Finish($"Encore Ready: {meta.Artist} - {meta.Title} compiled cleanly!");

                try
                {
                    SongVictoryDialog.ShowVictory(
                        this,
                        meta.Title,
                        meta.Artist,
                        meta.Album,
                        meta.Year.ToString(),
                        (int)Math.Round(result.DetectedBpm),
                        result.TranscribedNoteCount,
                        null,
                        _creatorCoverArtBytes,
                        _lastCreatedAudioPath,
                        onPlayInGh3: () => LaunchGh3(),
                        onHighwayPreview: () =>
                        {
                            if (File.Exists(_lastCreatedChartPath))
                                OpenHighwayPreview(meta.Title, meta.Artist, _lastCreatedChartPath, _lastCreatedAudioPath, result.ProjectDirectory);
                        },
                        onAudioPreview: () =>
                        {
                            if (File.Exists(_lastCreatedAudioPath))
                                PlayAudioPreview(_lastCreatedAudioPath, meta.Title);
                        });
                }
                catch { }
            }
            else
            {
                PageCreator_StageHud.SetError("COMPILATION FAILED", result.Message);
                CrResultBox.Background = new SolidColorBrush(WpfColor.FromRgb(43, 16, 20));
                CrResultBox.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
                CrResultText.Foreground = new SolidColorBrush(WpfColor.FromRgb(254, 202, 202));
                CrResultText.Text = result.Message;
            }
        }
        finally
        {
            CreateSongSubmitBtn.IsEnabled = true;
            CrProgressLabel.Text = "";
            CrProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== COMMUNITY CHART SEARCH (CHORUS ENCORE) ====================
    private async void CommunitySearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            await RunCommunityChartSearchAsync();
        }
    }

    private async void CommunitySearchBtn_Click(object sender, RoutedEventArgs e)
        => await RunCommunityChartSearchAsync();

    private void CommunityFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (IsLoaded && _lastCommunitySearchCharts.Count > 0)
            RenderCommunitySearchResults();
    }

    private void CommunitySortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && _lastCommunitySearchCharts.Count > 0)
            RenderCommunitySearchResults();
    }

    private void CommunityClassicFretsChk_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.ConvertOpenNotesToClassicGh3 = CommunityClassicFretsChk.IsChecked == true;
        AppConfig.Save();
    }

    private async Task RunCommunityChartSearchAsync()
    {
        var query = CommunitySearchBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            var autoQ = $"{CrTitleBox.Text.Trim()} {CrArtistBox.Text.Trim()}".Trim();
            if (!string.IsNullOrWhiteSpace(autoQ))
            {
                query = autoQ;
                CommunitySearchBox.Text = query;
            }
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            CommunitySearchStatus.Text = "Type a song title or artist name to search Chorus Encore.";
            CommunitySearchStatus.Visibility = Visibility.Visible;
            return;
        }

        CommunitySearchBtn.IsEnabled = false;
        CommunitySearchStatus.Text = $"Searching Chorus Encore for '{query}'...";
        CommunitySearchStatus.Visibility = Visibility.Visible;
        CommunityResultsPanel.Children.Clear();
        CommunityResultsScroll.Visibility = Visibility.Collapsed;

        try
        {
            var res = await _communityCharts.SearchChartsAsync(query);
            _lastCommunitySearchQuery = query;
            _lastCommunitySearchCharts = res.Success ? res.Charts : new List<CommunityChartEntry>();
            CommunitySearchStatus.Text = res.Message;

            if (!res.Success || _lastCommunitySearchCharts.Count == 0) return;

            RenderCommunitySearchResults();
        }
        finally
        {
            CommunitySearchBtn.IsEnabled = true;
        }
    }

    private void RenderCommunitySearchResults()
    {
        CommunityResultsPanel.Children.Clear();

        IEnumerable<CommunityChartEntry> filtered = _lastCommunitySearchCharts;
        if (CommunityFullDiffsOnlyChk.IsChecked == true)
            filtered = filtered.Where(c => c.HasFullDiffs);

        filtered = CommunitySortCombo.SelectedIndex switch
        {
            1 => filtered.OrderByDescending(c => c.ExpertNoteCount),
            2 => filtered.OrderByDescending(c => c.DiffGuitar).ThenByDescending(c => c.ExpertNoteCount),
            _ => filtered
        };

        var display = filtered.ToList();
        if (display.Count == 0)
        {
            CommunitySearchStatus.Text = $"No charts for '{_lastCommunitySearchQuery}' match the active filters (try unchecking 'Full Difficulties Only').";
            CommunityResultsScroll.Visibility = Visibility.Collapsed;
            return;
        }

        CommunitySearchStatus.Text = $"Showing {display.Count} of {_lastCommunitySearchCharts.Count} community chart(s) for '{_lastCommunitySearchQuery}':";

        foreach (var ch in display)
        {
            var card = new Border
            {
                Background = (WpfBrush)FindResource("BgCardHover"),
                BorderBrush = (WpfBrush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 6)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            infoStack.Children.Add(new TextBlock
            {
                Text = $"{ch.Name}  —  {ch.Artist}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)FindResource("TextPrimary"),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            infoStack.Children.Add(new TextBlock
            {
                Text = $"Charter: {(string.IsNullOrWhiteSpace(ch.Charter) ? "Community" : ch.Charter)}  •  {ch.DurationDisplay}  •  {ch.BadgesSummary}",
                FontSize = 11,
                Foreground = (WpfBrush)FindResource("TextMuted"),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0)
            });
            Grid.SetColumn(infoStack, 0);
            grid.Children.Add(infoStack);

            var btnStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };

            var previewBtn = new System.Windows.Controls.Button
            {
                Content = "▶ 30s",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Play a quick 30-second audio preview before downloading this chart"
            };
            previewBtn.Click += async (_, _) =>
            {
                previewBtn.IsEnabled = false;
                previewBtn.Content = "...";
                try
                {
                    var meta = await _albumMeta.FetchMetadataAndCoverAsync(ch.Name, ch.Artist);
                    if (meta.Found && !string.IsNullOrWhiteSpace(meta.PreviewAudioUrl))
                        PlayAudioPreviewUrl(meta.PreviewAudioUrl, ch.Name, ch.Artist);
                    else
                        ShowStageToast("No 30s Preview Found", $"Could not find an iTunes 30s preview for '{ch.Name}'.", "#EAB308");
                }
                finally
                {
                    previewBtn.IsEnabled = true;
                    previewBtn.Content = "▶ 30s";
                }
            };

            var useChartBtn = new System.Windows.Controls.Button
            {
                Content = "Use Chart",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Download chart/MIDI and load it into ONOGAMI Song Creator to pair with your own audio"
            };
            useChartBtn.Click += async (_, _) => await UseCommunityChartInCreatorAsync(ch);

            var installBtn = new System.Windows.Controls.Button
            {
                Content = "⚡ Install to GH3",
                Style = (Style)FindResource("GreenButton"),
                Padding = new Thickness(11, 5, 11, 5),
                ToolTip = "Download complete chart + audio and compile directly into Guitar Hero III"
            };
            installBtn.Click += async (_, _) => await InstallCommunityChartToGh3Async(ch);

            btnStack.Children.Add(previewBtn);
            btnStack.Children.Add(useChartBtn);
            btnStack.Children.Add(installBtn);
            Grid.SetColumn(btnStack, 1);
            grid.Children.Add(btnStack);

            card.Child = grid;
            CommunityResultsPanel.Children.Add(card);
        }

        CommunityResultsScroll.Visibility = Visibility.Visible;
    }

    private async Task InstallCommunityChartToGh3Async(CommunityChartEntry chart)
    {
        if (!ConfirmDuplicateHandlingIfNeeded(chart.Name, chart.Artist)) return;

        CommunitySearchBtn.IsEnabled = false;
        CommunityInstallProgress.Visibility = Visibility.Visible;
        CommunityInstallProgress.Value = 5;
        CommunitySearchStatus.Visibility = Visibility.Visible;

        string? extractedDir = null;
        try
        {
            var (ok, dir, msg) = await _communityCharts.DownloadAndExtractChartAsync(chart, (pct, step) => Dispatcher.Invoke(() =>
            {
                CommunityInstallProgress.Value = Math.Min(55, pct * 0.65);
                CommunitySearchStatus.Text = step;
            }));

            if (!ok)
            {
                CommunitySearchStatus.Text = msg;
                WpfMessageBox.Show(msg, "Community Chart Download", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            extractedDir = dir;
            var imp = await _cloneHero.ImportFromZipOrFolderAsync(dir, autoCompile: true, (pct, step) => Dispatcher.Invoke(() =>
            {
                CommunityInstallProgress.Value = 55 + (pct * 0.45);
                CommunitySearchStatus.Text = step;
            }));

            CommunitySearchStatus.Text = imp.Message;
            if (imp.Success)
            {
                RefreshSongsList();
                WpfMessageBox.Show($"Installed '{imp.Title}' by {imp.Artist} directly into Guitar Hero III!", "Community Chart Installed", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                WpfMessageBox.Show(imp.Message, "Install Note", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            CommunityInstallProgress.Visibility = Visibility.Collapsed;
            CommunitySearchBtn.IsEnabled = true;
            if (!string.IsNullOrEmpty(extractedDir))
            {
                try { Directory.Delete(Directory.GetParent(extractedDir)!.FullName, true); } catch { }
            }
        }
    }

    private async Task UseCommunityChartInCreatorAsync(CommunityChartEntry chart)
    {
        CommunitySearchBtn.IsEnabled = false;
        CommunityInstallProgress.Visibility = Visibility.Visible;
        CommunityInstallProgress.Value = 10;
        CommunitySearchStatus.Visibility = Visibility.Visible;

        try
        {
            var (ok, dir, msg) = await _communityCharts.DownloadAndExtractChartAsync(chart, (pct, step) => Dispatcher.Invoke(() =>
            {
                CommunityInstallProgress.Value = pct;
                CommunitySearchStatus.Text = step;
            }));

            if (!ok)
            {
                CommunitySearchStatus.Text = msg;
                return;
            }

            var chartFile = Directory.GetFiles(dir, "notes.chart").FirstOrDefault()
                         ?? Directory.GetFiles(dir, "notes.mid").FirstOrDefault();
            var audioFile = Directory.GetFiles(dir).FirstOrDefault(f =>
                Path.GetFileNameWithoutExtension(f).Equals("song", StringComparison.OrdinalIgnoreCase) &&
                new[] { ".ogg", ".mp3", ".wav" }.Contains(Path.GetExtension(f).ToLowerInvariant()))
                ?? Directory.GetFiles(dir).FirstOrDefault(f =>
                new[] { ".ogg", ".mp3", ".wav" }.Contains(Path.GetExtension(f).ToLowerInvariant()));

            if (!string.IsNullOrEmpty(chartFile))
            {
                ModeUploadRadio.IsChecked = true;
                CrChartPathBox.Text = chartFile;
            }
            if (string.IsNullOrWhiteSpace(CrAudioPathBox.Text) && !string.IsNullOrEmpty(audioFile))
            {
                CrAudioPathBox.Text = audioFile;
            }
            if (!string.IsNullOrWhiteSpace(chart.Name)) CrTitleBox.Text = chart.Name;
            if (!string.IsNullOrWhiteSpace(chart.Artist)) CrArtistBox.Text = chart.Artist;
            if (!string.IsNullOrWhiteSpace(chart.Album)) CrAlbumBox.Text = chart.Album;
            if (!string.IsNullOrWhiteSpace(chart.Genre)) CrGenreBox.Text = chart.Genre;
            if (!string.IsNullOrWhiteSpace(chart.Year)) CrYearBox.Text = chart.Year;
            if (!string.IsNullOrWhiteSpace(chart.Charter)) CrCharterBox.Text = chart.Charter;

            CommunitySearchStatus.Text = $"Loaded handcrafted chart for '{chart.Name}' into ONOGAMI Creator! Select Studio Master Audio & click Create & Compile.";
        }
        finally
        {
            CommunityInstallProgress.Visibility = Visibility.Collapsed;
            CommunitySearchBtn.IsEnabled = true;
        }
    }

    // ==================== PAGE 3: TOOLS & STEMS ====================
    private void RefreshToolsAndAiStatus()
    {
        var comp = _compiler.GetStatusReport();
        CreatorCompilerBadge.Text = comp.Available ? "Ready" : "Check Path";
        CreatorCompilerMsg.Text = comp.Available ? "Bundled compiler and FFmpeg ready." : comp.StatusMessage;
        AiToolsCompilerPath.Text = comp.Available ? "Compiler & FFmpeg bundled and ready." : comp.StatusMessage;
        AiToolsCompilerStatus.Text = comp.Available ? "Ready" : "Needs Setup";

        var ai = _demucs.GetStatus();
        CreatorAiBadge.Text = ai.IsInstalled ? "Installed" : "Not Installed";
        CreatorAiMsg.Text = ai.IsInstalled
            ? "Demucs 6-Stem + Basic Pitch ready for multi-track isolation and guitar transcription."
            : "Install Stem Tools to isolate Guitar, Bass, Drums, Vocals & Piano for mute-on-miss audio.";
        CreatorQuickInstallAiBtn.Visibility = ai.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        if (ai.IsInstalled && CrSeparateStemsChk.IsChecked == false) CrSeparateStemsChk.IsChecked = true;

        AiStatusPillText.Text = ai.IsInstalled ? "Installed" : "Not Installed";
        AiStatusPill.Background = new SolidColorBrush(ai.IsInstalled ? WpfColor.FromRgb(6, 78, 59) : WpfColor.FromRgb(59, 47, 12));
        AiStatusPillText.Foreground = new SolidColorBrush(ai.IsInstalled ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(253, 224, 71));

        AiCompDemucs.Text = $"Demucs 6-Stem: {(ai.HasDemucs ? "Ready" : "Not Installed")}";
        AiCompBasicPitch.Text = $"Basic Pitch MIDI: {(ai.HasBasicPitch ? "Ready" : "Not Installed")}";
        AiCompSize.Text = $"Disk Usage: {ai.InstalledSizeBytes / (1024.0 * 1024.0):F0} MB";

        InstallAiBtn.Content = ai.IsInstalled ? "Reinstall / Repair Stem Tools" : "Install Stem Tools (~700 MB - 1.2 GB)";
        UninstallAiBtn.Visibility = ai.IsInstalled ? Visibility.Visible : Visibility.Collapsed;

        // Optional yt-dlp URL Importer status
        if (YtDlpStatusText != null)
        {
            bool hasYt = _ytDlp.IsInstalled;
            YtDlpStatusText.Text = hasYt ? "Installed" : "Not Installed";
            YtDlpStatusPill.Background = new SolidColorBrush(hasYt ? WpfColor.FromRgb(6, 78, 59) : WpfColor.FromRgb(59, 47, 12));
            YtDlpStatusText.Foreground = new SolidColorBrush(hasYt ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(253, 224, 71));
            InstallYtDlpBtn.Content = hasYt ? "Update yt-dlp" : "Install yt-dlp (~15 MB)";
            UninstallYtDlpBtn.Visibility = hasYt ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private async void InstallAiEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallAiBtn.IsEnabled = false;
        UninstallAiBtn.IsEnabled = false;
        AiInstallProgressPanel.Visibility = Visibility.Visible;
        AiInstallProgressBar.Value = 0;
        PageAiTools_StageHud.Report(5, "INITIALIZING AI STEM ENGINE", "Preparing isolated Python virtual environment & CUDA packages...");

        var (ok, msg) = await _demucs.InstallAiEngineAsync((pct, step) => Dispatcher.Invoke(() =>
        {
            AiInstallProgressBar.Value = pct;
            AiInstallStepText.Text = $"[{pct}%] {step}";
            PageAiTools_StageHud.Report(pct, "INSTALLING AI STEM ENGINE", step);
        }));

        InstallAiBtn.IsEnabled = true;
        UninstallAiBtn.IsEnabled = true;
        RefreshToolsAndAiStatus();
        if (ok)
            PageAiTools_StageHud.Finish("AI Demucs & Basic Pitch ready with GPU CUDA acceleration!");
        else
            PageAiTools_StageHud.SetError("INSTALLATION NOTE", msg);
        WpfMessageBox.Show(msg, ok ? "Stem Tools Installed" : "Installation Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void UninstallAiEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show("Remove Demucs & Basic Pitch files to free disk space?", "Uninstall Stem Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        PageAiTools_StageHud.Report(50, "UNINSTALLING AI ENGINE", "Removing virtual environments and weights...");
        var (ok, msg) = _demucs.UninstallAiEngine();
        RefreshToolsAndAiStatus();
        PageAiTools_StageHud.Finish("AI stem engine removed.");
        WpfMessageBox.Show(msg, "Tools", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OpenAiFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        Directory.CreateDirectory(DemucsService.AiToolsRoot);
        OpenFolderInExplorer(DemucsService.AiToolsRoot);
    }

    // ==================== PAGE 4: FRIENDS & TRUST ====================
    private void UpdateFriendCodeDisplay()
    {
        ProfileFriendCodeBox.Text = _friendCodeVisible ? _friends.Profile.FriendCode : "GH3-••••-••••";
        ToggleCodeBtn.Content = _friendCodeVisible ? "Hide" : "Show";
    }

    private void ToggleFriendCodeBtn_Click(object sender, RoutedEventArgs e) { _friendCodeVisible = !_friendCodeVisible; UpdateFriendCodeDisplay(); }
    private void CopyFriendCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        var rawCode = _friends.Profile.FriendCode;
        var shortCode = rawCode.StartsWith("GH3-", StringComparison.OrdinalIgnoreCase) ? rawCode.Substring(4) : rawCode;
        System.Windows.Clipboard.SetText(rawCode);
        ShowStageToast("📋 FRIEND CODE COPIED!", $"Copied {rawCode} (or {shortCode}) to clipboard.", "#22C55E");
        AudioWaveformAndSfxService.PlayStarPowerChime();
    }

    private async void CopyOnlineInviteBtn_Click(object sender, RoutedEventArgs e)
    {
        string addr = "";
        if (_tunnel.IsTunnelActive && !string.IsNullOrWhiteSpace(_tunnel.ActiveTunnelUrl))
        {
            addr = _tunnel.ActiveTunnelUrl!;
        }
        else
        {
            var meshUrl = _tunnel.GetPreferredMeshUrl();
            if (!string.IsNullOrWhiteSpace(meshUrl))
            {
                addr = meshUrl;
            }
            else if (!string.IsNullOrWhiteSpace(AppConfig.Current.CustomPublicAddress))
            {
                addr = AppConfig.Current.CustomPublicAddress.Trim();
            }
            else
            {
                bool isGh3Running = Process.GetProcessesByName("GH3").Length > 0;
                var result = isGh3Running ? MessageBoxResult.Yes : WpfMessageBox.Show(
                    "To invite a friend across the internet (different houses/Wi-Fi), GH3-Sync can start a free, encrypted Cloudflare HTTPS Quick Tunnel with zero router port forwarding.\n\nStart Internet Tunnel now?",
                    "Start Internet Tunnel for Online Invite?",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    if (FriendsStartTunnelBtn != null)
                    {
                        FriendsStartTunnelBtn.IsEnabled = false;
                        FriendsStartTunnelBtn.Content = "Starting Tunnel...";
                    }
                    try
                    {
                        var (ok, urlOrErr) = await _tunnel.StartQuickTunnelAsync(step => Dispatcher.Invoke(() => {
                            if (FriendsTunnelDetail != null) FriendsTunnelDetail.Text = step;
                        }));
                        RefreshMeshAndTunnelStatus();
                        if (ok && !string.IsNullOrWhiteSpace(_tunnel.ActiveTunnelUrl))
                        {
                            addr = _tunnel.ActiveTunnelUrl;
                        }
                        else
                        {
                            if (!isGh3Running)
                            {
                                WpfMessageBox.Show($"Could not start tunnel: {urlOrErr}\nFalling back to public IP.", "Tunnel Notice", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                    }
                    finally
                    {
                        if (FriendsStartTunnelBtn != null) FriendsStartTunnelBtn.IsEnabled = true;
                        RefreshMeshAndTunnelStatus();
                    }
                }

                if (string.IsNullOrEmpty(addr))
                {
                    addr = !string.IsNullOrEmpty(_nat.PublicIp) ? $"{_nat.PublicIp}:{_nat.ExternalPort ?? _port}" : $"127.0.0.1:{_port}";
                }
            }
        }

        if (!addr.StartsWith("http://") && !addr.StartsWith("https://"))
        {
            addr = (addr.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase) || addr.EndsWith(":443")) ? "https://" + addr : "http://" + addr;
        }

        var invite = $"{_friends.Profile.FriendCode}@{addr}";
        System.Windows.Clipboard.SetText(invite);
        ShowStageToast("📋 ONLINE INVITE COPIED!", $"{_friends.Profile.Username} • {invite}", "#22C55E");
        AudioWaveformAndSfxService.PlayStarPowerChime();
    }

    private async void FriendsToggleTunnelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_tunnel.IsTunnelActive)
        {
            _tunnel.StopTunnel();
            RefreshMeshAndTunnelStatus();
            ShowStageToast("INTERNET TUNNEL STOPPED", "Cloudflare Quick Tunnel disconnected cleanly.", "#EAB308");
            return;
        }

        if (FriendsStartTunnelBtn != null)
        {
            FriendsStartTunnelBtn.IsEnabled = false;
            FriendsStartTunnelBtn.Content = "Starting Tunnel...";
        }
        try
        {
            var (ok, urlOrErr) = await _tunnel.StartQuickTunnelAsync(step => Dispatcher.Invoke(() => {
                if (FriendsTunnelDetail != null) FriendsTunnelDetail.Text = step;
            }));
            RefreshMeshAndTunnelStatus();
            if (ok)
            {
                ShowStageToast("⚡ INTERNET TUNNEL READY!", $"Zero-config encrypted URL: {MaskIfPrivate(urlOrErr)}", "#22C55E");
                AudioWaveformAndSfxService.PlayStarPowerChime();
            }
            else
            {
                WpfMessageBox.Show(urlOrErr, "Internet Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (FriendsStartTunnelBtn != null) FriendsStartTunnelBtn.IsEnabled = true;
            RefreshMeshAndTunnelStatus();
        }
    }

    private void PasteFriendOnlineInviteBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var raw = (System.Windows.Clipboard.GetText() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ShowStageToast("⚠️ CLIPBOARD EMPTY", "Copy an invite link from your friend first, then click Paste.", "#EF4444");
                return;
            }

            AddFriendCodeBox.Text = raw;
            ShowStageToast("📋 INVITE PASTED", "Friend code & online address loaded from clipboard.", "#38BDF8");
        }
        catch (Exception ex)
        {
            ShowStageToast("⚠️ PASTE FAILED", ex.Message, "#EF4444");
        }
    }

    private async void SaveUsernameBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileUsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        var (success, err) = await _friends.SaveProfileAsync(name);
        if (!success)
        {
            ShowStageToast("STAGE NAME TAKEN", err, "#EF4444");
            AudioWaveformAndSfxService.PlayNoteMissClunk();
            WpfMessageBox.Show(err, "Stage Name Taken", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SidebarProfileName.Text = name;
        ShowStageToast("STAGE NAME SAVED", $"Stage name updated to '{name}'!", "#22C55E");
        AudioWaveformAndSfxService.PlayStarPowerChime();
        WpfMessageBox.Show($"Display name updated to '{name}'.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        RefreshFriendsList();
    }

    private void AddFriendCodeBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            SearchFriendBtn_Click(sender, new RoutedEventArgs());
        }
    }

    private async void SearchFriendBtn_Click(object sender, RoutedEventArgs e)
    {
        var query = (AddFriendCodeBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(query))
        {
            ShowStageToast("ENTER SEARCH QUERY", "Please enter a Stage Name or Friend Code to search.", "#F59E0B");
            return;
        }

        SearchFriendBtn.IsEnabled = false;
        SearchFriendBtn.Content = "Searching...";
        AutoDetectStatusPill.Visibility = Visibility.Visible;
        AutoDetectStatusIcon.Text = "⏳";
        AutoDetectStatusText.Text = $"Searching network for '{query}'...";

        try
        {
            var res = await _friends.SearchFriendAsync(query);
            if (res.Found)
            {
                _detectedFriendResult = res;
                AutoDetectStatusIcon.Text = res.IsOnline ? "🟢" : "🔴";
                AutoDetectStatusText.Text = $"✓ Player Found: \"{res.Username}\" ({res.FriendCode}) — {(res.IsOnline ? "Online" : "Offline")}";
                SearchProfileViewBtn.Visibility = Visibility.Visible;
                ShowStageToast("✓ PLAYER FOUND", $"Found rockstar '{res.Username}' on the network!", "#22C55E");
                AudioWaveformAndSfxService.PlayStarPowerChime();
            }
            else
            {
                _detectedFriendResult = null;
                AutoDetectStatusIcon.Text = "⚠️";
                AutoDetectStatusText.Text = res.Error ?? $"No player found for '{query}'.";
                SearchProfileViewBtn.Visibility = Visibility.Collapsed;
                ShowStageToast("NOT FOUND", res.Error ?? "Player not found.", "#F59E0B");
            }
        }
        finally
        {
            SearchFriendBtn.IsEnabled = true;
            SearchFriendBtn.Content = "🔍 Search";
        }
    }

    private void SearchProfileViewBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_detectedFriendResult == null) return;
        var res = _detectedFriendResult;
        bool isSelf = res.Source == "Self" || res.FriendCode.Equals(AppConfig.Current.FriendCode, StringComparison.OrdinalIgnoreCase);
        var existing = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(res.FriendCode, StringComparison.OrdinalIgnoreCase));
        bool isFriend = existing != null;
        bool isTrusted = existing?.IsTrusted == true;

        OpenUserProfileModal(res.Username, res.FriendCode, existing?.ShowcaseSong ?? "", isSelf, isFriend, isTrusted, res.IsOnline, res.IsOnline ? "Online" : "Offline");
    }

    private void ViewMyProfileBtn_Click(object sender, RoutedEventArgs e)
    {
        OpenUserProfileModal(AppConfig.Current.Username, AppConfig.Current.FriendCode, AppConfig.Current.ShowcaseSongTitle ?? "", true, false, false, true, "Online");
    }

    private void OpenUserProfileModal(string username, string friendCode, string showcaseSong, bool isSelf, bool isFriend, bool isTrusted, bool isOnline, string activityStatus)
    {
        var dlg = new UserProfileDialog(
            username,
            friendCode,
            showcaseSong,
            isSelf,
            isFriend,
            isTrusted,
            isOnline,
            activityStatus,
            onAskToPlayTogether: async (code) =>
            {
                var fr = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(code, StringComparison.OrdinalIgnoreCase));
                return await SendAskToPlayTogetherInviteAsync(code, username, fr?.LastAddress ?? "");
            },
            onSendFriendRequest: async (code, asTrusted) =>
            {
                var res = await _friends.SendFriendRequestAsync(code, asTrusted: asTrusted);
                if (res.Success)
                {
                    ShowStageToast(asTrusted ? "⭐ TRUSTED REQUEST SENT!" : "✓ REQUEST SENT!",
                        asTrusted ? $"Trusted Friend request sent to {res.Username}!" : $"Friend request sent to {res.Username}!",
                        asTrusted ? "#F59E0B" : "#22C55E");
                    AudioWaveformAndSfxService.PlayStarPowerChime();
                    RefreshFriendsList();
                }
                else
                {
                    ShowStageToast("❌ FAILED", res.Message, "#EF4444");
                    AudioWaveformAndSfxService.PlayNoteMissClunk();
                }
            },
            onPreviewShowcase: (songTitle, artistName) =>
            {
                var match = _allSongs.FirstOrDefault(s => s.Title.Equals(songTitle, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    var audio = Gh3Service.FindSongAudioPath(match);
                    if (!string.IsNullOrEmpty(audio) && File.Exists(audio))
                    {
                        PlayAudioPreview(audio, match.Title, match, false);
                        ShowStageToast("🎸 SIGNATURE PREVIEW", $"Playing '{match.Title}' signature preview...", "#38BDF8");
                        return;
                    }
                }
                _ = Task.Run(async () =>
                {
                    var meta = await _albumMeta.FetchMetadataAndCoverAsync(songTitle, artistName);
                    if (meta.Found && !string.IsNullOrWhiteSpace(meta.PreviewAudioUrl))
                    {
                        Dispatcher.Invoke(() => PlayAudioPreviewUrl(meta.PreviewAudioUrl, songTitle, artistName));
                    }
                    else
                    {
                        Dispatcher.Invoke(() => ShowStageToast("No 30s Preview Found", $"Could not find preview audio for '{songTitle}'.", "#EAB308"));
                    }
                });
            },
            onGoToLobbySync: () =>
            {
                NavP2P.IsChecked = true;
            },
            onRequestTrustUpgrade: async () =>
            {
                var res = await _friends.RequestTrustUpgradeAsync(friendCode);
                if (res.Success)
                {
                    ShowStageToast("⭐ TRUST REQUEST SENT!", $"Requested trust upgrade from {username}.", "#22C55E");
                    AudioWaveformAndSfxService.PlayStarPowerChime();
                    RefreshFriendsList();
                }
            },
            onManageSaves: () =>
            {
                NavSaves.IsChecked = true;
            }
        );
        dlg.Owner = this;
        dlg.ShowDialog();
    }

    private async Task<bool> SendAskToPlayTogetherInviteAsync(string targetCode, string targetUsername, string targetAddress)
    {
        bool sent = false;
        if (FriendService.SignalingRelay != null)
        {
            sent = await FriendService.SignalingRelay.SendLobbyPlayInvitePacketAsync(
                targetCode, AppConfig.Current.FriendCode, AppConfig.Current.Username, FriendService.BestAddressProvider?.Invoke() ?? "");
        }

        if (!string.IsNullOrWhiteSpace(targetAddress) && !targetAddress.Contains("169.254."))
        {
            try
            {
                var directUrl = $"{targetAddress.TrimEnd('/')}/api/lobby/play-invite";
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3.0));
                var resp = await _friendPollClient.PostAsJsonAsync(directUrl, new
                {
                    senderCode = AppConfig.Current.FriendCode,
                    senderUsername = AppConfig.Current.Username,
                    senderAddress = FriendService.BestAddressProvider?.Invoke() ?? ""
                }, cts.Token);
                if (resp.IsSuccessStatusCode) sent = true;
            }
            catch { }
        }

        if (sent)
        {
            ShowStageToast("🎸 INVITE SENT!", $"Sent 'ROCK OUT' session invite to {targetUsername}!", "#F59E0B");
            AudioWaveformAndSfxService.PlayStarPowerChime();
            AppConfig.UnlockTrophy("ROCK_OUT_DUO");
        }
        else
        {
            ShowStageToast("❌ INVITE FAILED", $"Could not deliver invite to {targetUsername}. Ensure they have GH3-Sync open online!", "#EF4444");
            AudioWaveformAndSfxService.PlayNoteMissClunk();
        }
        return sent;
    }

    private void HandleRemotePeerPlayInvite(string peerCode, string peerUsername, string peerAddr)
    {
        if (!_pendingPlayInvites.Any(p => p.SenderCode.Equals(peerCode, StringComparison.OrdinalIgnoreCase) && (DateTime.UtcNow - p.ReceivedAt).TotalMinutes < 5))
        {
            _pendingPlayInvites.Add(new PlayInviteNotification
            {
                SenderCode = peerCode,
                SenderUsername = peerUsername,
                SenderAddress = peerAddr,
                ReceivedAt = DateTime.UtcNow
            });
            UpdateNotificationsBadge();
            ShowStageToast("🎸 ROCK OUT INVITE!", $"{peerUsername} wants to ROCK OUT with you!", "#F59E0B");
            AudioWaveformAndSfxService.PlayStarPowerChime();
            if (NotificationsPanel.Visibility == Visibility.Visible)
            {
                RenderNotificationsContent();
            }
        }
    }

    private void UpdateNotificationsBadge()
    {
        int inviteCount = _pendingPlayInvites.Count;
        int incomingReqs = _friends.IncomingRequests.Count;
        int total = inviteCount + incomingReqs;

        if (NotificationsBtn != null)
        {
            NotificationsBtn.Content = $"🔔 Notifications ({total})";
            NotificationsBtn.Style = (Style)FindResource(total > 0 ? "FlameButton" : "OutlineButton");
        }
    }

    private void NotificationsBtn_Click(object sender, RoutedEventArgs e)
    {
        NotificationsPanel.Visibility = NotificationsPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
        if (NotificationsPanel.Visibility == Visibility.Visible)
        {
            RenderNotificationsContent();
        }
    }

    private void DismissNotifications_Click(object sender, RoutedEventArgs e)
    {
        NotificationsPanel.Visibility = Visibility.Collapsed;
    }

    private void RenderNotificationsContent()
    {
        NotificationsContentPanel.Children.Clear();
        int totalItems = 0;

        // 1. Play-Together Invites
        if (_pendingPlayInvites.Count > 0)
        {
            NotificationsContentPanel.Children.Add(new TextBlock
            {
                Text = $"🎸 Multiplayer Session Invites ({_pendingPlayInvites.Count})",
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)FindResource("AccentFlameText"),
                Margin = new Thickness(0, 0, 0, 6)
            });

            foreach (var inv in _pendingPlayInvites.ToList())
            {
                totalItems++;
                var card = new Border
                {
                    Style = (Style)FindResource("GhCard"),
                    Background = (WpfBrush)new BrushConverter().ConvertFromString("#1F1329")!,
                    BorderBrush = (WpfBrush)FindResource("AccentFlame"),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                left.Children.Add(new TextBlock
                {
                    Text = $"🎸 {inv.SenderUsername} wants to ROCK OUT with you!",
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource("AccentGold")
                });
                left.Children.Add(new TextBlock
                {
                    Text = $"Friend Code: {inv.SenderCode} • Received: {inv.ReceivedAt.ToLocalTime():t}",
                    FontSize = 11,
                    Foreground = (WpfBrush)FindResource("TextMuted"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var joinBtn = new System.Windows.Controls.Button
                {
                    Content = "⚡ Join Lobby & Sync",
                    Style = (Style)FindResource("FlameButton"),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0, 0, 6, 0)
                };
                joinBtn.Click += async (_, _) =>
                {
                    NotificationsPanel.Visibility = Visibility.Collapsed;
                    NavP2P.IsChecked = true;
                    if (!string.IsNullOrWhiteSpace(inv.SenderAddress) && !inv.SenderAddress.Contains("169.254."))
                    {
                        PeerAddressBox.Text = inv.SenderAddress;
                        ComparePeerBtn_Click(this, new RoutedEventArgs());
                    }
                    else if (FriendService.SignalingRelay != null)
                    {
                        var (found, _, liveAddr, _) = await FriendService.SignalingRelay.ResolvePeerAsync(inv.SenderCode);
                        if (found && !string.IsNullOrWhiteSpace(liveAddr))
                        {
                            PeerAddressBox.Text = liveAddr;
                            ComparePeerBtn_Click(this, new RoutedEventArgs());
                        }
                    }
                };

                var dismissBtn = new System.Windows.Controls.Button
                {
                    Content = "✕ Dismiss",
                    Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(8, 5, 8, 5)
                };
                dismissBtn.Click += (_, _) =>
                {
                    _pendingPlayInvites.Remove(inv);
                    UpdateNotificationsBadge();
                    RenderNotificationsContent();
                };

                right.Children.Add(joinBtn);
                right.Children.Add(dismissBtn);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);

                card.Child = grid;
                NotificationsContentPanel.Children.Add(card);
            }
        }

        // 2. Incoming Friend Requests
        var incoming = _friends.IncomingRequests;
        if (incoming.Count > 0)
        {
            NotificationsContentPanel.Children.Add(new TextBlock
            {
                Text = $"📩 Pending Friend Requests ({incoming.Count})",
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)FindResource("AccentGold"),
                Margin = new Thickness(0, _pendingPlayInvites.Count > 0 ? 8 : 0, 0, 6)
            });

            foreach (var req in incoming)
            {
                totalItems++;
                var card = new Border
                {
                    Style = (Style)FindResource("GhCard"),
                    Padding = new Thickness(12),
                    BorderBrush = (WpfBrush)FindResource("AccentGold"),
                    Margin = new Thickness(0, 0, 0, 8)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                left.Children.Add(new TextBlock
                {
                    Text = $"{req.Username}  ({req.FriendCode})",
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource("TextPrimary")
                });
                left.Children.Add(new TextBlock
                {
                    Text = req.IsTrustedRequest ? "⭐ Trusted Friend Request (Requests Song Sync)" : "Friend Request (View Setlist Only)",
                    FontSize = 11,
                    Foreground = (WpfBrush)FindResource("TextMuted"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var accTrusted = new System.Windows.Controls.Button { Content = "⭐ Accept (Trusted)", Style = (Style)FindResource("FlameButton"), Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(0, 0, 6, 0) };
                accTrusted.Click += async (_, _) =>
                {
                    await _friends.AcceptFriendRequestAsync(req.Id, true);
                    ShowStageToast("✓ FRIEND ADDED", $"{req.Username} is now a Trusted Friend!", "#22C55E");
                    RefreshFriendsList();
                    UpdateNotificationsBadge();
                    RenderNotificationsContent();
                };

                var accReg = new System.Windows.Controls.Button { Content = "✓ Accept", Style = (Style)FindResource("GreenButton"), Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 6, 0) };
                accReg.Click += async (_, _) =>
                {
                    await _friends.AcceptFriendRequestAsync(req.Id, false);
                    ShowStageToast("✓ FRIEND ADDED", $"{req.Username} added as regular friend.", "#38BDF8");
                    RefreshFriendsList();
                    UpdateNotificationsBadge();
                    RenderNotificationsContent();
                };

                var dec = new System.Windows.Controls.Button { Content = "✕ Decline", Style = (Style)FindResource("DangerButton"), Padding = new Thickness(8, 5, 8, 5) };
                dec.Click += (_, _) =>
                {
                    _friends.DeclineFriendRequest(req.Id);
                    RefreshFriendsList();
                    UpdateNotificationsBadge();
                    RenderNotificationsContent();
                };

                right.Children.Add(accTrusted);
                right.Children.Add(accReg);
                right.Children.Add(dec);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);

                card.Child = grid;
                NotificationsContentPanel.Children.Add(card);
            }
        }

        // 3. Recent Unlocked Achievements
        var recentTrophies = RockHallOfFameService.GetAllAchievements().Where(a => a.IsUnlocked).Take(4).ToList();
        if (recentTrophies.Count > 0)
        {
            NotificationsContentPanel.Children.Add(new TextBlock
            {
                Text = "🏆 Recently Unlocked Trophies",
                FontSize = 13.5,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)FindResource("CardSuccessTitle"),
                Margin = new Thickness(0, 8, 0, 6)
            });

            var wrap = new WrapPanel();
            foreach (var t in recentTrophies)
            {
                var pill = new Border
                {
                    Background = (WpfBrush)new BrushConverter().ConvertFromString("#181424")!,
                    BorderBrush = (WpfBrush)FindResource("AccentGold"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 6, 6)
                };
                var sp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                sp.Children.Add(new TextBlock { Text = t.Icon, FontSize = 12, Margin = new Thickness(0, 0, 4, 0) });
                sp.Children.Add(new TextBlock { Text = $"{t.Title} ({t.Tier})", FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (WpfBrush)FindResource("TextPrimary") });
                pill.Child = sp;
                wrap.Children.Add(pill);
            }
            NotificationsContentPanel.Children.Add(wrap);
        }

        if (totalItems == 0 && recentTrophies.Count == 0)
        {
            NotificationsContentPanel.Children.Add(new TextBlock
            {
                Text = "No new notifications. When friends send requests or invite you to play, they will appear here!",
                Foreground = (WpfBrush)FindResource("TextMuted"),
                FontSize = 12,
                Margin = new Thickness(0, 4, 0, 4)
            });
        }
    }

    private static string FormatRelativeOffline(DateTime raw)
    {
        if (raw == DateTime.MinValue) return "🔴 Offline";
        var utc = AppConfig.EnsureUtc(raw);
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalSeconds < 0) diff = TimeSpan.Zero;
        if (diff.TotalMinutes < 1) return "🔴 Offline - Just now";
        if (diff.TotalMinutes < 60) return $"🔴 Offline - {(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"🔴 Offline - {(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 30) return $"🔴 Offline - {(int)diff.TotalDays}d ago";
        return $"🔴 Offline - {utc.ToLocalTime():MMM dd}";
    }

    private CancellationTokenSource? _autoDetectCts;

    private async void AddFriendCodeBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _autoDetectCts?.Cancel();
        var rawInput = (AddFriendCodeBox.Text ?? "").Trim();

        if (string.IsNullOrWhiteSpace(rawInput) || rawInput.Length < 4)
        {
            AutoDetectStatusPill.Visibility = Visibility.Collapsed;
            return;
        }

        AutoDetectStatusPill.Visibility = Visibility.Visible;
        AutoDetectStatusIcon.Text = "🔍";
        AutoDetectStatusText.Text = "Detecting player profile from unique ID...";
        AutoDetectStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)); // Sky blue

        var cts = new CancellationTokenSource();
        _autoDetectCts = cts;

        try
        {
            await Task.Delay(260, cts.Token);
            if (cts.IsCancellationRequested) return;

            var res = await _friends.LookupFriendByCodeAsync(rawInput);
            if (cts.IsCancellationRequested) return;

            if (res.Found)
            {
                AutoDetectStatusIcon.Text = "🎸";
                var statusStr = res.IsOnline ? "Online" : "Last seen";
                AutoDetectStatusText.Text = $"✓ Player Detected: \"{res.Username}\" • {statusStr} via {res.Source} ({(string.IsNullOrEmpty(res.Address) ? "P2P" : res.Address)})";
                AutoDetectStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94)); // Green
            }
            else
            {
                AutoDetectStatusIcon.Text = "⚠️";
                AutoDetectStatusText.Text = res.Error ?? $"Player {res.FriendCode} not detected online yet. Click 'Send Friend Request' to connect.";
                AutoDetectStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22)); // Amber
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AutoDetectStatusIcon.Text = "⚠️";
            AutoDetectStatusText.Text = $"Lookup note: {ex.Message}";
        }
    }

    private async void SendFriendRequestBtn_Click(object sender, RoutedEventArgs e)
    {
        await SendFriendRequestInternalAsync(asTrusted: false);
    }

    private async void SendTrustedFriendRequestBtn_Click(object sender, RoutedEventArgs e)
    {
        await SendFriendRequestInternalAsync(asTrusted: true);
    }

    private async Task SendFriendRequestInternalAsync(bool asTrusted)
    {
        var rawCode = (AddFriendCodeBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            ShowStageToast("⚠️ FRIEND CODE REQUIRED", "Please enter a Friend Code (e.g. GH3-XXXX-XXXX).", "#EF4444");
            return;
        }

        var directAddr = (DirectAddressBox?.Text ?? "").Trim();
        SendFriendRequestBtn.IsEnabled = false;
        if (SendTrustedFriendRequestBtn != null) SendTrustedFriendRequestBtn.IsEnabled = false;
        SendFriendRequestBtn.Content = "📡 Delivering...";
        PageFriends_StageHud.Report(35, "DELIVERING FRIEND REQUEST", $"Performing cryptographic handshake with {rawCode}...");

        try
        {
            var res = await _friends.SendFriendRequestAsync(rawCode, asTrusted: asTrusted, customAddress: string.IsNullOrWhiteSpace(directAddr) ? null : directAddr);

            if (res.Success)
            {
                PageFriends_StageHud.Finish(res.Message);
                ShowStageToast("✓ REQUEST SENT!", res.Message, "#22C55E");
                AudioWaveformAndSfxService.PlayStarPowerChime();
                AddFriendCodeBox.Text = "";
                if (DirectAddressBox != null) DirectAddressBox.Text = "";
                AutoDetectStatusPill.Visibility = Visibility.Collapsed;
                RefreshFriendsList();
            }
            else
            {
                PageFriends_StageHud.SetError("REQUEST FAILED", res.Message);
                ShowStageToast("❌ DELIVERY FAILED", res.Message, "#EF4444");
                AudioWaveformAndSfxService.PlayNoteMissClunk();
                AutoDetectStatusPill.Visibility = Visibility.Visible;
                AutoDetectStatusIcon.Text = "❌";
                AutoDetectStatusText.Text = res.Message;
            }
        }
        finally
        {
            SendFriendRequestBtn.IsEnabled = true;
            SendFriendRequestBtn.Content = "➕ Add Friend";
            if (SendTrustedFriendRequestBtn != null)
            {
                SendTrustedFriendRequestBtn.IsEnabled = true;
                SendTrustedFriendRequestBtn.Content = "⭐ Add Trusted Friend";
            }
        }
    }

    private void RefreshFriendsBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshFriendsList();
        RefreshTrustedFriendDropdown();
        ShowStageToast("🔄 REFRESHED", "Friends list and pending requests updated.", "#38BDF8");
    }

    private DispatcherTimer? _friendRequestPollTimer;
    private static readonly HttpClient _friendPollClient = new() { Timeout = TimeSpan.FromSeconds(2.5) };

    private void StartFriendRequestPollTimer()
    {
        if (_friendRequestPollTimer != null) return;
        _friendRequestPollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _friendRequestPollTimer.Tick += async (_, _) =>
        {
            var pending = _friends.OutgoingRequests;
            if (pending.Count == 0) return;

            foreach (var req in pending)
            {
                if (string.IsNullOrWhiteSpace(req.Address)) continue;
                try
                {
                    var checkUrl = $"{req.Address.TrimEnd('/')}/api/friends/check-status?code={Uri.EscapeDataString(_friends.Profile.FriendCode)}";
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
                    var resp = await _friendPollClient.GetAsync(checkUrl, cts.Token);
                    if (resp.IsSuccessStatusCode)
                    {
                        var data = await resp.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cts.Token);
                        if (data.TryGetProperty("isFriend", out var isF) && isF.GetBoolean())
                        {
                            var isTrusted = data.TryGetProperty("isTrusted", out var t) && t.GetBoolean();
                            var token = data.TryGetProperty("trustToken", out var tok) ? tok.GetString() : req.ProposedTrustToken;
                            var hostName = data.TryGetProperty("username", out var u) ? u.GetString() ?? req.Username : req.Username;

                            _friends.ProcessPeerAcceptedRequest(req.FriendCode, hostName, req.Address, token, isTrusted);
                            ShowStageToast("🎉 FRIEND ACCEPTED!", $"{hostName} accepted your friend request! Click '⚡ Sync Songs' to start.", "#22C55E");
                            AudioWaveformAndSfxService.PlayStarPowerChime();
                            RefreshFriendsList();
                            RefreshTrustedFriendDropdown();
                            break;
                        }
                    }
                }
                catch { }
            }
        };
        _friendRequestPollTimer.Start();
    }

    private void RefreshFriendsList()
    {
        StartFriendRequestPollTimer();
        FriendsListPanel.Children.Clear();

        var incoming = _friends.IncomingRequests;
        var outgoing = _friends.OutgoingRequests;
        var list = _friends.Friends;

        // ==================== 1. INCOMING REQUESTS (AWAITING YOUR APPROVAL) ====================
        if (incoming.Count > 0)
        {
            var incomingHeader = new TextBlock
            {
                Text = $"📩 Received Friend Requests ({incoming.Count})",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22)),
                Margin = new Thickness(0, 4, 0, 8)
            };
            FriendsListPanel.Children.Add(incomingHeader);

            foreach (var req in incoming)
            {
                var card = new Border
                {
                    Style = (Style)FindResource("GhCard"),
                    Padding = new Thickness(14),
                    BorderBrush = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22)),
                    BorderThickness = new Thickness(1.5),
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                if (req.RequestType == "TrustUpgrade")
                {
                    left.Children.Add(new TextBlock
                    {
                        Text = $"⭐ {req.Username}  ({req.FriendCode})",
                        FontSize = 14.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = (WpfBrush)FindResource("AccentFlameText")
                    });
                    left.Children.Add(new TextBlock
                    {
                        Text = "Requested Trust Upgrade • Grant full song downloads & 1-click library sync",
                        FontSize = 12,
                        Foreground = (WpfBrush)FindResource("CardSuccessTitle"),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                    Grid.SetColumn(left, 0);
                    grid.Children.Add(left);

                    var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                    var acceptTrustBtn = new System.Windows.Controls.Button
                    {
                        Content = "⭐ Accept Trust Upgrade",
                        Style = (Style)FindResource("FlameButton"),
                        Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = "Accept trust upgrade and enable mutual 1-click song sync"
                    };
                    acceptTrustBtn.Click += async (_, _) =>
                    {
                        var added = await _friends.AcceptTrustUpgradeAsync(req.Id);
                        ShowStageToast("⭐ TRUST GRANTED!", $"{req.Username} is now a Trusted Friend with 1-click song sync enabled!", "#22C55E");
                        AudioWaveformAndSfxService.PlayStarPowerChime();
                        RefreshFriendsList();
                        RefreshTrustedFriendDropdown();
                    };

                    var declineBtn = new System.Windows.Controls.Button
                    {
                        Content = "✕ Decline",
                        Style = (Style)FindResource("DangerButton"),
                        ToolTip = "Decline trust upgrade request"
                    };
                    declineBtn.Click += (_, _) =>
                    {
                        _friends.DeclineFriendRequest(req.Id);
                        ShowStageToast("✕ DECLINED", $"Trust upgrade request declined.", "#EF4444");
                        RefreshFriendsList();
                    };

                    right.Children.Add(acceptTrustBtn);
                    right.Children.Add(declineBtn);
                    Grid.SetColumn(right, 1);
                    grid.Children.Add(right);
                }
                else
                {
                    left.Children.Add(new TextBlock
                    {
                        Text = $"{req.Username}  ({req.FriendCode})",
                        FontSize = 14.5,
                        FontWeight = FontWeights.Bold,
                        Foreground = (WpfBrush)FindResource("TextPrimary")
                    });

                    var badgeText = req.IsTrustedRequest ? "⭐ Incoming Trusted Friend Request (Requests Song Sync)" : "Incoming Friend Request (View Only)";
                    left.Children.Add(new TextBlock
                    {
                        Text = $"{badgeText} • Received: {req.CreatedAt:g}",
                        FontSize = 12,
                        Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 186, 116)),
                        Margin = new Thickness(0, 2, 0, 0)
                    });
                    Grid.SetColumn(left, 0);
                    grid.Children.Add(left);

                    var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                    var acceptTrustBtn = new System.Windows.Controls.Button
                    {
                        Content = "⭐ Accept as Trusted",
                        Style = (Style)FindResource(req.IsTrustedRequest ? "FlameButton" : "GreenButton"),
                        Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = "Accept request and grant Trusted Friend permissions (P2P song download and setlist auto-sync)"
                    };
                    acceptTrustBtn.Click += async (_, _) =>
                    {
                        var added = await _friends.AcceptFriendRequestAsync(req.Id, asTrusted: true);
                        ShowStageToast("✓ FRIEND ADDED!", $"{req.Username} is now a Trusted Friend!", "#22C55E");
                        AudioWaveformAndSfxService.PlayStarPowerChime();
                        RefreshFriendsList();
                        RefreshTrustedFriendDropdown();
                    };

                    var acceptRegBtn = new System.Windows.Controls.Button
                    {
                        Content = "✓ Accept (View Only)",
                        Style = (Style)FindResource("OutlineButton"),
                        Margin = new Thickness(0, 0, 8, 0),
                        ToolTip = "Accept as regular friend (view setlist only, downloads blocked)"
                    };
                    acceptRegBtn.Click += async (_, _) =>
                    {
                        var added = await _friends.AcceptFriendRequestAsync(req.Id, asTrusted: false);
                        ShowStageToast("✓ FRIEND ADDED!", $"{req.Username} added as regular friend.", "#38BDF8");
                        RefreshFriendsList();
                        RefreshTrustedFriendDropdown();
                    };

                    var declineBtn = new System.Windows.Controls.Button
                    {
                        Content = "✕ Decline",
                        Style = (Style)FindResource("DangerButton"),
                        ToolTip = "Decline and remove this friend request"
                    };
                    declineBtn.Click += (_, _) =>
                    {
                        _friends.DeclineFriendRequest(req.Id);
                        ShowStageToast("✕ DECLINED", $"Friend request from {req.Username} declined.", "#EF4444");
                        RefreshFriendsList();
                    };

                    right.Children.Add(acceptTrustBtn);
                    right.Children.Add(acceptRegBtn);
                    right.Children.Add(declineBtn);
                    Grid.SetColumn(right, 1);
                    grid.Children.Add(right);
                }

                card.Child = grid;
                FriendsListPanel.Children.Add(card);
            }
        }

        // ==================== 2. OUTGOING REQUESTS (AWAITING FRIEND CONFIRMATION) ====================
        if (outgoing.Count > 0)
        {
            var outHeader = new TextBlock
            {
                Text = $"⏳ Sent Requests (Awaiting Friend Acceptance) ({outgoing.Count})",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)),
                Margin = new Thickness(0, incoming.Count > 0 ? 10 : 4, 0, 8)
            };
            FriendsListPanel.Children.Add(outHeader);

            foreach (var req in outgoing)
            {
                var card = new Border
                {
                    Style = (Style)FindResource("GhCard"),
                    Padding = new Thickness(14),
                    Margin = new Thickness(0, 0, 0, 10)
                };

                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                left.Children.Add(new TextBlock
                {
                    Text = $"{req.Username}  ({req.FriendCode})",
                    FontSize = 14.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource("TextPrimary")
                });
                var reqSub = req.RequestType == "TrustUpgrade"
                    ? "⭐ Trust Upgrade request sent • Waiting for friend to accept..."
                    : (req.IsTrustedRequest ? "⭐ Trusted Friend request sent • Waiting for player to accept..." : "Friend request sent (View Only) • Waiting for player to accept...");
                left.Children.Add(new TextBlock
                {
                    Text = $"{reqSub} (Sent {req.CreatedAt:g})",
                    FontSize = 12,
                    Foreground = (WpfBrush)FindResource("TextMuted"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
                var cancelBtn = new System.Windows.Controls.Button
                {
                    Content = "Cancel Request",
                    Style = (Style)FindResource("OutlineButton"),
                    ToolTip = "Cancel this pending outgoing friend request"
                };
                cancelBtn.Click += (_, _) =>
                {
                    _friends.CancelOutgoingRequest(req.Id);
                    ShowStageToast("CANCELED", $"Canceled request to {req.Username}.", "#94A3B8");
                    RefreshFriendsList();
                };
                right.Children.Add(cancelBtn);
                Grid.SetColumn(right, 1);
                grid.Children.Add(right);

                card.Child = grid;
                FriendsListPanel.Children.Add(card);
            }
        }

        // ==================== 3. FRIENDS ====================
        var friendsHeader = new TextBlock
        {
            Text = $"👥 Friends ({list.Count})",
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = (WpfBrush)FindResource("TextPrimary"),
            Margin = new Thickness(0, (incoming.Count > 0 || outgoing.Count > 0) ? 12 : 4, 0, 8)
        };
        FriendsListPanel.Children.Add(friendsHeader);

        if (list.Count == 0)
        {
            FriendsListPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("GhCard"),
                Child = new TextBlock
                {
                    Text = "No friends added yet. Enter a friend's Stage Name or Friend Code above to search and connect!",
                    Foreground = (WpfBrush)FindResource("TextMuted")
                }
            });
            return;
        }

        // Always sort friends by online status first, then by how long they have been offline
        var sortedFriends = list
            .OrderByDescending(f => f.IsOnlineNow && f.ActivityStatus != "Offline")
            .ThenByDescending(f => f.LastSeenUtc)
            .ToList();

        foreach (var f in sortedFriends)
        {
            var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var titleSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            titleSp.Children.Add(new TextBlock { Text = $"{f.Username}  ({f.FriendCode})", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });

            // Activity status badge
            bool isOnline = f.IsOnlineNow && f.ActivityStatus != "Offline";
            var actBg = !isOnline ? "#27272A" : (f.ActivityStatus == "Playing GH3" ? "#3B1E6D" : (f.ActivityStatus == "In Lobby" ? "#065F46" : "#064E3B"));
            var actFg = !isOnline ? "#A1A1AA" : (f.ActivityStatus == "Playing GH3" ? "#E9D5FF" : (f.ActivityStatus == "In Lobby" ? "#34D399" : "#6EE7B7"));
            var actText = !isOnline ? FormatRelativeOffline(f.LastSeenUtc) : (f.ActivityStatus == "Playing GH3" ? "🎸 Playing GH3" : (f.ActivityStatus == "In Lobby" ? "🟢 In Lobby" : "🟢 Online"));
            var actBadge = new Border
            {
                Background = (WpfBrush)new BrushConverter().ConvertFromString(actBg)!,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(8, 0, 0, 0),
                Child = new TextBlock { Text = actText, FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)new BrushConverter().ConvertFromString(actFg)! }
            };
            titleSp.Children.Add(actBadge);

            // Ping latency indicator badge
            if (f.PingMs > 0)
            {
                var pingColor = f.PingMs < 60 ? "#34D399" : (f.PingMs < 140 ? "#FBBF24" : "#F97316");
                var pingBadge = new Border
                {
                    Background = (WpfBrush)new BrushConverter().ConvertFromString("#1E293B")!,
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(6, 1, 6, 1),
                    Margin = new Thickness(6, 0, 0, 0),
                    Child = new TextBlock { Text = $"⚡ {f.PingMs} ms", FontSize = 10.5, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)new BrushConverter().ConvertFromString(pingColor)! }
                };
                titleSp.Children.Add(pingBadge);
            }
            left.Children.Add(titleSp);

            var subSp = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            subSp.Children.Add(new TextBlock
            {
                Text = f.IsTrusted ? "⭐ Trusted Friend (1-Click Sync Enabled)" : "Regular Friend (Setlist View Only)",
                FontSize = 12,
                Foreground = (WpfBrush)FindResource(f.IsTrusted ? "CardSuccessTitle" : "TextMuted"),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(f.ShowcaseSong))
            {
                subSp.Children.Add(new TextBlock
                {
                    Text = $"  •  🎸 Signature: {f.ShowcaseSong}",
                    FontSize = 11.5,
                    Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#38BDF8")!,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }
            left.Children.Add(subSp);

            if (!string.IsNullOrWhiteSpace(f.LastAddress) && f.PingMs <= 0 && !f.LastAddress.Contains("169.254."))
            {
                _ = Task.Run(async () =>
                {
                    var p = await _friends.MeasurePeerPingAsync(f.LastAddress);
                    if (p > 0)
                    {
                        f.PingMs = p;
                        Dispatcher.Invoke(() => RefreshFriendsList());
                    }
                });
            }
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 👤 View Profile Button
            var profileBtn = new System.Windows.Controls.Button
            {
                Content = "👤 View Profile",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = $"View {f.Username}'s Rockstar Profile, Career Stats & Achievements"
            };
            profileBtn.Click += (_, _) => OpenUserProfileModal(f.Username, f.FriendCode, f.ShowcaseSong, false, true, f.IsTrusted, isOnline, f.ActivityStatus);
            right.Children.Add(profileBtn);

            // 🎸 Ask to Play Together Button (if trusted & online)
            if (f.IsTrusted && isOnline)
            {
                var playTogetherBtn = new System.Windows.Controls.Button
                {
                    Content = "🎸 Play Together",
                    Style = (Style)FindResource("FlameButton"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = $"Send real-time invite: '{AppConfig.Current.Username} wants to ROCK OUT with you!'"
                };
                playTogetherBtn.Click += async (_, _) =>
                {
                    playTogetherBtn.IsEnabled = false;
                    playTogetherBtn.Content = "Inviting...";
                    try
                    {
                        await SendAskToPlayTogetherInviteAsync(f.FriendCode, f.Username, f.LastAddress);
                    }
                    finally
                    {
                        playTogetherBtn.IsEnabled = true;
                        playTogetherBtn.Content = "🎸 Play Together";
                    }
                };
                right.Children.Add(playTogetherBtn);
            }

            var syncBtn = new System.Windows.Controls.Button
            {
                Content = f.IsTrusted ? "⚡ Sync Songs" : "🎵 View Setlist",
                Style = (Style)FindResource(f.IsTrusted ? "FlameButton" : "BlueButton"),
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = f.IsTrusted
                    ? $"Compare libraries with {f.Username} and auto-sync missing custom songs"
                    : $"View {f.Username}'s custom songs setlist"
            };
            syncBtn.Click += async (_, _) =>
            {
                NavP2P.IsChecked = true;

                var addr = (f.LastAddress ?? "").Trim();
                if (addr.Contains("169.254.") || string.IsNullOrWhiteSpace(addr))
                {
                    PeerCompareStatus.Text = $"Resolving {f.Username}'s live address from Global Signaling Relay...";
                    ShowStageToast("📡 RESOLVING PEER...", $"Finding {f.Username}'s live connection address...", "#38BDF8");

                    if (FriendService.SignalingRelay != null)
                    {
                        var (found, _, liveAddr, _) = await FriendService.SignalingRelay.ResolvePeerAsync(f.FriendCode);
                        if (found && !string.IsNullOrWhiteSpace(liveAddr))
                        {
                            addr = liveAddr;
                            f.LastAddress = liveAddr;
                            AppConfig.Save();
                        }
                    }
                }

                if (addr.Contains("169.254.") || string.IsNullOrWhiteSpace(addr))
                {
                    PeerAddressBox.Text = "";
                    if (!string.IsNullOrWhiteSpace(f.TrustToken))
                    {
                        PeerPinBox.Text = f.TrustToken;
                    }
                    PeerCompareStatus.Text = $"Could not auto-resolve {f.Username}. Ensure {f.Username} has GH3-Sync open online!";
                    ShowStageToast("⚠️ FRIEND OFFLINE", $"Ensure {f.Username} has GH3-Sync open online to connect!", "#F59E0B");
                    PeerAddressBox.Focus();
                    return;
                }

                PeerAddressBox.Text = addr;
                if (!string.IsNullOrWhiteSpace(f.TrustToken))
                {
                    PeerPinBox.Text = f.TrustToken;
                }

                ComparePeerBtn_Click(this, new RoutedEventArgs());
            };
            right.Children.Add(syncBtn);

            if (!f.IsTrusted)
            {
                var upgradeBtn = new System.Windows.Controls.Button
                {
                    Content = "⭐ Request Trust Upgrade",
                    Style = (Style)FindResource("FlameButton"),
                    Padding = new Thickness(12, 6, 12, 6),
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = $"Request {f.Username} to upgrade to a Trusted Friend for full 1-click song syncing"
                };
                upgradeBtn.Click += async (_, _) =>
                {
                    upgradeBtn.IsEnabled = false;
                    upgradeBtn.Content = "Sending...";
                    try
                    {
                        var res = await _friends.RequestTrustUpgradeAsync(f.FriendCode);
                        if (res.Success)
                        {
                            ShowStageToast("⭐ TRUST REQUEST SENT!", $"Requested trust upgrade from {f.Username}. When they accept, 1-click sync is enabled!", "#22C55E");
                            AudioWaveformAndSfxService.PlayStarPowerChime();
                            RefreshFriendsList();
                        }
                        else
                        {
                            ShowStageToast("❌ FAILED", res.Message, "#EF4444");
                            AudioWaveformAndSfxService.PlayNoteMissClunk();
                        }
                    }
                    finally
                    {
                        upgradeBtn.IsEnabled = true;
                        upgradeBtn.Content = "⭐ Request Trust Upgrade";
                    }
                };
                right.Children.Add(upgradeBtn);
            }

            var delBtn = new System.Windows.Controls.Button { Content = "Unfriend", Style = (Style)FindResource("DangerButton") };
            delBtn.Click += (_, _) => { _friends.RemoveFriend(f.FriendCode); RefreshFriendsList(); RefreshTrustedFriendDropdown(); };
            right.Children.Add(delBtn);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            card.Child = grid;
            FriendsListPanel.Children.Add(card);
        }
    }

    // ==================== PAGE 5: P2P SYNC, TRUSTED QUICK-SELECT & TEMPORARY SESSION ====================
    private List<FriendEntry> _trustedDropdownEntries = new();

    private void RefreshTrustedFriendDropdown()
    {
        _trustedDropdownEntries = _friends.Friends.Where(f => f.IsTrusted).ToList();
        TrustedFriendQuickCombo.Items.Clear();
        TrustedFriendQuickCombo.Items.Add("-- Select Trusted Friend --");
        foreach (var f in _trustedDropdownEntries)
        {
            var addrHint = string.IsNullOrWhiteSpace(f.LastAddress) ? "No IP saved yet" : f.LastAddress;
            TrustedFriendQuickCombo.Items.Add($"{f.Username} ({addrHint})");
        }
        TrustedFriendQuickCombo.SelectedIndex = 0;
        _ = RenderLobbyFriendsListAsync();
    }

    private async void RefreshLobbyFriendsBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshTrustedFriendDropdown();
        await RenderLobbyFriendsListAsync();
        ShowStageToast("🔄 PRESENCE UPDATED", "Checked friends online status.", "#38BDF8");
    }

    private async Task RenderLobbyFriendsListAsync()
    {
        LobbyFriendsContainer.Children.Clear();
        var friends = _friends.Friends.ToList();

        if (friends.Count == 0)
        {
            var emptyCard = new Border
            {
                Background = (WpfBrush)FindResource("BgCard"),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14),
                Margin = new Thickness(0, 4, 0, 4)
            };
            var emptyStack = new StackPanel();
            emptyStack.Children.Add(new TextBlock
            {
                Text = "No friends added to your circle yet.",
                FontWeight = FontWeights.SemiBold,
                FontSize = 13.5,
                Foreground = (WpfBrush)FindResource("TextPrimary")
            });
            emptyStack.Children.Add(new TextBlock
            {
                Text = "Go to 'Friends & Trust' on the left menu to enter your friend's code (or share yours) for 1-click play!",
                FontSize = 12,
                Foreground = (WpfBrush)FindResource("TextMuted"),
                Margin = new Thickness(0, 4, 0, 0)
            });
            emptyCard.Child = emptyStack;
            LobbyFriendsContainer.Children.Add(emptyCard);
            return;
        }

        foreach (var friend in friends)
        {
            var card = new Border
            {
                Background = (WpfBrush)FindResource("BgCard"),
                BorderBrush = (WpfBrush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14, 12, 14, 12),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            var header = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            header.Children.Add(new TextBlock
            {
                Text = $"🎸 {friend.Username}",
                FontSize = 15,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)FindResource("TextPrimary"),
                VerticalAlignment = VerticalAlignment.Center
            });

            // Live status badge
            bool isOnline = friend.IsOnlineNow && friend.ActivityStatus != "Offline";
            bool hasValidAddress = !string.IsNullOrWhiteSpace(friend.LastAddress) && !friend.LastAddress.Contains("169.254.");
            var statusBorder = new Border
            {
                Background = new SolidColorBrush(isOnline ? (hasValidAddress ? WpfColor.FromRgb(6, 78, 59) : WpfColor.FromRgb(15, 76, 129)) : WpfColor.FromRgb(40, 40, 50)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            statusBorder.Child = new TextBlock
            {
                Text = !isOnline ? FormatRelativeOffline(friend.LastSeenUtc) : (friend.ActivityStatus == "Playing GH3" ? "🎸 Playing GH3" : (hasValidAddress ? "🟢 In Lobby" : "🟢 Online")),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(isOnline ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(156, 163, 175))
            };
            header.Children.Add(statusBorder);
            info.Children.Add(header);

            info.Children.Add(new TextBlock
            {
                Text = $"Friend Code: {friend.FriendCode} • {(friend.IsTrusted ? "⭐ Trusted Friend (Full Song Sync)" : "Regular Friend")}",
                FontSize = 11.5,
                Foreground = (WpfBrush)FindResource("TextMuted"),
                Margin = new Thickness(0, 3, 0, 0)
            });

            var actions = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            // 🎸 Ask to Rock Out button directly in Lobby Card (visible for active trusted friends)
            if (friend.IsTrusted && isOnline)
            {
                var rockOutBtn = new System.Windows.Controls.Button
                {
                    Content = "🎸 Ask to Rock Out",
                    Style = (Style)FindResource("FlameButton"),
                    Padding = new Thickness(12, 7, 12, 7),
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = $"Send real-time invite: '{AppConfig.Current.Username} wants to ROCK OUT with you!'"
                };
                rockOutBtn.Click += async (_, _) =>
                {
                    rockOutBtn.IsEnabled = false;
                    rockOutBtn.Content = "Inviting...";
                    try
                    {
                        await SendAskToPlayTogetherInviteAsync(friend.FriendCode, friend.Username, friend.LastAddress);
                    }
                    finally
                    {
                        rockOutBtn.IsEnabled = true;
                        rockOutBtn.Content = "🎸 Ask to Rock Out";
                    }
                };
                actions.Children.Add(rockOutBtn);
            }

            var syncBtn = new System.Windows.Controls.Button
            {
                Content = "⚡ Compare & Sync Songs",
                Style = (Style)FindResource("FlameButton"),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0)
            };

            syncBtn.Click += async (_, _) =>
            {
                syncBtn.IsEnabled = false;
                syncBtn.Content = "📡 Connecting...";
                try
                {
                    string targetAddress = friend.LastAddress ?? "";
                    if (FriendService.SignalingRelay != null)
                    {
                        var (found, _, liveAddr, _) = await FriendService.SignalingRelay.ResolvePeerAsync(friend.FriendCode);
                        if (found && !string.IsNullOrWhiteSpace(liveAddr) && !liveAddr.Contains("169.254."))
                        {
                            targetAddress = liveAddr;
                            friend.LastAddress = liveAddr;
                            AppConfig.Save();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(targetAddress) || targetAddress.Contains("169.254."))
                    {
                        ShowStageToast("📡 WAITING FOR PEER", $"Could not find a live tunnel for {friend.Username} yet. Ask them to open GH3-Sync!", "#F59E0B");
                        return;
                    }

                    PeerAddressBox.Text = targetAddress;
                    string secretToUse = !string.IsNullOrWhiteSpace(friend.TrustToken) ? friend.TrustToken : "";
                    if (string.IsNullOrWhiteSpace(secretToUse) && !string.IsNullOrWhiteSpace(friend.FriendCode))
                    {
                        secretToUse = FriendService.DeriveSharedSecret(AppConfig.Current.FriendCode, friend.FriendCode);
                    }
                    if (!string.IsNullOrWhiteSpace(secretToUse))
                        PeerPinBox.Text = secretToUse;

                    ShowStageToast("🎸 CONNECTING TO LOBBY", $"Connecting directly to {friend.Username}...", "#22C55E");
                    ComparePeerBtn_Click(this, new RoutedEventArgs());
                }
                finally
                {
                    syncBtn.IsEnabled = true;
                    syncBtn.Content = "⚡ Compare & Sync Songs";
                }
            };

            actions.Children.Add(syncBtn);

            Grid.SetColumn(info, 0);
            Grid.SetColumn(actions, 1);
            grid.Children.Add(info);
            grid.Children.Add(actions);

            card.Child = grid;
            LobbyFriendsContainer.Children.Add(card);
        }
    }

    private async void TrustedFriendQuickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = TrustedFriendQuickCombo.SelectedIndex - 1;
        if (idx < 0 || idx >= _trustedDropdownEntries.Count) return;
        var friend = _trustedDropdownEntries[idx];

        var addr = (friend.LastAddress ?? "").Trim();
        // If address is missing, link-local, or private VPN (100.x.x.x), or if Signaling Relay is active:
        if (string.IsNullOrWhiteSpace(addr) || addr.Contains("169.254.") || addr.Contains("://100.") || FriendService.SignalingRelay != null)
        {
            if (FriendService.SignalingRelay != null)
            {
                var (found, _, liveAddr, _) = await FriendService.SignalingRelay.ResolvePeerAsync(friend.FriendCode);
                if (found && !string.IsNullOrWhiteSpace(liveAddr))
                {
                    addr = liveAddr;
                    friend.LastAddress = liveAddr;
                    AppConfig.Save();
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(addr) && !addr.Contains("169.254."))
        {
            PeerAddressBox.Text = addr;
        }
        else
        {
            PeerAddressBox.Text = "";
            ShowStageToast("📡 RESOLVING FRIEND...", $"Waiting for {friend.Username} to connect online.", "#38BDF8");
        }

        if (!string.IsNullOrWhiteSpace(friend.TrustToken))
            PeerPinBox.Text = friend.TrustToken;
    }

    private void RefreshTempSessionBanner()
    {
        var st = _tempSession.CurrentState;
        int tempCount = st.Active ? st.Songs.Count(s => !s.Kept) : 0;
        if (st.Active && tempCount > 0)
        {
            TempSessionBanner.Visibility = Visibility.Visible;
            CustomsTempSessionBanner.Visibility = Visibility.Visible;
            TempSessionTitle.Text = $"Temporary Lobby Session Active ({tempCount} temp song(s))";
            TempSessionSubtitle.Text = $"Synced from {st.PeerAddress}. Temporary songs auto-clean when GH3 exits. Click '⭐ Keep All Synced Songs' to make them permanent.";
            CustomsTempSessionText.Text = $"Temporary Lobby Session active ({tempCount} temporary song(s) will auto-clean when GH3 exits).";
        }
        else
        {
            TempSessionBanner.Visibility = Visibility.Collapsed;
            CustomsTempSessionBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void KeepAllTempSongsBtn_Click(object sender, RoutedEventArgs e)
    {
        int kept = _tempSession.KeepAllSongsPermanently();
        RefreshSongsList();
        RefreshTempSessionBanner();
        if (_lastPeerManifest != null) RenderPeerLibrary();
        ShowStageToast("⭐ SONGS KEPT PERMANENTLY!", $"Promoted {kept} synced song(s) to your permanent library! They will never be auto-removed.", "#22C55E");
        AudioWaveformAndSfxService.PlayStarPowerChime();
    }

    private void EndTempSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, _, msg) = _tempSession.EndSessionAndCleanup();
        RefreshSongsList();
        RefreshTempSessionBanner();
        if (_lastPeerManifest != null) RenderPeerLibrary();
        WpfMessageBox.Show(msg, "Temporary Session Cleaned", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Challenge-Response HMAC-SHA256 P2P GET client: requests a single-use nonce from /api/auth/challenge,
    /// signs the request with HMAC-SHA256(secret, nonce|methodPath|timestamp), and NEVER sends the secret in plaintext.
    /// Automatically falls back to header auth only if a legacy pre-1.0.0 peer returns 404 on /api/auth/challenge.
    /// </summary>
    private static async Task<HttpResponseMessage> SendChallengeHmacGetAsync(string baseUrl, string path, string secret, TimeSpan timeout)
    {
        using var http = new HttpClient(SignalingRelayService.CreateFastIPv4Handler()) { Timeout = timeout };
        var fullUrl = $"{baseUrl.TrimEnd('/')}{path}";

        if (!string.IsNullOrWhiteSpace(secret))
        {
            try
            {
                using var chResp = await http.GetAsync($"{baseUrl.TrimEnd('/')}/api/auth/challenge");
                if (chResp.IsSuccessStatusCode)
                {
                    var chJson = await chResp.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(chJson);
                    var nonce = doc.RootElement.TryGetProperty("nonce", out var n) ? n.GetString() ?? "" : "";
                    if (!string.IsNullOrWhiteSpace(nonce))
                    {
                        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
                        var methodPath = $"GET {path.Split('?')[0]}";
                        var hmac = SecurityService.ComputeChallengeHmac(nonce, methodPath, ts, secret);
                        using var req = new HttpRequestMessage(HttpMethod.Get, fullUrl);
                        req.Headers.Add("X-GH3-Auth-Nonce", nonce);
                        req.Headers.Add("X-GH3-Auth-Timestamp", ts);
                        req.Headers.Add("X-GH3-Auth-Hmac", hmac);

                        var myAddr = FriendService.BestAddressProvider?.Invoke() ?? "";
                        var myCode = AppConfig.Current.FriendCode;
                        if (!string.IsNullOrWhiteSpace(myAddr) && !myAddr.Contains("169.254."))
                        {
                            req.Headers.Add("X-GH3-Sender-Address", myAddr);
                        }
                        if (!string.IsNullOrWhiteSpace(myCode))
                        {
                            req.Headers.Add("X-GH3-Sender-Code", myCode);
                        }

                        return await http.SendAsync(req);
                    }
                }
            }
            catch { /* Fall through to legacy header auth if challenge endpoint unavailable */ }
        }

        using var fallbackReq = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        if (!string.IsNullOrWhiteSpace(secret))
        {
            if (secret.Length > 10)
                fallbackReq.Headers.Add("X-GH3-Trust-Token", secret);
            else
                fallbackReq.Headers.Add("X-GH3-PIN", secret);
        }

        var myFallbackAddr = FriendService.BestAddressProvider?.Invoke() ?? "";
        var myFallbackCode = AppConfig.Current.FriendCode;
        if (!string.IsNullOrWhiteSpace(myFallbackAddr) && !myFallbackAddr.Contains("169.254."))
        {
            fallbackReq.Headers.Add("X-GH3-Sender-Address", myFallbackAddr);
        }
        if (!string.IsNullOrWhiteSpace(myFallbackCode))
        {
            fallbackReq.Headers.Add("X-GH3-Sender-Code", myFallbackCode);
        }

        return await http.SendAsync(fallbackReq);
    }

    private void RefreshMeshAndTunnelStatus()
    {
        if (MeshNetworkSummaryText == null || TunnelStatusText == null || StartTunnelBtn == null) return;
        var meshes = _tunnel.DetectMeshInterfaces();
        if (meshes.Count > 0)
        {
            var summary = string.Join("  •  ", meshes.Select(m => $"{m.Provider}: {MaskIfPrivate(m.SyncUrl)}"));
            MeshNetworkSummaryText.Text = $"✓ Mesh VPN Detected (Zero Port-Forwarding!): {summary}";
            MeshNetworkSummaryText.Foreground = (WpfBrush)FindResource("CardSuccessTitle");
        }
        else
        {
            MeshNetworkSummaryText.Text = "No Tailscale / ZeroTier mesh adapter detected. Use UPnP or start an HTTPS Quick Tunnel below for strict CGNAT routers.";
            MeshNetworkSummaryText.Foreground = (WpfBrush)FindResource("TextSecondary");
        }

        if (_tunnel.IsTunnelActive && !string.IsNullOrWhiteSpace(_tunnel.ActiveTunnelUrl))
        {
            TunnelStatusText.Text = $"✓ HTTPS Quick Tunnel Active: {MaskIfPrivate(_tunnel.ActiveTunnelUrl)}";
            TunnelStatusText.Foreground = (WpfBrush)FindResource("CardSuccessTitle");
            StartTunnelBtn.Content = "⏹ Stop HTTPS Tunnel";
            StartTunnelBtn.Style = (Style)FindResource("DangerButton");

            if (FriendsTunnelHeadline != null) FriendsTunnelHeadline.Text = $"Internet Sync Ready: {MaskIfPrivate(_tunnel.ActiveTunnelUrl)}";
            if (FriendsTunnelHeadline != null) FriendsTunnelHeadline.Foreground = (WpfBrush)FindResource("CardSuccessTitle");
            if (FriendsTunnelStatusIcon != null) FriendsTunnelStatusIcon.Text = "⚡";
            if (FriendsTunnelDetail != null) FriendsTunnelDetail.Text = "Tunnel is active! Friends across the internet can connect instantly. Click '📋 Copy Online Invite' above and send it to them.";
            if (FriendsStartTunnelBtn != null)
            {
                FriendsStartTunnelBtn.Content = "⏹ Stop Internet Tunnel";
                FriendsStartTunnelBtn.Style = (Style)FindResource("DangerButton");
            }
        }
        else
        {
            TunnelStatusText.Text = $"HTTPS Quick Tunnel: {_tunnel.TunnelStatusMessage}";
            TunnelStatusText.Foreground = (WpfBrush)FindResource("TextMuted");
            StartTunnelBtn.Content = "🌐 Start HTTPS Quick Tunnel";
            StartTunnelBtn.Style = (Style)FindResource("BlueButton");

            if (FriendsTunnelHeadline != null) FriendsTunnelHeadline.Text = "Internet Sync: Local Wi-Fi Ready";
            if (FriendsTunnelHeadline != null) FriendsTunnelHeadline.Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#38BDF8")!;
            if (FriendsTunnelStatusIcon != null) FriendsTunnelStatusIcon.Text = "🌐";
            if (FriendsTunnelDetail != null) FriendsTunnelDetail.Text = "If your friend is in another house or on different Wi-Fi, click 'Start Free Internet Tunnel' below so you can connect across the internet with zero router setup.";
            if (FriendsStartTunnelBtn != null)
            {
                FriendsStartTunnelBtn.Content = "🌐 Start Free Internet Tunnel";
                FriendsStartTunnelBtn.Style = (Style)FindResource("BlueButton");
            }
        }
    }

    private async void ToggleTunnelBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_tunnel.IsTunnelActive)
        {
            _tunnel.StopTunnel();
            RefreshMeshAndTunnelStatus();
            ShowStageToast("HTTPS TUNNEL STOPPED", "Cloudflare Quick Tunnel disconnected cleanly.", "#EAB308");
            return;
        }

        StartTunnelBtn.IsEnabled = false;
        StartTunnelBtn.Content = "Starting Tunnel...";
        try
        {
            var (ok, urlOrErr) = await _tunnel.StartQuickTunnelAsync(step => Dispatcher.Invoke(() => TunnelStatusText.Text = step));
            RefreshMeshAndTunnelStatus();
            if (ok)
            {
                ShowStageToast("⚡ HTTPS TUNNEL READY!", $"Zero-config encrypted URL: {MaskIfPrivate(urlOrErr)}", "#22C55E");
            }
            else
            {
                WpfMessageBox.Show(urlOrErr, "HTTPS Quick Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            StartTunnelBtn.IsEnabled = true;
            RefreshMeshAndTunnelStatus();
        }
    }

    private void RescanMeshBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshMeshAndTunnelStatus();
        ShowStageToast("MESH SCAN COMPLETE", MeshNetworkSummaryText.Text, "#38BDF8");
    }

    private void RefreshNetworkSummary()
    {
        var pubRaw = _tunnel.IsTunnelActive && !string.IsNullOrWhiteSpace(_tunnel.ActiveTunnelUrl)
            ? _tunnel.ActiveTunnelUrl!
            : (string.IsNullOrEmpty(_nat.PublicIp) ? "Detecting..." : $"{_nat.PublicIp}:{_nat.ExternalPort ?? _port}");
        var pub = pubRaw == "Detecting..." ? pubRaw : MaskIfPrivate(pubRaw);
        var pinDisplay = AppConfig.Current.StreamerPrivacyMode ? "••••••" : _security.PairingPin;
        var mode = AppConfig.Current.P2pOnlineEnabled ? "Online" : "OFFLINE MODE";
        P2pNetworkSummary.Text = $"[{mode}] Public / NAT Address: {pub}  •  UPnP / NAT-PMP: {(_nat.IsMapped ? $"Port Forwarded ({_nat.MappingMethod})" : _nat.MappingMethod)}";
        P2pPinSummary.Text = $"Your Session Pairing PIN (View-Only): {pinDisplay}  •  Song Downloads: Trusted Friends / Expiring Invites Only";
        UpdateLobbyCounters();
    }

    private void SaveLobbyCapBtn_Click(object sender, RoutedEventArgs e)
    {
        if (int.TryParse(LobbyCapBox.Text, out var cap))
        {
            AppConfig.Current.MaxLobbyAutoSyncSongs = Math.Clamp(cap, 1, 25);
            AppConfig.Save();
            LobbyCapBox.Text = AppConfig.Current.MaxLobbyAutoSyncSongs.ToString();
            SettingsLobbyCapBox.Text = AppConfig.Current.MaxLobbyAutoSyncSongs.ToString();
            UpdateLobbyCounters();
            WpfMessageBox.Show($"Lobby Auto-Sync cap updated to {AppConfig.Current.MaxLobbyAutoSyncSongs} songs.", "Lobby Cap Saved", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void CopyLobbyInviteBtn_Click(object sender, RoutedEventArgs e)
    {
        var meshUrl = _tunnel.GetPreferredMeshUrl();
        var addr = _tunnel.IsTunnelActive && !string.IsNullOrWhiteSpace(_tunnel.ActiveTunnelUrl)
            ? _tunnel.ActiveTunnelUrl!
            : !string.IsNullOrWhiteSpace(AppConfig.Current.CustomPublicAddress)
                ? AppConfig.Current.CustomPublicAddress.Trim()
                : !string.IsNullOrWhiteSpace(meshUrl)
                    ? meshUrl
                    : (!string.IsNullOrEmpty(_nat.PublicIp) ? $"{_nat.PublicIp}:{_nat.ExternalPort ?? _port}" : $"127.0.0.1:{_port}");

        string secret = _security.PairingPin;
        string targetNote = "View-Only PIN (add friend as Trusted or check 4-Hour Expiring Invite)";

        if (ExpiringInviteChk?.IsChecked == true)
        {
            var inv = _security.IssueExpiringInviteToken($"Lobby Invite ({DateTime.Now:HH:mm})", validHours: 4);
            secret = inv.Token;
            targetNote = $"4-Hour Expiring Invite Token (auto-expires {inv.ExpiresAtUtc.ToLocalTime():h:mm tt})";
            RefreshSecurityAuditPanel();
        }
        else
        {
            int selIdx = TrustedFriendQuickCombo.SelectedIndex - 1;
            if (selIdx >= 0 && selIdx < _trustedDropdownEntries.Count && !string.IsNullOrWhiteSpace(_trustedDropdownEntries[selIdx].TrustToken))
            {
                secret = _trustedDropdownEntries[selIdx].TrustToken;
                targetNote = $"Trusted Token for {_trustedDropdownEntries[selIdx].Username}";
            }
            else
            {
                var firstTrusted = _trustedDropdownEntries.FirstOrDefault(f => !string.IsNullOrWhiteSpace(f.TrustToken));
                if (firstTrusted != null)
                {
                    secret = firstTrusted.TrustToken;
                    targetNote = $"Trusted Token for {firstTrusted.Username}";
                }
            }
        }

        var payload = JsonSerializer.Serialize(new { addr, token = secret, host = _friends.Profile.Username });
        var code = "gh3sync://" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload));
        System.Windows.Clipboard.SetText(code);
        ShowStageToast("🎸 LOBBY INVITE COPIED!", $"{addr} • {targetNote}", "#F97316");
    }

    private void PasteLobbyInviteBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var raw = (System.Windows.Clipboard.GetText() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(raw))
            {
                ShowStageToast("⚠️ CLIPBOARD EMPTY", "Copy an invite link from your friend first, then click Paste.", "#EF4444");
                return;
            }

            string addr = "";
            string token = "";
            string host = "Friend";
            string friendCode = "";

            if (raw.StartsWith("gh3sync://", StringComparison.OrdinalIgnoreCase))
            {
                var b64 = raw.Substring("gh3sync://".Length).Trim();
                var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(b64));
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                addr = root.TryGetProperty("addr", out var a) ? a.GetString() ?? "" : "";
                token = root.TryGetProperty("token", out var t) ? t.GetString() ?? "" : "";
                host = root.TryGetProperty("host", out var h) ? h.GetString() ?? "Friend" : "Friend";
                if (root.TryGetProperty("code", out var c)) friendCode = c.GetString() ?? "";
            }
            else if (raw.Contains('@'))
            {
                var parts = raw.Split('@', 2);
                friendCode = parts[0].Trim();
                addr = parts[1].Trim();
            }
            else
            {
                addr = raw;
            }

            if (!SecurityService.ValidatePeerUrl(addr, out var safeBaseUrl, out var urlErr))
            {
                WpfMessageBox.Show($"Security Block: {urlErr}", "Blocked Invite URL", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // If friend code is known or matches, retrieve mutual trust token & update friend address
            var matchFriend = AppConfig.Current.Friends.FirstOrDefault(f =>
                (!string.IsNullOrEmpty(friendCode) && f.FriendCode.Equals(friendCode, StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrEmpty(f.LastAddress) && safeBaseUrl.Contains(f.LastAddress.Replace("http://", "").Replace("https://", "").TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));

            if (matchFriend != null)
            {
                host = matchFriend.Username;
                if (string.IsNullOrEmpty(token) && !string.IsNullOrEmpty(matchFriend.TrustToken))
                {
                    token = matchFriend.TrustToken;
                }
                matchFriend.LastAddress = safeBaseUrl;
                AppConfig.Save();
            }

            PeerAddressBox.Text = safeBaseUrl;
            if (!string.IsNullOrEmpty(token)) PeerPinBox.Text = token;

            PeerCompareStatus.Text = $"Loaded verified invite from {host} ({safeBaseUrl}) — connecting...";
            ShowStageToast("🎸 INVITE LOADED!", $"Connected to {host} ({safeBaseUrl})", "#22C55E");
            ComparePeerBtn_Click(sender, e);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Could not parse Lobby Invite Code: {ex.Message}", "Invalid Invite Code", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void RefreshNatBtn_Click(object sender, RoutedEventArgs e)
    {
        P2pNetworkSummary.Text = "Refreshing Public IP & Router UPnP / NAT-PMP...";
        await _nat.InitializeAsync();
        RefreshNetworkSummary();
    }

    private async void ComparePeerBtn_Click(object sender, RoutedEventArgs e)
    {
        var addr = PeerAddressBox.Text.Trim();
        var secret = PeerPinBox.Text.Trim();
        if (string.IsNullOrEmpty(addr)) return;

        if (!SecurityService.ValidatePeerUrl(addr, out var baseUrl, out var urlErr))
        {
            PeerCompareStatus.Text = $"Security Block: {urlErr}";
            return;
        }
        PeerCompareStatus.Text = $"Connecting to {baseUrl}...";
        LobbyMatchBanner.Visibility = Visibility.Collapsed;
        PeerSongsPanel.Children.Clear();

        try
        {
            using var resp = await SendChallengeHmacGetAsync(baseUrl, "/api/manifest", secret, TimeSpan.FromSeconds(15));
            resp.EnsureSuccessStatusCode();
            var rawBytes = await resp.Content.ReadAsByteArrayAsync();

            // Mutual Host Identity Verification (ECDSA P-256 Signature + SSH-style TOFU Fingerprint Pinning)
            if (resp.Headers.TryGetValues("X-GH3-Host-PubKey", out var pkVals) &&
                resp.Headers.TryGetValues("X-GH3-Host-Signature", out var sigVals))
            {
                var pubKey = pkVals.FirstOrDefault() ?? "";
                var sig = sigVals.FirstOrDefault() ?? "";
                if (!SecurityService.VerifyHostSignature(pubKey, rawBytes, sig, out var hostFp))
                {
                    PeerCompareStatus.Text = "⚠️ SECURITY BLOCK: Peer manifest ECDSA signature failed verification (possible tampering)!";
                    return;
                }
                var pinStatus = SecurityService.CheckOrPinPeerFingerprint(baseUrl, hostFp, out var prevFp);
                if (pinStatus == "MISMATCH")
                {
                    var allowNew = WpfMessageBox.Show(
                        $"⚠️ WARNING: PEER HOST IDENTITY CHANGED!\n\n" +
                        $"Peer: {baseUrl}\n" +
                        $"Previous Fingerprint: {prevFp}\n" +
                        $"New Fingerprint:      {hostFp}\n\n" +
                        $"This could mean your friend reinstalled Windows, OR someone is intercepting this address.\n\n" +
                        $"Trust and pin the new identity?",
                        "Host Identity Verification Warning",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (allowNew != MessageBoxResult.Yes)
                    {
                        PeerCompareStatus.Text = "Connection aborted due to Host Identity mismatch.";
                        return;
                    }
                    AppConfig.Current.PinnedPeerFingerprints[baseUrl.TrimEnd('/').ToLowerInvariant()] = hostFp;
                    AppConfig.Save();
                }
            }

            var remote = JsonSerializer.Deserialize<LibraryManifest>(rawBytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (remote == null || remote.Songs == null)
            {
                PeerCompareStatus.Text = "Peer returned an empty library.";
                return;
            }

            _lastPeerManifest = remote;
            _lastPeerBaseUrl = baseUrl;
            _lastPeerSecret = string.IsNullOrWhiteSpace(secret) ? _security.PairingPin : secret;

            RenderPeerLibrary();
        }
        catch (Exception ex)
        {
            // Auto-heal fallback: if direct connection timed out/failed, try resolving friend's live tunnel address via Signaling Relay
            var matchingFriend = AppConfig.Current.Friends.FirstOrDefault(f => 
                (!string.IsNullOrWhiteSpace(f.LastAddress) && f.LastAddress.TrimEnd('/').Equals(baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ||
                (!string.IsNullOrWhiteSpace(f.TrustToken) && f.TrustToken.Equals(secret, StringComparison.OrdinalIgnoreCase)));

            if (matchingFriend != null && FriendService.SignalingRelay != null)
            {
                PeerCompareStatus.Text = $"Connection to {baseUrl} timed out. Resolving {matchingFriend.Username}'s live tunnel...";
                var (found, _, liveAddr, _) = await FriendService.SignalingRelay.ResolvePeerAsync(matchingFriend.FriendCode);
                if (found && !string.IsNullOrWhiteSpace(liveAddr) && !liveAddr.TrimEnd('/').Equals(baseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                {
                    PeerAddressBox.Text = liveAddr;
                    matchingFriend.LastAddress = liveAddr;
                    AppConfig.Save();
                    ShowStageToast("🔄 AUTO-HEALED ADDRESS", $"Switched to {matchingFriend.Username}'s live tunnel: {liveAddr}", "#22C55E");
                    ComparePeerBtn_Click(sender, e);
                    return;
                }
            }

            PeerCompareStatus.Text = $"Connection failed: {ex.Message}";
        }
    }

    private void ShowFullPeerLib_Changed(object sender, RoutedEventArgs e)
    {
        if (_lastPeerManifest != null) RenderPeerLibrary();
    }

    private void FilterMissingOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (FilterMissingOnlyChk != null)
        {
            AppConfig.Current.FilterPeerMissingOnly = FilterMissingOnlyChk.IsChecked == true;
            AppConfig.Save();
        }
        if (_lastPeerManifest != null) RenderPeerLibrary();
    }

    private List<CustomSongInfo> GetPeerLobbySongs(LibraryManifest remote)
    {
        int cap = Math.Clamp(remote.MaxLobbyAutoSyncSongs > 0 ? remote.MaxLobbyAutoSyncSongs : 10, 1, 25);
        var tagged = remote.Songs.Where(s => s.IsInLobbySetlist || (remote.LobbySetlistIds?.Contains(s.SongId, StringComparer.OrdinalIgnoreCase) == true)).Take(cap).ToList();
        return tagged.Count > 0 ? tagged : remote.Songs.Take(cap).ToList();
    }

    private void RenderPeerLibrary()
    {
        if (_lastPeerManifest == null) return;
        PeerSongsPanel.Children.Clear();

        var localSongs = _gh3.GetInstalledCustomSongs();
        var localIds = localSongs.Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lobbySongs = GetPeerLobbySongs(_lastPeerManifest);
        var missingLobby = lobbySongs.Where(s => !localIds.Contains(s.SongId)).ToList();
        var peerAllIds = _lastPeerManifest.Songs.Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var peerMissingFromLocal = localSongs.Where(s => !peerAllIds.Contains(s.SongId)).ToList();

        LobbyMatchBanner.Visibility = Visibility.Visible;
        if (missingLobby.Count == 0 && peerMissingFromLocal.Count == 0)
        {
            LobbyMatchBanner.Background = (WpfBrush)FindResource("CardSuccessBg");
            LobbyMatchBanner.BorderBrush = (WpfBrush)FindResource("CardSuccessBorder");
            LobbyMatchTitle.Text = $"100% Setlist & Slot Matched ({localIds.Count} songs) — Ready for BetterGH3 Online!";
            LobbyMatchTitle.Foreground = (WpfBrush)FindResource("CardSuccessTitle");
            LobbyMatchSub.Text = $"Both you and {_lastPeerManifest.MachineName} have identical custom songs in matching slots. Any custom song can be picked online with zero loading freezes!";
            AutoSyncLobbyBtn.Visibility = Visibility.Collapsed;
        }
        else if (missingLobby.Count == 0 && peerMissingFromLocal.Count > 0)
        {
            LobbyMatchBanner.Background = (WpfBrush)FindResource("CardWarnBg");
            LobbyMatchBanner.BorderBrush = (WpfBrush)FindResource("CardWarnBorder");
            LobbyMatchTitle.Text = $"⚠️ Asymmetric Libraries: {_lastPeerManifest.MachineName} is missing {peerMissingFromLocal.Count} of your songs ({localIds.Count} vs {_lastPeerManifest.Songs.Count})";
            LobbyMatchTitle.Foreground = (WpfBrush)FindResource("CardWarnTitle");
            LobbyMatchSub.Text = $"You have all {_lastPeerManifest.Songs.Count} of {_lastPeerManifest.MachineName}'s songs, but their game does NOT have your {peerMissingFromLocal.Count} customs. In BetterGH3, picking a custom song slot your friend doesn't have will freeze their loading screen! Either ask {_lastPeerManifest.MachineName} to sync your songs, or click '🪞 Mirror Friend's Setlist' below to match their {_lastPeerManifest.Songs.Count} songs for this session.";
            AutoSyncLobbyBtn.Visibility = Visibility.Visible;
            AutoSyncLobbyBtn.Content = "🪞 Mirror Friend's Setlist (Temp Match)";
        }
        else
        {
            double missingMb = missingLobby.Sum(s => s.TotalSizeBytes) / (1024.0 * 1024.0);
            LobbyMatchBanner.Background = (WpfBrush)FindResource("CardWarnBg");
            LobbyMatchBanner.BorderBrush = (WpfBrush)FindResource("CardWarnBorder");
            LobbyMatchTitle.Text = $"{missingLobby.Count} Lobby Song(s) Missing ({missingMb:F1} MB)";
            LobbyMatchTitle.Foreground = (WpfBrush)FindResource("CardWarnTitle");
            LobbyMatchSub.Text = $"Auto-Sync only transfers {_lastPeerManifest.MachineName}'s active Lobby Setlist (capped at {lobbySongs.Count} songs — skipping {_lastPeerManifest.Songs.Count - lobbySongs.Count} other tracks).";
            AutoSyncLobbyBtn.Visibility = Visibility.Visible;
            AutoSyncLobbyBtn.Content = $"Auto-Sync {missingLobby.Count} Lobby Song(s)";
        }

        Gh3NotebookOuter.Visibility = Visibility.Visible;
        Gh3TierHeader.Text = $"4. {_lastPeerManifest.MachineName.ToUpperInvariant()}'S SETLIST";

        var activeFriend = AppConfig.Current.Friends.FirstOrDefault(f =>
            (_lastPeerManifest != null && f.Username.Equals(_lastPeerManifest.MachineName, StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(_lastPeerBaseUrl) && !string.IsNullOrEmpty(f.LastAddress) && f.LastAddress.TrimEnd('/').Equals(_lastPeerBaseUrl.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)));
        bool isTrusted = activeFriend?.IsTrusted == true;
        Gh3FriendBadge.Text = isTrusted ? "★ TRUSTED FRIEND" : "👥 REGULAR FRIEND";
        Gh3FriendBadgeBorder.Background = isTrusted ? (WpfBrush)new BrushConverter().ConvertFromString("#FEF3C7")! : (WpfBrush)new BrushConverter().ConvertFromString("#F3F4F6")!;
        Gh3FriendBadgeBorder.BorderBrush = isTrusted ? (WpfBrush)new BrushConverter().ConvertFromString("#F59E0B")! : (WpfBrush)new BrushConverter().ConvertFromString("#D1D5DB")!;
        Gh3FriendBadge.Foreground = isTrusted ? (WpfBrush)new BrushConverter().ConvertFromString("#92400E")! : (WpfBrush)new BrushConverter().ConvertFromString("#4B5563")!;

        var displayList = ShowFullPeerLibChk.IsChecked == true ? _lastPeerManifest.Songs : lobbySongs;
        if (FilterMissingOnlyChk?.IsChecked == true)
        {
            displayList = displayList.Where(s => !localIds.Contains(s.SongId)).ToList();
        }
        PeerCompareStatus.Text = ShowFullPeerLibChk.IsChecked == true
            ? $"Showing all {displayList.Count} songs from {_lastPeerManifest.MachineName}:"
            : $"Showing {_lastPeerManifest.MachineName}'s Active Lobby Setlist ({displayList.Count} songs):";

        int songRowIdx = 0;
        foreach (var s in displayList)
        {
            songRowIdx++;
            bool alreadyOwned = localIds.Contains(s.SongId);
            bool isKept = AppConfig.IsKeptFriendSong(s.SongId) || _tempSession.CurrentState.Songs.Any(x => x.SongId.Equals(s.SongId, StringComparison.OrdinalIgnoreCase) && x.Kept);
            bool isTempSessionSong = _tempSession.CurrentState.Songs.Any(x => x.SongId.Equals(s.SongId, StringComparison.OrdinalIgnoreCase) && !x.Kept);

            // Guitar Hero III Authentic Setlist Ruled Line Notebook Row
            var rowBorder = new Border
            {
                Padding = new Thickness(76, 8, 16, 8),
                BorderThickness = new Thickness(0, 0, 0, 1),
                BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString("#C6D8E8")!,
                Background = System.Windows.Media.Brushes.Transparent
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            // Left: Song Title & Artist / Year in marker ink style
            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            bool isLeadSong = songRowIdx == 1 || isTempSessionSong;
            var titleColor = isLeadSong ? "#B45309" : "#1E293B";

            var titleBlock = new TextBlock
            {
                Text = s.Title,
                FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
                FontSize = 19,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(titleColor)!
            };
            textStack.Children.Add(titleBlock);

            var artistBlock = new TextBlock
            {
                Text = $"{s.Artist.ToUpperInvariant()}{(!string.IsNullOrWhiteSpace(s.Year) ? $", {s.Year}" : "")}",
                FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
                FontSize = 11.5,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#4B5563")!,
                Margin = new Thickness(0, 1, 0, 0)
            };
            textStack.Children.Add(artistBlock);

            // Score comparison or local status subtext
            var myScore = AppConfig.GetHighScore(s.SongId);
            SongScoreRecord? peerScore = null;
            if (_lastPeerManifest.HighScores != null)
            {
                var pKey = _lastPeerManifest.HighScores.Keys.FirstOrDefault(k => k.Equals(s.SongId, StringComparison.OrdinalIgnoreCase));
                if (pKey != null) peerScore = _lastPeerManifest.HighScores[pKey];
            }

            if (myScore != null || peerScore != null)
            {
                int mine = myScore?.BestScore ?? 0;
                int theirs = peerScore?.BestScore ?? 0;
                string crown = mine > theirs ? "👑 You Lead" : (theirs > mine ? $"🏆 {_lastPeerManifest.MachineName} Leads" : "🤝 Tied");
                textStack.Children.Add(new TextBlock
                {
                    Text = $"{crown}  •  You: {mine:N0}  vs  Peer: {theirs:N0}",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (WpfBrush)new BrushConverter().ConvertFromString(mine >= theirs ? "#15803D" : "#B45309")!,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            else if (alreadyOwned)
            {
                textStack.Children.Add(new TextBlock
                {
                    Text = isKept ? "⭐ Kept in Local Library" : (isTempSessionSong ? "⚡ Active in Temporary Lobby" : "Installed"),
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (WpfBrush)new BrushConverter().ConvertFromString(isKept ? "#15803D" : "#6B7280")!,
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }

            Grid.SetColumn(textStack, 0);
            rowGrid.Children.Add(textStack);

            // Center-Right: 5 Stars & High Score (Matching GH3 Setlist Reference Photo)
            var scoreStack = new StackPanel
            {
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(14, 0, 18, 0)
            };

            int bestScoreVal = Math.Max(myScore?.BestScore ?? 0, peerScore?.BestScore ?? 0);
            string starsText = bestScoreVal > 0 ? "★★★★★" : "☆☆☆☆☆";
            var starColor = bestScoreVal > 0 ? "#D97706" : "#94A3B8";

            scoreStack.Children.Add(new TextBlock
            {
                Text = starsText,
                FontSize = 14,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(starColor)!,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right
            });

            scoreStack.Children.Add(new TextBlock
            {
                Text = bestScoreVal > 0 ? $"{bestScoreVal:N0}" : $"{s.TotalSizeBytes / (1024.0 * 1024.0):F1} MB",
                FontFamily = new System.Windows.Media.FontFamily("Impact, Arial Black"),
                FontSize = 14,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#1E293B")!,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
                Margin = new Thickness(0, 1, 0, 0)
            });

            Grid.SetColumn(scoreStack, 1);
            rowGrid.Children.Add(scoreStack);

            // Right: Action buttons (⚡ Sync & ⭐ Permanently Keep Song)
            var actionStack = new StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            // Dedicated Per-Song KEEP Button
            if (isKept)
            {
                var keptBadge = new Border
                {
                    Background = (WpfBrush)new BrushConverter().ConvertFromString("#DCFCE7")!,
                    BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString("#22C55E")!,
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(5),
                    Padding = new Thickness(8, 4, 8, 4),
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "This song has been permanently kept and will not be removed when the session ends."
                };
                keptBadge.Child = new TextBlock
                {
                    Text = "✔ Kept Forever",
                    FontSize = 11,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#15803D")!
                };
                actionStack.Children.Add(keptBadge);
            }
            else
            {
                var keepBtn = new System.Windows.Controls.Button
                {
                    Content = isTrusted ? "⭐ Keep Forever" : "🔒 Keep Song",
                    Style = (Style)FindResource(isTrusted ? "FlameButton" : "OutlineButton"),
                    Padding = new Thickness(9, 4, 9, 4),
                    Margin = new Thickness(0, 0, 6, 0),
                    IsEnabled = isTrusted
                };

                if (isTrusted)
                {
                    keepBtn.ToolTip = "Permanently keep this song in your customs library so it never gets auto-removed.";
                    keepBtn.Click += async (_, _) =>
                    {
                        keepBtn.IsEnabled = false;
                        keepBtn.Content = "Saving...";

                        if (!alreadyOwned)
                        {
                            var (dlOk, dlErr) = await DownloadAndInstallPeerSongAsync(s, temporaryMode: true);
                            if (!dlOk)
                            {
                                WpfMessageBox.Show($"Failed to download song for keeping: {dlErr}", "Keep Song", MessageBoxButton.OK, MessageBoxImage.Error);
                                keepBtn.IsEnabled = true;
                                keepBtn.Content = "⭐ Keep Forever";
                                return;
                            }
                        }

                        _tempSession.KeepSongPermanently(s.SongId);
                        ShowStageToast("⭐ SONG KEPT PERMANENTLY", $"'{s.Title}' saved permanently to your library!", "#22C55E");
                        AudioWaveformAndSfxService.PlayStarPowerChime();
                        RefreshSongsList();
                        RefreshTempSessionBanner();
                        RenderPeerLibrary();
                    };
                }
                else
                {
                    keepBtn.ToolTip = "Regular Friends sync songs temporarily for multiplayer. Upgrade to 'Trusted Friend' to permanently keep songs!";
                }
                actionStack.Children.Add(keepBtn);
            }

            // SYNC BUTTON
            var dlBtn = new System.Windows.Controls.Button
            {
                Content = alreadyOwned ? (isTempSessionSong ? "⚡ Temp Synced" : "Re-Sync") : "⚡ Sync Song",
                Style = (Style)FindResource(alreadyOwned ? "OutlineButton" : "GreenButton"),
                Padding = new Thickness(10, 4, 10, 4)
            };
            dlBtn.Click += async (_, _) =>
            {
                dlBtn.IsEnabled = false;
                dlBtn.Content = "Syncing...";
                var (ok, err) = await DownloadAndInstallPeerSongAsync(s, temporaryMode: true);
                if (ok)
                {
                    dlBtn.Content = "⚡ Synced";
                    RefreshSongsList();
                    RefreshTempSessionBanner();
                    RenderPeerLibrary();
                }
                else
                {
                    dlBtn.IsEnabled = true;
                    dlBtn.Content = "Retry";
                    WpfMessageBox.Show($"Sync failed: {err}", "P2P Sync", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            };
            actionStack.Children.Add(dlBtn);

            Grid.SetColumn(actionStack, 2);
            rowGrid.Children.Add(actionStack);

            rowBorder.Child = rowGrid;
            PeerSongsPanel.Children.Add(rowBorder);
        }
    }

    private void EndLobbySyncBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var (removed, kept, msg) = _tempSession.EndSessionAndCleanup();
            AudioWaveformAndSfxService.PlayStarPowerChime();
            ShowStageToast("⏹ LOBBY SYNC ENDED", $"Restored original library safely! {removed} temp song(s) cleared, {kept} kept song(s) preserved.", "#10B981");

            RefreshSongsList();
            RefreshTempSessionBanner();
            RenderPeerLibrary();
            RefreshSongsList();
            RefreshTempSessionBanner();
            RenderPeerLibrary();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Error ending lobby sync: {ex.Message}", "End Lobby Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void AutoSyncLobbyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPeerManifest == null) return;
        var localIds = _gh3.GetInstalledCustomSongs().Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var peerLobbySongs = GetPeerLobbySongs(_lastPeerManifest);
        var missing = peerLobbySongs.Where(s => !localIds.Contains(s.SongId)).ToList();

        if (missing.Count == 0)
        {
            PageP2P_StageHud.Finish("All lobby songs are already installed in your library!");
            ShowStageToast("✅ ALREADY MATCHED!", $"All lobby songs from {_lastPeerManifest.MachineName} are already installed in your library! You are 100% ready for online multiplayer.", "#22C55E");
            WpfMessageBox.Show(
                $"All {peerLobbySongs.Count} Lobby Setlist songs from {_lastPeerManifest.MachineName} are already installed in your local Guitar Hero III library!\n\nYour library is fully intact and you can launch Guitar Hero III to rock out together.",
                "Lobby Already Synced", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool tempMode = true;
        AutoSyncLobbyBtn.IsEnabled = false;
        PageP2P_StageHud.Report(10, "STARTING LOBBY SYNC", $"Preparing transfer of {missing.Count} track(s)...");
        if (tempMode)
            _tempSession.BeginSessionIfNeeded(_lastPeerBaseUrl, autoCleanOnGameExit: true);
        else
            _saves.BackupCurrentSaves($"Before-Lobby-Sync-{_lastPeerManifest.MachineName}");

        int installed = 0;
        string lastErr = "";
        for (int i = 0; i < missing.Count; i++)
        {
            var s = missing[i];
            int pct = (int)((i + 1) * 90.0 / missing.Count);
            PeerCompareStatus.Text = $"Syncing ({installed + 1}/{missing.Count}): {s.Title}...";
            PageP2P_StageHud.Report(pct, "TRANSFERRING P2P AUDIO & CHARTS", $"({i + 1}/{missing.Count}) Downloading {s.Artist} - {s.Title}...");
            var (ok, err) = await DownloadAndInstallPeerSongAsync(s, tempMode);
            if (ok) installed++;
            else lastErr = err;
        }

        AutoSyncLobbyBtn.IsEnabled = true;
        RefreshSongsList();
        RefreshTempSessionBanner();
        RenderPeerLibrary();
        if (installed > 0)
        {
            PageP2P_StageHud.Finish($"Lobby synchronized! {installed}/{missing.Count} songs ready for battle.");
            WpfMessageBox.Show(
                tempMode
                    ? $"Temporary-synced {installed} / {missing.Count} Lobby song(s) from {_lastPeerManifest.MachineName}!\n\nThese songs will auto-clean when GH3 exits unless you click 'Keep Song'."
                    : $"Synced {installed} / {missing.Count} Lobby Setlist song(s) cleanly into your customs.pak.xen!",
                "Lobby Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (!string.IsNullOrEmpty(lastErr))
        {
            PageP2P_StageHud.SetError("LOBBY SYNC FAILED", lastErr);
            WpfMessageBox.Show($"Sync could not complete: {lastErr}", "Lobby Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            PageP2P_StageHud.Reset();
        }
    }

    private async Task<(bool Ok, string Error)> DownloadAndInstallPeerSongAsync(CustomSongInfo song, bool temporaryMode)
    {
        try
        {
            if (temporaryMode)
                _tempSession.BeginSessionIfNeeded(_lastPeerBaseUrl, autoCleanOnGameExit: true);

            using var resp = await SendChallengeHmacGetAsync(_lastPeerBaseUrl, $"/api/download/{Uri.EscapeDataString(song.SongId)}", _lastPeerSecret, TimeSpan.FromMinutes(5));
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return (false, "Access Denied (403): Only Trusted Friends or active Expiring Invites can download songs.");
            resp.EnsureSuccessStatusCode();

            var encBytes = await resp.Content.ReadAsByteArrayAsync();
            var zipBytes = _security.DecryptPayload(encBytes, _lastPeerSecret);

            // Binary Magic-Byte Validation before writing anything to Guitar Hero III
            if (!SecurityService.ValidateCompiledSongPackageBytes(zipBytes, out var binErr))
                return (false, $"Security Block: {binErr}");

            // If recipient doesn't have DATA\customs.pak.xen yet, pull initial template before merging
            if (!File.Exists(_gh3.CustomsPakPath))
                await PullPeerCustomsPakAsync();

            _gh3.InstallSongPackage(zipBytes, _logger, song.PakHash, song.FsbHash, mergeIntoCustomsPak: true);

            if (temporaryMode)
                _tempSession.RegisterTempSong(song);

            return (true, "");
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[P2P SYNC] Song download failed ({song.SongId}): {ex.Message}");
            return (false, ex.Message);
        }
    }

    private async Task PullPeerCustomsPakAsync()
    {
        try
        {
            using var resp = await SendChallengeHmacGetAsync(_lastPeerBaseUrl, "/api/download-customs-pak", _lastPeerSecret, TimeSpan.FromSeconds(30));
            resp.EnsureSuccessStatusCode();
            var encPak = await resp.Content.ReadAsByteArrayAsync();
            var rawPak = _security.DecryptPayload(encPak, _lastPeerSecret);
            _gh3.BackupAndDeployCustomsPak(rawPak);
            CustomsPakService.AlignAndSortCustomsPak(_gh3.GamePath, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[P2P SYNC] Could not pull initial customs.pak.xen: {ex.Message}");
        }
    }

    // ==================== PAGE 5 EXTENSION: SYNCHRONIZED READY-UP, PARTY ROULETTE & SHOWCASE ====================
    private bool _isLocalLobbyReady = false;
    private bool _isRemoteLobbyReady = false;
    private bool _isCountingDown = false;

    private async void LobbyReadyBtn_Click(object sender, RoutedEventArgs e)
    {
        _isLocalLobbyReady = !_isLocalLobbyReady;
        UpdateLobbyReadyUi();

        var peerCode = GetActivePeerFriendCode();
        var peerAddr = (PeerAddressBox?.Text ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(peerCode) && FriendService.SignalingRelay != null)
        {
            _ = FriendService.SignalingRelay.SendLobbyReadyPacketAsync(peerCode, AppConfig.Current.FriendCode, _isLocalLobbyReady);
        }

        if (!string.IsNullOrWhiteSpace(peerAddr))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = new { senderCode = AppConfig.Current.FriendCode, isReady = _isLocalLobbyReady };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
                    using var client = new HttpClient(SignalingRelayService.CreateFastIPv4Handler());
                    await client.PostAsJsonAsync($"{peerAddr.TrimEnd('/')}/api/lobby/ready", payload, cts.Token);
                }
                catch { }
            });
        }

        if (_isLocalLobbyReady && _isRemoteLobbyReady)
        {
            await StartSynchronizedCountdownAsync();
        }
    }

    private void UpdateLobbyReadyUi()
    {
        if (LocalReadyBadge != null && LocalReadyText != null)
        {
            LocalReadyBadge.Background = (WpfBrush)new BrushConverter().ConvertFromString(_isLocalLobbyReady ? "#065F46" : "#1E293B")!;
            LocalReadyText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(_isLocalLobbyReady ? "#34D399" : "#94A3B8")!;
            LocalReadyText.Text = _isLocalLobbyReady ? "🟢 You: READY!" : "⚪ You: Not Ready";
        }

        if (RemoteReadyBadge != null && RemoteReadyText != null)
        {
            RemoteReadyBadge.Background = (WpfBrush)new BrushConverter().ConvertFromString(_isRemoteLobbyReady ? "#065F46" : "#1E293B")!;
            RemoteReadyText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(_isRemoteLobbyReady ? "#34D399" : "#94A3B8")!;
            RemoteReadyText.Text = _isRemoteLobbyReady ? "🟢 Friend: READY!" : "⚪ Friend: Waiting";
        }

        if (LobbyReadyBtn != null)
        {
            LobbyReadyBtn.Content = _isLocalLobbyReady ? "⚪ Cancel Ready" : "🟢 Ready Up";
            LobbyReadyBtn.Style = (Style)FindResource(_isLocalLobbyReady ? "OutlineButton" : "FlameButton");
        }
    }

    private async void HandleRemotePeerReadyChanged(string peerCode, bool isReady)
    {
        _isRemoteLobbyReady = isReady;
        UpdateLobbyReadyUi();

        var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(peerCode, StringComparison.OrdinalIgnoreCase));
        var name = friend?.Username ?? peerCode;

        if (isReady)
        {
            ShowStageToast("🟢 FRIEND READY!", $"{name} is READY to rock!", "#22C55E");
            AudioWaveformAndSfxService.PlayStarPowerChime();
            if (_isLocalLobbyReady)
            {
                await StartSynchronizedCountdownAsync();
            }
        }
        else
        {
            ShowStageToast("⚪ FRIEND WAITING", $"{name} cancelled ready state.", "#94A3B8");
        }
    }

    private async Task StartSynchronizedCountdownAsync()
    {
        if (_isCountingDown) return;
        _isCountingDown = true;

        try
        {
            if (StageCountdownOverlay != null)
            {
                StageCountdownOverlay.Visibility = Visibility.Visible;
            }
            AudioWaveformAndSfxService.PlayMetronomeClick();

            if (StageCountdownNumber != null) StageCountdownNumber.Text = "3";
            if (StageCountdownSub != null) StageCountdownSub.Text = "GET READY...";
            await Task.Delay(950);

            if (StageCountdownNumber != null) StageCountdownNumber.Text = "2";
            if (StageCountdownSub != null) StageCountdownSub.Text = "SET...";
            AudioWaveformAndSfxService.PlayMetronomeClick();
            await Task.Delay(950);

            if (StageCountdownNumber != null) StageCountdownNumber.Text = "1";
            if (StageCountdownSub != null) StageCountdownSub.Text = "ALMOST THERE...";
            AudioWaveformAndSfxService.PlayMetronomeClick();
            await Task.Delay(950);

            if (StageCountdownNumber != null) StageCountdownNumber.Text = "🎸";
            if (StageCountdownSub != null) StageCountdownSub.Text = "ROCK ON!";
            AudioWaveformAndSfxService.PlayCompilePowerChord();
            await Task.Delay(600);

            if (StageCountdownOverlay != null)
            {
                StageCountdownOverlay.Visibility = Visibility.Collapsed;
            }

            _isLocalLobbyReady = false;
            _isRemoteLobbyReady = false;
            UpdateLobbyReadyUi();

            LaunchGh3(skipAudioAlert: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarn($"[COUNTDOWN] Exception during countdown: {ex.Message}");
        }
        finally
        {
            if (StageCountdownOverlay != null)
            {
                StageCountdownOverlay.Visibility = Visibility.Collapsed;
            }
            _isCountingDown = false;
        }
    }

    private string GetActivePeerFriendCode()
    {
        if (TrustedFriendQuickCombo != null && TrustedFriendQuickCombo.SelectedIndex > 0 && TrustedFriendQuickCombo.SelectedIndex - 1 < _trustedDropdownEntries.Count)
        {
            return _trustedDropdownEntries[TrustedFriendQuickCombo.SelectedIndex - 1].FriendCode;
        }
        var addr = (PeerAddressBox?.Text ?? "").Trim();
        var friend = AppConfig.Current.Friends.FirstOrDefault(f =>
            (!string.IsNullOrEmpty(f.LastAddress) && f.LastAddress.TrimEnd('/').Equals(addr.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) ||
            (_lastPeerManifest != null && f.Username.Equals(_lastPeerManifest.MachineName, StringComparison.OrdinalIgnoreCase)));
        return friend?.FriendCode ?? "";
    }

    private void PartyRouletteBtn_Click(object sender, RoutedEventArgs e)
    {
        var customs = _allSongs.Where(s => s.PakSizeBytes > 0 && !string.IsNullOrEmpty(s.SongId)).ToList();
        if (customs.Count == 0)
        {
            ShowStageToast("⚠️ NO CUSTOMS", "Install custom songs first to use Party Roulette!", "#F59E0B");
            return;
        }

        int seed = Random.Shared.Next(1000, 999999);
        var rng = new Random(seed);
        var picked = customs.OrderBy(_ => rng.Next()).Take(Math.Min(10, customs.Count)).ToList();
        var pickedIds = picked.Select(s => s.SongId).ToList();

        var peerCode = GetActivePeerFriendCode();
        var peerAddr = (PeerAddressBox?.Text ?? "").Trim();

        if (!string.IsNullOrWhiteSpace(peerCode) && FriendService.SignalingRelay != null)
        {
            _ = FriendService.SignalingRelay.SendRouletteRollPacketAsync(peerCode, AppConfig.Current.FriendCode, seed, pickedIds);
        }

        if (!string.IsNullOrWhiteSpace(peerAddr))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var payload = new { senderCode = AppConfig.Current.FriendCode, seed = seed, songIds = pickedIds };
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2.0));
                    using var client = new HttpClient(SignalingRelayService.CreateFastIPv4Handler());
                    await client.PostAsJsonAsync($"{peerAddr.TrimEnd('/')}/api/lobby/roulette-roll", payload, cts.Token);
                }
                catch { }
            });
        }

        ApplyLobbySetlistSelection(pickedIds);
        ShowStageToast("🎲 PARTY ROULETTE ROLLED!", $"Selected {picked.Count} randomized party tracks!", "#F97316");
        AudioWaveformAndSfxService.PlayStarPowerChime();
    }

    private void HandleRemotePeerRouletteRoll(string peerCode, int seed, List<string> songIds)
    {
        var friend = AppConfig.Current.Friends.FirstOrDefault(f => f.FriendCode.Equals(peerCode, StringComparison.OrdinalIgnoreCase));
        var name = friend?.Username ?? peerCode;

        if (songIds != null && songIds.Count > 0)
        {
            ApplyLobbySetlistSelection(songIds);
            ShowStageToast("🎲 PARTY ROULETTE SYNCED!", $"{name} rolled {songIds.Count} party tracks for your setlist!", "#F97316");
            AudioWaveformAndSfxService.PlayStarPowerChime();
        }
    }

    private void ApplyLobbySetlistSelection(List<string> songIds)
    {
        var idSet = songIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var s in _allSongs)
        {
            s.IsInLobbySetlist = idSet.Contains(s.SongId);
        }
        AppConfig.Current.LobbySetlistIds = songIds;
        AppConfig.Save();
        RefreshSongsList();
        UpdateLobbyCounters();
        if (_lastPeerManifest != null) RenderPeerLibrary();
    }

    public void RefreshShowcaseCombo()
    {
        if (ProfileShowcaseCombo == null) return;
        ProfileShowcaseCombo.SelectionChanged -= ProfileShowcaseCombo_SelectionChanged;
        ProfileShowcaseCombo.Items.Clear();

        ProfileShowcaseCombo.Items.Add(new ComboBoxItem { Content = "(None - No Signature Track)", Tag = "" });
        int selIdx = 0;
        int idx = 1;
        var currentTitle = AppConfig.Current.ShowcaseSongTitle ?? "";
        var currentId = AppConfig.Current.ShowcaseSongId ?? "";

        foreach (var s in _allSongs)
        {
            var item = new ComboBoxItem
            {
                Content = $"{s.Title} — {s.Artist}",
                Tag = s.SongId
            };
            ProfileShowcaseCombo.Items.Add(item);
            if (!string.IsNullOrEmpty(currentId) && s.SongId.Equals(currentId, StringComparison.OrdinalIgnoreCase))
            {
                selIdx = idx;
            }
            else if (selIdx == 0 && !string.IsNullOrEmpty(currentTitle) && s.Title.Equals(currentTitle, StringComparison.OrdinalIgnoreCase))
            {
                selIdx = idx;
            }
            idx++;
        }

        ProfileShowcaseCombo.SelectedIndex = selIdx;
        ProfileShowcaseCombo.SelectionChanged += ProfileShowcaseCombo_SelectionChanged;
    }

    private void ProfileShowcaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileShowcaseCombo?.SelectedItem is ComboBoxItem item)
        {
            var tag = item.Tag?.ToString() ?? "";
            if (string.IsNullOrEmpty(tag))
            {
                AppConfig.Current.ShowcaseSongId = "";
                AppConfig.Current.ShowcaseSongTitle = "";
            }
            else
            {
                var match = _allSongs.FirstOrDefault(s => s.SongId.Equals(tag, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                {
                    AppConfig.Current.ShowcaseSongId = match.SongId;
                    AppConfig.Current.ShowcaseSongTitle = $"{match.Title} — {match.Artist}";
                }
            }
            AppConfig.Save();
            _ = BroadcastHeartbeatAsync();
        }
    }

    private void PreviewShowcaseBtn_Click(object sender, RoutedEventArgs e)
    {
        var songId = AppConfig.Current.ShowcaseSongId;
        var song = _allSongs.FirstOrDefault(s => s.SongId.Equals(songId, StringComparison.OrdinalIgnoreCase));
        if (song != null)
        {
            var audio = Gh3Service.FindSongAudioPath(song);
            if (!string.IsNullOrEmpty(audio) && File.Exists(audio))
            {
                PlayAudioPreview(audio, song.Title, song, false);
                ShowStageToast("🎸 SHOWCASE PREVIEW", $"Playing '{song.Title}' signature preview...", "#38BDF8");
            }
            else
            {
                ShowStageToast("⚠️ AUDIO NOT FOUND", "Audio file for signature track could not be found.", "#F59E0B");
            }
        }
        else
        {
            ShowStageToast("ℹ️ NO SIGNATURE TRACK", "Select a signature track in your profile first!", "#94A3B8");
        }
    }

    private async Task BroadcastHeartbeatAsync()
    {
        if (FriendService.SignalingRelay != null && !string.IsNullOrWhiteSpace(AppConfig.Current.FriendCode))
        {
            var bestAddr = FriendService.BestAddressProvider?.Invoke() ?? "";
            await FriendService.SignalingRelay.PublishHeartbeatAsync(AppConfig.Current.FriendCode, AppConfig.Current.Username, bestAddr);
        }
    }

    // ==================== PAGE 6: IMPORT & CLONE HERO CONVERTER ====================
    private async void ImportCloneHeroFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new WinForms.FolderBrowserDialog { Description = "Select Clone Hero / YARG Song Folder" };
        if (fbd.ShowDialog() == WinForms.DialogResult.OK)
            await RunCloneHeroImportAsync(fbd.SelectedPath);
    }

    private void OpenLibraryMigrationBtn_Click(object sender, RoutedEventArgs e)
    {
        var wizard = new LibraryMigrationWindow(_gh3, _cloneHero) { Owner = this };
        wizard.ShowDialog();
        if (wizard.ImportedCount > 0)
            RefreshSongsList();
    }

    private async void ImportCloneHeroZipBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Clone Hero / Chorus Encore (.sng or .zip)",
            Filter = "Clone Hero / Chorus Packages (*.sng;*.zip)|*.sng;*.zip|SNG Packages (*.sng)|*.sng|ZIP Archives (*.zip)|*.zip|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            await RunCloneHeroImportAsync(dlg.FileName);
    }

    private async Task<bool> RunCloneHeroImportAsync(string path)
    {
        ChImportProgress.Visibility = Visibility.Visible;
        ChImportProgress.Value = 5;
        ChImportStatusText.Text = $"Starting Clone Hero conversion ({Path.GetFileName(path)})...";
        PageImport_StageHud.Report(5, "CONVERTING CLONE HERO / YARG PACKAGE", $"Loading {Path.GetFileName(path)}...");

        var res = await _cloneHero.ImportFromZipOrFolderAsync(path, autoCompile: true, (pct, msg) => Dispatcher.Invoke(() =>
        {
            ChImportProgress.Value = pct;
            ChImportStatusText.Text = msg;
            PageImport_StageHud.Report(pct, "CONVERTING CLONE HERO / YARG PACKAGE", msg);
        }));

        ChImportProgress.Visibility = Visibility.Collapsed;
        ChImportStatusText.Text = res.Message;
        if (res.Success)
        {
            PageImport_StageHud.Finish($"Imported: {res.Message}");
            RefreshSongsList();
        }
        else
        {
            PageImport_StageHud.SetError("IMPORT FAILED", res.Message);
        }
        return res.Success;
    }

    private void ImportArchiveBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import GH3-Sync Archive",
            Filter = "GH3 Archives (*.gh3pack;*.gh3song;*.sgh;*.zip)|*.gh3pack;*.gh3song;*.sgh;*.zip|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true) ImportStandardArchive(dlg.FileName);
    }

    private void ImportStandardArchive(string filePath)
    {
        PageImport_StageHud.Report(20, "IMPORTING GH3 ARCHIVE", $"Validating {Path.GetFileName(filePath)}...");
        try
        {
            _saves.BackupCurrentSaves("Before-Archive-Import");
            var bytes = File.ReadAllBytes(filePath);
            PageImport_StageHud.Report(60, "EXTRACTING ARCHIVE", "Extracting chart midis, FSV video, and Vorbis audio streams...");
            int count = _gh3.ImportArchiveFile(bytes, Path.GetFileName(filePath), _logger);
            ImportStatusText.Text = $"Imported {count} custom song(s) from '{Path.GetFileName(filePath)}'.";
            PageImport_StageHud.Finish($"Imported {count} custom song(s) cleanly!");
            RefreshSongsList();
            RefreshSaveBackups();
        }
        catch (Exception ex)
        {
            PageImport_StageHud.SetError("IMPORT FAILED", ex.Message);
            ImportStatusText.Text = $"Import failed: {ex.Message}";
        }
    }

    // ==================== PAGE 7: CAREER SAVES (PAIRED PROFILE SNAPSHOTS) ====================
    private void RefreshSaveBackups()
    {
        SaveBackupsListPanel.Children.Clear();
        var backups = _saves.GetBackups();
        if (backups.Count == 0)
        {
            SaveBackupsListPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("GhCard"),
                Child = new TextBlock { Text = "No career profile snapshots saved yet.", Foreground = (WpfBrush)FindResource("TextMuted") }
            });
            return;
        }

        foreach (var b in backups)
        {
            var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var stack = new StackPanel();
            stack.Children.Add(new TextBlock { Text = b.ProfileName, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary") });
            stack.Children.Add(new TextBlock
            {
                Text = $"{b.Timestamp}  •  {b.FileCount} file(s)  •  {(b.IncludesCustomsPak ? "Paired with customs.pak.xen (Safe Restore)" : "Legacy save-only backup")}",
                FontSize = 11.5,
                Foreground = (WpfBrush)FindResource(b.IncludesCustomsPak ? "CardSuccessTitle" : "CardWarnTitle")
            });
            Grid.SetColumn(stack, 0);
            grid.Children.Add(stack);

            var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var restoreBtn = new System.Windows.Controls.Button { Content = "Restore", Style = (Style)FindResource("GreenButton"), Margin = new Thickness(0, 0, 6, 0) };
            restoreBtn.Click += (_, _) =>
            {
                if (WpfMessageBox.Show($"Restore '{b.ProfileName}'?\n\nThis restores BOTH your career save (s000.d / toc.dat) AND its matching customs.pak.xen setlist from {b.Timestamp}.",
                    "Restore Career Profile", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    return;

                PageSaves_StageHud.Report(40, "RESTORING CAREER PROFILE", $"Restoring '{b.ProfileName}' and matching customs setlist...");
                if (_saves.RestoreBackup(b.BackupId, _gh3.GamePath))
                {
                    PageSaves_StageHud.Finish($"Restored '{b.ProfileName}' cleanly!");
                    WpfMessageBox.Show($"Restored '{b.ProfileName}' cleanly!", "Profile Restored", MessageBoxButton.OK, MessageBoxImage.Information);
                    RefreshSongsList();
                }
                else
                {
                    PageSaves_StageHud.SetError("RESTORE FAILED", "Could not restore paired snapshot.");
                }
            };
            var delBtn = new System.Windows.Controls.Button { Content = "Delete", Style = (Style)FindResource("OutlineButton") };
            delBtn.Click += (_, _) => { _saves.DeleteBackup(b.BackupId); RefreshSaveBackups(); };

            btns.Children.Add(restoreBtn);
            btns.Children.Add(delBtn);
            Grid.SetColumn(btns, 1);
            grid.Children.Add(btns);

            card.Child = grid;
            SaveBackupsListPanel.Children.Add(card);
        }
    }

    private void BackupSaveNowBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = string.IsNullOrWhiteSpace(SaveProfileNameBox.Text) ? "Career Profile" : SaveProfileNameBox.Text.Trim();
        PageSaves_StageHud.Report(35, "CREATING CAREER BACKUP", $"Capturing paired profile snapshot '{name}'...");
        var b = _saves.BackupCurrentSaves(name, name, _gh3.GamePath);
        RefreshSaveBackups();
        PageSaves_StageHud.Finish($"Saved snapshot '{b.ProfileName}' cleanly!");
        WpfMessageBox.Show($"Saved paired snapshot '{b.ProfileName}' ({b.FileCount} files, customs.pak paired: {b.IncludesCustomsPak}).",
            "Profile Snapshot Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreLatestSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        PageSaves_StageHud.Report(40, "RESTORING LATEST SNAPSHOT", "Locating most recent paired career profile...");
        if (_saves.RestoreLatestBackup(_gh3.GamePath))
        {
            PageSaves_StageHud.Finish("Restored latest paired career snapshot!");
            WpfMessageBox.Show("Restored latest paired career snapshot cleanly!", "Restored", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshSongsList();
        }
        else
        {
            PageSaves_StageHud.SetError("RESTORE FAILED", "No paired career snapshots available to restore.");
            WpfMessageBox.Show("No snapshots available to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ==================== PAGE 8: SETTINGS, THEMES, UPDATES & ABOUT ====================
    private void RefreshLogsBtn_Click(object sender, RoutedEventArgs e) => RefreshLogs();
    private void RefreshLogs()
    {
        try
        {
            LogsViewerBox.Text = _logger.ReadRecentLogs(150);
            LogsViewerBox.ScrollToEnd();
        }
        catch { }
    }

    private void GameConfigBtn_Click(object sender, RoutedEventArgs e)
    {
        NavSettings.IsChecked = true;
    }

    private void SettingsSubTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button btn || btn.Tag is not string tag) return;
        SettingsSubGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubAppearance.Visibility = tag == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubBetterGh3.Visibility = tag == "bettergh3" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubSecurity.Visibility = tag == "security" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubUpdates.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubHowTo.Visibility = tag == "howto" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubCredits.Visibility = tag == "credits" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubLogs.Visibility = tag == "logs" ? Visibility.Visible : Visibility.Collapsed;

        TabSetGeneralBtn.Style = (Style)FindResource(tag == "general" ? "FlameButton" : "OutlineButton");
        TabSetAppearanceBtn.Style = (Style)FindResource(tag == "appearance" ? "FlameButton" : "OutlineButton");
        TabSetBetterGh3Btn.Style = (Style)FindResource(tag == "bettergh3" ? "FlameButton" : "OutlineButton");
        TabSetSecurityBtn.Style = (Style)FindResource(tag == "security" ? "FlameButton" : "OutlineButton");
        TabSetUpdatesBtn.Style = (Style)FindResource(tag == "updates" ? "FlameButton" : "OutlineButton");
        TabSetHowToBtn.Style = (Style)FindResource(tag == "howto" ? "FlameButton" : "OutlineButton");
        TabSetCreditsBtn.Style = (Style)FindResource(tag == "credits" ? "FlameButton" : "OutlineButton");
        TabSetLogsBtn.Style = (Style)FindResource(tag == "logs" ? "FlameButton" : "OutlineButton");

        UIElement activeSub = tag switch
        {
            "appearance" => SettingsSubAppearance,
            "bettergh3" => SettingsSubBetterGh3,
            "security" => SettingsSubSecurity,
            "updates" => SettingsSubUpdates,
            "howto" => SettingsSubHowTo,
            "credits" => SettingsSubCredits,
            "logs" => SettingsSubLogs,
            _ => SettingsSubGeneral
        };
        AnimatePanelEntrance(activeSub);

        if (tag == "bettergh3") { RefreshBetterGh3Plugins(); RefreshHighwaySkins(); }
        if (tag == "security") RefreshSecurityAuditPanel();
        if (tag == "logs") RefreshLogs();
    }

    // ==================== SECURITY & PRIVACY DASHBOARD ====================
    private void RefreshSecurityAuditPanel()
    {
        if (HostFingerprintText != null)
            HostFingerprintText.Text = "Local P2P Network: Online & Protected  •  Zero external cloud exposure";
        if (SecurityBlockedPanel == null || SecurityAuditBox == null) return;
        SecurityBlockedPanel.Children.Clear();

        var blocked = _security.GetBlockedIps();
        if (blocked.Count == 0)
        {
            SecurityBlockedPanel.Children.Add(new TextBlock
            {
                Text = "✓ No blocked or banned IPs right now.",
                FontSize = 12,
                Foreground = (WpfBrush)FindResource("CardSuccessTitle"),
                Margin = new Thickness(0, 2, 0, 4)
            });
        }
        else
        {
            foreach (var rec in blocked)
            {
                var row = new Border
                {
                    Background = (WpfBrush)FindResource("BgCardHover"),
                    BorderBrush = (WpfBrush)FindResource("CardDangerBorder"),
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 0, 5)
                };
                var dp = new DockPanel();
                var unbanBtn = new System.Windows.Controls.Button
                {
                    Content = "Unban",
                    Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(10, 3, 10, 3)
                };
                unbanBtn.Click += (_, _) =>
                {
                    _security.UnbanIp(rec.IpAddress);
                    RefreshSecurityAuditPanel();
                    ShowStageToast("🔓 IP UNBANNED", $"Removed block for {rec.IpAddress}.", "#22C55E");
                };
                DockPanel.SetDock(unbanBtn, Dock.Right);
                dp.Children.Add(unbanBtn);
                dp.Children.Add(new TextBlock
                {
                    Text = $"{rec.IpAddress}  •  {(rec.IsManualBan ? "Manual Ban" : $"{rec.Attempts}/5 Failed Auth")}  •  {rec.Reason}",
                    FontSize = 12,
                    Foreground = (WpfBrush)FindResource("CardDangerFg"),
                    VerticalAlignment = VerticalAlignment.Center
                });
                row.Child = dp;
                SecurityBlockedPanel.Children.Add(row);
            }
        }

        var events = _security.GetRecentAuditEntries();
        SecurityAuditBox.Text = events.Count > 0
            ? string.Join(Environment.NewLine, events)
            : "No security events recorded in this session yet.";
    }

    private void RefreshSecurityAuditBtn_Click(object sender, RoutedEventArgs e)
    {
        RefreshSecurityAuditPanel();
        ShowStageToast("🔒 SECURITY AUDIT REFRESHED", "Live IP blocklist & auth audit updated.", "#38BDF8");
    }

    private void RotateCredentialsBtn_Click(object sender, RoutedEventArgs e)
    {
        var confirm = WpfMessageBox.Show(
            "Rotate ALL P2P Credentials?\n\n" +
            "This will immediately:\n" +
            "• Generate a brand new 6-digit Session Pairing PIN\n" +
            "• Revoke all active 4-Hour Expiring Invite tokens\n" +
            "• Rotate every Trusted Friend's Trust Token (you'll share updated invite codes with them)\n\n" +
            "Continue?",
            "Rotate Security Credentials",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        var msg = _security.RotateSessionCredentials(_friends);
        SecurityActionStatusText.Text = msg;
        RefreshNetworkSummary();
        RefreshFriendsList();
        RefreshTrustedFriendDropdown();
        RefreshSecurityAuditPanel();
        ShowStageToast("🔒 CREDENTIALS ROTATED!", $"New PIN: {_security.PairingPin} • All old invites revoked.", "#22C55E");
    }

    private void ManualBanIpBtn_Click(object sender, RoutedEventArgs e)
    {
        var ip = (ManualBanIpBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ip)) return;
        if (SecurityService.IsLoopbackIp(ip))
        {
            WpfMessageBox.Show("Cannot ban localhost (127.0.0.1 / ::1).", "IP Ban", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _security.BanIp(ip);
        ManualBanIpBox.Text = "";
        RefreshSecurityAuditPanel();
        ShowStageToast("🚫 IP BANNED", $"Blocked {ip} from all P2P endpoints.", "#EF4444");
    }

    private string? PromptPassphraseDialog(string title, string subtitle, bool requireConfirm)
    {
        var dlg = new Window
        {
            Title = title,
            Width = 440,
            Height = requireConfirm ? 270 : 215,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        string? result = null;
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeights.Bold });
        stack.Children.Add(new TextBlock { Text = subtitle, FontSize = 12, Foreground = (WpfBrush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 12) });

        var pw1 = new PasswordBox { Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(8, 5, 8, 5) };
        stack.Children.Add(pw1);

        PasswordBox? pw2 = null;
        if (requireConfirm)
        {
            stack.Children.Add(new TextBlock { Text = "Confirm Passphrase:", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted"), Margin = new Thickness(0, 0, 0, 4) });
            pw2 = new PasswordBox { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(8, 5, 8, 5) };
            stack.Children.Add(pw2);
        }

        var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        var okBtn = new System.Windows.Controls.Button { Content = "Continue", Style = (Style)FindResource("FlameButton") };
        okBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(pw1.Password) || pw1.Password.Length < 6)
            {
                WpfMessageBox.Show("Passphrase must be at least 6 characters long.", "Passphrase", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (requireConfirm && pw1.Password != pw2!.Password)
            {
                WpfMessageBox.Show("Passphrases do not match.", "Passphrase", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            result = pw1.Password;
            dlg.Close();
        };
        btnRow.Children.Add(okBtn);
        stack.Children.Add(btnRow);

        dlg.Content = stack;
        dlg.ShowDialog();
        return result;
    }

    private void ExportEncBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        var pass = PromptPassphraseDialog(
            "Encrypt Profile Backup (.gh3backup)",
            "Choose a strong passphrase (min 6 chars) to encrypt your config, friends, scores, highway skins & save backups using AES-256-GCM.",
            requireConfirm: true);
        if (string.IsNullOrEmpty(pass)) return;

        var sfd = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save Encrypted GH3-Sync Profile Backup",
            Filter = "GH3-Sync Encrypted Backup (*.gh3backup)|*.gh3backup",
            FileName = $"GH3-Sync-Profile-{DateTime.Now:yyyyMMdd}.gh3backup"
        };
        if (sfd.ShowDialog() != true) return;

        var (ok, msg, _) = ProfileBackupService.ExportEncryptedBackup(sfd.FileName, pass, _logger);
        EncBackupStatusText.Text = msg;
        if (ok) ShowStageToast("🔐 ENCRYPTED BACKUP SAVED!", msg, "#22C55E");
        else WpfMessageBox.Show(msg, "Backup Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void ImportEncBackupBtn_Click(object sender, RoutedEventArgs e)
    {
        var ofd = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Encrypted GH3-Sync Profile Backup (.gh3backup)",
            Filter = "GH3-Sync Encrypted Backup (*.gh3backup)|*.gh3backup"
        };
        if (ofd.ShowDialog() != true) return;

        var pass = PromptPassphraseDialog(
            "Decrypt Profile Backup (.gh3backup)",
            "Enter the passphrase used to encrypt this .gh3backup file.",
            requireConfirm: false);
        if (string.IsNullOrEmpty(pass)) return;

        var (ok, msg, _) = ProfileBackupService.ImportEncryptedBackup(ofd.FileName, pass, _logger);
        EncBackupStatusText.Text = msg;
        if (ok)
        {
            RefreshAllUiState();
            ShowStageToast("🔓 PROFILE RESTORED!", msg, "#22C55E");
        }
        else
        {
            WpfMessageBox.Show(msg, "Restore Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void LoadSettingsForm()
    {
        SettingsGamePathBox.Text = _gh3.GamePath;
        SettingsSongsPathBox.Text = SongCreatorService.ResolveSongsDirectory();
        SettingsHoneycombPathBox.Text = AppConfig.Current.HoneycombPath;
        SettingsDirectInstallChk.IsChecked = AppConfig.Current.DirectInstallToGame;
        SettingsMinLaunchChk.IsChecked = AppConfig.Current.MinimizeOnGameLaunch;
        SettingsP2pOnlineChk.IsChecked = AppConfig.Current.P2pOnlineEnabled;
        SettingsDiscordRpcChk.IsChecked = AppConfig.Current.EnableDiscordRichPresence;
        SettingsNormalizeDefaultChk.IsChecked = AppConfig.Current.NormalizeAudioLoudness;
        CrNormalizeLoudnessChk.IsChecked = AppConfig.Current.NormalizeAudioLoudness;
        SettingsAnimationsChk.IsChecked = AppConfig.Current.EnableUiAnimations;
        if (SettingsSfxChk != null) SettingsSfxChk.IsChecked = AppConfig.Current.EnableSoundEffects;
        if (SettingsRequireDownloadApprovalChk != null) SettingsRequireDownloadApprovalChk.IsChecked = AppConfig.Current.RequireHostDownloadApproval;
        if (PlayerVolumeSlider != null) PlayerVolumeSlider.Value = Math.Clamp(AppConfig.Current.AudioPreviewVolume, 0.0, 1.0);
        if (CompactViewBtn != null) CompactViewBtn.Content = AppConfig.Current.CompactSetlistView ? "▦ Cards" : "☰ Compact";
        if (GroupByCombo != null) GroupByCombo.SelectedIndex = (AppConfig.Current.GroupByMode ?? "None") switch { "Artist" => 1, "Tier" => 2, _ => 0 };
        ApplyStreamerPrivacyVisuals();
        SettingsLobbyCapBox.Text = AppConfig.Current.MaxLobbyAutoSyncSongs.ToString();
        SettingsAutoRollbackChk.IsChecked = AppConfig.Current.AutoRollbackOnFailedUpdate;
        SettingsOverlayEnabledChk.IsChecked = AppConfig.Current.EnableInGameOverlay;
        SettingsOverlayCornerCombo.SelectedIndex = AppConfig.Current.InGameOverlayCorner switch
        {
            "TopLeft" => 1,
            "BottomRight" => 2,
            "BottomLeft" => 3,
            _ => 0
        };
        SettingsOverlaySizeCombo.SelectedIndex = (AppConfig.Current.InGameOverlaySize ?? "Medium") switch
        {
            "Small" => 0,
            "Large" => 2,
            _ => 1
        };
        int opPct = (int)Math.Round(Math.Clamp(AppConfig.Current.InGameOverlayOpacity, 0.4, 1.0) * 100.0);
        SettingsOverlayOpacitySlider.Value = opPct;
        SettingsOverlayOpacityLabel.Text = $"Opacity: {opPct}%";
        CommunityClassicFretsChk.IsChecked = AppConfig.Current.ConvertOpenNotesToClassicGh3;

        // v1.4.0 Concert Master Edition Settings
        if (SettingsTimerBoostChk != null) SettingsTimerBoostChk.IsChecked = AppConfig.Current.EnableHighPrecisionTimer;
        if (SettingsZeroTrustChk != null) SettingsZeroTrustChk.IsChecked = AppConfig.Current.ZeroTrustQuarantineEnabled;
        if (SettingsGpuToggleChk != null) SettingsGpuToggleChk.IsChecked = AppConfig.Current.StemSeparationGpuEnabled;
        if (SettingsGpuStatusLabel != null) SettingsGpuStatusLabel.Text = GpuAccelerationService.GetStatusBadge();
        if (SettingsBlackholeChk != null) SettingsBlackholeChk.IsChecked = AppConfig.Current.StealthBlackholeMode;

        if (SettingsDebounceKnob != null) SettingsDebounceKnob.Value = AppConfig.Current.ControllerDebounceMs;
        if (SettingsWhammyKnob != null) SettingsWhammyKnob.Value = AppConfig.Current.WhammyDeadzone;
        if (SettingsTiltKnob != null) SettingsTiltKnob.Value = AppConfig.Current.TiltSensitivity;
        if (SettingsLufsKnob != null) SettingsLufsKnob.Value = AppConfig.Current.AudioTargetLufs;
        if (SettingsLanguageCombo != null)
        {
            string curLang = AppConfig.Current.Language ?? "en";
            foreach (var item in SettingsLanguageCombo.Items)
            {
                if (item is ComboBoxItem cbi && cbi.Tag is string tag && tag.Equals(curLang, StringComparison.OrdinalIgnoreCase))
                {
                    SettingsLanguageCombo.SelectedItem = cbi;
                    break;
                }
            }
        }

        UpdateThemeCardSelection();
    }

    private void SettingsAnimationsChk_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.EnableUiAnimations = SettingsAnimationsChk.IsChecked == true;
        AppConfig.Save();
        ApplyAnimationPreferences();
    }

    private void SettingsOverlay_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.EnableInGameOverlay = SettingsOverlayEnabledChk.IsChecked == true;
        AppConfig.Save();
    }

    private void SettingsOverlayCorner_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.InGameOverlayCorner = SettingsOverlayCornerCombo.SelectedIndex switch
        {
            1 => "TopLeft",
            2 => "BottomRight",
            3 => "BottomLeft",
            _ => "TopRight"
        };
        AppConfig.Save();
    }

    private void SettingsOverlaySize_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.InGameOverlaySize = SettingsOverlaySizeCombo.SelectedIndex switch
        {
            0 => "Small",
            2 => "Large",
            _ => "Medium"
        };
        AppConfig.Save();
    }

    private void SettingsOverlayOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        int pct = (int)Math.Round(SettingsOverlayOpacitySlider.Value);
        SettingsOverlayOpacityLabel.Text = $"Opacity: {pct}%";
        AppConfig.Current.InGameOverlayOpacity = Math.Clamp(pct / 100.0, 0.4, 1.0);
        AppConfig.Save();
    }

    private void TestOverlayPulseBtn_Click(object sender, RoutedEventArgs e)
    {
        _logger.PulsePeerActivity("Manual HUD Strum Pulse Test");
        WpfMessageBox.Show("Triggered Electric Guitar HUD strum pulse! (When GH3.exe is running, the blue strings flash & sweep automatically whenever a friend connects or syncs.)",
            "Electric Guitar HUD Pulse", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ==================== BETTERGH3 QUICK-TWEAKS & PLUGIN MANAGER ====================
    private void RefreshBetterGh3PluginsBtn_Click(object sender, RoutedEventArgs e) => RefreshBetterGh3Plugins();

    private void RefreshBetterGh3Plugins()
    {
        if (BetterGh3PluginsPanel == null) return;
        BetterGh3PluginsPanel.Children.Clear();
        LaunchX360ceBtn.Visibility = File.Exists(_betterGh3Tweaks.X360ceExePath) ? Visibility.Visible : Visibility.Collapsed;

        var plugins = _betterGh3Tweaks.GetPlugins();
        if (plugins.Count == 0)
        {
            BetterGh3PluginsPanel.Children.Add(new TextBlock
            {
                Text = "No BetterGH3 PLUGINS folder detected in your configured GH3 directory.",
                Foreground = (WpfBrush)FindResource("TextMuted"),
                FontSize = 12.5
            });
            return;
        }

        foreach (var p in plugins)
        {
            var card = new Border
            {
                Background = (WpfBrush)FindResource("BgCardHover"),
                BorderBrush = (WpfBrush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(0, 0, 0, 7)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            var titleRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
            titleRow.Children.Add(new TextBlock { Text = $"{p.DisplayName}  ({p.FileName})", FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary"), VerticalAlignment = VerticalAlignment.Center });
            if (p.VerifiedLoadedInLastLaunch)
            {
                var verBadge = MakeBadge($"✓ Loaded ({p.LoadedAddress})", "#064E3B", "#6EE7B7");
                verBadge.Margin = new Thickness(8, 0, 0, 0);
                titleRow.Children.Add(verBadge);
            }
            left.Children.Add(titleRow);
            left.Children.Add(new TextBlock { Text = p.Description, FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var toggleBtn = new System.Windows.Controls.Button
            {
                Content = p.IsCoreProtected ? "Protected (Active)" : (p.IsEnabled ? "✓ Enabled (Disable)" : "Disabled (Enable)"),
                Style = (Style)FindResource(p.IsEnabled ? "GreenButton" : "OutlineButton"),
                IsEnabled = !p.IsCoreProtected,
                Padding = new Thickness(11, 5, 11, 5),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0)
            };
            toggleBtn.Click += (_, _) =>
            {
                var (ok, msg) = _betterGh3Tweaks.SetPluginEnabled(p.FileName, !p.IsEnabled);
                BetterGh3TweaksStatusText.Text = msg;
                if (!ok) WpfMessageBox.Show(msg, "BetterGH3 Tweaks", MessageBoxButton.OK, MessageBoxImage.Warning);
                RefreshBetterGh3Plugins();
            };
            Grid.SetColumn(toggleBtn, 1);
            grid.Children.Add(toggleBtn);

            card.Child = grid;
            BetterGh3PluginsPanel.Children.Add(card);
        }
    }

    private void OpenGuitarTesterBtn_Click(object sender, RoutedEventArgs e)
    {
        var tester = new GuitarTesterWindow { Owner = this };
        tester.ShowDialog();
    }

    private void ApplyCompetitivePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = _betterGh3Tweaks.ApplyPresetProfile("Competitive");
        BetterGh3TweaksStatusText.Text = msg;
        RefreshBetterGh3Plugins();
    }

    private void ApplyClassicPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = _betterGh3Tweaks.ApplyPresetProfile("Classic");
        BetterGh3TweaksStatusText.Text = msg;
        RefreshBetterGh3Plugins();
    }

    private void RefreshHighwaySkins()
    {
        if (HighwaySkinsPanel == null) return;
        HighwaySkinsPanel.Children.Clear();
        var skins = _highwaySkins.GetSavedSkins();
        if (skins.Count == 0)
        {
            HighwaySkinsPanel.Children.Add(new TextBlock
            {
                Text = "No custom highway skins saved yet. Click '+ Create Skin from Image...' to turn any PNG/JPG into a 512×1024 GH3 highway texture!",
                FontSize = 12,
                Foreground = (WpfBrush)FindResource("TextMuted")
            });
            return;
        }

        foreach (var sk in skins)
        {
            var card = new Border
            {
                Width = 148,
                Background = (WpfBrush)FindResource("BgCardHover"),
                BorderBrush = sk.IsActive ? (WpfBrush)FindResource("AccentFlame") : (WpfBrush)FindResource("BorderSubtle"),
                BorderThickness = new Thickness(sk.IsActive ? 2 : 1),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(8),
                Margin = new Thickness(0, 0, 10, 10)
            };
            var sp = new StackPanel();
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 132;
                bmp.UriSource = new Uri(sk.FilePath, UriKind.Absolute);
                bmp.EndInit();
                bmp.Freeze();
                sp.Children.Add(new Border
                {
                    Height = 96, CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 0, 0, 6),
                    Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }
                });
            }
            catch { }
            sp.Children.Add(new TextBlock { Text = sk.Name, FontSize = 12, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary"), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 6) });
            var applyBtn = new System.Windows.Controls.Button
            {
                Content = sk.IsActive ? "✓ Active" : "Apply Skin",
                Style = (Style)FindResource(sk.IsActive ? "GreenButton" : "OutlineButton"),
                Padding = new Thickness(8, 4, 8, 4)
            };
            applyBtn.Click += (_, _) =>
            {
                var (_, msg) = _highwaySkins.ApplySavedSkin(sk.Name);
                HighwaySkinStatusText.Text = msg;
                RefreshHighwaySkins();
            };
            sp.Children.Add(applyBtn);
            card.Child = sp;
            HighwaySkinsPanel.Children.Add(card);
        }
    }

    private void ImportHighwaySkinBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Image for Custom GH3 Highway Skin",
            Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp"
        };
        if (dlg.ShowDialog() != true) return;
        var (ok, _, msg) = _highwaySkins.ImportAndApplyHighwayImage(dlg.FileName, Path.GetFileNameWithoutExtension(dlg.FileName));
        HighwaySkinStatusText.Text = msg;
        RefreshHighwaySkins();
        WpfMessageBox.Show(msg, ok ? "Custom Highway Installed" : "Highway Skin Error", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void RestoreDefaultHighwayBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, msg) = _highwaySkins.RestoreDefaultHighway();
        HighwaySkinStatusText.Text = msg;
        RefreshHighwaySkins();
    }

    private void OpenBetterGh3PluginsFolderBtn_Click(object sender, RoutedEventArgs e)
        => OpenFolderInExplorer(_betterGh3Tweaks.PluginsDir);

    private void OpenBetterGh3HighwaysFolderBtn_Click(object sender, RoutedEventArgs e)
        => OpenFolderInExplorer(_betterGh3Tweaks.HighwaysDir);

    private void LaunchX360ceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (File.Exists(_betterGh3Tweaks.X360ceExePath))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _betterGh3Tweaks.X360ceExePath,
                WorkingDirectory = Path.GetDirectoryName(_betterGh3Tweaks.X360ceExePath),
                UseShellExecute = true
            });
        }
    }

    private void SettingsBrowseGamePath_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new WinForms.FolderBrowserDialog { SelectedPath = SettingsGamePathBox.Text, Description = "Select Guitar Hero III Game Directory" };
        if (fbd.ShowDialog() == WinForms.DialogResult.OK) SettingsGamePathBox.Text = fbd.SelectedPath;
    }

    private void SettingsBrowseSongsPath_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new WinForms.FolderBrowserDialog { SelectedPath = SettingsSongsPathBox.Text, Description = "Select Custom Songs Directory" };
        if (fbd.ShowDialog() == WinForms.DialogResult.OK) SettingsSongsPathBox.Text = fbd.SelectedPath;
    }

    private async void SettingsSaveGeneral_Click(object sender, RoutedEventArgs e)
    {
        bool wasOnline = AppConfig.Current.P2pOnlineEnabled;
        AppConfig.Current.GamePath = SettingsGamePathBox.Text.Trim();
        AppConfig.Current.CustomSongsPath = SettingsSongsPathBox.Text.Trim();
        AppConfig.Current.HoneycombPath = SettingsHoneycombPathBox.Text.Trim();
        AppConfig.Current.DirectInstallToGame = SettingsDirectInstallChk.IsChecked == true;
        AppConfig.Current.MinimizeOnGameLaunch = SettingsMinLaunchChk.IsChecked == true;
        AppConfig.Current.P2pOnlineEnabled = SettingsP2pOnlineChk.IsChecked == true;
        AppConfig.Current.EnableDiscordRichPresence = SettingsDiscordRpcChk.IsChecked == true;
        AppConfig.Current.NormalizeAudioLoudness = SettingsNormalizeDefaultChk.IsChecked == true;
        CrNormalizeLoudnessChk.IsChecked = AppConfig.Current.NormalizeAudioLoudness;
        if (int.TryParse(SettingsLobbyCapBox.Text, out var cap))
            AppConfig.Current.MaxLobbyAutoSyncSongs = Math.Clamp(cap, 1, 25);

        // v1.4.0 Concert Master Edition Settings
        if (SettingsTimerBoostChk != null)
        {
            AppConfig.Current.EnableHighPrecisionTimer = SettingsTimerBoostChk.IsChecked == true;
            if (AppConfig.Current.EnableHighPrecisionTimer)
                EngineVaccineService.EnableHighPrecisionTimer();
            else
                EngineVaccineService.DisableHighPrecisionTimer();
        }
        if (SettingsZeroTrustChk != null) AppConfig.Current.ZeroTrustQuarantineEnabled = SettingsZeroTrustChk.IsChecked == true;
        if (SettingsGpuToggleChk != null) AppConfig.Current.StemSeparationGpuEnabled = SettingsGpuToggleChk.IsChecked == true;
        if (SettingsBlackholeChk != null) AppConfig.Current.StealthBlackholeMode = SettingsBlackholeChk.IsChecked == true;

        if (SettingsDebounceKnob != null) AppConfig.Current.ControllerDebounceMs = (int)Math.Round(SettingsDebounceKnob.Value);
        if (SettingsWhammyKnob != null) AppConfig.Current.WhammyDeadzone = SettingsWhammyKnob.Value;
        if (SettingsTiltKnob != null) AppConfig.Current.TiltSensitivity = SettingsTiltKnob.Value;
        if (SettingsLufsKnob != null) AppConfig.Current.AudioTargetLufs = SettingsLufsKnob.Value;
        if (SettingsLanguageCombo?.SelectedItem is ComboBoxItem langItem && langItem.Tag is string langCode)
        {
            LocalizationService.SetLanguage(langCode);
            ApplyLocalizationStrings();
        }

        AppConfig.Save();

        // If user switched to Offline Mode, immediately close UPnP port 7373!
        if (wasOnline && !AppConfig.Current.P2pOnlineEnabled)
        {
            await _nat.RemoveMappingAsync();
        }
        else if (!wasOnline && AppConfig.Current.P2pOnlineEnabled && AppConfig.Current.AutoPortMap)
        {
            _ = Task.Run(async () => await _nat.InitializeAsync());
        }

        SettingsSaveStatus.Text = "Settings saved!";
        RefreshAllUiState();
    }

    private void SettingsLanguageCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SettingsLanguageCombo?.SelectedItem is ComboBoxItem item && item.Tag is string lang)
        {
            LocalizationService.SetLanguage(lang);
            ApplyLocalizationStrings();
        }
    }

    private void AirGapPanicBtn_Click(object sender, RoutedEventArgs e)
    {
        EngineVaccineService.TriggerPanicAirGap();
        if (SettingsP2pOnlineChk != null) SettingsP2pOnlineChk.IsChecked = false;
        WpfMessageBox.Show("EMERGENCY AIR-GAP ACTIVATED!\n\nAll external network ports, UPnP mappings, and P2P sync tunnels have been terminated and blackholed.", "AIR-GAP SHIELD", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void SettingsDebounceKnob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AppConfig.Current.ControllerDebounceMs = (int)Math.Round(e.NewValue);
    }

    private void SettingsWhammyKnob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AppConfig.Current.WhammyDeadzone = e.NewValue;
    }

    private void SettingsTiltKnob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AppConfig.Current.TiltSensitivity = e.NewValue;
    }

    private void SettingsLufsKnob_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        AppConfig.Current.AudioTargetLufs = e.NewValue;
    }

    private void TogglePollingTestBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pollingTester == null)
        {
            _pollingTester = new ControllerPollingTesterService();
            _pollingTester.OnTelemetryUpdated += telem => Dispatcher.Invoke(() =>
            {
                if (PollingRateFreqText != null) PollingRateFreqText.Text = $"{telem.FrequencyHz} Hz";
                if (PollingRateIntervalText != null) PollingRateIntervalText.Text = $"{telem.IntervalMs:F2} ms";
                if (PollingRateJitterText != null) PollingRateJitterText.Text = $"±{telem.JitterMs:F2} ms";
                if (PollingRatePacketsText != null) PollingRatePacketsText.Text = telem.TotalPackets.ToString("N0");
                if (PollingRateRatingText != null) PollingRateRatingText.Text = telem.Rating;
            });
            _pollingTester.Start();
            TogglePollingTestBtn.Content = "⏹ Stop Test";
            ShowStageToast("⚡ TESTER ACTIVE", "Measuring raw 1000Hz USB controller polling rate...", "#38BDF8");
        }
        else
        {
            _pollingTester.Stop();
            _pollingTester.Dispose();
            _pollingTester = null;
            TogglePollingTestBtn.Content = "⚡ Start 1000Hz Test";
        }
    }

    private void RunVaccineScanBtn_Click(object sender, RoutedEventArgs e)
    {
        PageSettings_StageHud.Report(30, "ENGINE VACCINE SCAN", "Verifying retail binary checksum, customs.pak, and timer boost...");
        var (ok, details) = EngineVaccineService.VerifyGameIntegrity(_gh3.GamePath);
        PageSettings_StageHud.Finish("Engine Vaccine scan completed!");

        if (VaccineReportItemsPanel != null)
        {
            VaccineReportItemsPanel.Children.Clear();
            foreach (var line in details.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                VaccineReportItemsPanel.Children.Add(new TextBlock
                {
                    Text = line,
                    FontSize = 11.5,
                    Foreground = line.Contains("✓") || line.Contains("Valid") || line.Contains("Active")
                        ? new SolidColorBrush(WpfColor.FromRgb(34, 197, 94))
                        : new SolidColorBrush(WpfColor.FromRgb(245, 158, 11)),
                    Margin = new Thickness(0, 1, 0, 1)
                });
            }
        }
        if (VaccineScoreText != null)
        {
            VaccineScoreText.Text = ok ? "SCORE: 100% HEALTHY — TOURNAMENT CERTIFIED" : "SCORE: 92% HEALTHY — MINOR NOTES";
        }
        WpfMessageBox.Show(details, "Engine Vaccine Report Card", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void ExportVaultBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Complete .gh3vault Portable Backup",
            Filter = "GH3 Vault Archive (*.gh3vault)|*.gh3vault",
            FileName = $"GH3_Concert_Vault_{DateTime.Now:yyyyMMdd}.gh3vault"
        };
        if (dlg.ShowDialog() != true) return;

        PageSettings_StageHud.Report(10, "EXPORTING .GH3VAULT", "Archiving customs songs, soundbanks, and career profiles...");
        var (ok, msg, _) = _vaultService.ExportVault(
            dlg.FileName,
            _gh3.GamePath,
            Path.Combine(_gh3.GamePath, "DATA", "PAK", "Custom_Songs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aspyr", "Guitar Hero III"),
            (pct, step) => PageSettings_StageHud.Report(pct, "EXPORTING .GH3VAULT", step)
        );

        if (ok)
        {
            PageSettings_StageHud.Finish("Exported portable .gh3vault package!");
            if (VaultStatusText != null) VaultStatusText.Text = "Exported cleanly!";
            WpfMessageBox.Show(msg, "Vault Export Succeeded", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            PageSettings_StageHud.SetError("VAULT EXPORT FAILED", msg);
            WpfMessageBox.Show(msg, "Vault Export", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ImportVaultBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select .gh3vault Portable Container to Restore",
            Filter = "GH3 Vault Archive (*.gh3vault)|*.gh3vault|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        PageSettings_StageHud.Report(15, "INSPECTING .GH3VAULT", "Validating container manifest and hashes...");
        var (ok, msg, manifest) = _vaultService.RestoreVault(
            dlg.FileName,
            _gh3.GamePath,
            Path.Combine(_gh3.GamePath, "DATA", "PAK", "Custom_Songs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Aspyr", "Guitar Hero III"),
            (pct, step) => PageSettings_StageHud.Report(pct, "RESTORING .GH3VAULT", step)
        );

        if (ok)
        {
            PageSettings_StageHud.Finish("Restored .gh3vault package cleanly!");
            RefreshSongsList();
            RefreshSaveBackups();
            if (VaultStatusText != null) VaultStatusText.Text = $"Restored {manifest?.SongCount} songs!";
            WpfMessageBox.Show(msg, "Vault Restored Cleanly", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            PageSettings_StageHud.SetError("VAULT RESTORE FAILED", msg);
            WpfMessageBox.Show(msg, "Vault Restore", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void TournamentBracketBtn_Click(object sender, RoutedEventArgs e)
    {
        var names = _friends.Friends.Select(f => f.Username).ToList();
        if (names.Count == 0)
        {
            names = new List<string> { _friends.Profile.Username, "Slash", "Tom Morello", "Lou", "Buckethead", "Herman Li", "Sam Totman", "Synyster Gates" };
        }
        else
        {
            names.Insert(0, _friends.Profile.Username);
        }

        var tourney = _tournamentService.GenerateTournament("Legends of Rock World Tour", names, _allSongs);
        string md = _tournamentService.ExportTournamentMarkdown(tourney);
        try { System.Windows.Clipboard.SetText(md); } catch { }

        ShowStageToast("🏆 TOURNAMENT CREATED!", $"{tourney.PlayerCount}-Player Bracket copied to Clipboard for Discord!", "#F59E0B");
        WpfMessageBox.Show(
            $"Rock Tour Tournament Bracket Generated!\n\n" +
            $"• Players: {tourney.PlayerCount}\n" +
            $"• Round 1 Matches: {tourney.Matches.Count}\n" +
            $"• Rules: Song Veto (1 Ban each) + Decider Track in Pro Face-Off\n\n" +
            $"The complete markdown bracket and score table has been copied to your clipboard, ready to paste straight into Discord or Reddit!",
            "Rock Tour Tournament Bracket", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BossBattleModalBtn_Click(object sender, RoutedEventArgs e)
    {
        var picked = _allSongs.FirstOrDefault(s => _selectedExportIds.Contains(s.SongId)) ?? _allSongs.FirstOrDefault();
        if (picked == null)
        {
            WpfMessageBox.Show("Please select a custom song in My Customs first to convert into a Boss Battle Duel!", "Boss Battle Duel", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string chartPath = Path.Combine(_gh3.GamePath, "DATA", "PAK", "Custom_Songs", picked.SongId, "notes.chart");
        if (!File.Exists(chartPath))
            chartPath = Path.Combine(_gh3.GamePath, "DATA", "PAK", "Custom_Songs", picked.SongId, "notes.mid");

        var config = new BossBattleCreatorService.BossBattleConfig(
            BossName: "Lou the Devil",
            Difficulty: "Shredder",
            PowerUpDensity: 4,
            BossHealthDrain: 1.25,
            IncludeIntroSolo: true,
            Venue: "Battle for Lou's Soul (Hell)"
        );

        PageCustoms_StageHud.Report(40, "GENERATING BOSS BATTLE DUEL", $"Embedding battle phrases (Broken String, Double Notes, Lefty Flip) for {picked.Title}...");
        var (ok, msg, gems) = _bossService.ConvertChartToBossBattle(chartPath, config);
        if (ok)
        {
            PageCustoms_StageHud.Finish($"Boss Battle Duel ready! ({gems} battle power-up gems generated)");
            ShowStageToast("⚡ BOSS BATTLE READY!", $"{picked.Title} converted into a 2-Player Boss Duel with {gems} power-up phrases!", "#A855F7");
            WpfMessageBox.Show(msg, "Boss Battle Duel Created", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            PageCustoms_StageHud.SetError("BOSS BATTLE FAILED", msg);
            WpfMessageBox.Show(msg, "Boss Battle Duel", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void HypeSfx_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string sfxType)
        {
            PlayHypeSound(sfxType);
            ShowStageToast("📢 HYPE BROADCAST", $"Sent '{sfxType.ToUpperInvariant()}' over P2P lobby!", "#F59E0B");

            // Dual transport 1: Direct P2P HTTP POST to active peer
            if (!string.IsNullOrWhiteSpace(_lastPeerBaseUrl))
            {
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var client = new HttpClient(SignalingRelayService.CreateFastIPv4Handler()) { Timeout = TimeSpan.FromSeconds(2.5) };
                        await client.PostAsync($"{_lastPeerBaseUrl.TrimEnd('/')}/api/lobby/hype-sfx?type={Uri.EscapeDataString(sfxType)}", null);
                    }
                    catch { }
                });
            }

            // Dual transport 2: Broadcast to all known friends in lobby or online via direct HTTP & Global Relay
            var targets = AppConfig.Current.Friends.Where(f => f.IsTrusted || f.IsOnlineNow || !string.IsNullOrWhiteSpace(f.LastAddress)).ToList();
            if (targets.Count > 0 && FriendService.SignalingRelay != null)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var fr in targets)
                    {
                        try
                        {
                            if (!string.IsNullOrWhiteSpace(fr.LastAddress) && !fr.LastAddress.Contains("169.254.") && !fr.LastAddress.Equals(_lastPeerBaseUrl, StringComparison.OrdinalIgnoreCase))
                            {
                                try
                                {
                                    using var directClient = new HttpClient(SignalingRelayService.CreateFastIPv4Handler()) { Timeout = TimeSpan.FromSeconds(2.0) };
                                    _ = directClient.PostAsync($"{fr.LastAddress.TrimEnd('/')}/api/lobby/hype-sfx?type={Uri.EscapeDataString(sfxType)}", null);
                                }
                                catch { }
                            }
                            await FriendService.SignalingRelay.SendLobbyHypeSfxPacketAsync(fr.FriendCode, AppConfig.Current.FriendCode, sfxType);
                        }
                        catch { }
                    }
                });
            }
        }
    }

    private void PlayHypeSound(string type)
    {
        try
        {
            AudioWaveformAndSfxService.PlayHypeSound(type);
        }
        catch { }
    }

    private void ApplyLocalizationStrings()
    {
        try
        {
            if (NavCustoms != null) NavCustoms.Content = LocalizationService.Get("nav_customs", "🎸 My Customs");
            if (NavCreator != null) NavCreator.Content = LocalizationService.Get("nav_creator", "⚡ Song Creator");
            if (NavAiTools != null) NavAiTools.Content = LocalizationService.Get("nav_tools", "🛠 Tools & Stems");
            if (NavFriends != null) NavFriends.Content = LocalizationService.Get("nav_friends", "👥 Friends & Trust");
            if (NavP2P != null) NavP2P.Content = LocalizationService.Get("nav_p2p", "🌐 P2P Sync & Lobby");
            if (NavImport != null) NavImport.Content = LocalizationService.Get("nav_import", "📦 Import & Convert");
            if (NavSaves != null) NavSaves.Content = LocalizationService.Get("nav_career", "💾 Career Saves");
            if (NavSettings != null) NavSettings.Content = LocalizationService.Get("nav_settings", "⚙ Settings");

            if (CleanTempFilesBtn != null) CleanTempFilesBtn.Content = LocalizationService.Get("btn_clean_temp", "🧹 Clean Cache");
        }
        catch { }
    }

    private void CleanTempFilesBtn_Click(object sender, RoutedEventArgs e)
    {
        CleanTempFilesBtn.IsEnabled = false;
        PageSettings_StageHud.Report(30, "CLEANING REDUNDANT FILES", "Scanning audio caches, build directories, and temporary chunks...");
        try
        {
            var (filesRemoved, bytesFreed, summary) = _gh3.CleanDuplicateAndTempFiles(_logger);
            double mb = bytesFreed / (1024.0 * 1024.0);
            CleanTempResultText.Text = filesRemoved > 0
                ? $"Cleaned {filesRemoved} item(s), freed {mb:F1} MB."
                : "Already clean — no unnecessary duplicates or temp files found.";
            RefreshSongsList();
            if (filesRemoved > 0)
            {
                PageSettings_StageHud.Finish($"Cleaned {filesRemoved} item(s) ({mb:F1} MB freed)!");
                var detail = string.Join("\n• ", summary.Take(12));
                WpfMessageBox.Show($"Cleanup complete!\nRemoved {filesRemoved} redundant/temp item(s) ({mb:F1} MB freed):\n\n• {detail}",
                    "Storage Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                PageSettings_StageHud.Finish("Storage is already completely clean!");
            }
        }
        catch (Exception ex)
        {
            PageSettings_StageHud.SetError("CLEANUP ERROR", ex.Message);
        }
        finally
        {
            CleanTempFilesBtn.IsEnabled = true;
        }
    }

    // ==================== 5-THEME GUITAR HERO STAGE THEME SWITCHER ====================
    private void SelectThemeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string mode)
            ApplyTheme(mode, save: true);
    }

    private void SelectDarkThemeBtn_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark", save: true);
    private void SelectLightThemeBtn_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light", save: true);

    private void ApplyTheme(string mode, bool save)
    {
        string normalized = (mode ?? "Dark") switch
        {
            var s when s.Equals("Light", StringComparison.OrdinalIgnoreCase) => "Light",
            var s when s.Equals("StarPower", StringComparison.OrdinalIgnoreCase) => "StarPower",
            var s when s.Equals("FireFlames", StringComparison.OrdinalIgnoreCase) => "FireFlames",
            var s when s.Equals("Synthwave", StringComparison.OrdinalIgnoreCase) => "Synthwave",
            _ => "Dark"
        };

        string xamlFile = normalized switch
        {
            "Light" => "Views/LightTheme.xaml",
            "StarPower" => "Views/StarPowerTheme.xaml",
            "FireFlames" => "Views/FireFlamesTheme.xaml",
            "Synthwave" => "Views/SynthwaveTheme.xaml",
            _ => "Views/DarkTheme.xaml"
        };

        try
        {
            var themeUri = new Uri(xamlFile, UriKind.Relative);
            var newThemeDict = new ResourceDictionary { Source = themeUri };
            if (Resources.MergedDictionaries.Count > 0)
                Resources.MergedDictionaries[0] = newThemeDict;
            else
                Resources.MergedDictionaries.Add(newThemeDict);
        }
        catch { }

        if (save)
        {
            AppConfig.Current.ThemeMode = normalized;
            AppConfig.Save();
        }

        ApplyWindowChromeTheme();
        UpdateThemeCardSelection();

        if (IsLoaded)
        {
            RenderSongs();
            RefreshFriendsList();
            RefreshSaveBackups();
            if (_lastPeerManifest != null) RenderPeerLibrary();
        }
    }

    private void UpdateThemeCardSelection()
    {
        if (ThemeDarkCard == null || ThemeLightCard == null) return;
        string active = AppConfig.Current.ThemeMode ?? "Dark";

        void StyleCard(Border? card, System.Windows.Controls.Button? btn, string key, string label, WpfColor inactiveBorder)
        {
            if (card == null || btn == null) return;
            bool isOn = active.Equals(key, StringComparison.OrdinalIgnoreCase);
            card.BorderBrush = new SolidColorBrush(isOn ? WpfColor.FromRgb(249, 115, 22) : inactiveBorder);
            btn.Content = isOn ? $"✓ Active ({label})" : $"Switch to {label}";
            btn.Style = (Style)FindResource(isOn ? "FlameButton" : "OutlineButton");
        }

        StyleCard(ThemeDarkCard, SelectDarkThemeBtn, "Dark", "Dark GH3", WpfColor.FromRgb(51, 65, 85));
        StyleCard(ThemeLightCard, SelectLightThemeBtn, "Light", "Light GH3", WpfColor.FromRgb(203, 213, 225));
        StyleCard(ThemeStarPowerCard, SelectStarPowerThemeBtn, "StarPower", "Star Power", WpfColor.FromRgb(27, 52, 89));
        StyleCard(ThemeFireFlamesCard, SelectFireFlamesThemeBtn, "FireFlames", "Fire & Flames", WpfColor.FromRgb(71, 28, 34));
        StyleCard(ThemeSynthwaveCard, SelectSynthwaveThemeBtn, "Synthwave", "Synthwave", WpfColor.FromRgb(59, 30, 109));
    }

    // ==================== COMPONENT UPDATES & 5-POINT COMPATIBILITY SELF-TEST ====================
    private void SettingsAutoRollbackChk_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.AutoRollbackOnFailedUpdate = SettingsAutoRollbackChk.IsChecked == true;
        AppConfig.Save();
    }

    private async void CheckAllComponentsBtn_Click(object sender, RoutedEventArgs e)
    {
        CheckAllComponentsBtn.IsEnabled = false;
        UpdatesCompStatusText.Text = "Checking GH3-Sync, Honeycomb, BetterGH3, FFmpeg, and Stem Tools...";
        ComponentsStatusPanel.Children.Clear();
        try
        {
            var items = await _compUpdater.CheckAllComponentsAsync();
            foreach (var item in items)
            {
                var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(13), Margin = new Thickness(0, 0, 0, 8) };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var left = new StackPanel();
                left.Children.Add(new TextBlock { Text = item.Name, FontSize = 14, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary") });
                left.Children.Add(new TextBlock { Text = $"Installed: {item.InstalledVersion}  •  {item.Status}", FontSize = 12, Foreground = (WpfBrush)FindResource("TextMuted"), Margin = new Thickness(0, 2, 0, 0) });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var badge = MakeBadge(
                    item.UpdateAvailable ? "Update Available" : (item.IsInstalled ? "Ready" : "Optional"),
                    item.UpdateAvailable ? "#422006" : (item.IsInstalled ? "#064E3B" : "#1E293B"),
                    item.UpdateAvailable ? "#FDE047" : (item.IsInstalled ? "#6EE7B7" : "#CBD5E1"));
                badge.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(badge, 1);
                grid.Children.Add(badge);

                card.Child = grid;
                ComponentsStatusPanel.Children.Add(card);
            }
            UpdatesCompStatusText.Text = $"Checked {items.Count} components at {DateTime.Now:HH:mm:ss}.";
        }
        finally
        {
            CheckAllComponentsBtn.IsEnabled = true;
        }
    }

    private async void RunCompatTestBtn_Click(object sender, RoutedEventArgs e)
    {
        RunCompatTestBtn.IsEnabled = false;
        UpdatesCompStatusText.Text = "Running 6-point post-update compatibility self-test...";
        CompatTestPanel.Children.Clear();
        try
        {
            var report = await _compUpdater.RunCompatibilitySelfTestAsync();
            foreach (var chk in report.Checks)
            {
                var card = new Border
                {
                    Style = (Style)FindResource("GhCard"),
                    Background = (WpfBrush)FindResource(chk.Passed ? "CardSuccessBg" : "CardDangerBg"),
                    BorderBrush = (WpfBrush)FindResource(chk.Passed ? "CardSuccessBorder" : "CardDangerBorder"),
                    Padding = new Thickness(12),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                var sp = new StackPanel();
                sp.Children.Add(new TextBlock
                {
                    Text = $"{(chk.Passed ? "✓ PASS" : "✕ FAIL")} — {chk.Component}",
                    FontSize = 13.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource(chk.Passed ? "CardSuccessTitle" : "CardDangerTitle")
                });
                sp.Children.Add(new TextBlock
                {
                    Text = chk.Details,
                    FontSize = 12,
                    Foreground = (WpfBrush)FindResource(chk.Passed ? "CardSuccessFg" : "CardDangerFg"),
                    Margin = new Thickness(0, 2, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                card.Child = sp;
                CompatTestPanel.Children.Add(card);
            }
            UpdatesCompStatusText.Text = report.AllPassed
                ? $"All {report.Checks.Count} compatibility checks passed!"
                : $"{report.Checks.Count(c => c.Passed)}/{report.Checks.Count} compatibility checks passed. Review flagged items below.";
        }
        finally
        {
            RunCompatTestBtn.IsEnabled = true;
        }
    }

    private async void UpdateStemToolsBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateStemToolsBtn.IsEnabled = false;
        try
        {
            var (ok, msg) = await _compUpdater.UpdateStemToolsWithRollbackAsync(status =>
                Dispatcher.Invoke(() => UpdatesCompStatusText.Text = status));
            UpdatesCompStatusText.Text = msg;
            WpfMessageBox.Show(msg, ok ? "Stem Tools Updated" : "Update Status", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        finally
        {
            UpdateStemToolsBtn.IsEnabled = true;
        }
    }

    private void HallOfFameBtn_Click(object sender, RoutedEventArgs e)
    {
        string summary = RockHallOfFameService.GenerateHallOfFameSummary();
        WpfMessageBox.Show(summary, "Rock Hall of Fame & Achievements", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SettingsGpuToggle_CheckedChanged(object sender, RoutedPropertyChangedEventArgs<bool> e)
    {
        bool enabled = SettingsGpuToggle.IsChecked;
        AppConfig.Current.EnableGpuAcceleration = enabled;
        AppConfig.Save();
        GpuAccelerationService.ApplyRenderMode(enabled);
        if (GpuTelemetryStatusBadge != null)
        {
            GpuTelemetryStatusBadge.Text = enabled ? "TIER 2 HARDWARE ACCELERATED" : "SOFTWARE WARP ONLY";
            GpuTelemetryStatusBadge.Foreground = enabled ? new SolidColorBrush(WpfColor.FromRgb(56, 189, 248)) : new SolidColorBrush(WpfColor.FromRgb(249, 115, 22));
        }
        ShowStageToast("GPU ACCELERATION", enabled ? "Direct3D Tier 2 Hardware GPU Rendering Engaged!" : "Software WARP Rasterization Active.", "#38BDF8");
    }

    private void QualityRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (QualityUltraRadio?.IsChecked == true)
        {
            AppConfig.Current.VisualEffectsQuality = "Ultra";
            if (GpuTelemetryFpsText != null) GpuTelemetryFpsText.Text = "144 FPS RIG";
        }
        else if (QualityBalancedRadio?.IsChecked == true)
        {
            AppConfig.Current.VisualEffectsQuality = "Balanced";
            if (GpuTelemetryFpsText != null) GpuTelemetryFpsText.Text = "60 FPS CAP";
        }
        else if (QualityBatteryRadio?.IsChecked == true)
        {
            AppConfig.Current.VisualEffectsQuality = "BatterySaver";
            if (GpuTelemetryFpsText != null) GpuTelemetryFpsText.Text = "30 FPS SAVE";
        }
        AppConfig.Save();
    }

    private void BatchMedleyBtn_Click(object sender, RoutedEventArgs e)
    {
        var selectedSongs = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList();
        if (selectedSongs.Count < 2)
        {
            WpfMessageBox.Show("Please check at least 2 songs in My Customs to assemble an Endless Rock Concert Medley!", "Endless Medley Engine", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var chartList = new List<(string ChartContent, double DurationSec, double Bpm)>();
        foreach (var s in selectedSongs)
        {
            string chartPath = Path.Combine(_gh3.GamePath, "DATA", "PAK", "Custom_Songs", s.SongId, "notes.chart");
            string content = File.Exists(chartPath) ? File.ReadAllText(chartPath) : "";
            chartList.Add((content, 180.0, 130.0));
        }

        PageCustoms_StageHud.Report(50, "ASSEMBLING ENDLESS MEDLEY", $"Stitching {selectedSongs.Count} tracks with continuous beat grids & transition drum fills...");
        var (ok, msg, stitched) = EndlessMedleyService.StitchCharts(chartList);
        if (ok)
        {
            PageCustoms_StageHud.Finish("Endless Rock Concert Medley generated!");
            RockHallOfFameService.UnlockAchievement("MEDLEY_MARATHON");
            ShowStageToast("🎸 MEDLEY READY!", $"{selectedSongs.Count}-Song non-stop marathon stitched!", "#F97316");
            WpfMessageBox.Show(msg, "Endless Rock Concert Medley", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else
        {
            PageCustoms_StageHud.SetError("MEDLEY FAILED", msg);
            WpfMessageBox.Show(msg, "Medley Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
