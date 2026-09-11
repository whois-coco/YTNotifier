using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class VideoSummaryPopupWindow : Window
{
    private const string PublishedAtLabelPrefix = "投稿日時：";

    private readonly ChannelInfo _channel;
    private readonly string      _videoId;
    private readonly string      _title;
    private readonly TimeSpan?   _duration;
    private readonly DateTime?   _publishedAt;

    /// <summary>プラグインへ渡す種別文字列。終了済みのライブアーカイブ・公開済みプレミアは <c>video</c> に正規化。</summary>
    private readonly string      _contributionKind;

    public VideoSummaryPopupWindow(Window owner, ChannelInfo channel, VideoKind kind, string title,
        string videoId, TimeSpan? duration = null, bool allowSummary = true, bool isPending = false,
        string? thumbnailUrl = null, DateTime? publishedAt = null)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCornerHelper.Apply(this);
        MaxWidth               = WindowMaxWidthExpanded;
        LeftColumnPanel.MaxWidth = LeftColumnMaxWidth;
        SidePanelBorder.Width    = SidePanelWidth;
        Owner    = owner;
        _channel  = channel;
        _videoId  = videoId;
        _title       = title;
        _duration    = duration;
        _publishedAt = publishedAt;
        _contributionKind = NormalizeContributionKind(kind, isPending);

        ChannelNameText.Text = channel.ChannelName;
        TitleText.Text       = title;

        LoadChannelIcon(channel);
        LoadThumbnailIfAvailable(channel, kind, thumbnailUrl);
        SetKindPill(kind, isPending);
        SetPublishedAtText(kind, publishedAt);
        SetDurationText(duration);
        if (allowSummary) SetupSummarySection(kind, videoId);
        SetupPluginActions();

        AppLogger.Log(LogMsg.VideoSummaryPopupOpened, null, channel.ChannelName);
    }

    private void SetPublishedAtText(VideoKind kind, DateTime? publishedAt)
    {
        if (kind is not (VideoKind.Video or VideoKind.Short or VideoKind.Premiere)) return;

        var text = YouTubeApiClient.FormatPublishedAt(publishedAt);
        if (text == null) return;

        PublishedAtText.Text       = PublishedAtLabelPrefix + text;
        PublishedAtText.Visibility = Visibility.Visible;
    }

    private void SetDurationText(TimeSpan? duration)
    {
        var text = YouTubeApiClient.FormatDurationHms(duration);
        if (text == null) return;

        DurationText.Text       = "動画時間：" + text;
        DurationText.Visibility = Visibility.Visible;
    }

    private void LoadChannelIcon(ChannelInfo channel)
    {
        if (ImageCacheService.TryGetCachedIcon(channel.ThumbnailUrl, out var cached) && cached != null)
        {
            ChannelIconBorder.Background = new ImageBrush(cached) { Stretch = Stretch.UniformToFill };
            return;
        }

        if (string.IsNullOrEmpty(channel.ThumbnailUrl)) return;

        Task.Run(async () =>
        {
            var bmp = await ImageCacheService.GetOrDownloadIconAsync(channel.ThumbnailUrl);
            if (bmp == null) return;
            await Dispatcher.InvokeAsync(() =>
                ChannelIconBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill });
        });
    }

    private void LoadThumbnailIfAvailable(ChannelInfo channel, VideoKind kind, string? thumbnailUrl = null)
    {
        if (SettingsService.Instance.Settings.ToastStyle != ToastStyle.Thumbnail) return;

        if (ImageCacheService.TryGetCachedThumbnail(channel.ChannelId, kind, _videoId, out var cached) && cached != null)
        {
            ThumbnailImage.Source      = cached;
            ThumbnailBorder.Visibility = Visibility.Visible;
            return;
        }

        // 動画固有のサムネイルURLが指定されていればそちらを使う。未指定の場合は従来通り
        // チャンネルの現在の最新動画のサムネイルにフォールバックする。
        var url = thumbnailUrl ?? channel.LatestThumbnailUrl;
        if (string.IsNullOrEmpty(url)) return;

        Task.Run(async () =>
        {
            var bmp = await ImageCacheService.GetOrDownloadThumbnailAsync(url, channel.ChannelId, kind, _videoId);
            if (bmp == null) return;
            await Dispatcher.InvokeAsync(() =>
            {
                ThumbnailImage.Source      = bmp;
                ThumbnailBorder.Visibility = Visibility.Visible;
            });
        });
    }

    private void SetKindPill(VideoKind kind, bool isPending)
    {
        var (label, bgKey, fgKey) = kind switch
        {
            VideoKind.Video    => ("動画",  "KindPillVideoBgBrush",    "KindPillVideoFgBrush"),
            VideoKind.Short    => ("Short", "KindPillShortBgBrush",    "KindPillShortFgBrush"),
            VideoKind.Live     => (isPending ? "配信予定" : (_channel.ActiveLives.Count == 0 ? "アーカイブ" : "ライブ"),
                                   "KindPillLiveBgBrush",     "KindPillLiveFgBrush"),
            VideoKind.Premiere => ("プレミア", "KindPillPremiereBgBrush", "KindPillPremiereFgBrush"),
            _                  => (string.Empty, "KindPillVideoBgBrush", "KindPillVideoFgBrush"),
        };

        KindPillText.Text       = label;
        KindPill.Background     = (System.Windows.Media.Brush)TryFindResource(bgKey);
        KindPillText.Foreground = (System.Windows.Media.Brush)TryFindResource(fgKey);
    }

    private void SetupSummarySection(VideoKind kind, string videoId)
    {
        if ((kind != VideoKind.Video && kind != VideoKind.Short && kind != VideoKind.Premiere) || string.IsNullOrEmpty(videoId)) return;

        var apiKey = GeminiApiKeyService.Load(SettingsService.Instance.ConfDir);
        if (string.IsNullOrEmpty(apiKey)) return;

        SummaryPanel.Visibility  = Visibility.Visible;
        SummaryButton.Visibility = Visibility.Visible;
    }

    private void ShowSummaryResult(string headline, string detail)
    {
        SummaryButton.Visibility        = Visibility.Collapsed;
        SummaryStatusText.Visibility    = Visibility.Collapsed;
        SummaryErrorText.Visibility     = Visibility.Collapsed;
        SummaryHeadlineText.Text        = headline;
        SummaryDetailText.Text          = detail;
        SummaryResultPanel.Visibility   = Visibility.Visible;
    }

    private async void SummaryButton_Click(object sender, RoutedEventArgs e)
    {
        var apiKey = GeminiApiKeyService.Load(SettingsService.Instance.ConfDir);
        if (string.IsNullOrEmpty(apiKey)) return;

        SummaryButton.IsEnabled      = false;
        SummaryErrorText.Visibility  = Visibility.Collapsed;
        SummaryStatusText.Text       = "要約中...";
        SummaryStatusText.Visibility = Visibility.Visible;

        // 要約は Gemini 直渡しのみ。プラグインの要約は video.detail.actions のプラグインボタン経由で提供する。
        var result = await GeminiSummaryService.SummarizeVideoAsync(apiKey, _videoId,
            onChunk: partialText => SummaryStatusText.Text = "要約中...\n" + partialText);

        if (result.Success && result.Headline != null && result.Detail != null)
        {
            ShowSummaryResult(result.Headline, result.Detail);
        }
        else
        {
            SummaryStatusText.Visibility = Visibility.Collapsed;
            SummaryErrorText.Text        = result.ErrorMessage ?? "要約に失敗しました";
            SummaryErrorText.Visibility  = Visibility.Visible;
            SummaryButton.IsEnabled      = true;
        }
    }

    private void TitleBar_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ButtonState == System.Windows.Input.MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
