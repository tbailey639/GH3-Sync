using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

using WinForms = System.Windows.Forms;
using WpfColor = System.Windows.Media.Color;
using WpfMessageBox = System.Windows.MessageBox;

namespace GH3_Sync.Views;

internal sealed class DiscoveredChSong
{
    public string FolderPath { get; set; } = "";
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Charter { get; set; } = "";
    public bool AlreadyInstalled { get; set; }
    public bool IsSelected { get; set; }
}

public partial class LibraryMigrationWindow : Window
{
    private readonly Gh3Service _gh3;
    private readonly CloneHeroImportService _cloneHero;
    private readonly List<DiscoveredChSong> _discovered = new();

    public int ImportedCount { get; private set; }

    public LibraryMigrationWindow(Gh3Service gh3, CloneHeroImportService cloneHero)
    {
        InitializeComponent();
        _gh3 = gh3;
        _cloneHero = cloneHero;

        // Pre-populate common Clone Hero Songs path if detected
        string[] guesses =
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Clone Hero", "Songs"),
            @"C:\Clone Hero\Songs",
            @"D:\Clone Hero\Songs"
        };
        var found = guesses.FirstOrDefault(Directory.Exists);
        if (!string.IsNullOrEmpty(found)) RootFolderBox.Text = found;
    }

    private void BrowseRootBtn_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new WinForms.FolderBrowserDialog
        {
            Description = "Select Clone Hero / YARG Songs Root Folder",
            SelectedPath = RootFolderBox.Text
        };
        if (fbd.ShowDialog() == WinForms.DialogResult.OK)
        {
            RootFolderBox.Text = fbd.SelectedPath;
            ScanLibraryFolder();
        }
    }

    private void ScanBtn_Click(object sender, RoutedEventArgs e) => ScanLibraryFolder();

    public void ScanLibraryFolder()
    {
        var root = RootFolderBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            ScanSummaryText.Text = "Please select a valid Clone Hero / YARG Songs folder.";
            return;
        }

        _discovered.Clear();
        ScanSummaryText.Text = "Scanning folders...";

        var songDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var chart in Directory.EnumerateFiles(root, "notes.chart", SearchOption.AllDirectories))
                songDirs.Add(Path.GetDirectoryName(chart)!);
            foreach (var mid in Directory.EnumerateFiles(root, "notes.mid", SearchOption.AllDirectories))
                songDirs.Add(Path.GetDirectoryName(mid)!);
        }
        catch { }

        foreach (var dir in songDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            var (title, artist, charter) = ReadIniMeta(dir);
            bool dup = _gh3.FindDuplicateInstalledSong(title, artist) != null;
            _discovered.Add(new DiscoveredChSong
            {
                FolderPath = dir,
                Title = title,
                Artist = artist,
                Charter = charter,
                AlreadyInstalled = dup,
                IsSelected = !dup
            });
        }

        int newCount = _discovered.Count(s => !s.AlreadyInstalled);
        ScanSummaryText.Text = $"Found {_discovered.Count} song(s) ({newCount} new, {_discovered.Count - newCount} already in GH3).";
        RenderList();
    }

    private static (string Title, string Artist, string Charter) ReadIniMeta(string dir)
    {
        string title = Path.GetFileName(dir);
        string artist = "Unknown Artist";
        string charter = "Community";
        var ini = Path.Combine(dir, "song.ini");
        if (File.Exists(ini))
        {
            try
            {
                foreach (var l in File.ReadAllLines(ini))
                {
                    var eq = l.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = l.Substring(0, eq).Trim();
                    var v = l.Substring(eq + 1).Trim();
                    if (k.Equals("name", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v)) title = v;
                    else if (k.Equals("artist", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(v)) artist = v;
                    else if ((k.Equals("charter", StringComparison.OrdinalIgnoreCase) || k.Equals("frets", StringComparison.OrdinalIgnoreCase)) && !string.IsNullOrWhiteSpace(v)) charter = v;
                }
            }
            catch { }
        }
        return (title, artist, charter);
    }

    private void SearchFilterBox_TextChanged(object sender, TextChangedEventArgs e) => RenderList();

    private void SelectAllNewBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _discovered) s.IsSelected = !s.AlreadyInstalled;
        RenderList();
    }

    private void SelectNoneBtn_Click(object sender, RoutedEventArgs e)
    {
        foreach (var s in _discovered) s.IsSelected = false;
        RenderList();
    }

    private void UpdateSelectedButton()
    {
        int count = _discovered.Count(s => s.IsSelected);
        StartMigrateBtn.Content = $"Convert & Install Selected ({count})";
        StartMigrateBtn.IsEnabled = count > 0;
    }

    private void RenderList()
    {
        SongsCheckPanel.Children.Clear();
        var q = (SearchFilterBox.Text ?? "").Trim();

        var filtered = string.IsNullOrEmpty(q)
            ? _discovered
            : _discovered.Where(s =>
                s.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Artist.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Charter.Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();

        if (filtered.Count == 0)
        {
            SongsCheckPanel.Children.Add(new TextBlock
            {
                Text = _discovered.Count == 0 ? "No Clone Hero / YARG songs scanned yet." : "No songs match your search filter.",
                Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
                Margin = new Thickness(10)
            });
            UpdateSelectedButton();
            return;
        }

        foreach (var item in filtered)
        {
            var row = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(20, 23, 34)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(38, 43, 61)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 0, 0, 5)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var chk = new System.Windows.Controls.CheckBox
            {
                IsChecked = item.IsSelected,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            };
            chk.Checked += (_, _) => { item.IsSelected = true; UpdateSelectedButton(); };
            chk.Unchecked += (_, _) => { item.IsSelected = false; UpdateSelectedButton(); };
            Grid.SetColumn(chk, 0);
            grid.Children.Add(chk);

            var info = new StackPanel();
            info.Children.Add(new TextBlock
            {
                Text = $"{item.Title}  —  {item.Artist}",
                FontSize = 13,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            info.Children.Add(new TextBlock
            {
                Text = $"Charter: {item.Charter}  •  {item.FolderPath}",
                FontSize = 11,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(info, 1);
            grid.Children.Add(info);

            var badge = new Border
            {
                Background = new SolidColorBrush(item.AlreadyInstalled ? WpfColor.FromRgb(66, 32, 6) : WpfColor.FromRgb(6, 78, 59)),
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
                Child = new TextBlock
                {
                    Text = item.AlreadyInstalled ? "Already in GH3" : "New",
                    FontSize = 10.5,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = new SolidColorBrush(item.AlreadyInstalled ? WpfColor.FromRgb(253, 224, 71) : WpfColor.FromRgb(110, 231, 183))
                }
            };
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);

            row.Child = grid;
            SongsCheckPanel.Children.Add(row);
        }

        UpdateSelectedButton();
    }

    private async void StartMigrateBtn_Click(object sender, RoutedEventArgs e)
    {
        var selected = _discovered.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0) return;

        StartMigrateBtn.IsEnabled = false;
        ScanBtn.IsEnabled = false;
        BrowseRootBtn.IsEnabled = false;
        MigrationProgressBar.Visibility = Visibility.Visible;

        int okCount = 0;
        int failCount = 0;
        try
        {
            for (int i = 0; i < selected.Count; i++)
            {
                var song = selected[i];
                MigrationStatusText.Text = $"[{i + 1}/{selected.Count}] Converting '{song.Title}' by {song.Artist}...";
                var res = await _cloneHero.ImportFromZipOrFolderAsync(song.FolderPath, autoCompile: true, (pct, step) =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        double overall = ((i + (pct / 100.0)) / selected.Count) * 100.0;
                        MigrationProgressBar.Value = overall;
                        MigrationStatusText.Text = $"[{i + 1}/{selected.Count}] {song.Title}: {step}";
                    });
                });
                if (res.Success)
                {
                    okCount++;
                    song.AlreadyInstalled = true;
                    song.IsSelected = false;
                }
                else failCount++;
            }

            ImportedCount += okCount;
            MigrationStatusText.Text = $"Migration complete! Converted {okCount} / {selected.Count} song(s){(failCount > 0 ? $" ({failCount} skipped/failed)" : "")}.";
            RenderList();
            WpfMessageBox.Show(MigrationStatusText.Text, "Library Migration Complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            MigrationProgressBar.Visibility = Visibility.Collapsed;
            ScanBtn.IsEnabled = true;
            BrowseRootBtn.IsEnabled = true;
            UpdateSelectedButton();
        }
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e) => Close();
}
