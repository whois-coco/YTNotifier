using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using Brush   = System.Windows.Media.Brush;
using Cursors = System.Windows.Input.Cursors;
using Image   = System.Windows.Controls.Image;

namespace YTNotifier.Views;

public partial class VideoListPopupWindow : Window
{
    private const string HeaderTextLive             = "配信中のライブ一覧";
    private const string HeaderTextPremiere         = "公開中のプレミア一覧";
    private const string HeaderTextRecentUploadsFormat = "{0} の最新動画";
    private const double RowBottomMargin            = 10;

    /// <summary>最新動画一覧の行サムネイルの表示サイズ</summary>
    private const double RecentUploadThumbnailWidth  = 96;
    private const double RecentUploadThumbnailHeight = 54;

    /// <summary>ライブ配信中（配信終了していない）の動画時間バッジに表示する文言</summary>
    private const string LiveBadgeText = "LIVE";

    private readonly ChannelInfo _channel;
    private readonly VideoKind   _kind;
    private readonly bool        _allowSummary;

    public VideoListPopupWindow(Window owner, ChannelInfo channel, VideoKind kind, List<PendingVideoEntry> entries, bool allowSummary = true)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCornerHelper.Apply(this);
        Owner         = owner;
        _channel      = channel;
        _kind         = kind;
        _allowSummary = allowSummary;

        HeaderText.Text = kind == VideoKind.Premiere ? HeaderTextPremiere : HeaderTextLive;

