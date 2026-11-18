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

using WinForms = System.Windows.Forms;
using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = System.Windows.MessageBox;

namespace GH3_Sync.Views;

public partial class MainWindow : Window
{
    private readonly Gh3Service _gh3;
    private readonly SyncLogger _logger;
    private readonly SecurityService _security;
    private readonly FriendService _friends;
    private readonly SaveManager _saves;
    private readonly HoneycombCompilerService _compiler;
    private readonly AudioAnalysisService _audioAnalysis;
    private readonly DemucsService _demucs;
    private readonly SongCreatorService _creator;
    private readonly NatPortMappingService _nat;
    private readonly UpdateService _updater;
    private readonly int _port;

    private List<CustomSongInfo> _allSongs = new();
    private string _currentFilter = "all";
    private bool _friendCodeVisible = false;
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
        AudioAnalysisService audioAnalysis,
        DemucsService demucs,
        SongCreatorService creator,
        NatPortMappingService nat,
        UpdateService updater,
        int port)
    {
        _gh3 = gh3;
        _logger = logger;
        _security = security;
        _friends = friends;
        _saves = saves;
        _compiler = compiler;
        _audioAnalysis = audioAnalysis;
        _demucs = demucs;
        _creator = creator;
        _nat = nat;
        _updater = updater;
        _port = port;

        InitializeComponent();

        SourceInitialized += MainWindow_SourceInitialized;
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;

        InitializeTrayIcon();
    }

    private void MainWindow_SourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int dark = 1;
            DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int)); // DWMWA_USE_IMMERSIVE_DARK_MODE
            int cornerPref = 2; // DWMWCP_ROUND (Windows 11 rounded corners)
            DwmSetWindowAttribute(hwnd, 33, ref cornerPref, sizeof(int));
        }
        catch { }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshAllUiState();

        // Automatically check GitHub for updates upon opening!
        await RunUpdateCheckAsync(silentIfUpToDate: true);
    }

    private void InitializeTrayIcon()
    {
        try
        {
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("🎸 Open GH3-Sync", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
            menu.Items.Add("🔄 Check for Updates", null, (_, _) => Dispatcher.Invoke(async () =>
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
                Text = "GH3-Sync Beta 5 — BetterGH3 Companion",
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
        _trayIcon?.Dispose();
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_isExiting)
        {
            e.Cancel = true;
            Hide();
            _trayIcon?.ShowBalloonTip(2000, "GH3-Sync Active in System Tray",
                "GH3-Sync continues running in the background for P2P sync. Double-click the tray icon to reopen.",
                WinForms.ToolTipIcon.Info);
        }
        else
        {
            _trayIcon?.Dispose();
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
        PageAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavCustoms.IsChecked == true) RefreshSongsList();
        if (NavCreator.IsChecked == true || NavAiTools.IsChecked == true) RefreshToolsAndAiStatus();
        if (NavFriends.IsChecked == true) RefreshFriendsList();
        if (NavP2P.IsChecked == true) RefreshNetworkSummary();
        if (NavSaves.IsChecked == true) RefreshSaveBackups();
        if (NavAbout.IsChecked == true) RefreshLogs();
    }

    private void GoToAiManager_Click(object sender, RoutedEventArgs e)
    {
        NavAiTools.IsChecked = true;
    }

    // ==================== FULL UI STATE REFRESH ====================
    public void RefreshAllUiState()
    {
        // Header & Game Compat
        var compat = _gh3.VerifyGamePath(_gh3.GamePath);
        if (compat.IsBetterGh3)
        {
            GameStatusText.Text = "BetterGH3 Connected";
            GameStatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(34, 197, 94));
            GameStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(16, 185, 129));
        }
        else if (compat.IsValid)
        {
            GameStatusText.Text = "Vanilla GH3 Detected";
            GameStatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
            GameStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(234, 179, 8));
        }
        else
        {
            GameStatusText.Text = "Game Path Not Set";
            GameStatusDot.Fill = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
            GameStatusPill.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
        }

        CustomsGamePathLabel.Text = $"Game Path: {_gh3.GamePath}  ({(compat.IsBetterGh3 ? "BetterGH3 Mod Active" : compat.StatusMessage)})";

        // Profile Sidebar & Friends Page
        SidebarProfileName.Text = _friends.Profile.Username;
        SidebarFriendCode.Text = _friends.Profile.FriendCode;
        SidebarPortInfo.Text = $"P2P Port {_port} Active";
        ProfileUsernameBox.Text = _friends.Profile.Username;
        UpdateFriendCodeDisplay();

        RefreshSongsList();
        RefreshToolsAndAiStatus();
        RefreshFriendsList();
        RefreshNetworkSummary();
        RefreshSaveBackups();
        RefreshLogs();
    }

    // ==================== AUTO-UPDATER ====================
    private async void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
    {
        await RunUpdateCheckAsync(silentIfUpToDate: false);
    }

    private async Task RunUpdateCheckAsync(bool silentIfUpToDate)
    {
        HeaderUpdateBtn.IsEnabled = false;
        HeaderUpdateBtn.Content = "🔄 Checking...";
        try
        {
            var res = await _updater.CheckForUpdatesAsync();
            _latestUpdate = res;

            if (!res.Success)
            {
                HeaderUpdateBtn.Content = "🔄 Check Updates";
                AboutUpdateStatus.Text = $"Current Version: {UpdateService.CurrentAppVersion}  •  {res.Message}";
                if (!silentIfUpToDate)
                    WpfMessageBox.Show(res.Message, "Update Check", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (res.UpdateAvailable)
            {
                HeaderUpdateBtn.Content = $"⬆ Update v{res.LatestVersion}";
                HeaderUpdateBtn.Style = (Style)FindResource("FlameButton");
                UpdateBannerTitle.Text = $"🚀 Update Available: {res.ReleaseName} (v{res.LatestVersion})";
                UpdateBannerSubtitle.Text = $"You are on v{res.CurrentVersion}. Click 'Update Now' to download GH3-Sync-Setup.exe and upgrade automatically.";
                UpdateBanner.Visibility = Visibility.Visible;
                AboutUpdateStatus.Text = $"Update available: v{res.LatestVersion} (You have v{res.CurrentVersion})";
            }
            else
            {
                HeaderUpdateBtn.Content = "✓ Up to Date";
                AboutUpdateStatus.Text = $"✓ You are running the latest version (v{res.CurrentVersion}).";
                if (!silentIfUpToDate)
                {
                    WpfMessageBox.Show($"You are already running the latest version of GH3-Sync (v{res.CurrentVersion})!",
                        "GH3-Sync Up to Date", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
        }
        finally
        {
            HeaderUpdateBtn.IsEnabled = true;
        }
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
            // Open GitHub release page directly if no .exe asset attached
            Process.Start(new ProcessStartInfo { FileName = _latestUpdate.ReleaseUrl, UseShellExecute = true });
            return;
        }

        UpdateNowBtn.IsEnabled = false;
        UpdateBannerProgress.Visibility = Visibility.Visible;
        UpdateBannerProgress.Value = 0;

        var (ok, installerPath, err) = await _updater.DownloadUpdateAsync(_latestUpdate, (pct, msg) =>
        {
            Dispatcher.Invoke(() =>
            {
                UpdateBannerProgress.Value = pct;
                UpdateBannerSubtitle.Text = msg;
            });
        });

        if (!ok)
        {
            UpdateNowBtn.IsEnabled = true;
            WpfMessageBox.Show($"Update failed: {err}", "Update Error", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (_updater.LaunchInstallerAndPrepareExit(installerPath))
        {
            ExitApplication();
        }
        else
        {
            UpdateNowBtn.IsEnabled = true;
            WpfMessageBox.Show($"Downloaded installer to:\n{installerPath}\nPlease run it manually.", "Installer Ready", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void DismissUpdateBanner_Click(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    // ==================== PAGE 1: MY CUSTOMS (+ FULL SONG DELETION!) ====================
    private void RefreshSongs_Click(object sender, RoutedEventArgs e) => RefreshSongsList();

    private void FilterSongs_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string tag)
        {
            _currentFilter = tag;
            FilterAllBtn.Style = (Style)FindResource(tag == "all" ? "FlameButton" : "OutlineButton");
            FilterCreatedBtn.Style = (Style)FindResource(tag == "created" ? "FlameButton" : "OutlineButton");
            FilterSyncedBtn.Style = (Style)FindResource(tag == "synced" ? "FlameButton" : "OutlineButton");
            RenderSongs();
        }
    }

    private void SongSearchBox_TextChanged(object sender, TextChangedEventArgs e) => RenderSongs();

    private void RefreshSongsList()
    {
        _allSongs = _gh3.GetInstalledCustomSongs();
        CustomsHeaderTitle.Text = $"🎸 Installed Custom Songs ({_allSongs.Count})";
        RenderSongs();
    }

    private void RenderSongs()
    {
        SongsListPanel.Children.Clear();
        string query = (SongSearchBox.Text ?? "").Trim();

        var filtered = _allSongs.Where(s =>
        {
            bool isOnogami = s.CreatedWithOnogamiCreator || s.IsCreatedLocally;
            if (_currentFilter == "created" && !isOnogami) return false;
            if (_currentFilter == "synced" && isOnogami) return false;

            if (!string.IsNullOrEmpty(query))
            {
                return (s.Title?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                       (s.Artist?.Contains(query, StringComparison.OrdinalIgnoreCase) == true) ||
                       (s.SongId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true);
            }
            return true;
        }).ToList();

        if (filtered.Count == 0)
        {
            SongsListPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("GhCard"),
                Child = new TextBlock
                {
                    Text = "No custom songs match this filter. Create one in 🔥 ONOGAMI Creator or import a pack!",
                    Foreground = (WpfBrush)FindResource("TextMuted"),
                    FontSize = 13,
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    Margin = new Thickness(0, 14, 0, 14)
                }
            });
            return;
        }

        foreach (var song in filtered)
        {
            SongsListPanel.Children.Add(BuildSongCard(song));
        }
    }

    private Border BuildSongCard(CustomSongInfo song)
    {
        bool isOnogami = song.CreatedWithOnogamiCreator || song.IsCreatedLocally;
        bool hasProject = !string.IsNullOrWhiteSpace(song.ProjectDirectory) && Directory.Exists(song.ProjectDirectory);
        double sizeMb = song.TotalSizeBytes / (1024.0 * 1024.0);

        var card = new Border
        {
            Background = new SolidColorBrush(WpfColor.FromRgb(20, 23, 34)),
            BorderBrush = new SolidColorBrush(WpfColor.FromRgb(38, 43, 61)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 12, 16, 12),
            Margin = new Thickness(0, 0, 0, 10)
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // Left info
        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        infoStack.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(song.Title) ? song.SongId : song.Title,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White
        });
        infoStack.Children.Add(new TextBlock
        {
            Text = $"{(string.IsNullOrWhiteSpace(song.Artist) ? "Unknown Artist" : song.Artist)}  •  {song.SongId}  •  {sizeMb:F1} MB",
            FontSize = 12,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
            Margin = new Thickness(0, 2, 0, 6)
        });

        // Badges row
        var badgeRow = new WrapPanel();
        badgeRow.Children.Add(MakeBadge(
            isOnogami ? "🎸 ONOGAMI Creator" : "🌐 Synced / Imported",
            isOnogami ? "#2E1065" : "#172554",
            isOnogami ? "#C4B5FD" : "#93C5FD"));

        badgeRow.Children.Add(MakeBadge(
            song.IsProject ? "📁 Project Created" : "✓ In-Game Ready",
            song.IsProject ? "#422006" : "#064E3B",
            song.IsProject ? "#FDE047" : "#6EE7B7"));

        infoStack.Children.Add(badgeRow);
        Grid.SetColumn(infoStack, 0);
        grid.Children.Add(infoStack);

        // Right action buttons
        var btnRow = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        if (song.IsProject || !song.IsCompiled)
        {
            var compileBtn = new System.Windows.Controls.Button
            {
                Content = "⚡ Compile",
                Style = (Style)FindResource("GreenButton"),
                Padding = new Thickness(11, 6, 11, 6),
                Margin = new Thickness(0, 0, 6, 0)
            };
            compileBtn.Click += async (_, _) => await CompileSongCardAsync(song, compileBtn);
            btnRow.Children.Add(compileBtn);
        }

        if (hasProject)
        {
            var hcBtn = new System.Windows.Controls.Button
            {
                Content = "⚡ Honeycomb",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 0),
                ToolTip = "Open project in Honeycomb-GUI"
            };
            hcBtn.Click += (_, _) => OpenInHoneycomb(song);
            btnRow.Children.Add(hcBtn);

            var folderBtn = new System.Windows.Controls.Button
            {
                Content = "📂 Folder",
                Style = (Style)FindResource("OutlineButton"),
                Padding = new Thickness(10, 6, 10, 6),
                Margin = new Thickness(0, 0, 6, 0)
            };
            folderBtn.Click += (_, _) => OpenFolderInExplorer(song.ProjectDirectory);
            btnRow.Children.Add(folderBtn);
        }

        var exportBtn = new System.Windows.Controls.Button
        {
            Content = "💾 Export",
            Style = (Style)FindResource("OutlineButton"),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 6, 0),
            ToolTip = "Export standalone .gh3song package"
        };
        exportBtn.Click += (_, _) => ExportSingleSong(song);
        btnRow.Children.Add(exportBtn);

        // DELETE SONG BUTTON (Removes game files + related project files!)
        var deleteBtn = new System.Windows.Controls.Button
        {
            Content = "🗑️ Delete",
            Style = (Style)FindResource("DangerButton"),
            Padding = new Thickness(11, 6, 11, 6),
            ToolTip = "Remove this custom song and all related files from Guitar Hero III"
        };
        deleteBtn.Click += (_, _) => ConfirmAndDeleteSong(song);
        btnRow.Children.Add(deleteBtn);

        Grid.SetColumn(btnRow, 1);
        grid.Children.Add(btnRow);

        card.Child = grid;
        return card;
    }

    private static Border MakeBadge(string text, string bgHex, string fgHex)
    {
        return new Border
        {
            Background = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(bgHex)),
            CornerRadius = new CornerRadius(5),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush((WpfColor)System.Windows.Media.ColorConverter.ConvertFromString(fgHex))
            }
        };
    }

    private async Task CompileSongCardAsync(CustomSongInfo song, System.Windows.Controls.Button btn)
    {
        var target = !string.IsNullOrWhiteSpace(song.GhprojPath) ? song.GhprojPath : song.ProjectDirectory;
        if (string.IsNullOrWhiteSpace(target))
        {
            WpfMessageBox.Show("No .ghproj project found for this song.", "Compile", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        btn.IsEnabled = false;
        btn.Content = "⚡ Compiling...";
        try
        {
            var res = await _compiler.CompileSongProjectAsync(target);
            if (res.Success)
            {
                WpfMessageBox.Show($"✓ '{song.Title}' compiled & installed directly into Guitar Hero III!\n\nPAK: {res.PakFilePath}\nFSB: {res.FsbFilePath}",
                    "Compilation Complete", MessageBoxButton.OK, MessageBoxImage.Information);
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
            btn.Content = "⚡ Compile";
        }
    }

    private void OpenInHoneycomb(CustomSongInfo song)
    {
        string? ghproj = !string.IsNullOrWhiteSpace(song.GhprojPath) && File.Exists(song.GhprojPath)
            ? song.GhprojPath
            : Directory.Exists(song.ProjectDirectory)
                ? Directory.GetFiles(song.ProjectDirectory, "*.ghproj").FirstOrDefault()
                : null;

        if (string.IsNullOrEmpty(ghproj))
        {
            WpfMessageBox.Show("Could not locate .ghproj file for this song.", "Honeycomb", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var hDir = HoneycombCompilerService.FindHoneycombDirectory();
        var hExe = !string.IsNullOrEmpty(hDir) ? Path.Combine(hDir, "Honeycomb-GUI.exe") : null;
        if (!string.IsNullOrEmpty(hExe) && File.Exists(hExe))
        {
            Process.Start(new ProcessStartInfo { FileName = hExe, Arguments = $"\"{ghproj}\"", WorkingDirectory = hDir, UseShellExecute = true });
        }
        else
        {
            OpenFolderInExplorer(Path.GetDirectoryName(ghproj)!);
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
                Filter = "GH3-Sync Song Package (*.gh3song)|*.gh3song|ZIP Archive (*.zip)|*.zip",
                FileName = $"{song.SongId}.gh3song"
            };
            if (dlg.ShowDialog() == true)
            {
                File.WriteAllBytes(dlg.FileName, bytes);
                WpfMessageBox.Show($"Exported '{song.Title}' ({bytes.Length / (1024.0 * 1024.0):F1} MB) cleanly!", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Export error: {ex.Message}", "Export Failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ConfirmAndDeleteSong(CustomSongInfo song)
    {
        // Build a native Windows 11 confirmation dialog with checkbox for deleting source project folder!
        var dlg = new Window
        {
            Title = "Delete Custom Song",
            Width = 490,
            Height = 290,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = $"🗑️ Permanently Delete '{song.Title}'?",
            FontSize = 17,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(248, 113, 113)),
            Margin = new Thickness(0, 0, 0, 8)
        });
        stack.Children.Add(new TextBlock
        {
            Text = $"This will remove all compiled game files for '{song.SongId}' from DATA/SONGS (.pak.xen) and DATA/MUSIC (.fsb.xen / .dat.xen / previews) and automatically back up your career saves first.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(203, 213, 225)),
            FontSize = 12.5,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var deleteProjChk = new System.Windows.Controls.CheckBox
        {
            Content = "Also delete source ONOGAMI project folder, audio & Demucs stems",
            IsChecked = true,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 224, 71)),
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 18)
        };
        stack.Children.Add(deleteProjChk);

        var btnPanel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var cancelBtn = new System.Windows.Controls.Button { Content = "Cancel", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(0, 0, 8, 0) };
        var confirmBtn = new System.Windows.Controls.Button { Content = "🗑️ Yes, Delete All Files", Style = (Style)FindResource("DangerButton") };

        cancelBtn.Click += (_, _) => { dlg.DialogResult = false; dlg.Close(); };
        confirmBtn.Click += (_, _) => { dlg.DialogResult = true; dlg.Close(); };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);
        stack.Children.Add(btnPanel);

        dlg.Content = stack;

        if (dlg.ShowDialog() == true)
        {
            var res = _gh3.DeleteCustomSong(song, deleteProjChk.IsChecked == true, _saves, _logger);
            if (res.Success)
            {
                var summary = res.DeletedItems.Count > 0
                    ? string.Join("\n• ", res.DeletedItems.Take(8))
                    : "Config entries cleaned.";
                WpfMessageBox.Show($"{res.Message}\n\nRemoved:\n• {summary}", "Song Deleted", MessageBoxButton.OK, MessageBoxImage.Information);
                RefreshSongsList();
                RefreshSaveBackups();
            }
            else
            {
                WpfMessageBox.Show(res.Message, "Delete Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ==================== PAGE 2: ONOGAMI SONG CREATOR ====================
    private void ChartMode_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool isUpload = ModeUploadRadio.IsChecked == true;
        ChartPickerPanel.Visibility = isUpload ? Visibility.Visible : Visibility.Collapsed;
        BpmPickerPanel.Visibility = isUpload ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BrowseAudioBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Song Audio File",
            Filter = "Audio Files (*.mp3;*.wav;*.ogg)|*.mp3;*.wav;*.ogg|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            CrAudioPathBox.Text = dlg.FileName;

            // Auto-fill Title/Artist cleanly if empty
            if (string.IsNullOrWhiteSpace(CrTitleBox.Text))
            {
                var fname = Path.GetFileNameWithoutExtension(dlg.FileName);
                if (fname.Contains(" - "))
                {
                    var parts = fname.Split(" - ", 2);
                    if (string.IsNullOrWhiteSpace(CrArtistBox.Text)) CrArtistBox.Text = parts[0].Trim();
                    CrTitleBox.Text = parts[1].Trim();
                }
                else
                {
                    CrTitleBox.Text = fname;
                }
            }

            await DetectBpmInternalAsync(dlg.FileName);
        }
    }

    private void BrowseChartBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select Custom Chart or MIDI File",
            Filter = "Chart / MIDI Files (*.chart;*.mid;*.midi)|*.chart;*.mid;*.midi|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() == true)
        {
            CrChartPathBox.Text = dlg.FileName;
        }
    }

    private async void DetectBpmBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CrAudioPathBox.Text) || !File.Exists(CrAudioPathBox.Text))
        {
            WpfMessageBox.Show("Please browse and select an audio file first.", "BPM Detection", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        await DetectBpmInternalAsync(CrAudioPathBox.Text);
    }

    private async Task DetectBpmInternalAsync(string audioPath)
    {
        BpmStatusLabel.Text = "🎵 Analyzing FFT & guitar pitch...";
        var res = await Task.Run(() => _audioAnalysis.AnalyzeAudio(audioPath));
        if (res.Success)
        {
            CrBpmBox.Text = res.Bpm.ToString("F1");
            BpmStatusLabel.Text = $"✓ {res.Bpm:F1} BPM ({res.TranscribedNotes.Count} pitched notes)";
        }
        else
        {
            BpmStatusLabel.Text = "⚠️ " + res.Message;
        }
    }

    private async void CreateSongBtn_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(CrTitleBox.Text) || string.IsNullOrWhiteSpace(CrArtistBox.Text))
        {
            WpfMessageBox.Show("Please enter both Song Title and Artist Name.", "Missing Info", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(CrAudioPathBox.Text) || !File.Exists(CrAudioPathBox.Text))
        {
            WpfMessageBox.Show("Please select an audio file (.mp3, .wav, or .ogg).", "Missing Audio", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool autoGen = ModeAutoGenRadio.IsChecked == true;
        if (!autoGen && (string.IsNullOrWhiteSpace(CrChartPathBox.Text) || !File.Exists(CrChartPathBox.Text)))
        {
            WpfMessageBox.Show("Please select a .chart or .mid file (or switch to AI Auto-Chart mode).", "Missing Chart", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        bool wantDemucs = CrSeparateStemsChk.IsChecked == true;
        if (wantDemucs && !_demucs.GetStatus().IsInstalled)
        {
            var ask = WpfMessageBox.Show(
                "Demucs 6-Stem + Spotify Basic Pitch AI is not installed yet.\n\nWould you like to open the AI & Tools Manager to install it with 1 click now?\n(Click 'No' to proceed using the built-in FFT/YIN Pitch Engine.)",
                "AI Engine Not Installed", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (ask == MessageBoxResult.Yes)
            {
                NavAiTools.IsChecked = true;
                return;
            }
            wantDemucs = false;
        }

        var meta = new SongCreationRequest
        {
            Title = CrTitleBox.Text.Trim(),
            Artist = CrArtistBox.Text.Trim(),
            Album = CrAlbumBox.Text.Trim(),
            Year = int.TryParse(CrYearBox.Text, out var y) ? y : DateTime.Now.Year,
            Genre = string.IsNullOrWhiteSpace(CrGenreBox.Text) ? "Rock" : CrGenreBox.Text.Trim(),
            Charter = string.IsNullOrWhiteSpace(CrCharterBox.Text) ? "ONOGAMI AI Creator" : CrCharterBox.Text.Trim(),
            Bpm = double.TryParse(CrBpmBox.Text, out var bpm) ? bpm : 120.0,
            AutoDetectBpm = true,
            AutoGenerateChart = autoGen,
            SeparateStemsWithDemucs = wantDemucs,
            AutoCompile = CrAutoCompileChk.IsChecked == true
        };

        CreateSongSubmitBtn.IsEnabled = false;
        CrProgressBar.Visibility = Visibility.Visible;
        CrProgressBar.Value = 5;
        CrResultBox.Visibility = Visibility.Collapsed;

        try
        {
            var result = await _creator.CreateSongFromFilesAsync(
                meta,
                CrAudioPathBox.Text,
                autoGen ? null : CrChartPathBox.Text,
                (pct, msg) => Dispatcher.Invoke(() =>
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
                    $"🎉 {result.Message}\n" +
                    $"• Engine: {result.ChartingEngineUsed} ({result.TranscribedNoteCount} notes transcribed)\n" +
                    $"• Tempo: {result.DetectedBpm:F1} BPM  •  Stems: {(result.StemsSeparated ? "6-Stem Isolated Guitar (Mute-on-Miss!)" : "Single Mix")}\n" +
                    $"• Project Folder: {result.ProjectDirectory}" +
                    (result.CompiledSuccessfully ? $"\n• Deployed PAK: {result.PakFilePath}\n• Deployed FSB: {result.FsbFilePath}" : "");

                RefreshSongsList();
            }
            else
            {
                CrResultBox.Background = new SolidColorBrush(WpfColor.FromRgb(43, 16, 20));
                CrResultBox.BorderBrush = new SolidColorBrush(WpfColor.FromRgb(239, 68, 68));
                CrResultText.Foreground = new SolidColorBrush(WpfColor.FromRgb(254, 202, 202));
                CrResultText.Text = "❌ " + result.Message;
            }
        }
        finally
        {
            CreateSongSubmitBtn.IsEnabled = true;
            CrProgressLabel.Text = "";
            CrProgressBar.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== PAGE 3: AI & TOOLS MANAGER ====================
    private void RefreshToolsAndAiStatus()
    {
        var comp = _compiler.GetStatusReport();
        CreatorCompilerBadge.Text = comp.Available ? "✓ Ready" : "Check Path";
        CreatorCompilerMsg.Text = comp.StatusMessage;
        AiToolsCompilerPath.Text = $"Engine Path: {(string.IsNullOrEmpty(comp.HoneycombDirectory) ? "Not Found" : comp.HoneycombDirectory)}  •  FFmpeg: {(comp.HasFfmpeg ? "✓ Ready" : "Missing")}";
        AiToolsCompilerStatus.Text = comp.Available ? "✓ Bundled & Ready" : "Needs Setup";

        var ai = _demucs.GetStatus();
        CreatorAiBadge.Text = ai.IsInstalled ? "✓ AI Ready" : "Not Installed";
        CreatorAiMsg.Text = ai.StatusMessage;
        CreatorQuickInstallAiBtn.Visibility = ai.IsInstalled ? Visibility.Collapsed : Visibility.Visible;
        if (ai.IsInstalled && CrSeparateStemsChk.IsChecked == false)
        {
            CrSeparateStemsChk.IsChecked = true;
        }

        AiStatusPillText.Text = ai.IsInstalled ? "✓ Installed & Ready" : "Not Installed";
        AiStatusPill.Background = new SolidColorBrush(ai.IsInstalled ? WpfColor.FromRgb(6, 78, 59) : WpfColor.FromRgb(59, 47, 12));
        AiStatusPillText.Foreground = new SolidColorBrush(ai.IsInstalled ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(253, 224, 71));

        AiCompPython.Text = $"Portable Python 3.11: {(ai.HasPython ? "✓ Ready" : "Not Installed")}";
        AiCompDemucs.Text = $"Meta Demucs 6-Stem (guitar.wav): {(ai.HasDemucs ? "✓ Ready" : "Not Installed")}";
        AiCompBasicPitch.Text = $"Spotify Basic Pitch MIDI AI: {(ai.HasBasicPitch ? "✓ Ready" : "Not Installed")}";
        AiCompSize.Text = $"Disk Usage: {ai.InstalledSizeBytes / (1024.0 * 1024.0):F0} MB  •  GPU: {(ai.HasNvidiaGpu ? "NVIDIA Detected" : "CPU Mode")}";

        InstallAiBtn.Content = ai.IsInstalled ? "🔄 Reinstall / Repair AI Engine" : "⬇ 1-Click Install AI Engine (Demucs 6-Stem + Basic Pitch)";
        UninstallAiBtn.Visibility = ai.IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void InstallAiEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        InstallAiBtn.IsEnabled = false;
        UninstallAiBtn.IsEnabled = false;
        AiInstallProgressPanel.Visibility = Visibility.Visible;
        AiInstallProgressBar.Value = 0;

        var (ok, msg) = await _demucs.InstallAiEngineAsync((pct, step) =>
        {
            Dispatcher.Invoke(() =>
            {
                AiInstallProgressBar.Value = pct;
                AiInstallStepText.Text = $"[{pct}%] {step}";
            });
        });

        InstallAiBtn.IsEnabled = true;
        UninstallAiBtn.IsEnabled = true;
        RefreshToolsAndAiStatus();

        WpfMessageBox.Show(msg, ok ? "AI Engine Installed!" : "AI Installation Note",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void UninstallAiEngineBtn_Click(object sender, RoutedEventArgs e)
    {
        if (WpfMessageBox.Show("Remove the portable AI Engine (Demucs + Basic Pitch) from AppData to free disk space?",
            "Uninstall AI Engine", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var (ok, msg) = _demucs.UninstallAiEngine();
        RefreshToolsAndAiStatus();
        WpfMessageBox.Show(msg, "AI Engine", MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
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
        ToggleCodeBtn.Content = _friendCodeVisible ? "🙈 Hide" : "👁 Show";
    }

    private void ToggleFriendCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        _friendCodeVisible = !_friendCodeVisible;
        UpdateFriendCodeDisplay();
    }

    private void CopyFriendCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetText(_friends.Profile.FriendCode);
        WpfMessageBox.Show($"Copied your Friend Code: {_friends.Profile.FriendCode}", "Copied", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void SaveUsernameBtn_Click(object sender, RoutedEventArgs e)
    {
        var name = ProfileUsernameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        _friends.SaveProfile(name);
        SidebarProfileName.Text = name;
        WpfMessageBox.Show($"Display name updated to '{name}'!", "Profile Saved", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void AddFriendBtn_Click(object sender, RoutedEventArgs e)
    {
        var code = AddFriendCodeBox.Text.Trim();
        var name = AddFriendNameBox.Text.Trim();
        if (string.IsNullOrEmpty(code))
        {
            WpfMessageBox.Show("Please enter a Friend Code (e.g. GH3-6475-71AF).", "Add Friend", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
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
                Child = new TextBlock { Text = "No friends added yet. Enter a friend's GH3 code above to connect!", Foreground = (WpfBrush)FindResource("TextMuted") }
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
            left.Children.Add(new TextBlock { Text = $"{f.Username}  ({f.FriendCode})", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White });
            left.Children.Add(new TextBlock
            {
                Text = f.IsTrusted ? "✓ Trusted Friend (Auto IP Sharing Enabled)" : "Regular Friend (IP Private • PIN Required)",
                FontSize = 12,
                Foreground = new SolidColorBrush(f.IsTrusted ? WpfColor.FromRgb(52, 211, 153) : WpfColor.FromRgb(148, 163, 184))
            });
            Grid.SetColumn(left, 0);
            grid.Children.Add(left);

            var right = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var trustBtn = new System.Windows.Controls.Button
            {
                Content = f.IsTrusted ? "Revoke Trust" : "Allow Trusted",
                Style = (Style)FindResource(f.IsTrusted ? "OutlineButton" : "GreenButton"),
                Margin = new Thickness(0, 0, 8, 0)
            };
            trustBtn.Click += (_, _) =>
            {
                _friends.ToggleTrust(f.FriendCode, !f.IsTrusted);
                RefreshFriendsList();
            };
            var delBtn = new System.Windows.Controls.Button { Content = "Unfriend", Style = (Style)FindResource("DangerButton") };
            delBtn.Click += (_, _) =>
            {
                _friends.RemoveFriend(f.FriendCode);
                RefreshFriendsList();
            };

            right.Children.Add(trustBtn);
            right.Children.Add(delBtn);
            Grid.SetColumn(right, 1);
            grid.Children.Add(right);

            card.Child = grid;
            FriendsListPanel.Children.Add(card);
        }
    }

    // ==================== PAGE 5: P2P SYNC & NETWORK ====================
    private void RefreshNetworkSummary()
    {
        var pub = string.IsNullOrEmpty(_nat.PublicIp) ? "Detecting..." : $"{_nat.PublicIp}:{_nat.ExternalPort ?? _port}";
        P2pNetworkSummary.Text = $"Public / NAT Address: {pub}  •  UPnP/NAT-PMP: {(_nat.IsMapped ? $"✓ Port Forwarded ({_nat.MappingMethod})" : _nat.MappingMethod)}";
        P2pPinSummary.Text = $"Your Active Session Pairing PIN: {_security.PairingPin}";
    }

    private async void RefreshNatBtn_Click(object sender, RoutedEventArgs e)
    {
        P2pNetworkSummary.Text = "Refreshing Public IP & Router UPnP / NAT-PMP mapping...";
        await _nat.InitializeAsync();
        RefreshNetworkSummary();
    }

    private async void ComparePeerBtn_Click(object sender, RoutedEventArgs e)
    {
        var addr = PeerAddressBox.Text.Trim();
        var pin = PeerPinBox.Text.Trim();
        if (string.IsNullOrEmpty(addr)) return;

        var baseUrl = addr.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? addr.TrimEnd('/') : $"http://{addr.TrimEnd('/')}";
        PeerCompareStatus.Text = $"Connecting to {baseUrl}...";
        PeerSongsPanel.Children.Clear();

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var manifestUrl = $"{baseUrl}/api/manifest" + (!string.IsNullOrEmpty(pin) ? $"?pin={Uri.EscapeDataString(pin)}" : "");
            var json = await http.GetStringAsync(manifestUrl);
            var remote = JsonSerializer.Deserialize<LibraryManifest>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            if (remote == null || remote.Songs == null)
            {
                PeerCompareStatus.Text = "Peer returned an empty library.";
                return;
            }

            var localIds = _gh3.GetInstalledCustomSongs().Select(s => s.SongId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            PeerCompareStatus.Text = $"✓ Connected to {remote.MachineName}: {remote.Songs.Count} total songs found.";

            foreach (var s in remote.Songs)
            {
                bool alreadyOwned = localIds.Contains(s.SongId);
                var card = new Border { Style = (Style)FindResource("GhCard"), Padding = new Thickness(12), Margin = new Thickness(0, 0, 0, 8) };
                var grid = new Grid();
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var info = new StackPanel();
                info.Children.Add(new TextBlock { Text = $"{s.Title} — {s.Artist}", FontSize = 14, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White });
                info.Children.Add(new TextBlock
                {
                    Text = alreadyOwned ? $"✓ Already Installed ({s.SongId})" : $"⬇ Missing Locally ({s.TotalSizeBytes / (1024.0 * 1024.0):F1} MB)",
                    FontSize = 11.5,
                    Foreground = new SolidColorBrush(alreadyOwned ? WpfColor.FromRgb(148, 163, 184) : WpfColor.FromRgb(52, 211, 153))
                });
                Grid.SetColumn(info, 0);
                grid.Children.Add(info);

                var dlBtn = new System.Windows.Controls.Button
                {
                    Content = alreadyOwned ? "⬇️ Re-Download" : "⚡ Install into Game",
                    Style = (Style)FindResource(alreadyOwned ? "OutlineButton" : "GreenButton")
                };
                dlBtn.Click += async (_, _) =>
                {
                    dlBtn.IsEnabled = false;
                    dlBtn.Content = "Downloading...";
                    try
                    {
                        var dlUrl = $"{baseUrl}/api/download/{Uri.EscapeDataString(s.SongId)}" + (!string.IsNullOrEmpty(pin) ? $"?pin={Uri.EscapeDataString(pin)}" : "");
                        var pkgBytes = await http.GetByteArrayAsync(dlUrl);
                        _saves.BackupCurrentSaves($"Before-Sync-{s.SongId}");
                        _gh3.InstallSongPackage(pkgBytes, _logger);
                        dlBtn.Content = "✓ Installed!";
                        RefreshSongsList();
                    }
                    catch (Exception ex)
                    {
                        dlBtn.IsEnabled = true;
                        dlBtn.Content = "Retry";
                        WpfMessageBox.Show($"Download failed: {ex.Message}", "P2P Sync", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                };
                Grid.SetColumn(dlBtn, 1);
                grid.Children.Add(dlBtn);

                card.Child = grid;
                PeerSongsPanel.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            PeerCompareStatus.Text = $"❌ Connection failed: {ex.Message}";
        }
    }

    // ==================== PAGE 6: IMPORT / EXPORT ====================
    private void ImportArchiveBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Import Guitar Hero III Custom Song Archive",
            Filter = "GH3 Archives (*.gh3pack;*.gh3song;*.sgh;*.zip)|*.gh3pack;*.gh3song;*.sgh;*.zip|All Files (*.*)|*.*"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            _saves.BackupCurrentSaves("Before-Archive-Import");
            var bytes = File.ReadAllBytes(dlg.FileName);
            int count = _gh3.ImportArchiveFile(bytes, Path.GetFileName(dlg.FileName), _logger);
            ImportStatusText.Text = $"🎉 Imported {count} custom song(s) from '{Path.GetFileName(dlg.FileName)}' directly into BetterGH3!";
            RefreshSongsList();
            RefreshSaveBackups();
        }
        catch (Exception ex)
        {
            ImportStatusText.Text = $"❌ Import failed: {ex.Message}";
        }
    }

    private void ExportAllPack_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export Full Customs Pack",
            Filter = "GH3-Sync Pack (*.gh3pack)|*.gh3pack",
            FileName = "GH3_Full_Customs_Pack.gh3pack"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            var songs = _gh3.GetInstalledCustomSongs();
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

            WpfMessageBox.Show($"Exported {songs.Count} songs into:\n{dlg.FileName}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show($"Export failed: {ex.Message}", "Export Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ==================== PAGE 7: CAREER SAVES ====================
    private void RefreshSaveBackups()
    {
        SaveBackupsListPanel.Children.Clear();
        var backups = _saves.GetBackups();
        if (backups.Count == 0)
        {
            SaveBackupsListPanel.Children.Add(new Border
            {
                Style = (Style)FindResource("GhCard"),
                Child = new TextBlock { Text = "No career save backups yet. Click 'Backup Now' to create a snapshot!", Foreground = (WpfBrush)FindResource("TextMuted") }
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
            stack.Children.Add(new TextBlock { Text = b.BackupId, FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White });
            stack.Children.Add(new TextBlock { Text = $"{b.Timestamp}  •  {b.FileCount} file(s)  •  {b.Path}", FontSize = 11.5, Foreground = (WpfBrush)FindResource("TextMuted") });
            Grid.SetColumn(stack, 0);
            grid.Children.Add(stack);

            var openBtn = new System.Windows.Controls.Button { Content = "📂 Open", Style = (Style)FindResource("OutlineButton") };
            openBtn.Click += (_, _) => OpenFolderInExplorer(b.Path);
            Grid.SetColumn(openBtn, 1);
            grid.Children.Add(openBtn);

            card.Child = grid;
            SaveBackupsListPanel.Children.Add(card);
        }
    }

    private void BackupSaveNowBtn_Click(object sender, RoutedEventArgs e)
    {
        var b = _saves.BackupCurrentSaves("Manual-User-Backup");
        RefreshSaveBackups();
        WpfMessageBox.Show(b.FileCount > 0 ? $"Career save backed up: {b.BackupId} ({b.FileCount} files)" : "No GH3 career save files found in Documents\\Aspyr\\Guitar Hero III\\Save.",
            "Career Save Backup", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void RestoreLatestSaveBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_saves.RestoreLatestBackup())
            WpfMessageBox.Show("Restored latest career save cleanly!", "Success", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            WpfMessageBox.Show("No backups available to restore.", "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    // ==================== PAGE 8: LOGS & GAME CONFIG ====================
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
        var dlg = new Window
        {
            Title = "Game Directory & Compiler Configuration",
            Width = 560,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ResizeMode = ResizeMode.NoResize,
            Background = new SolidColorBrush(WpfColor.FromRgb(17, 19, 27)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock { Text = "⚙️ Guitar Hero III Directory", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });

        var gamePathRow = new DockPanel { Margin = new Thickness(0, 0, 0, 14) };
        var gamePathBox = new System.Windows.Controls.TextBox { Text = _gh3.GamePath };
        var browseGameBtn = new System.Windows.Controls.Button { Content = "📂 Browse", Style = (Style)FindResource("OutlineButton"), Margin = new Thickness(8, 0, 0, 0) };
        browseGameBtn.Click += (_, _) =>
        {
            using var fbd = new WinForms.FolderBrowserDialog { SelectedPath = gamePathBox.Text };
            if (fbd.ShowDialog() == WinForms.DialogResult.OK) gamePathBox.Text = fbd.SelectedPath;
        };
        DockPanel.SetDock(browseGameBtn, Dock.Right);
        gamePathRow.Children.Add(browseGameBtn);
        gamePathRow.Children.Add(gamePathBox);
        stack.Children.Add(gamePathRow);

        stack.Children.Add(new TextBlock { Text = "⚡ Custom Honeycomb Override Path (Optional — Bundled engine used automatically)", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 5) });
        var hcBox = new System.Windows.Controls.TextBox { Text = AppConfig.Current.HoneycombPath, Margin = new Thickness(0, 0, 0, 14) };
        stack.Children.Add(hcBox);

        var directChk = new System.Windows.Controls.CheckBox
        {
            Content = "Install downloaded & compiled songs directly into Guitar Hero III (Recommended)",
            IsChecked = AppConfig.Current.DirectInstallToGame,
            Margin = new Thickness(0, 0, 0, 18)
        };
        stack.Children.Add(directChk);

        var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        var saveBtn = new System.Windows.Controls.Button { Content = "✓ Save Settings", Style = (Style)FindResource("FlameButton") };
        saveBtn.Click += (_, _) =>
        {
            AppConfig.Current.GamePath = gamePathBox.Text.Trim();
            AppConfig.Current.HoneycombPath = hcBox.Text.Trim();
            AppConfig.Current.DirectInstallToGame = directChk.IsChecked == true;
            AppConfig.Save();
            dlg.Close();
            RefreshAllUiState();
        };
        btns.Children.Add(saveBtn);
        stack.Children.Add(btns);

        dlg.Content = stack;
        dlg.ShowDialog();
    }
}
