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
using System.Windows.Navigation;
using System.Windows.Threading;

using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = System.Windows.MessageBox;

namespace GH3_Sync.Views;

internal record QueuedSongItem(SongCreationRequest Meta, string AudioPath, string? ChartPath);

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
    private readonly int _port;

    private readonly DispatcherTimer _stageAnimTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
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

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

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

        _tunnel.TunnelStateChanged += _ => Dispatcher.Invoke(RefreshMeshAndTunnelStatus);
        _careerScores.StartGh3ExitWatcher(scan => Dispatcher.Invoke(() =>
        {
            if (scan.ScoresUpdated > 0)
            {
                RefreshSongsList();
                ShowStageToast("🏆 NEW CAREER SCORES!", scan.Summary, "#22C55E");
            }
        }));

        _tempSession.SessionCleanedUp += msg => Dispatcher.Invoke(() =>
        {
            RefreshSongsList();
            RefreshTempSessionBanner();
            _trayIcon?.ShowBalloonTip(3000, "Temporary Lobby Session Cleaned", msg, WinForms.ToolTipIcon.Info);
        });

        _friends.OnFriendRequestReceived += req => Dispatcher.Invoke(() =>
        {
            RefreshFriendsList();
            AudioWaveformAndSfxService.PlayStarPowerChime();
            ShowStageToast("📩 NEW FRIEND REQUEST!", $"{req.Username} ({req.FriendCode}) sent you a friend request!", "#F97316");
        });

        _friends.OnFriendRequestAccepted += friend => Dispatcher.Invoke(() =>
        {
            RefreshFriendsList();
            RefreshTrustedFriendDropdown();
            AudioWaveformAndSfxService.PlayFcFanfare();
            ShowStageToast("🎉 FRIEND ACCEPTED!", $"{friend.Username} accepted your friend request! You can now sync songs!", "#22C55E");
        });

        _playerTimer.Tick += PlayerTimer_Tick;
        _player.MediaEnded += (_, _) => { _isPlayerPlaying = false; PlayerPlayPauseBtn.Content = "▶"; };

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        InitializeTrayIcon();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        ApplyWindowChromeTheme();
    }

    private void ApplyWindowChromeTheme()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int dark = string.Equals(AppConfig.Current.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            int cornerPref = 2;
            DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Title = $"GH3-Sync v{UpdateService.CurrentAppVersion}";
        if (HeaderVersionBadge != null) HeaderVersionBadge.Text = $"v{UpdateService.CurrentAppVersion}";

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
    }

    private void AnimatePanelEntrance(UIElement panel)
    {
        if (!AppConfig.Current.EnableUiAnimations) return;
        try
        {
            var tt = new TranslateTransform(0, 10);
            panel.RenderTransform = tt;
            panel.Opacity = 0;
            var dur = TimeSpan.FromMilliseconds(190);
            var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
            panel.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, dur) { EasingFunction = ease });
            tt.BeginAnimation(TranslateTransform.YProperty, new System.Windows.Media.Animation.DoubleAnimation(10, 0, dur) { EasingFunction = ease });
        }
        catch { }
    }

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        bool ctrl = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
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
        StopAudioPreview();
        try { _tunnel?.StopTunnel(); } catch { }
        TunnelAndMeshService.KillAllOrphanCloudflaredProcesses();
        try { _trayIcon?.Dispose(); } catch { }
        System.Windows.Application.Current.Shutdown();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        ExitApplication();
    }

    // ==================== LAUNCH GH3 & HYPERLINKS ====================
    private void LaunchGh3Btn_Click(object sender, RoutedEventArgs e)
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
            // 100ms Pre-Flight Health Check before launching Guitar Hero III
            if (!string.IsNullOrWhiteSpace(_gh3.GamePath))
            {
                try { CustomsPakService.RepairCustomsPakIfCorrupted(_gh3.GamePath, _logger); } catch { }
            }
            bool coreOk = File.Exists(Path.Combine(_gh3.GamePath ?? "", "PLUGINS", "core.dll"));
            ShowStageToast("🎸 LAUNCHING GUITAR HERO III!",
                $"{(coreOk ? "BetterGH3 core.dll Verified" : "Vanilla GH3")} • {_allSongs.Count} Customs Armed • Career Score Watcher Active",
                "#22C55E");
            AudioWaveformAndSfxService.PlayCompilePowerChord();

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
                AboutUpdateStatus.Text = $"Up to date (v{res.CurrentVersion})";
                if (!silentIfUpToDate)
                {
                    WpfMessageBox.Show($"You are running the latest version of GH3-Sync (v{res.CurrentVersion}).",
                        "Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
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
        ExportSelectedBtn.Content = $"Export Selected ({_selectedExportIds.Count})";

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
        bool hasAudio = !string.IsNullOrEmpty(Gh3Service.FindSongAudioPath(song));
        double sizeMb = song.TotalSizeBytes / (1024.0 * 1024.0);

        var baseBorder = song.IsTemporarySessionSong
            ? (WpfBrush)FindResource("CardInfoBorder")
            : (song.IsInLobbySetlist ? (WpfBrush)FindResource("AccentFlame") : (WpfBrush)FindResource("BorderSubtle"));

        var card = new Border
        {
            Background = (WpfBrush)FindResource("BgCard"),
            BorderBrush = baseBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 9)
        };
        card.MouseEnter += (_, _) =>
        {
            card.Background = (WpfBrush)FindResource("BgCardHover");
            if (AppConfig.Current.EnableUiAnimations)
                card.BorderBrush = (WpfBrush)FindResource("AccentFlame");
        };
        card.MouseLeave += (_, _) =>
        {
            card.Background = (WpfBrush)FindResource("BgCard");
            card.BorderBrush = baseBorder;
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Select Checkbox + Favorite Star + Album Art Thumbnail
        var leftTools = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var selChk = new System.Windows.Controls.CheckBox
        {
            IsChecked = _selectedExportIds.Contains(song.SongId),
            ToolTip = "Select for Pack Export",
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        selChk.Checked += (_, _) => { _selectedExportIds.Add(song.SongId); ExportSelectedBtn.Content = $"Export Selected ({_selectedExportIds.Count})"; };
        selChk.Unchecked += (_, _) => { _selectedExportIds.Remove(song.SongId); ExportSelectedBtn.Content = $"Export Selected ({_selectedExportIds.Count})"; };

        var favBtn = new System.Windows.Controls.Button
        {
            Content = song.IsFavorite ? "★" : "☆",
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = new SolidColorBrush(song.IsFavorite ? WpfColor.FromRgb(250, 204, 21) : WpfColor.FromRgb(100, 116, 139)),
            FontSize = 16,
            Cursor = System.Windows.Input.Cursors.Hand,
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Toggle Favorite"
        };
        favBtn.Click += (_, _) => { AppConfig.ToggleFavorite(song.SongId); RefreshSongsList(); };

        leftTools.Children.Add(selChk);
        leftTools.Children.Add(favBtn);

        // Optional Real Album Art Thumbnail (non-locking BitmapCacheOption.OnLoad)
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
                    Width = 44,
                    Height = 44,
                    CornerRadius = new CornerRadius(7),
                    Margin = new Thickness(6, 0, 2, 0),
                    BorderBrush = (WpfBrush)FindResource("BorderSubtle"),
                    BorderThickness = new Thickness(1),
                    ClipToBounds = true,
                    Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }
                };
                leftTools.Children.Add(artBorder);
            }
            catch { }
        }

        Grid.SetColumn(leftTools, 0);
        grid.Children.Add(leftTools);

        // Info
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(song.Title) ? song.SongId : song.Title,
            FontSize = 14.5,
            FontWeight = FontWeights.Bold,
            Foreground = (WpfBrush)FindResource("TextPrimary")
        });
        var metaLine = $"{(string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist)}  •  {song.SongId}  •  {sizeMb:F1} MB";
        if (song.ExpertNoteCount > 0) metaLine += $"  •  {song.ExpertNoteCount:N0} notes";
        infoStack.Children.Add(new TextBlock
        {
            Text = metaLine,
            FontSize = 11.5,
            Foreground = (WpfBrush)FindResource("TextMuted"),
            Margin = new Thickness(0, 2, 0, 5)
        });

        var badgeRow = new WrapPanel();
        if (song.IsCloneHeroImport)
            badgeRow.Children.Add(MakeBadge("Imported (Clone Hero)", "#1E3A8A", "#BFDBFE"));
        else if (isOnogami)
            badgeRow.Children.Add(MakeBadge("Made with GH3-Sync", "#2E1065", "#DDD6FE"));
        else
            badgeRow.Children.Add(MakeBadge("Synced / Imported", "#172554", "#93C5FD"));

        badgeRow.Children.Add(MakeBadge(song.IsProject ? "Project Only" : "In-Game Ready", song.IsProject ? "#422006" : "#064E3B", song.IsProject ? "#FDE047" : "#6EE7B7"));
        if (!string.IsNullOrWhiteSpace(song.AudioModeTag))
        {
            var modeBg = song.AudioModeTag.Contains("Multi-Stem", StringComparison.OrdinalIgnoreCase) ? "#0F3D3E" : "#1E293B";
            var modeFg = song.AudioModeTag.Contains("Multi-Stem", StringComparison.OrdinalIgnoreCase) ? "#5EEAD4" : "#CBD5E1";
            badgeRow.Children.Add(MakeBadge(song.AudioModeTag, modeBg, modeFg));
        }
        if (song.HasCoopBass)
            badgeRow.Children.Add(MakeBadge("Co-Op Bass", "#312E81", "#C7D2FE"));
        if (song.IsInLobbySetlist)
            badgeRow.Children.Add(MakeBadge("Lobby Auto-Sync", "#431407", "#FDBA74"));
        if (song.IsTemporarySessionSong)
            badgeRow.Children.Add(MakeBadge("Temporary Session", "#1E3A8A", "#93C5FD"));

        // Personal Best Score / Stars / FC Badge
        var bestScore = AppConfig.GetHighScore(song.SongId);
        if (bestScore != null && bestScore.BestScore > 0)
        {
            var starStr = new string('★', Math.Clamp(bestScore.Stars, 1, 5));
            var scoreTxt = bestScore.IsFullCombo
                ? $"{starStr} FC • {bestScore.BestScore:N0}"
                : $"{starStr} {bestScore.BestPercent:F0}% • {bestScore.BestScore:N0}";
            badgeRow.Children.Add(MakeBadge(scoreTxt, bestScore.IsFullCombo ? "#422006" : "#064E3B", bestScore.IsFullCombo ? "#FDE047" : "#6EE7B7"));
        }

        // Custom Colored Song Tags
        foreach (var tag in AppConfig.GetSongTags(song.SongId))
            badgeRow.Children.Add(MakeBadge($"#{tag}", "#3B0764", "#E9D5FF"));

        // Mini Note Intensity Sparkline + 5-Level Difficulty Tier Meter (cached per chart!)
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
                var sparkWrap = new StackPanel
                {
                    Orientation = System.Windows.Controls.Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(4, 0, 4, 0),
                    ToolTip = $"Difficulty Tier {spark.Tier}/5  •  Peak {spark.PeakNps:F0} Notes/Sec (Note Intensity Timeline)"
                };
                sparkWrap.Children.Add(new TextBlock
                {
                    Text = string.Concat(Enumerable.Repeat("🔥", Math.Clamp(spark.Tier, 1, 5))),
                    FontSize = 9.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 5, 0)
                });
                var miniCanvas = new Canvas { Width = 54, Height = 14, VerticalAlignment = VerticalAlignment.Center };
                for (int bi = 0; bi < spark.Buckets.Length; bi++)
                {
                    double val = spark.Buckets[bi];
                    double bh = Math.Max(2.0, val * 14.0);
                    var col = val > 0.72 ? WpfColor.FromRgb(239, 68, 68) : val > 0.42 ? WpfColor.FromRgb(234, 179, 8) : WpfColor.FromRgb(34, 197, 94);
                    var bar = new System.Windows.Shapes.Rectangle { Width = 2.4, Height = bh, Fill = new SolidColorBrush(col), RadiusX = 1, RadiusY = 1 };
                    Canvas.SetLeft(bar, bi * 3.3);
                    Canvas.SetTop(bar, 14.0 - bh);
                    miniCanvas.Children.Add(bar);
                }
                sparkWrap.Children.Add(miniCanvas);
                badgeRow.Children.Add(sparkWrap);
            }
        }

        infoStack.Children.Add(badgeRow);
        Grid.SetColumn(infoStack, 1);
        grid.Children.Add(infoStack);

        // Action Buttons
        var btnRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        if (song.IsTemporarySessionSong)
        {
            var keepBtn = new System.Windows.Controls.Button
            {
                Content = "Keep Song",
                Style = (Style)FindResource("GreenButton"),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = "Keep this temporary lobby song permanently in your library"
            };
            keepBtn.Click += (_, _) =>
            {
                _tempSession.KeepSongPermanently(song.SongId);
                RefreshSongsList();
                RefreshTempSessionBanner();
            };
            btnRow.Children.Add(keepBtn);
        }

        // Lobby Sync Toggle (10-song cap!)
        var lobbyBtn = new System.Windows.Controls.Button
        {
            Content = song.IsInLobbySetlist ? "✓ Lobby" : "+ Lobby",
            Style = (Style)FindResource(song.IsInLobbySetlist ? "FlameButton" : "OutlineButton"),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 5, 0),
            ToolTip = "Include in your Lobby Auto-Sync Setlist (up to 10 songs auto-shared with friends)"
        };
        lobbyBtn.Click += (_, _) =>
        {
            var (_, msg) = AppConfig.ToggleLobbySetlist(song.SongId);
            if (msg.Contains("limit reached", StringComparison.OrdinalIgnoreCase))
                WpfMessageBox.Show(msg, "Lobby Setlist Cap", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshSongsList();
        };
        btnRow.Children.Add(lobbyBtn);

        if (hasAudio)
        {
            var playBtn = new System.Windows.Controls.Button
            {
                Content = "▶",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = "Preview Song Audio / Guitar Stem"
            };
            playBtn.Click += (_, _) =>
            {
                var audio = Gh3Service.FindSongAudioPath(song);
                if (!string.IsNullOrEmpty(audio)) PlayAudioPreview(audio, song.Title, song, false);
            };
            btnRow.Children.Add(playBtn);
        }

        if (song.IsProject || !song.IsCompiled)
        {
            var compileBtn = new System.Windows.Controls.Button
            {
                Content = "Compile",
                Style = (Style)FindResource("GreenButton"),
                Padding = new Thickness(10, 5, 10, 5),
                Margin = new Thickness(0, 0, 5, 0)
            };
            compileBtn.Click += async (_, _) => await CompileSongCardAsync(song, compileBtn);
            btnRow.Children.Add(compileBtn);
        }

        if (hasProject)
        {
            var chartFile = ChartDoctorService.FindChartFileInProject(song.ProjectDirectory);
            if (!string.IsNullOrEmpty(chartFile))
            {
                var hwBtn = new System.Windows.Controls.Button
                {
                    Content = "🎸 Highway",
                    Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = "Watch, Play (XInput Guitar / Keyboard), or Quick-Edit this song's chart"
                };
                hwBtn.Click += (_, _) => OpenHighwayPreview(song.Title, song.Artist, chartFile, Gh3Service.FindSongAudioPath(song), song.ProjectDirectory, song.SongId);
                btnRow.Children.Add(hwBtn);

                var docBtn = new System.Windows.Controls.Button
                {
                    Content = "🩺 Doctor",
                    Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = "Run Chart Doctor: check & auto-fix missing difficulties, Star Power, Practice sections, open notes & sync offset"
                };
                docBtn.Click += (_, _) => OpenChartDoctorDialog(song);
                btnRow.Children.Add(docBtn);
            }

            if (hasAudio)
            {
                var stemsBtn = new System.Windows.Controls.Button
                {
                    Content = "🎛 Stems",
                    Style = (Style)FindResource("OutlineButton"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = "Export Guitarless Backing Track, Solo Guitar Isolation, or 75% Slowdown Practice MP3"
                };
                stemsBtn.Click += (_, _) => OpenPracticeStemExportDialog(song);
                btnRow.Children.Add(stemsBtn);
            }

            var artBtn = new System.Windows.Controls.Button
            {
                Content = "🎨 Art",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(8, 5, 8, 5),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = "Auto-fetch high-res 600x600 album art from iTunes"
            };
            artBtn.Click += async (_, _) => await FetchSongCoverArtAsync(song, artBtn);
            btnRow.Children.Add(artBtn);

            if (isOnogami && SongCreatorService.HasAiCache(song.ProjectDirectory))
            {
                var fastBtn = new System.Windows.Controls.Button
                {
                    Content = "⚡ Fast Re-Chart",
                    Style = (Style)FindResource("GreenButton"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = "5-second Instant Re-Chart from cached AI analysis (tweak Casual/Standard/Shredder or Sync Offset without re-running Demucs!)"
                };
                fastBtn.Click += (_, _) => OpenFastRechartDialog(song);
                btnRow.Children.Add(fastBtn);
            }

            if (isOnogami && hasAudio)
            {
                var rechartBtn = new System.Windows.Controls.Button
                {
                    Content = "Re-Chart AI v2",
                    Style = (Style)FindResource("BlueButton"),
                    Padding = new Thickness(8, 5, 8, 5),
                    Margin = new Thickness(0, 0, 5, 0),
                    ToolTip = "Load this song's audio into ONOGAMI Song Creator to re-chart with the latest AI Engine v2"
                };
                rechartBtn.Click += async (_, _) => await RechartSongInCreatorAsync(song);
                btnRow.Children.Add(rechartBtn);
            }

            var editBtn = new System.Windows.Controls.Button
            {
                Content = "Edit",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 5, 0),
                ToolTip = "Edit Song Info & Guitar/Band Volume Mixer"
            };
            editBtn.Click += (_, _) => OpenEditSongDialog(song);
            btnRow.Children.Add(editBtn);

            var folderBtn = new System.Windows.Controls.Button
            {
                Content = "Folder",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(9, 5, 9, 5),
                Margin = new Thickness(0, 0, 5, 0)
            };
            folderBtn.Click += (_, _) => OpenFolderInExplorer(song.ProjectDirectory);
            btnRow.Children.Add(folderBtn);
        }

        var tagBtn = new System.Windows.Controls.Button
        {
            Content = "# Tag",
            Style = (Style)FindResource("OutlineButton"),
            Padding = new Thickness(8, 5, 8, 5),
            Margin = new Thickness(0, 0, 5, 0),
            ToolTip = "Assign custom tags (Warmup, Shred, Party, Chill, Co-Op, Live)"
        };
        tagBtn.Click += (_, _) => OpenTagPickerDialog(song);
        btnRow.Children.Add(tagBtn);

        var exportBtn = new System.Windows.Controls.Button
        {
            Content = "Export",
            Style = (Style)FindResource("OutlineButton"),
            Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 5, 0)
        };
        exportBtn.Click += (_, _) => ExportSingleSong(song);
        btnRow.Children.Add(exportBtn);

        var deleteBtn = new System.Windows.Controls.Button
        {
            Content = "Delete",
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(10, 5, 10, 5)
        };
        deleteBtn.Click += (_, _) => ConfirmAndDeleteSong(song);
        btnRow.Children.Add(deleteBtn);

        Grid.SetColumn(btnRow, 2);
        grid.Children.Add(btnRow);

        card.Child = grid;
        return card;
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

        try
        {
            SetlistPosterService.ExportSetlistPosterPng(dlg.FileName, _friends.Profile.Username, _friends.Profile.FriendCode, title, list);
            WpfMessageBox.Show($"Saved high-res Concert Tour Setlist Poster (1200×1600) to:\n{dlg.FileName}\n\nReady to drop straight into Discord!", "Setlist Poster Exported", MessageBoxButton.OK, MessageBoxImage.Information);
            OpenFolderInExplorer(Path.GetDirectoryName(dlg.FileName)!);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Could not generate poster: {ex.Message}", "Poster Error", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void VolumeSoundcheckBtn_Click(object sender, RoutedEventArgs e)
    {
        VolumeSoundcheckBtn.IsEnabled = false;
        try
        {
            var reports = await _practiceAudio.ScanLibraryLoudnessAsync(_allSongs);
            if (reports.Count == 0)
            {
                WpfMessageBox.Show("No local project audio files found to soundcheck.", "Volume Soundcheck", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var needsFix = reports.Where(r => r.NeedsNormalization).ToList();
            var lines = reports.Take(12).Select(r => $"• {r.Song.Title}: {r.StatusLabel}");
            var summary = $"Scanned {reports.Count} custom song(s):\n\n" + string.Join("\n", lines) +
                          (reports.Count > 12 ? $"\n...and {reports.Count - 12} more." : "");

            if (needsFix.Count == 0)
            {
                WpfMessageBox.Show(summary + "\n\nAll scanned tracks are already nicely balanced near -14 LUFS!", "🔊 Volume Soundcheck — All Balanced", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var ask = WpfMessageBox.Show(
                summary + $"\n\n{needsFix.Count} song(s) are outside target GH3 loudness (-14 LUFS).\nNormalize & recompile those {needsFix.Count} song(s) now?",
                "🔊 Library Volume Soundcheck", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask != MessageBoxResult.Yes) return;

            var (_, _, resultMsg) = await _practiceAudio.NormalizeSongsBatchAsync(needsFix);
            RefreshSongsList();
            WpfMessageBox.Show(resultMsg, "Volume Soundcheck Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            VolumeSoundcheckBtn.IsEnabled = true;
        }
    }

    private void AutoHealLibraryBtn_Click(object sender, RoutedEventArgs e)
    {
        var report = _libraryHealth.ScanLibrary();
        var details = string.Join("\n", report.Findings.Select(f => $"[{(f.Severity == "OK" ? "✓ OK" : f.Severity.ToUpperInvariant())}] {f.Category}: {f.Message}"));
        if (report.IssueCount == 0)
        {
            WpfMessageBox.Show(report.Summary + "\n\n" + details, "🩺 Library Health Check — All Healthy", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ask = WpfMessageBox.Show(report.Summary + "\n\n" + details + "\n\nRun 1-click Auto-Heal now? (Creates a Career Save backup first.)",
            "🩺 Library Health Scanner", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (ask == MessageBoxResult.Yes)
        {
            var (ok, msg) = _libraryHealth.AutoHealLibrary();
            RefreshSongsList();
            WpfMessageBox.Show(msg, ok ? "Auto-Heal Complete" : "Auto-Heal Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
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

    private async Task FetchSongCoverArtAsync(CustomSongInfo song, System.Windows.Controls.Button btn)
    {
        btn.IsEnabled = false;
        btn.Content = "...";
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
            btn.IsEnabled = true;
            btn.Content = "🎨 Art";
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
        sp.Children.Add(new TextBlock { Text = $"🎛 Practice Audio Exporter — {song.Title}", FontSize = 15.5, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 6) });
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

    private async Task RechartSongInCreatorAsync(CustomSongInfo song)
    {
        var audio = Gh3Service.FindSongAudioPath(song, preferGuitarStem: false);
        if (string.IsNullOrWhiteSpace(audio) || !File.Exists(audio))
        {
            WpfMessageBox.Show("Source audio file was not found in this song's project folder.", "Re-Chart AI v2", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

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
        CrProgressLabel.Text = "Fetching album art & track metadata...";
        try
        {
            var res = await _albumMeta.FetchMetadataAndCoverAsync(CrTitleBox.Text.Trim(), CrArtistBox.Text.Trim());
            if (res.Found)
            {
                if (!string.IsNullOrWhiteSpace(res.Title)) CrTitleBox.Text = res.Title;
                if (!string.IsNullOrWhiteSpace(res.Artist)) CrArtistBox.Text = res.Artist;
                if (!string.IsNullOrWhiteSpace(res.Album)) CrAlbumBox.Text = res.Album;
                if (res.Year > 0) CrYearBox.Text = res.Year.ToString();
                if (!string.IsNullOrWhiteSpace(res.Genre)) CrGenreBox.Text = res.Genre;
                CrProgressLabel.Text = res.Message;
            }
            else CrProgressLabel.Text = res.Message;
        }
        finally
        {
            FetchCreatorMetaBtn.IsEnabled = true;
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
            UseCachedAiAnalysis = true
        };
    }

    private bool ConfirmDuplicateHandlingIfNeeded(string title, string artist)
    {
        var existing = _gh3.FindDuplicateInstalledSong(title, artist);
        if (existing == null) return true;

        var choice = WpfMessageBox.Show(
            $"A song matching '{existing.Title}' by '{existing.Artist}' ({existing.SongId}) is already installed in Guitar Hero III.\n\n" +
            "• Click YES to replace the existing version cleanly (recommended — removes the old one first).\n" +
            "• Click NO to keep both versions.\n" +
            "• Click CANCEL to abort.",
            "Duplicate Song Detected",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        if (choice == MessageBoxResult.Cancel) return false;
        if (choice == MessageBoxResult.Yes)
        {
            StopAudioPreview();
            var del = _gh3.DeleteCustomSong(existing, deleteProjectFolder: true, _saves, _logger);
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
                RefreshSongsList();
            }
            else
            {
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

        var (ok, msg) = await _demucs.InstallAiEngineAsync((pct, step) => Dispatcher.Invoke(() =>
        {
            AiInstallProgressBar.Value = pct;
            AiInstallStepText.Text = $"[{pct}%] {step}";
        }));

        InstallAiBtn.IsEnabled = true;
        UninstallAiBtn.IsEnabled = true;
        RefreshToolsAndAiStatus();
        WpfMessageBox.Show(msg, ok ? "Stem Tools Installed" : "Installation Note", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void UninstallAiEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show("Remove Demucs & Basic Pitch files to free disk space?", "Uninstall Stem Tools", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var (ok, msg) = _demucs.UninstallAiEngine();
        RefreshToolsAndAiStatus();
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

    private void SaveUsernameBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileUsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _friends.SaveProfile(name);
        SidebarProfileName.Text = name;
        WpfMessageBox.Show($"Display name updated to '{name}'.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var rawCode = (AddFriendCodeBox.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(rawCode))
        {
            ShowStageToast("⚠️ FRIEND CODE REQUIRED", "Please enter a Friend Code (e.g. GH3-XXXX-XXXX).", "#EF4444");
            return;
        }

        var directAddr = (DirectAddressBox?.Text ?? "").Trim();
        SendFriendRequestBtn.IsEnabled = false;
        SendFriendRequestBtn.Content = "📡 Delivering Request...";

        try
        {
            var res = await _friends.SendFriendRequestAsync(rawCode, string.IsNullOrWhiteSpace(directAddr) ? null : directAddr);

            if (res.Success && res.ReceivedByPeer)
            {
                ShowStageToast("✓ REQUEST DELIVERED!", res.Message, "#22C55E");
                AudioWaveformAndSfxService.PlayStarPowerChime();
                AddFriendCodeBox.Text = "";
                if (DirectAddressBox != null) DirectAddressBox.Text = "";
                AutoDetectStatusPill.Visibility = Visibility.Collapsed;
                RefreshFriendsList();
            }
            else
            {
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
            SendFriendRequestBtn.Content = "🚀 Send Friend Request";
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
                left.Children.Add(new TextBlock
                {
                    Text = $"{req.Username}  ({req.FriendCode})",
                    FontSize = 14.5,
                    FontWeight = FontWeights.Bold,
                    Foreground = (WpfBrush)FindResource("TextPrimary")
                });

                var addrText = string.IsNullOrWhiteSpace(req.Address) ? "LAN / Mesh" : req.Address;
                left.Children.Add(new TextBlock
                {
                    Text = $"Incoming request • From: {addrText} • Received: {req.CreatedAt:g}",
                    FontSize = 12,
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 186, 116)),
                    Margin = new Thickness(0, 2, 0, 0)
                });
                Grid.SetColumn(left, 0);
                grid.Children.Add(left);

                var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

                var acceptTrustBtn = new System.Windows.Controls.Button
                {
                    Content = "✓ Accept & Trust",
                    Style = (Style)FindResource("GreenButton"),
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
                left.Children.Add(new TextBlock
                {
                    Text = $"✓ Delivery confirmed to peer • Sent: {req.CreatedAt:g} • Waiting for player to accept...",
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

        // ==================== 3. ACTIVE FRIENDS ====================
        var friendsHeader = new TextBlock
        {
            Text = $"👥 Active Friends ({list.Count})",
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
                    Text = "No active friends yet. Enter a friend's code above to send a verified request, or share your Friend Code so friends can add you!",
                    Foreground = (WpfBrush)FindResource("TextMuted")
                }
            });
            return;
        }

        foreach (var f in list)
        {
            var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 10) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel();
            left.Children.Add(new TextBlock { Text = $"{f.Username}  ({f.FriendCode})", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary") });
            left.Children.Add(new TextBlock
            {
                Text = f.IsTrusted ? "Trusted Friend (Can Download Songs & Auto-Sync)" : "Regular Friend (PIN View-Only — Downloads Blocked)",
                FontSize = 12,
                Foreground = (WpfBrush)FindResource(f.IsTrusted ? "CardSuccessTitle" : "TextMuted")
            });
            if (!string.IsNullOrWhiteSpace(f.LastAddress))
            {
                left.Children.Add(new TextBlock
                {
                    Text = $"Last Connection: {f.LastAddress}",
                    FontSize = 11,
                    Foreground = (WpfBrush)FindResource("TextMuted"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

            var syncBtn = new System.Windows.Controls.Button
            {
                Content = f.IsTrusted ? "⚡ Sync Songs" : "🎵 View Setlist",
                Style = (Style)FindResource(f.IsTrusted ? "FlameButton" : "BlueButton"),
                Padding = new Thickness(12, 6, 12, 6),
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

            if (f.IsTrusted && !string.IsNullOrWhiteSpace(f.TrustToken))
            {
                var copyTokBtn = new System.Windows.Controls.Button
                {
                    Content = "Copy Trust Token",
                    Style = (Style)FindResource("OutlineButton"),
                    Margin = new Thickness(0, 0, 8, 0),
                    ToolTip = "Copy this friend's Trust Token so they can sync songs from you"
                };
                copyTokBtn.Click += (_, _) =>
                {
                    System.Windows.Clipboard.SetText(f.TrustToken);
                    WpfMessageBox.Show($"Copied Trust Token for {f.Username}. Share it privately so they can download your customs.", "Trust Token Copied", MessageBoxButton.OK, MessageBoxImage.Information);
                };
                right.Children.Add(copyTokBtn);
            }
            var trustBtn = new System.Windows.Controls.Button
            {
                Content = f.IsTrusted ? "Revoke Trust" : "Allow Trusted",
                Style = (Style)FindResource(f.IsTrusted ? "OutlineButton" : "GreenButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            trustBtn.Click += (_, _) => { _friends.ToggleTrust(f.FriendCode, !f.IsTrusted); RefreshFriendsList(); RefreshTrustedFriendDropdown(); };
            var delBtn = new System.Windows.Controls.Button { Content = "Unfriend", Style = (Style)FindResource("DangerButton") };
            delBtn.Click += (_, _) => { _friends.RemoveFriend(f.FriendCode); RefreshFriendsList(); RefreshTrustedFriendDropdown(); };

            right.Children.Add(trustBtn);
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
        using var http = new HttpClient { Timeout = timeout };
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
            AutoSyncLobbyBtn.Content = TempSessionModeChk.IsChecked == true
                ? $"Temp-Sync {missingLobby.Count} Lobby Song(s)"
                : $"Auto-Sync {missingLobby.Count} Lobby Song(s)";
        }

        var displayList = ShowFullPeerLibChk.IsChecked == true ? _lastPeerManifest.Songs : lobbySongs;
        PeerCompareStatus.Text = ShowFullPeerLibChk.IsChecked == true
            ? $"Showing all {displayList.Count} songs from {_lastPeerManifest.MachineName}:"
            : $"Showing {_lastPeerManifest.MachineName}'s Active Lobby Setlist ({displayList.Count} songs):";

        foreach (var s in displayList)
        {
            bool alreadyOwned = localIds.Contains(s.SongId);
            var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var info = new StackPanel();
            info.Children.Add(new TextBlock { Text = $"{s.Title} — {s.Artist}", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary") });
            info.Children.Add(new TextBlock
            {
                Text = alreadyOwned ? $"Already Installed ({s.SongId})" : $"Missing Locally • {s.TotalSizeBytes / (1024.0 * 1024.0):F1} MB",
                FontSize = 11.5,
                Foreground = (WpfBrush)FindResource(alreadyOwned ? "TextMuted" : "CardSuccessTitle")
            });

            // Friend High-Score Leaderboard Comparison!
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
                info.Children.Add(new TextBlock
                {
                    Text = $"{crown}  •  You: {mine:N0}{(myScore?.IsFullCombo == true ? " (FC)" : "")}  vs  Peer: {theirs:N0}{(peerScore?.IsFullCombo == true ? " (FC)" : "")}",
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = (WpfBrush)FindResource(mine >= theirs ? "CardSuccessTitle" : "CardWarnTitle"),
                    Margin = new Thickness(0, 2, 0, 0)
                });
            }
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var actionStack = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            bool isTempSessionSong = _tempSession.CurrentState.Songs.Any(x => x.SongId.Equals(s.SongId, StringComparison.OrdinalIgnoreCase) && !x.Kept);
            if (isTempSessionSong)
            {
                var keepBtn = new System.Windows.Controls.Button
                {
                    Content = "⭐ Keep Song",
                    Style = (Style)FindResource("FlameButton"),
                    Margin = new Thickness(0, 0, 6, 0),
                    ToolTip = "Permanently keep this song in your library so it never gets auto-removed"
                };
                keepBtn.Click += (_, _) =>
                {
                    _tempSession.KeepSongPermanently(s.SongId);
                    ShowStageToast("⭐ SONG KEPT PERMANENTLY", $"'{s.Title}' was saved permanently to your library!", "#22C55E");
                    AudioWaveformAndSfxService.PlayStarPowerChime();
                    RefreshSongsList();
                    RefreshTempSessionBanner();
                    RenderPeerLibrary();
                };
                actionStack.Children.Add(keepBtn);
            }

            var dlBtn = new System.Windows.Controls.Button
            {
                Content = alreadyOwned ? (isTempSessionSong ? "Temp Owned" : "Re-Download") : (TempSessionModeChk.IsChecked == true ? "Temp Sync" : "Sync Song"),
                Style = (Style)FindResource(alreadyOwned ? "OutlineButton" : "GreenButton")
            };
            dlBtn.Click += async (_, _) =>
            {
                dlBtn.IsEnabled = false;
                dlBtn.Content = "Syncing...";
                bool tempMode = TempSessionModeChk.IsChecked == true;
                var (ok, err) = await DownloadAndInstallPeerSongAsync(s, tempMode);
                if (ok)
                {
                    dlBtn.Content = tempMode ? "Temp Installed" : "Installed";
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
            Grid.SetColumn(actionStack, 1);
            grid.Children.Add(actionStack);

            card.Child = grid;
            PeerSongsPanel.Children.Add(card);
        }
    }

    private async void AutoSyncLobbyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPeerManifest == null) return;
        var localIds = _gh3.GetInstalledCustomSongs().Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = GetPeerLobbySongs(_lastPeerManifest).Where(s => !localIds.Contains(s.SongId)).ToList();

        if (missing.Count == 0)
        {
            // Asymmetric library: Local has all of Peer's songs, but Peer is missing Local's songs.
            // Temporarily mirror Peer's customs.pak.xen so both games have identical slots 0..N for this match!
            AutoSyncLobbyBtn.IsEnabled = false;
            PeerCompareStatus.Text = $"Mirroring {_lastPeerManifest.MachineName}'s customs.pak.xen setlist slots...";
            _tempSession.BeginSessionIfNeeded(_lastPeerBaseUrl, autoCleanOnGameExit: true);
            await PullPeerCustomsPakAsync();
            AutoSyncLobbyBtn.IsEnabled = true;

            RefreshSongsList();
            RefreshTempSessionBanner();
            RenderPeerLibrary();

            ShowStageToast("🪞 SETLIST MIRRORED!", $"Temporarily matched {_lastPeerManifest.MachineName}'s setlist slots. Both games are 100% ready for online multiplayer!", "#22C55E");
            WpfMessageBox.Show(
                $"Setlist slots successfully mirrored from {_lastPeerManifest.MachineName}!\n\n" +
                $"Both games now share the exact same {_lastPeerManifest.Songs.Count} custom song slots in matching order. You can launch GH3 and play together with zero loading screen freezes!\n\n" +
                $"When you exit Guitar Hero III, your original custom library will be automatically restored.",
                "Setlist Mirrored", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        bool tempMode = TempSessionModeChk.IsChecked == true;
        AutoSyncLobbyBtn.IsEnabled = false;
        if (tempMode)
            _tempSession.BeginSessionIfNeeded(_lastPeerBaseUrl, autoCleanOnGameExit: true);
        else
            _saves.BackupCurrentSaves($"Before-Lobby-Sync-{_lastPeerManifest.MachineName}");

        int installed = 0;
        string lastErr = "";
        foreach (var s in missing)
        {
            PeerCompareStatus.Text = $"Syncing ({installed + 1}/{missing.Count}): {s.Title}...";
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
            WpfMessageBox.Show(
                tempMode
                    ? $"Temporary-synced {installed} / {missing.Count} Lobby song(s) from {_lastPeerManifest.MachineName}!\n\nThese songs will auto-clean when GH3 exits unless you click 'Keep Song'."
                    : $"Synced {installed} / {missing.Count} Lobby Setlist song(s) cleanly into your customs.pak.xen!",
                "Lobby Sync Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        else if (!string.IsNullOrEmpty(lastErr))
        {
            WpfMessageBox.Show($"Sync could not complete: {lastErr}", "Lobby Sync", MessageBoxButton.OK, MessageBoxImage.Warning);
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

        var res = await _cloneHero.ImportFromZipOrFolderAsync(path, autoCompile: true, (pct, msg) => Dispatcher.Invoke(() =>
        {
            ChImportProgress.Value = pct;
            ChImportStatusText.Text = msg;
        }));

        ChImportProgress.Visibility = Visibility.Collapsed;
        ChImportStatusText.Text = res.Message;
        if (res.Success) RefreshSongsList();
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
        try
        {
            _saves.BackupCurrentSaves("Before-Archive-Import");
            var bytes = File.ReadAllBytes(filePath);
            int count = _gh3.ImportArchiveFile(bytes, Path.GetFileName(filePath), _logger);
            ImportStatusText.Text = $"Imported {count} custom song(s) from '{Path.GetFileName(filePath)}'.";
            RefreshSongsList();
            RefreshSaveBackups();
        }
        catch (Exception ex)
        {
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

                if (_saves.RestoreBackup(b.BackupId, _gh3.GamePath))
                {
                    WpfMessageBox.Show($"Restored '{b.ProfileName}' cleanly!", "Profile Restored", MessageBoxButton.OK, MessageBoxImage.Information);
                    RefreshSongsList();
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
        var b = _saves.BackupCurrentSaves(name, name, _gh3.GamePath);
        RefreshSaveBackups();
        WpfMessageBox.Show($"Saved paired snapshot '{b.ProfileName}' ({b.FileCount} files, customs.pak paired: {b.IncludesCustomsPak}).",
            "Profile Snapshot Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreLatestSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_saves.RestoreLatestBackup(_gh3.GamePath))
        {
            WpfMessageBox.Show("Restored latest paired career snapshot cleanly!", "Restored", MessageBoxButton.OK, MessageBoxImage.Information);
            RefreshSongsList();
        }
        else
            WpfMessageBox.Show("No snapshots available to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
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
            HostFingerprintText.Text = $"Host ECDSA P-256 Fingerprint: {_security.HostPublicKeyFingerprint}  •  DPAPI Token Vault: Active";
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

    private void CleanTempFilesBtn_Click(object sender, RoutedEventArgs e)
    {
        CleanTempFilesBtn.IsEnabled = false;
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
                var detail = string.Join("\n• ", summary.Take(12));
                WpfMessageBox.Show($"Cleanup complete!\nRemoved {filesRemoved} redundant/temp item(s) ({mb:F1} MB freed):\n\n• {detail}",
                    "Storage Cleanup Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
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
}
