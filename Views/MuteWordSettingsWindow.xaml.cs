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
using Path         = System.Windows.Shapes.Path;

namespace YTNotifier.Views;

public partial class MuteWordSettingsWindow : Window
{
    private readonly List<ChannelInfo> _allChannels;
    // null のときは「共通ミュートワード」を選択中
    private ChannelInfo? _selectedChannel;
    private string _channelFilter = string.Empty;

    // チャンネル一覧行のアイコン（MainWindow のコンパクト表示アイコンサイズに合わせる）
    private const double ChannelRowIconSize = 22;
    private static readonly Thickness ChannelRowIconMargin = new(0, 0, 8, 0);

    // チャンネル一覧行の見た目
    private const double ChannelRowCornerRadius = 6;
    private static readonly Thickness ChannelRowPadding = new(10, 7, 10, 7);
    private static readonly Thickness ChannelRowMargin  = new(0, 0, 0, 2);
    private const double ChannelRowNameFontSize = 12;

    // 件数の札（チャンネル行と共通の固定枠で同じ見た目）
    private const double CountBadgeCornerRadius = 8;
    private static readonly Thickness CountBadgePadding = new(6, 1, 6, 1);
    private static readonly Thickness CountBadgeMargin  = new(6, 0, 0, 0);
    private const double CountBadgeFontSize = 10;

    // 地球儀アイコン（XAML の Window.Resources に定義した絵柄・スタイルを使う）
    private const string GlobeIconGeometryKey  = "GlobeIconGeometry";
    private const string GlobeIconPathStyleKey = "GlobeIconPath";
    private const double GlobeIconCanvasSize   = 24;

    // ブラシのリソースキー
    private const string PrimaryBrushKey     = "PrimaryBrush";
    private const string HoverBrushKey       = "HoverBrush";
    private const string SurfaceAltBrushKey  = "SurfaceAltBrush";
    private const string WarningBrushKey     = "WarningBrush";
    private const string TextPrimaryBrushKey = "TextPrimaryBrush";
    private const string TextOnColorBrushKey = "TextOnColorBrush";

    // 右側の表示文言
    private const string CommonTargetName          = "共通ミュートワード";
    private const string CommonScopeLabel          = "全チャンネル";
    private const string ChannelScopeLabel         = "このチャンネルのみ";
    private const string CommonDescription         = "ここに追加した単語を含むタイトルの動画は、一覧には表示されますが、すべてのチャンネルで通知されません。";
    private const string ChannelDescription        = "ここに追加した単語を含むタイトルの動画は、一覧には表示されますが、このチャンネルでは通知されません。";
    private const string CommonNoticeTextFormat    = "このほか共通ミュートワード({0}件)も適用されます";

    public MuteWordSettingsWindow(Window owner)
    {
        InitializeComponent();
        WindowScaleService.ApplyToNewWindow(this);
        Owner   = owner;
        WindowCornerHelper.ApplyOnLoaded(this);

        _allChannels = SettingsService.Instance.Channels.GetEnabledChannelsSnapshot();

        SelectCommon();
    }

    // ===== タイトルバー =====

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        => WindowTitleBarHelper.DragMoveOnPress(this, e);

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void MuteWordSettingsWindow_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    // ===== 左側：共通の固定枠 =====

    private void CommonEntryRow_Click(object sender, MouseButtonEventArgs e) => SelectCommon();

    private void SelectCommon()
    {
        _selectedChannel = null;
        UpdateCommonEntryRowBackground();
        UpdateTargetPanel();
        RefreshChannelList();
    }

    private void UpdateCommonEntryRowBackground()
    {
        var brushKey = _selectedChannel == null ? PrimaryBrushKey : SurfaceAltBrushKey;
        CommonEntryRow.SetResourceReference(Border.BackgroundProperty, brushKey);
    }

