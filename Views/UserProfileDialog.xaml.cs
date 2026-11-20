using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WpfBrush = System.Windows.Media.Brush;
using WpfOrientation = System.Windows.Controls.Orientation;
using WpfClipboard = System.Windows.Clipboard;

namespace GH3_Sync.Views;

public partial class UserProfileDialog : Window
{
    private readonly string _username;
    private readonly string _friendCode;
    private readonly string _showcaseSong;
    private readonly bool _isSelf;
    private readonly bool _isFriend;
    private readonly bool _isTrusted;
    private readonly bool _isOnline;
    private readonly Func<string, Task<bool>>? _onAskToPlayTogether;
    private readonly Action<string, bool>? _onSendFriendRequest;
    private readonly Action<string, string>? _onPreviewShowcase;
    private readonly Action? _onGoToLobbySync;
    private readonly Action? _onRequestTrustUpgrade;
    private readonly Action? _onManageSaves;

    public UserProfileDialog(
        string username,
        string friendCode,
        string showcaseSong,
        bool isSelf,
        bool isFriend,
        bool isTrusted,
        bool isOnline,
        string statusText = "Online",
        Func<string, Task<bool>>? onAskToPlayTogether = null,
        Action<string, bool>? onSendFriendRequest = null,
        Action<string, string>? onPreviewShowcase = null,
        Action? onGoToLobbySync = null,
        Action? onRequestTrustUpgrade = null,
        Action? onManageSaves = null)
    {
        InitializeComponent();

        _username = string.IsNullOrWhiteSpace(username) ? "Rockstar" : username;
        _friendCode = friendCode ?? "";
        _showcaseSong = showcaseSong ?? "";
        _isSelf = isSelf;
        _isFriend = isFriend;
        _isTrusted = isTrusted;
        _isOnline = isOnline;
        _onAskToPlayTogether = onAskToPlayTogether;
        _onSendFriendRequest = onSendFriendRequest;
        _onPreviewShowcase = onPreviewShowcase;
        _onGoToLobbySync = onGoToLobbySync;
        _onRequestTrustUpgrade = onRequestTrustUpgrade;
        _onManageSaves = onManageSaves;

        PopulateProfile(statusText);
    }

