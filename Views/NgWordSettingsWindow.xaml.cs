using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YTNotifier.Models;
using YTNotifier.Services;
using Brushes      = System.Windows.Media.Brushes;
using Button       = System.Windows.Controls.Button;
using Cursors      = System.Windows.Input.Cursors;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace YTNotifier.Views;

public partial class NgWordSettingsWindow : Window
{
    private readonly List<ChannelInfo> _allChannels;
    private ChannelInfo? _selectedChannel;
    private string _channelFilter = string.Empty;

    public NgWordSettingsWindow(Window owner)
    {
        InitializeComponent();
        Owner   = owner;
        WindowCornerHelper.ApplyOnLoaded(this);

        _allChannels = SettingsService.Instance.Channels.GetEnabledChannelsSnapshot();

        RefreshCommonChips();
        RefreshCommonRefChips();
        RefreshChannelList();
        UpdateChannelPanel();
    }

    // ===== タイトルバー =====

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        => WindowTitleBarHelper.DragMoveOnPress(this, e);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    // ===== 共通NGワード =====

    private void RefreshCommonChips()
    {
        CommonNgWordsItems.ItemsSource = SettingsService.Instance.Settings.NgWords;
    }

    private void RefreshCommonRefChips()
    {
        CommonNgWordsRefItems.ItemsSource = SettingsService.Instance.Settings.NgWords;
    }

    private void AddCommonNgWord_Click(object sender, RoutedEventArgs e) => AddCommonNgWord();

    private void CommonNgWordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddCommonNgWord();
    }

    private void AddCommonNgWord()
    {
        var word = CommonNgWordInput.Text.Trim();
        if (string.IsNullOrEmpty(word)) return;

        var settings = SettingsService.Instance.Settings;
        if (settings.NgWords.Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)))
        {
            CommonNgWordInput.Clear();
            return;
        }

        settings.NgWords = settings.NgWords.Append(word).ToList();
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.NgWordAddedCommon, null, word);

        CommonNgWordInput.Clear();
        RefreshCommonChips();
        RefreshCommonRefChips();
    }

    private void RemoveCommonNgWord_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string word) return;

        var settings = SettingsService.Instance.Settings;
        settings.NgWords = settings.NgWords.Where(w => !string.Equals(w, word, StringComparison.Ordinal)).ToList();
        SettingsService.Instance.SaveSettings();
        AppLogger.Log(LogMsg.NgWordRemovedCommon, null, word);

        RefreshCommonChips();
        RefreshCommonRefChips();
    }

    // ===== チャンネル別NGワード：左側一覧 =====

    private void ChannelSearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _channelFilter = ChannelSearchBox.Text.Trim();
        RefreshChannelList();
    }

    private void RefreshChannelList()
    {
        var filtered = _allChannels
            .Where(c => string.IsNullOrEmpty(_channelFilter) ||
                        c.ChannelName.Contains(_channelFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ChannelListPanel.Children.Clear();
        foreach (var channel in filtered)
            ChannelListPanel.Children.Add(CreateChannelRow(channel));
    }

    private Border CreateChannelRow(ChannelInfo channel)
    {
        var isSelected = _selectedChannel != null && channel.ChannelId == _selectedChannel.ChannelId;

        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding      = new Thickness(10, 7, 10, 7),
            Margin       = new Thickness(0, 0, 0, 2),
            Cursor       = Cursors.Hand,
            Tag          = channel
        };
        if (isSelected)
            row.SetResourceReference(Border.BackgroundProperty, "PrimaryBrush");
        else
            row.Background = Brushes.Transparent;

        row.MouseEnter += (_, _) =>
        {
            if (!isSelected) row.SetResourceReference(Border.BackgroundProperty, "HoverBrush");
        };
        row.MouseLeave += (_, _) =>
        {
            if (!isSelected) row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, _) => SelectChannel(channel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock
        {
            Text              = channel.ChannelName,
            FontSize          = 12,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");
        Grid.SetColumn(nameText, 0);
        grid.Children.Add(nameText);

        if (channel.NgWords.Count > 0)
        {
            var badge = new Border
            {
                CornerRadius      = new CornerRadius(8),
                Padding           = new Thickness(6, 1, 6, 1),
                Margin            = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            badge.SetResourceReference(Border.BackgroundProperty, "PrimaryBrush");

            var badgeText = new TextBlock
            {
                Text       = channel.NgWords.Count.ToString(),
                FontSize   = 10,
                FontWeight = FontWeights.Bold
            };
            badgeText.SetResourceReference(TextBlock.ForegroundProperty, "TextOnColorBrush");
            badge.Child = badgeText;

            Grid.SetColumn(badge, 1);
            grid.Children.Add(badge);
        }

        row.Child = grid;
        return row;
    }

    private void SelectChannel(ChannelInfo channel)
    {
        _selectedChannel = channel;
        UpdateChannelPanel();
        RefreshChannelList();
    }

    // ===== チャンネル別NGワード：右側パネル =====

    private void UpdateChannelPanel()
    {
        if (_selectedChannel == null)
        {
            ChannelNgPanel.Visibility       = Visibility.Collapsed;
            ChannelNgPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        ChannelNgPlaceholder.Visibility  = Visibility.Collapsed;
        ChannelNgPanel.Visibility        = Visibility.Visible;
        SelectedChannelNameText.Text     = _selectedChannel.ChannelName;
        RefreshChannelNgWords();
    }

    private void RefreshChannelNgWords()
    {
        if (_selectedChannel == null) return;
        ChannelNgWordsItems.ItemsSource = _selectedChannel.NgWords;
    }

    private void AddChannelNgWord_Click(object sender, RoutedEventArgs e) => AddChannelNgWord();

    private void ChannelNgWordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddChannelNgWord();
    }

    private void AddChannelNgWord()
    {
        if (_selectedChannel == null) return;

        var word = ChannelNgWordInput.Text.Trim();
        if (string.IsNullOrEmpty(word)) return;

        if (_selectedChannel.NgWords.Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)))
        {
            ChannelNgWordInput.Clear();
            return;
        }

        _selectedChannel.NgWords = _selectedChannel.NgWords.Append(word).ToList();
        SettingsService.Instance.Channels.UpdateChannel(_selectedChannel);
        AppLogger.Log(LogMsg.NgWordAddedChannel, _selectedChannel.ChannelName, word);

        ChannelNgWordInput.Clear();
        RefreshChannelNgWords();
        RefreshChannelList();
    }

    private void RemoveChannelNgWord_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedChannel == null) return;
        if (((Button)sender).Tag is not string word) return;

        _selectedChannel.NgWords =
            _selectedChannel.NgWords.Where(w => !string.Equals(w, word, StringComparison.Ordinal)).ToList();
        SettingsService.Instance.Channels.UpdateChannel(_selectedChannel);
        AppLogger.Log(LogMsg.NgWordRemovedChannel, _selectedChannel.ChannelName, word);

        RefreshChannelNgWords();
        RefreshChannelList();
    }
}
