using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class VideoSummaryPopupWindow : Window
{
    private readonly ChannelInfo _channel;
    private readonly VideoKind   _kind;
    private readonly string      _videoId;

    public VideoSummaryPopupWindow(Window owner, ChannelInfo channel, VideoKind kind, string title,
        string videoId, TimeSpan? duration = null, bool allowSummary = true, bool isPending = false,
        string? thumbnailUrl = null)
    {
        InitializeComponent();
        Loaded += (_, _) => WindowCornerHelper.Apply(this);
        Owner    = owner;
        _channel = channel;
        _kind    = kind;
        _videoId = videoId;

        ChannelNameText.Text = channel.ChannelName;
        TitleText.Text       = title;

        LoadChannelIcon(channel);
        LoadThumbnailIfAvailable(channel, kind, thumbnailUrl);
        SetKindPill(kind, isPending);
        SetDurationText(duration);
        if (allowSummary) SetupSummarySection(kind, videoId);

        AppLogger.Log(LogMsg.VideoSummaryPopupOpened, null, channel.ChannelName);
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

        SummaryPanel.Visibility = Visibility.Visible;
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

        if (SummaryScriptService.IsAvailable)
        {
            AppLogger.Log(LogMsg.SummaryScriptRequested, null, _videoId);
            var scriptVideoUrl = $"{YouTubeConstants.WatchUrlBase}{_videoId}";
            var scripted       = await SummaryScriptService.TrySummarizeAsync(scriptVideoUrl, apiKey);
            if (scripted != null && !string.IsNullOrEmpty(scripted.Headline))
            {
                ShowSummaryResult(scripted.Headline, scripted.Detail);
                return;
            }
            // scripted == null（スクリプト実行失敗）の場合は下の DLL 経路・既存ロジックへフォールバックする
        }

        if (ExternalSummaryBridge.IsAvailable)
        {
            AppLogger.Log(LogMsg.ExternalSummaryBridgeRequested, null, _videoId);
            var videoUrl = $"{YouTubeConstants.WatchUrlBase}{_videoId}";
            var bridged  = await ExternalSummaryBridge.TrySummarizeAsync(_videoId, videoUrl, apiKey);
            if (bridged != null && !string.IsNullOrEmpty(bridged.Headline))
            {
                ShowSummaryResult(bridged.Headline, bridged.Detail);
                return;
            }
            // bridged == null（DLL呼び出し失敗）の場合は下の既存ロジックへフォールバックする
        }

        // 既存ロジック（DLL未導入、またはバイパス失敗時のフォールバック）
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
