using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

using WpfBrush = System.Windows.Media.Brush;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = GH3_Sync.Views.GuitarHeroModalDialog;

namespace GH3_Sync.Views;

/// <summary>
/// GH3-Sync v1.1.0 ("Encore & Zero-Trust" Edition) feature & security UI handlers:
/// - Streamer Privacy Mode (1-click mask IPs, PINs, Friend Codes & Tunnel URLs)
/// - Ctrl+K Spotlight Quick-Command Palette
/// - Real Audio Waveform Scrubber + Volume/Mute
/// - Compact Setlist View + Group By Artist / 8-Tier GH3 Venue Tour
/// - 1-Click Concert Medley Builder, Chart Quality Scorecard Comparator, Tour Trophies Wall
/// - Streamer !sr Song Request Queue, Discord Setlist Exporter, Setlist Draft Match Tracker
/// - Live Security Self-Test (A+ Hardened) & Optional Per-Download Host Approval Popup
/// </summary>
public partial class MainWindow
{
    private AudioWaveformProfile? _activeWaveform;
    private double _lastPreMuteVolume = 0.85;
    private bool _isMuted;

    // ==================== STREAMER PRIVACY MODE ====================
    private void StreamerPrivacyBtn_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.StreamerPrivacyMode = !AppConfig.Current.StreamerPrivacyMode;
        AppConfig.Save();
        ApplyStreamerPrivacyVisuals();
        ShowStageToast(
            AppConfig.Current.StreamerPrivacyMode ? "👁 STREAMER PRIVACY ON" : "👁 STREAMER PRIVACY OFF",
            AppConfig.Current.StreamerPrivacyMode
                ? "All IPs, PINs, Friend Codes & Tunnel URLs are now masked on screen."
                : "Network addresses & Friend Codes restored.",
            AppConfig.Current.StreamerPrivacyMode ? "#38BDF8" : "#F97316");
    }

    private void ApplyStreamerPrivacyVisuals()
    {
        bool priv = AppConfig.Current.StreamerPrivacyMode;
        if (StreamerPrivacyBtn != null)
        {
            StreamerPrivacyBtn.Content = priv ? "🔒 Privacy: ON" : "👁 Privacy";
            StreamerPrivacyBtn.Style = (Style)FindResource(priv ? "BlueButton" : "OutlineButton");
        }
        if (SidebarFriendCode != null)
            SidebarFriendCode.Text = priv ? "GH3-••••-••••" : _friends.Profile.FriendCode;
        if (ProfileFriendCodeBox != null)
            ProfileFriendCodeBox.Text = priv ? "GH3-••••-•••• (Privacy Mode)" : (_friendCodeVisible ? _friends.Profile.FriendCode : "GH3-••••-••••");

        RefreshNetworkSummary();
        RefreshMeshAndTunnelStatus();
    }

    private static string MaskIfPrivate(string text)
        => AppConfig.Current.StreamerPrivacyMode ? "•••.•••.•••.••• (Hidden)" : text;

    // ==================== CTRL+K SPOTLIGHT QUICK-COMMAND PALETTE ====================
    private void QuickPaletteBtn_Click(object sender, RoutedEventArgs e) => OpenCommandPalette();

    private void OpenCommandPalette()
    {
        if (CommandPaletteOverlay == null) return;
        CommandPaletteOverlay.Visibility = Visibility.Visible;
        CommandPaletteSearchBox.Text = "";
        RenderCommandPaletteItems("");
        CommandPaletteSearchBox.Focus();
    }

    private void CloseCommandPalette()
    {
        if (CommandPaletteOverlay != null)
            CommandPaletteOverlay.Visibility = Visibility.Collapsed;
    }

    private void CommandPaletteBackdrop_Click(object sender, MouseButtonEventArgs e)
    {
        if (e.Source == CommandPaletteOverlay) CloseCommandPalette();
    }

    private void CommandPaletteSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => RenderCommandPaletteItems(CommandPaletteSearchBox.Text ?? "");

    private void CommandPaletteSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CloseCommandPalette(); e.Handled = true; }
        else if (e.Key == Key.Enter && CommandPaletteResultsPanel.Children.Count > 0 && CommandPaletteResultsPanel.Children[0] is System.Windows.Controls.Button firstBtn)
        {
            firstBtn.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            e.Handled = true;
        }
    }

    private void RenderCommandPaletteItems(string query)
    {
        if (CommandPaletteResultsPanel == null) return;
        CommandPaletteResultsPanel.Children.Clear();
        var q = query.Trim();

        var actions = new List<(string Label, string Sub, Action Run)>
        {
            ("▶ Launch Guitar Hero III", "Pre-flight check & launch GH3.exe", () => LaunchGh3Btn_Click(this, new RoutedEventArgs())),
            ("👁 Toggle Streamer Privacy Mode", "Mask/unmask all IPs, PINs & Friend Codes", () => StreamerPrivacyBtn_Click(this, new RoutedEventArgs())),
            ("🎮 Universal Controller Tester", "Test DualSense Edge, DualSense, Xbox & Guitar inputs", () => OpenGuitarTesterBtn_Click(this, new RoutedEventArgs())),
            ("🎯 Audio / Video Lag Calibration", "12-beat hardware latency calibration mini-game", () => CalibrateLagBtn_Click(this, new RoutedEventArgs())),
            ("🛡️ Run Live Security Self-Test", "Probe local lockdown, CSRF shield, HMAC & DPAPI vault", () => { NavSettings.IsChecked = true; SettingsSubTab_Click(TabSetSecurityBtn, new RoutedEventArgs()); RunSecuritySelfTestBtn_Click(this, new RoutedEventArgs()); }),
            ("🎰 Spin Encore Roulette", "Pick a random installed custom song", () => SpinRouletteBtn_Click(this, new RoutedEventArgs())),
            ("🌐 Toggle HTTPS Quick Tunnel", "Zero-config Cloudflare HTTPS tunnel for CGNAT P2P", () => { NavP2P.IsChecked = true; ToggleTunnelBtn_Click(this, new RoutedEventArgs()); }),
            ("🎸 Concert Medley Builder", "Stitch 2–4 songs into one continuous Concert Medley", () => MedleyBuilderBtn_Click(this, new RoutedEventArgs())),
            ("⚖️ Chart Quality Scorecard", "Compare Note Count, Peak NPS, HOPO%, Grades & Tiers", () => CompareChartsBtn_Click(this, new RoutedEventArgs())),
            ("🏆 Tour Stats & Trophies Wall", "View lifetime notes hit, FCs & unlocked trophies", () => TrophiesWallBtn_Click(this, new RoutedEventArgs())),
            ("🎤 Streamer Song Request Queue", "Manage !sr song requests & push to OBS overlay", () => SongRequestsBtn_Click(this, new RoutedEventArgs())),
            ("⚔️ Multiplayer Setlist Draft", "Best-of-3 setlist draft & match tracker", () => SetlistDraftBtn_Click(this, new RoutedEventArgs())),
            ("📺 Copy OBS Overlay URL", "Copy local-only http://127.0.0.1 OBS browser source", () => CopyObsOverlayBtn_Click(this, new RoutedEventArgs())),
            ("📋 Copy Discord Setlist", "Copy formatted setlist with stars & tiers", () => CopyDiscordSetlistBtn_Click(this, new RoutedEventArgs()))
        };

        foreach (var song in _allSongs.Take(40))
        {
            var captured = song;
            actions.Add(($"🎵 {captured.Title} — {captured.Artist}", $"Play audio preview • {captured.SongId}", () =>
            {
                var ap = Gh3Service.FindSongAudioPath(captured);
                if (!string.IsNullOrEmpty(ap)) PlayAudioPreview(ap, captured.Title, captured);
                else ShowStageToast("No Audio Found", $"No preview audio in '{captured.Title}'.", "#EAB308");
            }));
        }

        var matched = string.IsNullOrEmpty(q)
            ? actions.Take(10)
            : actions.Where(a => a.Label.Contains(q, StringComparison.OrdinalIgnoreCase) || a.Sub.Contains(q, StringComparison.OrdinalIgnoreCase)).Take(12);

        foreach (var item in matched)
        {
            var btn = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("OutlineButton"),
                HorizontalContentAlignment = System.Windows.HorizontalAlignment.Left,
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 5),
                Content = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = item.Label, FontSize = 13, FontWeight = FontWeights.Bold, Foreground = (WpfBrush)FindResource("TextPrimary") },
                        new TextBlock { Text = item.Sub, FontSize = 11, Foreground = (WpfBrush)FindResource("TextMuted") }
                    }
                }
            };
            var act = item.Run;
            btn.Click += (_, _) => { CloseCommandPalette(); act(); };
            CommandPaletteResultsPanel.Children.Add(btn);
        }
    }

    // ==================== REAL AUDIO WAVEFORM SCRUBBER & VOLUME / MUTE ====================
    private void LoadWaveformForPreviewAsync(string filePath)
    {
        _activeWaveform = null;
        if (PlayerWaveformCanvas != null) PlayerWaveformCanvas.Children.Clear();
        _ = Task.Run(async () =>
        {
            var wf = await AudioWaveformAndSfxService.ExtractWaveformAsync(filePath, 140);
            if (wf != null)
            {
                Dispatcher.Invoke(() =>
                {
                    _activeWaveform = wf;
                    RenderAudioWaveformBar();
                });
            }
        });
    }

    private void RenderAudioWaveformBar()
    {
        if (PlayerWaveformCanvas == null || _activeWaveform == null || _activeWaveform.Peaks.Length == 0) return;
        double w = PlayerWaveformCanvas.ActualWidth;
        double h = PlayerWaveformCanvas.ActualHeight;
        if (w < 40 || h < 10) return;

        PlayerWaveformCanvas.Children.Clear();
        int n = _activeWaveform.Peaks.Length;
        double step = w / n;
        double barW = Math.Max(1.5, step - 1.0);
        double progressRatio = PlayerSeekSlider.Maximum > 0 ? Math.Clamp(PlayerSeekSlider.Value / PlayerSeekSlider.Maximum, 0, 1) : 0;

        for (int i = 0; i < n; i++)
        {
            double peak = _activeWaveform.Peaks[i];
            double bh = Math.Max(3.0, peak * (h - 2.0));
            bool played = (i / (double)n) <= progressRatio;
            var rect = new System.Windows.Shapes.Rectangle
            {
                Width = barW,
                Height = bh,
                RadiusX = 1,
                RadiusY = 1,
                Fill = new SolidColorBrush(played ? WpfColor.FromRgb(249, 115, 22) : WpfColor.FromArgb(135, 148, 163, 184))
            };
            Canvas.SetLeft(rect, i * step);
            Canvas.SetTop(rect, (h - bh) * 0.5);
            PlayerWaveformCanvas.Children.Add(rect);
        }
    }

    private void PlayerWaveformCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_player.NaturalDuration.HasTimeSpan || PlayerWaveformCanvas.ActualWidth <= 10) return;
        double ratio = Math.Clamp(e.GetPosition(PlayerWaveformCanvas).X / PlayerWaveformCanvas.ActualWidth, 0.0, 1.0);
        var targetSec = ratio * _player.NaturalDuration.TimeSpan.TotalSeconds;
        _player.Position = TimeSpan.FromSeconds(targetSec);
        PlayerSeekSlider.Value = targetSec;
        RenderAudioWaveformBar();
    }

    private void PlayerVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        _player.Volume = Math.Clamp(e.NewValue, 0.0, 1.0);
        _isMuted = _player.Volume <= 0.001;
        if (PlayerMuteBtn != null) PlayerMuteBtn.Content = _isMuted ? "🔇" : "🔊";
        AppConfig.Current.AudioPreviewVolume = _player.Volume;
    }

    private void PlayerMuteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_isMuted)
        {
            _isMuted = false;
            PlayerVolumeSlider.Value = Math.Max(0.25, _lastPreMuteVolume);
            PlayerMuteBtn.Content = "🔊";
        }
        else
        {
            _lastPreMuteVolume = Math.Max(0.25, PlayerVolumeSlider.Value);
            _isMuted = true;
            PlayerVolumeSlider.Value = 0;
            PlayerMuteBtn.Content = "🔇";
        }
    }

    // ==================== COMPACT SETLIST VIEW & GROUPING ====================
    private void CompactViewBtn_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.CompactSetlistView = !AppConfig.Current.CompactSetlistView;
        AppConfig.Save();
        CompactViewBtn.Content = AppConfig.Current.CompactSetlistView ? "▦ Cards" : "☰ Compact";
        RenderSongs();
    }

    private void GroupByCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || GroupByCombo == null) return;
        AppConfig.Current.GroupByMode = GroupByCombo.SelectedIndex switch
        {
            1 => "Artist",
            2 => "Tier",
            _ => "None"
        };
        AppConfig.Save();
        RenderSongs();
    }

    private Border BuildCompactSongRow(CustomSongInfo song)
    {
        var row = new Border
        {
            Background = (WpfBrush)FindResource("BgCard"),
            BorderBrush = (WpfBrush)FindResource(song.IsInLobbySetlist ? "AccentFlame" : "BorderSubtle"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 6, 10, 6),
            Margin = new Thickness(0, 0, 0, 5)
        };
        var dp = new DockPanel();

        var rightBtns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        var editBtn = new System.Windows.Controls.Button { Content = "Edit", Style = (Style)FindResource("OutlineButton"), Padding = new Thickness(8, 3, 8, 3), Margin = new Thickness(0, 0, 5, 0) };
        editBtn.Click += (_, _) => OpenEditSongDialog(song);
        rightBtns.Children.Add(editBtn);

        var moreBtn = new System.Windows.Controls.Button { Content = "⋮", Style = (Style)FindResource("OutlineButton"), FontWeight = FontWeights.Bold, Padding = new Thickness(8, 2, 8, 2), ToolTip = "More Actions" };
        moreBtn.Click += (_, _) =>
        {
            var menu = BuildSongContextMenu(song);
            menu.PlacementTarget = moreBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        };
        rightBtns.Children.Add(moreBtn);

        row.ContextMenu = BuildSongContextMenu(song);
        DockPanel.SetDock(rightBtns, Dock.Right);
        dp.Children.Add(rightBtns);

        var chk = new System.Windows.Controls.CheckBox
        {
            IsChecked = _selectedExportIds.Contains(song.SongId),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };
        chk.Checked += (_, _) => { _selectedExportIds.Add(song.SongId); UpdateBatchSelectionBarUi(); };
        chk.Unchecked += (_, _) => { _selectedExportIds.Remove(song.SongId); UpdateBatchSelectionBarUi(); };
        DockPanel.SetDock(chk, Dock.Left);
        dp.Children.Add(chk);

        var score = AppConfig.GetHighScore(song.SongId);
        var scoreStr = score != null ? $"  •  {score.BestScore:N0} ({score.Stars}★{(score.IsFullCombo ? " FC" : "")})" : "";
        dp.Children.Add(new TextBlock
        {
            Text = $"{(song.IsFavorite ? "★ " : "")}{song.Title}  —  {song.Artist}{scoreStr}",
            FontSize = 12.5,
            FontWeight = FontWeights.SemiBold,
            Foreground = (WpfBrush)FindResource("TextPrimary"),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        row.Child = dp;
        return row;
    }

    // ==================== STUDIO PRO TOOLS: MEDLEY, SCORECARD, CALIBRATE, TROPHIES, REQUESTS, DISCORD, DRAFT ====================
    private async void MedleyBuilderBtn_Click(object sender, RoutedEventArgs e)
    {
        var picked = _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList();
        if (picked.Count < 2 || picked.Count > 4)
        {
            WpfMessageBox.Show("Check 2 to 4 custom songs in My Customs first, then click '🎸 Medley' to stitch them into a single playable Live Concert Medley!",
                "Concert Medley Builder", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        string defaultTitle = $"Live Encore Medley ({picked.Count} Songs)";
        PageCustoms_StageHud.Report(25, "BUILDING CONCERT MEDLEY", $"Stitching stems & calculating crossfade markers for {picked.Count} tracks...");
        try
        {
            var (ok, msg) = await _studioTools.BuildConcertMedleyAsync(picked, defaultTitle, autoCompile: true);
            if (ok)
            {
                PageCustoms_StageHud.Finish($"Live Concert Medley created cleanly! ({picked.Count} songs)");
                AudioWaveformAndSfxService.PlayCompilePowerChord();
                RefreshSongsList();
                ShowStageToast("🎸 MEDLEY BUILT!", msg, "#22C55E");
            }
            else
            {
                PageCustoms_StageHud.SetError("MEDLEY BUILD FAILED", msg);
                WpfMessageBox.Show(msg, "Concert Medley Builder", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            PageCustoms_StageHud.SetError("MEDLEY ERROR", ex.Message);
        }
    }

    private void CompareChartsBtn_Click(object sender, RoutedEventArgs e)
    {
        var cards = new List<ChartQualityScorecard>();
        var pool = _selectedExportIds.Count > 0
            ? _allSongs.Where(s => _selectedExportIds.Contains(s.SongId)).ToList()
            : _allSongs;

        foreach (var s in pool)
        {
            var ch = ChartDoctorService.FindChartFileInProject(s.ProjectDirectory);
            if (!string.IsNullOrEmpty(ch))
                cards.Add(ChartStudioToolsService.ComputeChartQualityScorecard(ch, $"{s.Title} — {s.Artist}"));
        }

        var dlg = new Window
        {
            Title = $"Chart Quality Scorecard & GH3 Venue Tour Ranking ({cards.Count} Charts)",
            Width = 760,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(WpfColor.FromRgb(14, 16, 24)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var root = new DockPanel { Margin = new Thickness(18) };
        var hdr = new TextBlock { Text = "⚖️ Chart Quality Scorecard & 8-Tier Venue Ranking", FontSize = 17, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(hdr, Dock.Top);
        root.Children.Add(hdr);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var stack = new StackPanel();
        if (cards.Count == 0)
        {
            stack.Children.Add(new TextBlock { Text = "No editable .chart projects found to score.", Foreground = System.Windows.Media.Brushes.Gray });
        }
        else
        {
            foreach (var c in cards.OrderBy(x => x.VenueTier).ThenByDescending(x => x.PeakNps))
            {
                var b = new Border
                {
                    Background = new SolidColorBrush(WpfColor.FromRgb(22, 26, 39)),
                    BorderBrush = new SolidColorBrush(c.QualityGrade == "S" ? WpfColor.FromRgb(34, 197, 94) : WpfColor.FromRgb(51, 65, 85)),
                    BorderThickness = new Thickness(1.2),
                    CornerRadius = new CornerRadius(9),
                    Padding = new Thickness(12, 9, 12, 9),
                    Margin = new Thickness(0, 0, 0, 8)
                };
                b.Child = new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"[{c.QualityGrade}]  {c.SongTitle}", FontSize = 13.5, FontWeight = FontWeights.Bold, Foreground = System.Windows.Media.Brushes.White },
                        new TextBlock { Text = c.SummaryLine, FontSize = 11.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)), Margin = new Thickness(0, 3, 0, 0), TextWrapping = TextWrapping.Wrap }
                    }
                };
                stack.Children.Add(b);
            }
        }
        scroll.Content = stack;
        root.Children.Add(scroll);
        dlg.Content = root;
        dlg.ShowDialog();
    }

    private void CalibrateLagBtn_Click(object sender, RoutedEventArgs e)
    {
        var cal = new CalibrationWindow { Owner = this };
        if (cal.ShowDialog() == true)
        {
            ShowStageToast("🎯 CALIBRATION SAVED!", $"Hardware A/V lag offset set to {cal.MeasuredCalibrationMs:+0;-0;0} ms.", "#22C55E");
        }
    }

    private void TrophiesWallBtn_Click(object sender, RoutedEventArgs e)
    {
        var st = AppConfig.Current.LifetimeStats ?? new TourStats();
        int customs = _allSongs.Count;
        int fcs = Math.Max(st.TotalFullCombos, AppConfig.Current.LocalHighScores.Values.Count(v => v.IsFullCombo));
        if (customs >= 1) AppConfig.UnlockTrophy("FirstCustom");
        if (customs >= 10) AppConfig.UnlockTrophy("SetlistCollector");
        if (AppConfig.Current.Usage.P2pSyncCount >= 1) AppConfig.UnlockTrophy("MultiplayerPioneer");
        if (AppConfig.Current.Usage.SongCreatorCount >= 1) AppConfig.UnlockTrophy("OnogamiArchitect");

        var trophyDefs = new (string Key, string Icon, string Title, string Desc)[]
        {
            ("FirstCustom", "🎸", "Opening Act", "Install your first custom song in BetterGH3"),
            ("SetlistCollector", "💿", "World Tour Collector", "Have 10+ custom songs installed simultaneously"),
            ("OnogamiArchitect", "⚡", "ONOGAMI Architect", "Create a custom song with ONOGAMI Song Creator"),
            ("FirstFullCombo", "👑", "Flawless Victory (FC)", "Hit a 100% Full Combo in Playable Highway Previewer"),
            ("SoloShredder", "✂️", "Solo Surgeon", "Build a standalone Playable Solo Cut"),
            ("MedleyArchitect", "🔥", "Encore Maestro", "Stitch a multi-song Live Concert Medley"),
            ("MultiplayerPioneer", "🌐", "Battle of the Bands", "Complete a P2P custom sync with a Trusted Friend"),
            ("ZeroTrustGuardian", "🛡️", "Zero-Trust Guardian", "Pass the Live Security Self-Test with Grade A+")
        };

        var dlg = new Window
        {
            Title = "🏆 Legends of Rock — Tour Stats & Trophies Wall",
            Width = 640,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(WpfColor.FromRgb(14, 16, 24)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var sp = new StackPanel { Margin = new Thickness(20) };
        sp.Children.Add(new TextBlock { Text = "🏆 Legends of Rock — Tour Stats & Trophies", FontSize = 18, FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock
        {
            Text = $"Customs Installed: {customs}  •  Lifetime Notes Hit: {st.TotalNotesHit:N0}  •  Full Combos: {fcs}  •  Solo Cuts: {st.TotalSolosExtracted}  •  Medleys: {st.TotalMedleysBuilt}",
            FontSize = 12,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 186, 116)),
            Margin = new Thickness(0, 4, 0, 14)
        });

        var grid = new System.Windows.Controls.Primitives.UniformGrid { Columns = 2 };
        foreach (var (key, icon, title, desc) in trophyDefs)
        {
            bool unlocked = st.UnlockedTrophies?.Contains(key, StringComparer.OrdinalIgnoreCase) == true;
            var card = new Border
            {
                Background = new SolidColorBrush(unlocked ? WpfColor.FromRgb(22, 34, 48) : WpfColor.FromRgb(18, 20, 29)),
                BorderBrush = new SolidColorBrush(unlocked ? WpfColor.FromRgb(245, 158, 11) : WpfColor.FromRgb(45, 55, 72)),
                BorderThickness = new Thickness(1.4),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 9, 12, 9),
                Margin = new Thickness(4),
                Opacity = unlocked ? 1.0 : 0.55
            };
            card.Child = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = $"{icon} {title} {(unlocked ? "✓" : "🔒")}", FontSize = 13, FontWeight = FontWeights.Bold, Foreground = new SolidColorBrush(unlocked ? WpfColor.FromRgb(253, 224, 71) : WpfColor.FromRgb(148, 163, 184)) },
                    new TextBlock { Text = desc, FontSize = 11, Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) }
                }
            };
            grid.Children.Add(card);
        }
        sp.Children.Add(grid);
        dlg.Content = new ScrollViewer { Content = sp, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        dlg.ShowDialog();
    }

    private void SongRequestsBtn_Click(object sender, RoutedEventArgs e)
    {
        AppConfig.Current.SongRequests ??= new List<SongRequestItem>();
        var dlg = new Window
        {
            Title = "🎤 Streamer !sr Song Request Queue (OBS Overlay Linked)",
            Width = 620,
            Height = 490,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            Background = new SolidColorBrush(WpfColor.FromRgb(14, 16, 24)),
            Foreground = System.Windows.Media.Brushes.White
        };

        var root = new DockPanel { Margin = new Thickness(18) };
        var topPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
        topPanel.Children.Add(new TextBlock { Text = "🎤 Streamer !sr Song Request Queue", FontSize = 17, FontWeight = FontWeights.Bold });
        topPanel.Children.Add(new TextBlock { Text = "Add viewer requests — GH3-Sync auto-matches your installed customs (or searches Chorus Encore if missing) and updates OBS Now Playing!", FontSize = 11.5, Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 8) });

        var addGrid = new Grid();
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(8) });
        addGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var viewerBox = new System.Windows.Controls.TextBox { Text = "Viewer" };
        var songQueryBox = new System.Windows.Controls.TextBox { ToolTip = "Song title or artist requested" };
        var addBtn = new System.Windows.Controls.Button { Content = "+ Add Request", Style = (Style)FindResource("FlameButton") };
        Grid.SetColumn(viewerBox, 0); Grid.SetColumn(songQueryBox, 2); Grid.SetColumn(addBtn, 4);
        addGrid.Children.Add(viewerBox); addGrid.Children.Add(songQueryBox); addGrid.Children.Add(addBtn);
        topPanel.Children.Add(addGrid);
        DockPanel.SetDock(topPanel, Dock.Top);
        root.Children.Add(topPanel);

        var listPanel = new StackPanel();
        void RenderQueue()
        {
            listPanel.Children.Clear();
            foreach (var req in AppConfig.Current.SongRequests.ToList())
            {
                var match = _allSongs.FirstOrDefault(s =>
                    s.Title.Contains(req.Query, StringComparison.OrdinalIgnoreCase) ||
                    s.Artist.Contains(req.Query, StringComparison.OrdinalIgnoreCase));
                var card = new Border { Background = new SolidColorBrush(WpfColor.FromRgb(22, 26, 39)), BorderBrush = new SolidColorBrush(match != null ? WpfColor.FromRgb(34, 197, 94) : WpfColor.FromRgb(234, 179, 8)), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 7, 10, 7), Margin = new Thickness(0, 0, 0, 6) };
                var dp = new DockPanel();
                var btns = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
                if (match != null)
                {
                    var playObs = new System.Windows.Controls.Button { Content = "▶ Play & OBS", Style = (Style)FindResource("GreenButton"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0) };
                    playObs.Click += (_, _) =>
                    {
                        ObsOverlayService.UpdateNowPlaying(match.Title, match.Artist, $"Requested by {req.Requester}", "Expert", 0, isPlaying: true);
                        var ap = Gh3Service.FindSongAudioPath(match);
                        if (!string.IsNullOrEmpty(ap)) PlayAudioPreview(ap, match.Title, match);
                        ShowStageToast("📺 OBS UPDATED!", $"Now Playing '{match.Title}' (Requested by {req.Requester})", "#22C55E");
                    };
                    btns.Children.Add(playObs);
                }
                else
                {
                    var searchChorus = new System.Windows.Controls.Button { Content = "Search Chorus", Style = (Style)FindResource("BlueButton"), Padding = new Thickness(9, 4, 9, 4), Margin = new Thickness(0, 0, 6, 0) };
                    searchChorus.Click += (_, _) =>
                    {
                        dlg.Close();
                        NavCreator.IsChecked = true;
                        CommunitySearchBox.Text = req.Query;
                        CommunitySearchBtn_Click(this, new RoutedEventArgs());
                    };
                    btns.Children.Add(searchChorus);
                }
                var doneBtn = new System.Windows.Controls.Button { Content = "✓ Done", Style = (Style)FindResource("OutlineButton"), Padding = new Thickness(8, 4, 8, 4) };
                doneBtn.Click += (_, _) => { AppConfig.Current.SongRequests.Remove(req); AppConfig.Save(); RenderQueue(); };
                btns.Children.Add(doneBtn);
                DockPanel.SetDock(btns, Dock.Right);
                dp.Children.Add(btns);
                dp.Children.Add(new StackPanel
                {
                    Children =
                    {
                        new TextBlock { Text = $"{req.Query}  (by {req.Requester})", FontSize = 13, FontWeight = FontWeights.Bold },
                        new TextBlock { Text = match != null ? $"✓ Installed: {match.Title} — {match.Artist}" : "Not installed yet — click Search Chorus", FontSize = 11, Foreground = new SolidColorBrush(match != null ? WpfColor.FromRgb(110, 231, 183) : WpfColor.FromRgb(253, 224, 71)) }
                    }
                });
                card.Child = dp;
                listPanel.Children.Add(card);
            }
        }

        addBtn.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(songQueryBox.Text)) return;
            AppConfig.Current.SongRequests.Add(new SongRequestItem { Requester = string.IsNullOrWhiteSpace(viewerBox.Text) ? "Viewer" : viewerBox.Text.Trim(), Query = songQueryBox.Text.Trim() });
            AppConfig.Save();
            songQueryBox.Text = "";
            RenderQueue();
        };

        RenderQueue();
        root.Children.Add(new ScrollViewer { Content = listPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        dlg.Content = root;
        dlg.ShowDialog();
    }

    private void CopyDiscordSetlistBtn_Click(object sender, RoutedEventArgs e)
    {
        var lobby = _allSongs.Where(s => s.IsInLobbySetlist).ToList();
        var list = lobby.Count > 0 ? lobby : _allSongs.Take(15).ToList();
        var sb = new StringBuilder();
        sb.AppendLine($"🎸 **{_friends.Profile.Username}'s Guitar Hero III Setlist ({list.Count} Songs)**");
        sb.AppendLine("```");
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var sc = AppConfig.GetHighScore(s.SongId);
            var scTxt = sc != null ? $" | Best: {sc.BestScore:N0} ({sc.Stars}★{(sc.IsFullCombo ? " FC" : "")})" : "";
            sb.AppendLine($"{i + 1,2}. {s.Title} — {s.Artist}{scTxt}");
        }
        sb.AppendLine("```");
        sb.AppendLine("_Synced with GH3-Sync v1.1.0_");
        try { System.Windows.Clipboard.SetText(sb.ToString()); } catch { }
        ShowStageToast("📋 DISCORD SETLIST COPIED!", $"Copied {list.Count} song(s) formatted for Discord!", "#5865F2");
    }

    private void SetlistDraftBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_allSongs.Count < 2)
        {
            WpfMessageBox.Show("Install at least 2 custom songs to draft a Best-of-3 Multiplayer Match!", "Setlist Draft", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var rng = new Random();
        var hostPick = _allSongs[rng.Next(_allSongs.Count)];
        var pool = _lastPeerManifest?.Songs?.Count > 0 ? _lastPeerManifest.Songs : _allSongs;
        var peerPick = pool.FirstOrDefault(s => !s.SongId.Equals(hostPick.SongId, StringComparison.OrdinalIgnoreCase)) ?? _allSongs.Last();
        var tiebreaker = _allSongs.FirstOrDefault(s => !s.SongId.Equals(hostPick.SongId, StringComparison.OrdinalIgnoreCase) && !s.SongId.Equals(peerPick.SongId, StringComparison.OrdinalIgnoreCase)) ?? hostPick;

        var summary =
            $"⚔️ BEST-OF-3 MULTIPLAYER SETLIST DRAFT\n\n" +
            $"Round 1 (Your Pick):   {hostPick.Title} — {hostPick.Artist}\n" +
            $"Round 2 (Peer Pick):   {peerPick.Title} — {peerPick.Artist}\n" +
            $"Round 3 (Tiebreaker):  {tiebreaker.Title} — {tiebreaker.Artist}\n\n" +
            $"Tag all 3 songs into your Active Lobby Setlist now?";

        if (WpfMessageBox.Show(summary, "⚔️ Setlist Draft — Best of 3", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            AppConfig.Current.LobbySetlistIds = new[] { hostPick.SongId, peerPick.SongId, tiebreaker.SongId }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            AppConfig.Current.LifetimeStats.TotalMatchesPlayed++;
            AppConfig.Save();
            RefreshSongsList();
            AudioWaveformAndSfxService.PlayStarPowerChime();
            ShowStageToast("⚔️ MATCH SETLIST LOCKED!", "3 drafted songs tagged to your Active Lobby Setlist!", "#38BDF8");
        }
    }

    // ==================== ZERO-TRUST SECURITY SELF-TEST & DOWNLOAD APPROVAL ====================
    private void SettingsSfxChk_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.EnableSoundEffects = SettingsSfxChk.IsChecked == true;
        AppConfig.Save();
    }

    private void SettingsRequireDownloadApprovalChk_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        AppConfig.Current.RequireHostDownloadApproval = SettingsRequireDownloadApprovalChk.IsChecked == true;
        AppConfig.Save();
        ShowStageToast("🔒 DOWNLOAD POLICY UPDATED",
            AppConfig.Current.RequireHostDownloadApproval
                ? "Host confirmation popup REQUIRED for every incoming friend download."
                : "Trusted Friends can download automatically.",
            "#38BDF8");
    }

    private async void RunSecuritySelfTestBtn_Click(object sender, RoutedEventArgs e)
    {
        RunSecuritySelfTestBtn.IsEnabled = false;
        RunSecuritySelfTestBtn.Content = "Running Self-Test...";
        try
        {
            var (grade, results) = await _security.RunLiveSecuritySelfTestAsync(_port);
            var lines = results.Select(r => $"{(r.Passed ? "[PASS]" : "[FAIL]")} {r.Check} — {r.Detail}");
            SecurityActionStatusText.Text = $"SECURITY GRADE: {grade} ({results.Count(r => r.Passed)}/{results.Count} passed)\n" + string.Join(Environment.NewLine, lines);
            RefreshSecurityAuditPanel();
            if (grade.StartsWith("A+"))
            {
                AppConfig.UnlockTrophy("ZeroTrustGuardian");
                AudioWaveformAndSfxService.PlayStarPowerChime();
                ShowStageToast("🛡️ GRADE A+ HARDENED!", $"All {results.Count} live security probes passed!", "#22C55E");
            }
            else
            {
                ShowStageToast($"🛡️ SECURITY GRADE: {grade}", "Check Security & Privacy audit details.", "#EAB308");
            }
        }
        finally
        {
            RunSecuritySelfTestBtn.IsEnabled = true;
            RunSecuritySelfTestBtn.Content = "🛡️ Run Live Security Self-Test";
        }
    }

    private Task<bool> PromptHostDownloadApprovalAsync(string remoteIp, string songId, string songTitle, long sizeBytes)
    {
        var tcs = new TaskCompletionSource<bool>();
        Dispatcher.Invoke(() =>
        {
            var res = WpfMessageBox.Show(
                $"Incoming Trusted Peer Download Request\n\n" +
                $"Peer IP: {remoteIp}\n" +
                $"Song: {songTitle} ({songId})\n" +
                $"Size: {sizeBytes / (1024.0 * 1024.0):F1} MB\n\n" +
                $"Allow this peer to download this song?",
                "GH3-Sync — Host Download Approval",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            tcs.TrySetResult(res == MessageBoxResult.Yes);
        });
        return tcs.Task;
    }
}
