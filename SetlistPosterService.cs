using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using WpfColor = System.Windows.Media.Color;
using WpfSize = System.Windows.Size;

namespace GH3_Sync;

/// <summary>
/// Renders a stylish 1200x1600 Concert Tour Poster PNG of the user's active Lobby Setlist
/// (or full custom song library) ready to share in Discord before a multiplayer session.
/// </summary>
public static class SetlistPosterService
{
    public static void ExportSetlistPosterPng(
        string outputPath,
        string hostUsername,
        string friendCode,
        string posterTitle,
        List<CustomSongInfo> songs)
    {
        const int width = 1200;
        const int height = 1600;

        var root = new Grid
        {
            Width = width,
            Height = height,
            Background = new LinearGradientBrush(
                WpfColor.FromRgb(10, 11, 18),
                WpfColor.FromRgb(22, 11, 20),
                90)
        };

        // Top 5-Fret Neon Accent Strip
        var topStrip = new Border
        {
            Height = 14,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(WpfColor.FromRgb(34, 197, 94), 0.00),
                new GradientStop(WpfColor.FromRgb(239, 68, 68), 0.25),
                new GradientStop(WpfColor.FromRgb(234, 179, 8), 0.50),
                new GradientStop(WpfColor.FromRgb(59, 130, 246), 0.75),
                new GradientStop(WpfColor.FromRgb(249, 115, 22), 1.00)
            }, 0)
        };
        root.Children.Add(topStrip);

        var content = new StackPanel { Margin = new Thickness(64, 56, 64, 50) };

        // Header Banner
        content.Children.Add(new TextBlock
        {
            Text = "GUITAR HERO III  •  LEGENDS OF ROCK",
            FontSize = 22,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(251, 146, 60)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });

        content.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(posterTitle) ? "TONIGHT'S LIVE SETLIST" : posterTitle.ToUpperInvariant(),
            FontSize = 54,
            FontWeight = FontWeights.Black,
            Foreground = System.Windows.Media.Brushes.White,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });

        content.Children.Add(new TextBlock
        {
            Text = $"HOSTED BY {hostUsername.ToUpperInvariant()}   •   FRIEND CODE: {friendCode}",
            FontSize = 20,
            FontWeight = FontWeights.SemiBold,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            Foreground = new SolidColorBrush(WpfColor.FromRgb(125, 211, 252)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 32)
        });

        // Song Rows (up to 12 featured songs on the concert poster)
        var featured = songs.Take(12).ToList();
        for (int i = 0; i < featured.Count; i++)
        {
            var s = featured[i];
            var rowCard = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromArgb(215, 20, 23, 36)),
                BorderBrush = new SolidColorBrush(WpfColor.FromRgb(56, 65, 92)),
                BorderThickness = new Thickness(1.5),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(18, 12, 22, 12),
                Margin = new Thickness(0, 0, 0, 14)
            };

            var rowGrid = new Grid();
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(56) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            rowGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            rowGrid.Children.Add(new TextBlock
            {
                Text = $"{i + 1:D2}",
                FontSize = 26,
                FontWeight = FontWeights.Black,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                Foreground = new SolidColorBrush(WpfColor.FromRgb(249, 115, 22)),
                VerticalAlignment = VerticalAlignment.Center
            });

            if (!string.IsNullOrWhiteSpace(s.AlbumArtPath) && File.Exists(s.AlbumArtPath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 128;
                    bmp.UriSource = new Uri(s.AlbumArtPath, UriKind.Absolute);
                    bmp.EndInit();
                    bmp.Freeze();

                    var art = new Border
                    {
                        Width = 60, Height = 60, CornerRadius = new CornerRadius(9),
                        Margin = new Thickness(0, 0, 18, 0),
                        Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill }
                    };
                    Grid.SetColumn(art, 1);
                    rowGrid.Children.Add(art);
                }
                catch { }
            }

            var titleStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            titleStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(s.Title) ? s.SongId : s.Title,
                FontSize = 24,
                FontWeight = FontWeights.Bold,
                Foreground = System.Windows.Media.Brushes.White,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            titleStack.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(s.Artist) ? "Unknown Artist" : s.Artist,
                FontSize = 17,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(148, 163, 184)),
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            Grid.SetColumn(titleStack, 2);
            rowGrid.Children.Add(titleStack);

            var rightNote = s.ExpertNoteCount > 0 ? $"{s.ExpertNoteCount:N0} NOTES" : "CUSTOM";
            var badge = new Border
            {
                Background = new SolidColorBrush(WpfColor.FromRgb(30, 58, 138)),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(12, 6, 12, 6),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = rightNote,
                    FontSize = 14,
                    FontWeight = FontWeights.Bold,
                    FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                    Foreground = new SolidColorBrush(WpfColor.FromRgb(191, 219, 254))
                }
            };
            Grid.SetColumn(badge, 3);
            rowGrid.Children.Add(badge);

            rowCard.Child = rowGrid;
            content.Children.Add(rowCard);
        }

        if (songs.Count > 12)
        {
            content.Children.Add(new TextBlock
            {
                Text = $"+ {songs.Count - 12} MORE ENCORE TRACKS",
                FontSize = 19,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(WpfColor.FromRgb(253, 224, 71)),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }

        root.Children.Add(content);

        // Footer
        var footer = new TextBlock
        {
            Text = $"GENERATED WITH GH3-SYNC v{UpdateService.CurrentAppVersion}  •  BY MAD SCIENTIST  •  READY TO JAM",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(WpfColor.FromRgb(100, 116, 139)),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 26)
        };
        root.Children.Add(footer);

        // Layout & render offscreen to high-res PNG
        root.Measure(new WpfSize(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(outputPath);
        encoder.Save(fs);
    }
}
