using System.IO;
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

    public VideoSummaryPopupWindow(Window owner, ChannelInfo channel, VideoKind kind, string title, string videoId, TimeSpan? duration = null)
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
        LoadThumbnailIfAvailable(channel, kind);
        SetKindPill(kind);
        SetDurationText(duration);
        SetupSummarySection(kind, videoId);

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
        try
        {
            var diskPath = ImageCacheService.GetIconDiskPath(channel.ThumbnailUrl, channel.ChannelId);
            if (!File.Exists(diskPath)) return;

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = new Uri(diskPath);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            ChannelIconBorder.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
        }
        catch { }
    }

    private void LoadThumbnailIfAvailable(ChannelInfo channel, VideoKind kind)
    {
        if (SettingsService.Instance.Settings.ToastStyle != ToastStyle.Thumbnail) return;

        var path = ImageCacheService.GetThumbnailDiskPathIfExists(channel.ChannelId, kind);
        if (path == null) return;

        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource   = new Uri(path);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();

            ThumbnailImage.Source        = bmp;
            ThumbnailBorder.Visibility   = Visibility.Visible;
        }
        catch { }
    }

    private void SetKindPill(VideoKind kind)
    {
        var (label, bgKey, fgKey) = kind switch
        {
            VideoKind.Video    => ("動画",  "KindPillVideoBgBrush",    "KindPillVideoFgBrush"),
            VideoKind.Short    => ("Short", "KindPillShortBgBrush",    "KindPillShortFgBrush"),
            VideoKind.Live     => (_channel.ActiveLives.Count == 0 ? "アーカイブ" : "ライブ",
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
        if (kind != VideoKind.Video || string.IsNullOrEmpty(videoId)) return;

        var apiKey = GeminiApiKeyService.Load(SettingsService.Instance.ConfDir);
        if (string.IsNullOrEmpty(apiKey)) return;

        SummaryPanel.Visibility = Visibility.Visible;

        var cached = GeminiSummaryCacheService.Get(SettingsService.Instance.AppDataDir, videoId);
        if (cached != null)
        {
            AppLogger.Log(LogMsg.GeminiSummaryCacheHit, null, videoId);
            ShowSummaryResult(cached.Headline, cached.Detail);
        }
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

        var result = await GeminiSummaryService.SummarizeVideoAsync(apiKey, _videoId);

        if (result.Success && result.Headline != null && result.Detail != null)
        {
            GeminiSummaryCacheService.Save(SettingsService.Instance.AppDataDir, _videoId,
                new GeminiSummaryEntry { Headline = result.Headline, Detail = result.Detail });
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