        foreach (var entry in entries)
        {
            var titleText = new TextBlock
            {
                Text = entry.Title,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                Cursor = Cursors.Hand,
                Margin = new Thickness(0, 0, 0, RowBottomMargin),
                Foreground = (Brush)TryFindResource("TextPrimaryBrush")
            };
            titleText.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                new VideoSummaryPopupWindow(this, _channel, _kind, entry.Title, entry.VideoId, allowSummary: _allowSummary).Show();
            };
            EntriesPanel.Children.Add(titleText);
        }

        AppLogger.Log(LogMsg.VideoListPopupOpened, null, channel.ChannelName, entries.Count);
    }

    /// <summary>チャンネルの最新投稿スナップショット一覧（サムネイル・種別ピル・動画時間・経過表示つき）</summary>
    public VideoListPopupWindow(Window owner, ChannelInfo channel, List<RecentUploadEntry> entries)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCornerHelper.Apply(this);
        Owner         = owner;
        _channel      = channel;
        _allowSummary = true;

        HeaderText.Text = string.Format(HeaderTextRecentUploadsFormat, channel.ChannelName);

        foreach (var entry in entries)
            EntriesPanel.Children.Add(BuildRecentUploadRow(channel, entry));

        AppLogger.Log(LogMsg.RecentUploadsPopupOpened, null, channel.ChannelName, entries.Count);
    }

    private UIElement BuildRecentUploadRow(ChannelInfo channel, RecentUploadEntry entry)
    {
        var thumbnailArea = new Grid { Width = RecentUploadThumbnailWidth, Height = RecentUploadThumbnailHeight };

        var thumbnailImage = new Image { Stretch = Stretch.UniformToFill };
        var thumbnailBorder = new Border
        {
            CornerRadius = new CornerRadius(4),
            ClipToBounds = true,
            Background   = (Brush)TryFindResource("SurfaceElevatedBrush"),
            Child         = thumbnailImage
        };
        thumbnailArea.Children.Add(thumbnailBorder);
        LoadRecentUploadThumbnail(channel, entry, thumbnailImage);

        // ライブ中（配信終了していない）の場合は動画時間の代わりにLIVEを表示する
        var isCurrentlyLive = entry.Kind == VideoKind.Live && !entry.Duration.HasValue;
        var durationLabel = isCurrentlyLive ? LiveBadgeText : YouTubeApiClient.FormatDurationHms(entry.Duration);
        if (durationLabel != null)
        {
            var durationBadge = new Border
            {
                Background          = (Brush)TryFindResource("SidebarBrush"),
                CornerRadius         = new CornerRadius(3),
                Padding              = new Thickness(4, 1, 4, 1),
                Margin               = new Thickness(0, 0, 4, 4),
                HorizontalAlignment  = System.Windows.HorizontalAlignment.Right,
                VerticalAlignment    = VerticalAlignment.Bottom,
                Child = new TextBlock
                {
                    Text       = durationLabel,
                    FontSize   = 10,
                    Foreground = (Brush)TryFindResource("SidebarTextBrush")
                }
            };
            thumbnailArea.Children.Add(durationBadge);
        }

        var (pillLabel, pillBgKey, pillFgKey) = entry.Kind switch
        {
            VideoKind.Video    => ("動画",    "KindPillVideoBgBrush",    "KindPillVideoFgBrush"),
            VideoKind.Short    => ("Short",   "KindPillShortBgBrush",    "KindPillShortFgBrush"),
            VideoKind.Live     => ("ライブ",  "KindPillLiveBgBrush",     "KindPillLiveFgBrush"),
            VideoKind.Premiere => ("プレミア", "KindPillPremiereBgBrush", "KindPillPremiereFgBrush"),
            _                  => ("動画",    "KindPillVideoBgBrush",    "KindPillVideoFgBrush"),
        };
        var kindPill = new Border
        {
            CornerRadius = new CornerRadius(3),
            Padding      = new Thickness(4, 1, 4, 1),
            Margin       = new Thickness(0, 0, 6, 0),
            Background   = (Brush)TryFindResource(pillBgKey),
            Child = new TextBlock
            {
                Text       = pillLabel,
                FontSize   = 11,
                Foreground = (Brush)TryFindResource(pillFgKey)
            }
        };

        var elapsedText = new TextBlock
        {
            Text                = FormatElapsed(entry.PublishedAt),
            FontSize            = 11,
            VerticalAlignment   = VerticalAlignment.Center,
            Foreground          = (Brush)TryFindResource("TextSecondaryBrush")
        };

        var metaRow = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 3) };
        metaRow.Children.Add(kindPill);
        metaRow.Children.Add(elapsedText);

        var titleText = new TextBlock
        {
            Text         = entry.Title,
            FontSize     = 13,
            TextWrapping = TextWrapping.Wrap,
            Foreground   = (Brush)TryFindResource("TextPrimaryBrush")
        };

        var infoPanel = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        infoPanel.Children.Add(metaRow);
        infoPanel.Children.Add(titleText);

        thumbnailArea.Cursor = Cursors.Hand;
        thumbnailArea.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            var url = YouTubeConstants.WatchUrlBase + entry.VideoId;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            AppLogger.Log(LogMsg.RecentUploadThumbnailOpened, null, channel.ChannelName);
        };

        infoPanel.Cursor = Cursors.Hand;
        infoPanel.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            new VideoSummaryPopupWindow(this, channel, entry.Kind, entry.Title, entry.VideoId,
                duration: entry.Duration, thumbnailUrl: entry.ThumbnailUrl).Show();
        };

        var row = new DockPanel();
        DockPanel.SetDock(thumbnailArea, Dock.Left);
        row.Children.Add(thumbnailArea);
        row.Children.Add(infoPanel);

        var card = new Border
        {
            CornerRadius    = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(10),
            Margin          = new Thickness(0, 0, 0, RowBottomMargin),
            Background      = (Brush)TryFindResource("SurfaceElevatedBrush"),
            BorderBrush     = (Brush)TryFindResource("BorderBrush"),
            Child           = row
        };
        return card;
    }

    private static void LoadRecentUploadThumbnail(ChannelInfo channel, RecentUploadEntry entry, Image image)
    {
        if (ImageCacheService.TryGetCachedThumbnail(channel.ChannelId, entry.Kind, entry.VideoId, out var cached) && cached != null)
        {
            image.Source = cached;
            return;
        }

        if (string.IsNullOrEmpty(entry.ThumbnailUrl)) return;

        Task.Run(async () =>
        {
            var bmp = await ImageCacheService.GetOrDownloadThumbnailAsync(entry.ThumbnailUrl, channel.ChannelId, entry.Kind, entry.VideoId);
            if (bmp == null) return;
            await image.Dispatcher.InvokeAsync(() => image.Source = bmp);
        });
    }

    /// <summary>投稿日時からの相対経過表示（例："今日 09:12" / "1日前" / "7日前"）</summary>
    private static string FormatElapsed(DateTime? publishedAt)
    {
        if (!publishedAt.HasValue) return string.Empty;

        var elapsedDays = (DateTime.Now.Date - publishedAt.Value.Date).Days;
        return elapsedDays <= 0
            ? $"今日 {publishedAt.Value:HH:mm}"
            : $"{elapsedDays}日前";
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