    // ===== 左側：チャンネル一覧 =====

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
            CornerRadius = new CornerRadius(ChannelRowCornerRadius),
            Padding      = ChannelRowPadding,
            Margin       = ChannelRowMargin,
            Cursor       = Cursors.Hand,
            Tag          = channel
        };
        if (isSelected)
            row.SetResourceReference(Border.BackgroundProperty, PrimaryBrushKey);
        else
            row.Background = Brushes.Transparent;

        row.MouseEnter += (_, _) =>
        {
            if (!isSelected) row.SetResourceReference(Border.BackgroundProperty, HoverBrushKey);
        };
        row.MouseLeave += (_, _) =>
        {
            if (!isSelected) row.Background = Brushes.Transparent;
        };
        row.MouseLeftButtonUp += (_, _) => SelectChannel(channel);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var iconBorder = CreateChannelRowIcon(channel);
        Grid.SetColumn(iconBorder, 0);
        grid.Children.Add(iconBorder);

        var nameText = new TextBlock
        {
            Text              = channel.ChannelName,
            FontSize          = ChannelRowNameFontSize,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, TextPrimaryBrushKey);
        Grid.SetColumn(nameText, 1);
        grid.Children.Add(nameText);

        if (channel.MuteWords.Count > 0)
        {
            var badge = CreateCountBadge(channel.MuteWords.Count);
            Grid.SetColumn(badge, 2);
            grid.Children.Add(badge);
        }

        row.Child = grid;
        return row;
    }

    /// <summary>件数の札を構築する（チャンネル行と共通の固定枠で共用）。</summary>
    private static Border CreateCountBadge(int count)
    {
        var badge = new Border
        {
            CornerRadius      = new CornerRadius(CountBadgeCornerRadius),
            Padding           = CountBadgePadding,
            Margin            = CountBadgeMargin,
            VerticalAlignment = VerticalAlignment.Center
        };
        badge.SetResourceReference(Border.BackgroundProperty, PrimaryBrushKey);

        var badgeText = new TextBlock
        {
            Text       = count.ToString(),
            FontSize   = CountBadgeFontSize,
            FontWeight = FontWeights.Bold
        };
        badgeText.SetResourceReference(TextBlock.ForegroundProperty, TextOnColorBrushKey);
        badge.Child = badgeText;

        return badge;
    }

    /// <summary>
    /// チャンネル一覧行のアイコンを構築する（VideoSummaryPopupWindow.LoadChannelIcon と同方式）。
    /// ChannelListPanel は RefreshChannelList() のたびに行を作り直すため、
    /// ダウンロード完了時はこのメソッド呼び出しごとに生成される Border インスタンスをクロージャで直接更新する
    /// （固定の x:Name 要素は参照できない）。
    /// </summary>
    private Border CreateChannelRowIcon(ChannelInfo channel)
    {
        var iconBorder = new Border
        {
            Width             = ChannelRowIconSize,
            Height            = ChannelRowIconSize,
            CornerRadius      = new CornerRadius(ChannelRowIconSize / 2),
            Margin            = ChannelRowIconMargin,
            VerticalAlignment = VerticalAlignment.Center,
            ClipToBounds      = true
        };

        if (ImageCacheService.TryGetCachedIcon(channel.ThumbnailUrl, out var cached) && cached != null)
        {
            iconBorder.Background = new ImageBrush(cached) { Stretch = Stretch.UniformToFill };
            return iconBorder;
        }

        if (string.IsNullOrEmpty(channel.ThumbnailUrl)) return iconBorder;

        Task.Run(async () =>
        {
            var bmp = await ImageCacheService.GetOrDownloadIconAsync(channel.ThumbnailUrl);
            if (bmp == null) return;
            await Dispatcher.InvokeAsync(() =>
                iconBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill });
        });

        return iconBorder;
    }

    /// <summary>右側見出し用の地球儀アイコンを構築する（チャンネルアイコンと同じ大きさ・余白）。</summary>
    private Viewbox CreateGlobeIcon()
    {
        var globePath = new Path
        {
            Style = (Style)FindResource(GlobeIconPathStyleKey),
            Data  = (Geometry)FindResource(GlobeIconGeometryKey)
        };

        var canvas = new Canvas
        {
            Width  = GlobeIconCanvasSize,
            Height = GlobeIconCanvasSize
        };
        canvas.Children.Add(globePath);

        return new Viewbox
        {
            Width             = ChannelRowIconSize,
            Height            = ChannelRowIconSize,
            Margin            = ChannelRowIconMargin,
            VerticalAlignment = VerticalAlignment.Center,
            Child             = canvas
        };
    }

    private void SelectChannel(ChannelInfo channel)
    {
        _selectedChannel = channel;
        UpdateCommonEntryRowBackground();
        UpdateTargetPanel();
        RefreshChannelList();
    }

    // ===== 右側：選択中の対象のミュートワード =====

    private void UpdateTargetPanel()
    {
        if (_selectedChannel == null)
        {
            SelectedTargetIconHost.Child = CreateGlobeIcon();
            SelectedTargetNameText.Text  = CommonTargetName;
            ScopeBadgeText.Text          = CommonScopeLabel;
            ScopeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, WarningBrushKey);
            TargetDescriptionText.Text   = CommonDescription;
        }
        else
        {
            SelectedTargetIconHost.Child = CreateChannelRowIcon(_selectedChannel);
            SelectedTargetNameText.Text  = _selectedChannel.ChannelName;
            ScopeBadgeText.Text          = ChannelScopeLabel;
            ScopeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, PrimaryBrushKey);
            TargetDescriptionText.Text   = ChannelDescription;
        }

        RefreshMuteWords();
    }

    /// <summary>選択中の対象の単語を札に表示し、0件表示・お知らせ行・共通の件数札も更新する。</summary>
    private void RefreshMuteWords()
    {
        var commonWords   = SettingsService.Instance.Settings.MuteWords;
        var selectedWords = _selectedChannel?.MuteWords ?? commonWords;

        MuteWordsItems.ItemsSource    = selectedWords;
        MuteWordsEmptyText.Visibility = selectedWords.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (commonWords.Count > 0)
        {
            CommonCountBadgeHost.Child      = CreateCountBadge(commonWords.Count);
            CommonCountBadgeHost.Visibility = Visibility.Visible;
        }
        else
        {
            CommonCountBadgeHost.Child      = null;
            CommonCountBadgeHost.Visibility = Visibility.Collapsed;
        }

        var showCommonNotice = _selectedChannel != null && commonWords.Count > 0;
        CommonNoticeText.Text      = string.Format(CommonNoticeTextFormat, commonWords.Count);
        CommonNoticeRow.Visibility = showCommonNotice ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewCommonLink_Click(object sender, MouseButtonEventArgs e) => SelectCommon();

    private void AddMuteWord_Click(object sender, RoutedEventArgs e) => AddMuteWord();

    private void MuteWordInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) AddMuteWord();
    }

    private void MuteWordInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (MuteWordInputPlaceholder != null)
            MuteWordInputPlaceholder.Visibility =
                string.IsNullOrEmpty(MuteWordInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AddMuteWord()
    {
        var word = MuteWordInput.Text.Trim();
        if (string.IsNullOrEmpty(word)) return;

        var selectedWords = _selectedChannel?.MuteWords ?? SettingsService.Instance.Settings.MuteWords;
        if (selectedWords.Any(w => string.Equals(w, word, StringComparison.OrdinalIgnoreCase)))
        {
            MuteWordInput.Clear();
            return;
        }

        if (_selectedChannel == null)
        {
            var settings = SettingsService.Instance.Settings;
            settings.MuteWords = settings.MuteWords.Append(word).ToList();
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.MuteWordAddedCommon, null, word);
        }
        else
        {
            _selectedChannel.MuteWords = _selectedChannel.MuteWords.Append(word).ToList();
            SettingsService.Instance.Channels.UpdateChannel(_selectedChannel);
            AppLogger.Log(LogMsg.MuteWordAddedChannel, _selectedChannel.ChannelName, word);
            RefreshChannelList();
        }

        MuteWordInput.Clear();
        RefreshMuteWords();
    }

    private void RemoveMuteWord_Click(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).Tag is not string word) return;

        if (_selectedChannel == null)
        {
            var settings = SettingsService.Instance.Settings;
            settings.MuteWords = settings.MuteWords.Where(w => !string.Equals(w, word, StringComparison.Ordinal)).ToList();
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.MuteWordRemovedCommon, null, word);
        }
        else
        {
            _selectedChannel.MuteWords =
                _selectedChannel.MuteWords.Where(w => !string.Equals(w, word, StringComparison.Ordinal)).ToList();
            SettingsService.Instance.Channels.UpdateChannel(_selectedChannel);
            AppLogger.Log(LogMsg.MuteWordRemovedChannel, _selectedChannel.ChannelName, word);
            RefreshChannelList();
        }

        RefreshMuteWords();
    }
}