    private void PopulateProfile(string statusText)
    {
        ProfileUsername.Text = _username;
        ProfileCode.Text = _friendCode;

        // Status Badge
        StatusBadgeText.Text = _isOnline ? (statusText.StartsWith("🟢") || statusText.StartsWith("🎸") ? statusText : $"🟢 {statusText}") : $"🔴 {statusText}";
        StatusBadgeBorder.Background = (WpfBrush)new BrushConverter().ConvertFromString(_isOnline ? "#064E3B" : "#27272A")!;
        StatusBadgeText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(_isOnline ? "#6EE7B7" : "#A1A1AA")!;

        // Relationship Badge
        if (_isSelf)
        {
            RelationshipBadgeText.Text = "👑 You (Active Profile)";
            RelationshipBadgeBorder.Background = (WpfBrush)new BrushConverter().ConvertFromString("#1E1B4B")!;
            RelationshipBadgeBorder.BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString("#6366F1")!;
            RelationshipBadgeText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#C7D2FE")!;
        }
        else if (_isFriend)
        {
            RelationshipBadgeText.Text = _isTrusted ? "⭐ Trusted Friend (Song Sync Enabled)" : "👥 Regular Friend (Setlist View Only)";
            RelationshipBadgeBorder.Background = (WpfBrush)new BrushConverter().ConvertFromString(_isTrusted ? "#261B0E" : "#18181B")!;
            RelationshipBadgeBorder.BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString(_isTrusted ? "#F59E0B" : "#3F3F46")!;
            RelationshipBadgeText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString(_isTrusted ? "#F59E0B" : "#D4D4D8")!;
        }
        else
        {
            RelationshipBadgeText.Text = "🔍 Discovered Player (Not in Friends List)";
            RelationshipBadgeBorder.Background = (WpfBrush)new BrushConverter().ConvertFromString("#1E293B")!;
            RelationshipBadgeBorder.BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString("#38BDF8")!;
            RelationshipBadgeText.Foreground = (WpfBrush)new BrushConverter().ConvertFromString("#38BDF8")!;
        }

        // Showcase / Signature Track
        if (!string.IsNullOrWhiteSpace(_showcaseSong))
        {
            ShowcaseTrackTitle.Text = _showcaseSong;
            ShowcaseTrackSub.Text = "Signature Custom Track • Ready for Audio Preview";
            PreviewShowcaseBtn.Visibility = Visibility.Visible;
        }
        else
        {
            ShowcaseTrackTitle.Text = "No signature track chosen yet";
            ShowcaseTrackSub.Text = "Select a signature track in Friends & Trust tab";
            PreviewShowcaseBtn.Visibility = Visibility.Collapsed;
        }

        // Career Statistics
        var stats = AppConfig.Current.LifetimeStats ?? new TourStats();
        int totalFCs = stats.TotalFullCombos;
        long totalNotes = stats.TotalNotesHit;
        int matches = stats.TotalMatchesPlayed;
        int fiveStars = AppConfig.Current.LocalHighScores.Count(kv => kv.Value.Stars >= 5);

        StatFullCombos.Text = $"{totalFCs:N0} 👑";
        StatFiveStars.Text = $"{fiveStars:N0} ⭐";
        StatNotesHit.Text = $"{totalNotes:N0} ⚡";
        StatMatchesPlayed.Text = $"{matches:N0} ⚔️";

        // Trophies Showcase
        var allAchievements = RockHallOfFameService.GetAllAchievements();
        int unlockedCount = allAchievements.Count(a => a.IsUnlocked);
        TrophyCountText.Text = $"({unlockedCount} / {allAchievements.Count} Unlocked)";

        TrophiesWrapPanel.Children.Clear();
        foreach (var ach in allAchievements)
        {
            var tierColor = ach.Tier switch
            {
                "Diamond" => "#38BDF8",
                "Platinum" => "#C084FC",
                _ => "#F59E0B"
            };

            var trophyBorder = new Border
            {
                Width = 180,
                Margin = new Thickness(4),
                Padding = new Thickness(8, 6, 8, 6),
                CornerRadius = new CornerRadius(6),
                Background = (WpfBrush)new BrushConverter().ConvertFromString(ach.IsUnlocked ? "#1A1528" : "#13111C")!,
                BorderBrush = (WpfBrush)new BrushConverter().ConvertFromString(ach.IsUnlocked ? tierColor : "#27272A")!,
                BorderThickness = new Thickness(1),
                Opacity = ach.IsUnlocked ? 1.0 : 0.45,
                ToolTip = $"{ach.Title} ({ach.Tier})\n{ach.Description}\nStatus: {(ach.IsUnlocked ? "✓ Unlocked" : "🔒 Locked")}"
            };

            var stack = new StackPanel();
            var topSp = new StackPanel { Orientation = WpfOrientation.Horizontal };
            topSp.Children.Add(new TextBlock { Text = ach.Icon, FontSize = 14, Margin = new Thickness(0, 0, 6, 0) });
            topSp.Children.Add(new TextBlock
            {
                Text = ach.Title,
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(ach.IsUnlocked ? "#F3F4F6" : "#71717A")!,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = 135
            });
            stack.Children.Add(topSp);

            var subText = new TextBlock
            {
                Text = ach.IsUnlocked ? $"✓ {ach.Tier.ToUpperInvariant()}" : "🔒 LOCKED",
                FontSize = 9.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = (WpfBrush)new BrushConverter().ConvertFromString(ach.IsUnlocked ? tierColor : "#71717A")!,
                Margin = new Thickness(20, 2, 0, 0)
            };
            stack.Children.Add(subText);

            trophyBorder.Child = stack;
            TrophiesWrapPanel.Children.Add(trophyBorder);
        }

        // Action Buttons Visibility
        AskToPlayTogetherBtn.Visibility = (!_isSelf && _isFriend && _isTrusted && _isOnline) ? Visibility.Visible : Visibility.Collapsed;
        SyncSongsBtn.Visibility = (!_isSelf && _isFriend) ? Visibility.Visible : Visibility.Collapsed;
        RequestTrustUpgradeBtn.Visibility = (!_isSelf && _isFriend && !_isTrusted) ? Visibility.Visible : Visibility.Collapsed;
        AddRegularFriendBtn.Visibility = (!_isSelf && !_isFriend) ? Visibility.Visible : Visibility.Collapsed;
        AddTrustedFriendBtn.Visibility = (!_isSelf && !_isFriend) ? Visibility.Visible : Visibility.Collapsed;
        ManageSavesBtn.Visibility = _isSelf ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyCodeBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            WpfClipboard.SetText(_friendCode);
            CopyCodeBtn.Content = "✓ Copied";
        }
        catch { }
    }

    private void PreviewShowcaseBtn_Click(object sender, RoutedEventArgs e)
    {
        _onPreviewShowcase?.Invoke(_showcaseSong, _username);
    }

    private async void AskToPlayTogetherBtn_Click(object sender, RoutedEventArgs e)
    {
        AskToPlayTogetherBtn.IsEnabled = false;
        AskToPlayTogetherBtn.Content = "Inviting...";
        try
        {
            if (_onAskToPlayTogether != null)
            {
                bool ok = await _onAskToPlayTogether(_friendCode);
                if (ok)
                {
                    AskToPlayTogetherBtn.Content = "✓ Invite Sent!";
                    await Task.Delay(1400);
                }
            }
        }
        finally
        {
            AskToPlayTogetherBtn.IsEnabled = true;
            AskToPlayTogetherBtn.Content = "🎸 Ask to Play Together!";
        }
    }

    private void SyncSongsBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onGoToLobbySync?.Invoke();
    }

    private void RequestTrustUpgradeBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onRequestTrustUpgrade?.Invoke();
    }

    private void AddRegularFriendBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onSendFriendRequest?.Invoke(_friendCode, false);
    }

    private void AddTrustedFriendBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onSendFriendRequest?.Invoke(_friendCode, true);
    }

    private void ManageSavesBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
        _onManageSaves?.Invoke();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            Close();
        }
    }
}
