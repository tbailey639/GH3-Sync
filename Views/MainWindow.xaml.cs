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
    private readonly int _port;

    private List<CustomSongInfo> _allSongs = new();
    private readonly HashSet<string> _selectedExportIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<QueuedSongItem> _batchQueue = new();
    private string _currentFilter = "all";
    private bool _friendCodeVisible = false;
    private int _creatorPreviewStartMs = 5000;

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
        int port)
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

        InitializeComponent();
        ApplyTheme(AppConfig.Current.ThemeMode, save: false);

        _tempSession.SessionCleanedUp += msg => Dispatcher.Invoke(() =>
        {
            RefreshSongsList();
            RefreshTempSessionBanner();
            _trayIcon?.ShowBalloonTip(3000, "Temporary Lobby Session Cleaned", msg, WinForms.ToolTipIcon.Info);
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
            menu.Items.Add("Open GH3-Sync", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
            menu.Items.Add("Launch Guitar Hero III", null, (_, _) => Dispatcher.Invoke(() => LaunchGh3Btn_Click(this, new RoutedEventArgs())));
            menu.Items.Add("Check for Updates", null, (_, _) => Dispatcher.Invoke(async () =>
            {
                RestoreWindow();
                await RunUpdateCheckAsync(silentIfUpToDate: false);
            }));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit GH3-Sync", null, (_, _) => Dispatcher.Invoke(ExitApplication));

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
            _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
        }
        catch { }
    }

    private void RestoreWindow()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void ExitApplication()
    {
        _isExiting = true;
        StopAudioPreview();
        _trayIcon?.Dispose();
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(1800, "GH3-Sync Running in Tray",
                "GH3-Sync stays active in the background for P2P sync. Double-click the tray icon to reopen.",
                WinForms.ToolTipIcon.Info);
        }
        else
        {
            StopAudioPreview();
            _trayIcon?.Dispose();
        }
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

        var first = dropped[0];

        // 1. Folder dropped -> check Clone Hero folder!
        if (Directory.Exists(first))
        {
            if (CloneHeroImportService.IsCloneHeroFolder(first))
            {
                NavImport.IsChecked = true;
                await RunCloneHeroImportAsync(first);
                return;
            }
            WpfMessageBox.Show("Dropped folder does not contain notes.chart, notes.mid, or song.ini.", "Folder Drop", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ext = Path.GetExtension(first).ToLowerInvariant();
        if (ext is ".mp3" or ".wav" or ".ogg")
        {
            NavCreator.IsChecked = true;
            await LoadAudioIntoCreatorAsync(first);
        }
        else if (ext is ".chart" or ".mid" or ".midi")
        {
            NavCreator.IsChecked = true;
            ModeUploadRadio.IsChecked = true;
            CrChartPathBox.Text = first;
        }
        else if (ext is ".gh3pack" or ".gh3song" or ".sgh")
        {
            NavImport.IsChecked = true;
            ImportStandardArchive(first);
        }
        else if (ext == ".zip")
        {
            NavImport.IsChecked = true;
            // Auto-detect whether this .zip is a Clone Hero song or a GH3-Sync pack
            bool looksCloneHero = false;
            try
            {
                using var zip = ZipFile.OpenRead(first);
                looksCloneHero = zip.Entries.Any(en =>
                    en.Name.Equals("notes.chart", StringComparison.OrdinalIgnoreCase) ||
                    en.Name.Equals("notes.mid", StringComparison.OrdinalIgnoreCase));
            }
            catch { }

            if (looksCloneHero)
                await RunCloneHeroImportAsync(first);
            else
                ImportStandardArchive(first);
        }
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
        RefreshToolsAndAiStatus();
        RefreshFriendsList();
        RefreshTrustedFriendDropdown();
        RefreshTempSessionBanner();
        RefreshNetworkSummary();
        RefreshSaveBackups();
        RefreshLogs();
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
                        Arguments = $"-y -v error -i \"{filePath}\" -ar 44100 -ac 2 \"{tmpWav}\"",
                        CreateNoWindow = true,
                        UseShellExecute = false
                    };
                    using var p = Process.Start(psi);
                    p?.WaitForExit(8000);
                    if (File.Exists(tmpWav)) playablePath = tmpWav;
                }
            }

            _player.Open(new Uri(playablePath));
            _player.Play();
            _isPlayerPlaying = true;
            _playerTimer.Start();

            AudioPlayerBar.Visibility = Visibility.Visible;
            PlayerTrackTitle.Text = isGuitarStem ? $"{trackTitle} [Guitar Stem]" : trackTitle;
            PlayerPlayPauseBtn.Content = "⏸";

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
    }

    private void PlayerPlayPauseBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlayerPlaying)
        {
            _player.Pause();
            _isPlayerPlaying = false;
            PlayerPlayPauseBtn.Content = "▶";
        }
        else
        {
            _player.Play();
            _isPlayerPlaying = true;
            PlayerPlayPauseBtn.Content = "⏸";
        }
    }

    private void PlayerStopBtn_Click(object sender, RoutedEventArgs e)
    {
        _player.Stop();
        _isPlayerPlaying = false;
        PlayerPlayPauseBtn.Content = "▶";
        PlayerSeekSlider.Value = 0;
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

        foreach (var song in list)
            SongsListPanel.Children.Add(BuildSongCard(song));
    }

    private Border BuildSongCard(CustomSongInfo song)
    {
        bool isOnogami = song.CreatedWithOnogamiCreator || song.IsCreatedLocally;
        bool hasProject = !string.IsNullOrWhiteSpace(song.ProjectDirectory) && Directory.Exists(song.ProjectDirectory);
        bool hasAudio = !string.IsNullOrEmpty(Gh3Service.FindSongAudioPath(song));
        double sizeMb = song.TotalSizeBytes / (1024.0 * 1024.0);

        var card = new Border
        {
            Background = (WpfBrush)FindResource("BgCard"),
            BorderBrush = song.IsTemporarySessionSong
                ? (WpfBrush)FindResource("CardInfoBorder")
                : (song.IsInLobbySetlist ? (WpfBrush)FindResource("AccentFlame") : (WpfBrush)FindResource("BorderSubtle")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 11, 14, 11),
            Margin = new Thickness(0, 0, 0, 9)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Select Checkbox + Favorite Star
        var leftTools = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var selChk = new System.Windows.Controls.CheckBox
        {
            IsChecked = _selectedExportIds.Contains(song.SongId),
            ToolTip = "Select for Pack Export",
            Margin = new Thickness(0, 0, 6, 0)
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
            ToolTip = "Toggle Favorite"
        };
        favBtn.Click += (_, _) => { AppConfig.ToggleFavorite(song.SongId); RefreshSongsList(); };

        leftTools.Children.Add(selChk);
        leftTools.Children.Add(favBtn);
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
        infoStack.Children.Add(new TextBlock
        {
            Text = $"{(string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist)}  •  {song.SongId}  •  {sizeMb:F1} MB",
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
        if (song.IsInLobbySetlist)
            badgeRow.Children.Add(MakeBadge("Lobby Auto-Sync", "#431407", "#FDBA74"));
        if (song.IsTemporarySessionSong)
            badgeRow.Children.Add(MakeBadge("Temporary Session", "#1E3A8A", "#93C5FD"));

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

    private void OpenEditSongDialog(CustomSongInfo song)
    {
        var dlg = new Window
        {
            Title = $"Edit Song — {song.Title}",
            Width = 500,
            Height = 470,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "Edit Song Info & Audio Volumes", FontSize = 16, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12) });

        var titleBox = new System.Windows.Controls.TextBox { Text = song.Title, Margin = new Thickness(0, 0, 0, 8) };
        var artistBox = new System.Windows.Controls.TextBox { Text = song.Artist, Margin = new Thickness(0, 0, 0, 8) };
        var albumBox = new System.Windows.Controls.TextBox { Text = "", Margin = new Thickness(0, 0, 0, 8) };
        var yearBox = new System.Windows.Controls.TextBox { Text = DateTime.Now.Year.ToString(), Margin = new Thickness(0, 0, 0, 8) };
        var genreBox = new System.Windows.Controls.TextBox { Text = "Rock", Margin = new Thickness(0, 0, 0, 8) };
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
        stack.Children.Add(new TextBlock { Text = "Genre", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
        stack.Children.Add(genreBox);
        stack.Children.Add(gtrVolLbl);
        stack.Children.Add(gtrVolSlider);
        stack.Children.Add(bandVolLbl);
        stack.Children.Add(bandVolSlider);

        var recompileChk = new System.Windows.Controls.CheckBox { Content = "Recompile into Guitar Hero III immediately", IsChecked = true, Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(recompileChk);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var saveBtn = new System.Windows.Controls.Button { Content = "Save & Apply", Style = (Style)FindResource("FlameButton") };
        saveBtn.Click += async (_, _) =>
        {
            saveBtn.IsEnabled = false;
            saveBtn.Content = "Saving...";
            int yr = int.TryParse(yearBox.Text, out var parsedYr) ? parsedYr : DateTime.Now.Year;
            var (ok, msg) = await _creator.UpdateSongMetadataAndRecompileAsync(
                song.ProjectDirectory, titleBox.Text.Trim(), artistBox.Text.Trim(), albumBox.Text.Trim(),
                yr, genreBox.Text.Trim(), 4, 3, gtrVolSlider.Value, bandVolSlider.Value, recompileChk.IsChecked == true);
            dlg.Close();
            RefreshSongsList();
            WpfMessageBox.Show(msg, ok ? "Song Updated" : "Update Error", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Error);
        };
        btns.Children.Add(saveBtn);
        stack.Children.Add(btns);

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

    private async Task LoadAudioIntoCreatorAsync(string path)
    {
        CrAudioPathBox.Text = path;
        if (string.IsNullOrWhiteSpace(CrTitleBox.Text))
        {
            var fname = Path.GetFileNameWithoutExtension(path);
            if (fname.Contains(" - "))
            {
                var parts = fname.Split(" - ", 2);
                if (string.IsNullOrWhiteSpace(CrArtistBox.Text)) CrArtistBox.Text = parts[0].Trim();
                CrTitleBox.Text = parts[1].Trim();
            }
            else CrTitleBox.Text = fname;
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

    private async void DetectBpmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CrAudioPathBox.Text) || !File.Exists(CrAudioPathBox.Text)) return;
        await DetectBpmInternalAsync(CrAudioPathBox.Text);
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
            AutoDetectBpm = true,
            UseVariableTempoMap = CrVariableTempoChk.IsChecked == true,
            ChartFeel = (CrFeelCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Standard",
            InstrumentFocus = (CrFocusCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Guitar",
            AutoGenerateChart = autoGen,
            SeparateStemsWithDemucs = CrSeparateStemsChk.IsChecked == true && _demucs.GetStatus().IsInstalled,
            AutoCompile = CrAutoCompileChk.IsChecked == true
        };
    }

    private void AddToQueueBtn_Click(object sender, RoutedEventArgs e)
    {
        var req = BuildCreatorRequest(out var audioPath, out var chartPath);
        if (req == null) return;

        _batchQueue.Add(new QueuedSongItem(req, audioPath, chartPath));
        RunQueueBtn.Visibility = Visibility.Visible;
        RunQueueBtn.Content = $"Compile Batch Queue ({_batchQueue.Count})";
        BatchQueueListText.Visibility = Visibility.Visible;
        BatchQueueListText.Text = "Batch Queue: " + string.Join("  •  ", _batchQueue.Select((q, i) => $"{i + 1}. {q.Meta.Artist} - {q.Meta.Title}"));

        CrTitleBox.Text = "";
        CrArtistBox.Text = "";
        CrAudioPathBox.Text = "";
        CrChartPathBox.Text = "";
    }

    private async void RunQueueBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_batchQueue.Count == 0) return;

        CreateSongSubmitBtn.IsEnabled = false;
        RunQueueBtn.IsEnabled = false;
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
            RunQueueBtn.Visibility = Visibility.Collapsed;
            BatchQueueListText.Visibility = Visibility.Collapsed;
            RefreshSongsList();
        }
        finally
        {
            CreateSongSubmitBtn.IsEnabled = true;
            RunQueueBtn.IsEnabled = true;
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

        CreateSongSubmitBtn.IsEnabled = false;
        CrProgressBar.Visibility = Visibility.Visible;
        CrProgressBar.Value = 5;
        CrResultBox.Visibility = Visibility.Collapsed;

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
                CrResultBox.Background = new SolidColorBrush(WpfColor.FromRgb(13, 34, 26));
                CrResultBox.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
                CrResultText.Foreground = new SolidColorBrush(WpfColor.FromRgb(167, 243, 208));
                CrResultText.Text =
                    $"{result.Message}\n" +
                    $"• Tempo: {result.DetectedBpm:F1} BPM ({(meta.UseVariableTempoMap ? "Dynamic Variable Tempo Map" : "Constant BPM")})\n" +
                    $"• Feel: {meta.ChartFeel}  •  Stems: {(result.StemsSeparated ? "6-Stem Isolated Guitar" : "Single Mix")}\n" +
                    $"• Folder: {result.ProjectDirectory}";
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
        System.Windows.Clipboard.SetText(_friends.Profile.FriendCode);
        WpfMessageBox.Show($"Copied Friend Code: {_friends.Profile.FriendCode}", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveUsernameBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileUsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _friends.SaveProfile(name);
        SidebarProfileName.Text = name;
        WpfMessageBox.Show($"Display name updated to '{name}'.", "Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddFriendBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = AddFriendCodeBox.Text.Trim();
        var name = AddFriendNameBox.Text.Trim();
        if (string.IsNullOrEmpty(code)) return;
        _friends.AddFriend(string.IsNullOrEmpty(name) ? code : name, code);
        AddFriendCodeBox.Text = "";
        AddFriendNameBox.Text = "";
        RefreshFriendsList();
    }

    private void RefreshFriendsList()
    {
        FriendsListPanel.Children.Clear();
        var list = _friends.Friends;
        if (list.Count == 0)
        {
            FriendsListPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("GhCard"),
                Child = new TextBlock { Text = "No friends added yet.", Foreground = (WpfBrush)FindResource("TextMuted") }
            });
            return;
        }

        foreach (var f in list)
        {
            var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(14) };
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
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
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

    private void TrustedFriendQuickCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        int idx = TrustedFriendQuickCombo.SelectedIndex - 1;
        if (idx < 0 || idx >= _trustedDropdownEntries.Count) return;
        var friend = _trustedDropdownEntries[idx];
        if (!string.IsNullOrWhiteSpace(friend.LastAddress))
            PeerAddressBox.Text = friend.LastAddress.Replace("http://", "", StringComparison.OrdinalIgnoreCase).Replace("https://", "", StringComparison.OrdinalIgnoreCase);
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
            TempSessionSubtitle.Text = $"Synced from {st.PeerAddress}. Temporary songs auto-clean when GH3 exits — click 'Keep Song' in My Customs to save any track permanently.";
            CustomsTempSessionText.Text = $"Temporary Lobby Session active ({tempCount} temporary song(s) will auto-clean when GH3 exits).";
        }
        else
        {
            TempSessionBanner.Visibility = Visibility.Collapsed;
            CustomsTempSessionBanner.Visibility = Visibility.Collapsed;
        }
    }

    private void EndTempSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        var (_, _, msg) = _tempSession.EndSessionAndCleanup();
        RefreshSongsList();
        RefreshTempSessionBanner();
        if (_lastPeerManifest != null) RenderPeerLibrary();
        WpfMessageBox.Show(msg, "Temporary Session Cleaned", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static HttpClient CreateAuthenticatedPeerClient(string secret, TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(secret))
        {
            // Headers-only auth: never expose secrets in query strings or URL logs
            if (secret.Length > 10)
                http.DefaultRequestHeaders.Add("X-GH3-Trust-Token", secret);
            else
                http.DefaultRequestHeaders.Add("X-GH3-PIN", secret);
        }
        return http;
    }

    private void RefreshNetworkSummary()
    {
        var pub = string.IsNullOrEmpty(_nat.PublicIp) ? "Detecting..." : $"{_nat.PublicIp}:{_nat.ExternalPort ?? _port}";
        var mode = AppConfig.Current.P2pOnlineEnabled ? "Online" : "OFFLINE MODE";
        P2pNetworkSummary.Text = $"[{mode}] Public / NAT Address: {pub}  •  UPnP / NAT-PMP: {(_nat.IsMapped ? $"Port Forwarded ({_nat.MappingMethod})" : _nat.MappingMethod)}";
        P2pPinSummary.Text = $"Your Session Pairing PIN (View-Only): {_security.PairingPin}  •  Song Downloads: Trusted Friends Only";
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

        var baseUrl = addr.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? addr.TrimEnd('/') : $"http://{addr.TrimEnd('/')}";
        PeerCompareStatus.Text = $"Connecting to {baseUrl}...";
        LobbyMatchBanner.Visibility = Visibility.Collapsed;
        PeerSongsPanel.Children.Clear();

        try
        {
            using var http = CreateAuthenticatedPeerClient(secret, TimeSpan.FromSeconds(15));
            var json = await http.GetStringAsync($"{baseUrl}/api/manifest");
            var remote = JsonSerializer.Deserialize<LibraryManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

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

        var localIds = _gh3.GetInstalledCustomSongs().Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lobbySongs = GetPeerLobbySongs(_lastPeerManifest);
        var missingLobby = lobbySongs.Where(s => !localIds.Contains(s.SongId)).ToList();

        LobbyMatchBanner.Visibility = Visibility.Visible;
        if (missingLobby.Count == 0)
        {
            LobbyMatchBanner.Background = (WpfBrush)FindResource("CardSuccessBg");
            LobbyMatchBanner.BorderBrush = (WpfBrush)FindResource("CardSuccessBorder");
            LobbyMatchTitle.Text = $"100% Lobby Setlist Matched ({lobbySongs.Count} songs) — Ready for BetterGH3 Online!";
            LobbyMatchTitle.Foreground = (WpfBrush)FindResource("CardSuccessTitle");
            LobbyMatchSub.Text = $"Connected to {_lastPeerManifest.MachineName} ({_lastPeerManifest.Songs.Count} total songs in their library).";
            AutoSyncLobbyBtn.Visibility = Visibility.Collapsed;
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
            Grid.SetColumn(info, 0);
            grid.Children.Add(info);

            var dlBtn = new System.Windows.Controls.Button
            {
                Content = alreadyOwned ? "Re-Download" : (TempSessionModeChk.IsChecked == true ? "Temp Sync" : "Sync Song"),
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
            Grid.SetColumn(dlBtn, 1);
            grid.Children.Add(dlBtn);

            card.Child = grid;
            PeerSongsPanel.Children.Add(card);
        }
    }

    private async void AutoSyncLobbyBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_lastPeerManifest == null) return;
        var localIds = _gh3.GetInstalledCustomSongs().Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = GetPeerLobbySongs(_lastPeerManifest).Where(s => !localIds.Contains(s.SongId)).ToList();
        if (missing.Count == 0) return;

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

            using var http = CreateAuthenticatedPeerClient(_lastPeerSecret, TimeSpan.FromMinutes(5));
            using var resp = await http.GetAsync($"{_lastPeerBaseUrl}/api/download/{Uri.EscapeDataString(song.SongId)}");
            if (resp.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return (false, "Access Denied (403): Only Trusted Friends can download songs. Ask the host to mark you as Trusted and share your Trust Token.");
            resp.EnsureSuccessStatusCode();

            var encBytes = await resp.Content.ReadAsByteArrayAsync();
            var zipBytes = _security.DecryptPayload(encBytes, _lastPeerSecret);

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
            using var http = CreateAuthenticatedPeerClient(_lastPeerSecret, TimeSpan.FromSeconds(30));
            var encPak = await http.GetByteArrayAsync($"{_lastPeerBaseUrl}/api/download-customs-pak");
            var rawPak = _security.DecryptPayload(encPak, _lastPeerSecret);
            _gh3.BackupAndDeployCustomsPak(rawPak);
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

    private async void ImportCloneHeroZipBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Clone Hero / YARG .zip Archive",
            Filter = "ZIP Archives (*.zip)|*.zip|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
            await RunCloneHeroImportAsync(dlg.FileName);
    }

    private async Task RunCloneHeroImportAsync(string path)
    {
        ChImportProgress.Visibility = Visibility.Visible;
        ChImportProgress.Value = 5;
        ChImportStatusText.Text = "Starting Clone Hero conversion...";

        var res = await _cloneHero.ImportFromZipOrFolderAsync(path, autoCompile: true, (pct, msg) => Dispatcher.Invoke(() =>
        {
            ChImportProgress.Value = pct;
            ChImportStatusText.Text = msg;
        }));

        ChImportProgress.Visibility = Visibility.Collapsed;
        ChImportStatusText.Text = res.Message;
        if (res.Success) RefreshSongsList();
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
        SettingsSubUpdates.Visibility = tag == "updates" ? Visibility.Visible : Visibility.Collapsed;
        SettingsSubAbout.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;

        TabSetGeneralBtn.Style = (Style)FindResource(tag == "general" ? "FlameButton" : "OutlineButton");
        TabSetAppearanceBtn.Style = (Style)FindResource(tag == "appearance" ? "FlameButton" : "OutlineButton");
        TabSetUpdatesBtn.Style = (Style)FindResource(tag == "updates" ? "FlameButton" : "OutlineButton");
        TabSetAboutBtn.Style = (Style)FindResource(tag == "about" ? "FlameButton" : "OutlineButton");

        if (tag == "about") RefreshLogs();
    }

    private void LoadSettingsForm()
    {
        SettingsGamePathBox.Text = _gh3.GamePath;
        SettingsSongsPathBox.Text = SongCreatorService.ResolveSongsDirectory();
        SettingsHoneycombPathBox.Text = AppConfig.Current.HoneycombPath;
        SettingsDirectInstallChk.IsChecked = AppConfig.Current.DirectInstallToGame;
        SettingsMinLaunchChk.IsChecked = AppConfig.Current.MinimizeOnGameLaunch;
        SettingsP2pOnlineChk.IsChecked = AppConfig.Current.P2pOnlineEnabled;
        SettingsLobbyCapBox.Text = AppConfig.Current.MaxLobbyAutoSyncSongs.ToString();
        SettingsAutoRollbackChk.IsChecked = AppConfig.Current.AutoRollbackOnFailedUpdate;
        UpdateThemeCardSelection();
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

    // ==================== CUSTOM LIGHT & DARK GH3 THEME SWITCHER ====================
    private void SelectDarkThemeBtn_Click(object sender, RoutedEventArgs e) => ApplyTheme("Dark", save: true);
    private void SelectLightThemeBtn_Click(object sender, RoutedEventArgs e) => ApplyTheme("Light", save: true);

    private void ApplyTheme(string mode, bool save)
    {
        bool isLight = string.Equals(mode, "Light", StringComparison.OrdinalIgnoreCase);
        string normalized = isLight ? "Light" : "Dark";

        try
        {
            var themeUri = new Uri(isLight ? "Views/LightTheme.xaml" : "Views/DarkTheme.xaml", UriKind.Relative);
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
        bool isLight = string.Equals(AppConfig.Current.ThemeMode, "Light", StringComparison.OrdinalIgnoreCase);

        ThemeDarkCard.BorderBrush = new SolidColorBrush(isLight ? WpfColor.FromRgb(51, 65, 85) : WpfColor.FromRgb(249, 115, 22));
        ThemeLightCard.BorderBrush = new SolidColorBrush(isLight ? WpfColor.FromRgb(220, 38, 38) : WpfColor.FromRgb(203, 213, 225));

        SelectDarkThemeBtn.Content = isLight ? "Switch to Dark GH3 Theme" : "✓ Active Theme (Dark GH3)";
        SelectDarkThemeBtn.Style = (Style)FindResource(isLight ? "OutlineButton" : "FlameButton");

        SelectLightThemeBtn.Content = isLight ? "✓ Active Theme (Light GH3)" : "Switch to Light GH3 Theme";
        SelectLightThemeBtn.Style = (Style)FindResource(isLight ? "FlameButton" : "OutlineButton");
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
        UpdatesCompStatusText.Text = "Running 5-point post-update compatibility self-test...";
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
