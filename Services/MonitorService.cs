using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;
using Timer = System.Threading.Timer;
using Microsoft.Toolkit.Uwp.Notifications;
using YTNotifier.Models;

namespace YTNotifier.Services;

public class MonitorService : IDisposable
{
    private static MonitorService? _instance;
    public static MonitorService Instance => _instance ??= new MonitorService();

    private readonly YouTubeApiClient _youtubeClient = new();
    private Timer? _timer;
    private bool _isRunning    = false;
    private bool _isChecking   = false;
    private bool _isStartupCheck = true; // 起動時チェックフラグ

    public event Action<bool>? StatusChanged;
    public event Action? ChannelUpdated;
    public bool IsRunning => _isRunning;

    private MonitorService()
    {
        // トースト通知クリックハンドラはインスタンス生成時に1回だけ登録
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning      = true;
        _isStartupCheck = true;

        var interval = TimeSpan.FromMinutes(SettingsService.Instance.Settings.CheckIntervalMinutes);
        _timer = new Timer(async _ => await CheckAllChannelsAsync(), null, TimeSpan.Zero, interval);
        LoggerService.Instance.Success("監視を開始しました");
        StatusChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _timer?.Dispose();
        _timer = null;
        _isRunning = false;
        LoggerService.Instance.Info("監視を停止しました");
        StatusChanged?.Invoke(false);
    }

    public void RestartWithNewInterval()
    {
        if (_isRunning) { Stop(); Start(); }
    }

    // ===== トースト通知クリック処理 =====
    private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);

        if (!args.TryGetValue("channelId", out var channelId) || string.IsNullOrEmpty(channelId))
            return;

        Application.Current?.Dispatcher.Invoke(async () =>
        {
            var ch = SettingsService.Instance.Channels
                .FirstOrDefault(c => c.ChannelId == channelId);
            if (ch == null) return;

            // NEWバッジをクリア
            if (ch.HasUnread)
            {
                ch.HasUnread = false;
                SettingsService.Instance.UpdateChannel(ch);
                Instance.ChannelUpdated?.Invoke();
            }

            // アイコンクリックと同じ挙動：有効な種別の最新動画を開く
            await YTNotifier.Views.MainWindow.OpenChannelLatestVideoFromToastAsync(ch);
        });
    }

    // ===== チャンネル一括チェック =====
    private async Task CheckAllChannelsAsync()
    {
        if (_isChecking) return;
        _isChecking = true;

        var channels = SettingsService.Instance.Channels
            .Where(c => c.IsEnabled)
            .ToList();

        if (channels.Count == 0) { _isChecking = false; return; }

        var label = _isStartupCheck ? "起動時チェック" : "定期チェック";
        LoggerService.Instance.Info($"{label}開始 ({channels.Count}チャンネル)");

        var tasks = channels.Select(ch => CheckChannelAsync(ch)).ToList();
        await Task.WhenAll(tasks);

        _isChecking = false;
        _isStartupCheck = false;
        LoggerService.Instance.Info($"{label}完了");
    }

    private async Task CheckChannelAsync(ChannelInfo channel)
    {
        try
        {
            var video = await _youtubeClient.CheckLatestVideoAsync(
                channel.ChannelId, channel.LastCheckedVideoId);

            channel.LastCheckedAt = DateTime.Now;

            if (video == null)
            {
                LoggerService.Instance.Info("新着なし", channel.ChannelName);
                SettingsService.Instance.UpdateChannel(channel);
                return;
            }

            // 種別フィルター確認
            bool shouldNotify = video.Kind switch
            {
                VideoKind.Short => channel.NotifyShort,
                VideoKind.Live  => channel.NotifyLive,
                _               => channel.NotifyVideo
            };

            // 既読更新（通知オフでも重複防止のため更新）
            channel.LastCheckedVideoId = video.VideoId;
            switch (video.Kind)
            {
                case VideoKind.Video: channel.LastVideoId = video.VideoId; break;
                case VideoKind.Live:  channel.LastLiveId  = video.VideoId; break;
                case VideoKind.Short: channel.LastShortId = video.VideoId; break;
            }
            SettingsService.Instance.UpdateChannel(channel);

            if (!shouldNotify)
            {
                LoggerService.Instance.Info(
                    $"新着{video.KindLabel}（通知オフ）: {video.Title}", channel.ChannelName);
                return;
            }

            LoggerService.Instance.Success(
                $"新着{video.KindLabel}: {video.Title}", channel.ChannelName);

            // 未読フラグをセット（UIのバッジ表示用）
            channel.HasUnread = true;
            SettingsService.Instance.UpdateChannel(channel);
            ChannelUpdated?.Invoke();

            var settings  = SettingsService.Instance.Settings;
            var videoUrl = $"https://www.youtube.com/watch?v={video.VideoId}";

            if (settings.ShowDesktopNotification)
                ShowToastNotification(channel.ChannelName, video.Title, video.KindLabel, videoUrl, channel.ChannelId);
            else if (settings.NotificationSound)
                PlayNotificationSound();
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"チェック失敗: {ex.Message}", channel.ChannelName);
        }
    }

    // ===== トースト通知送信 =====
    private static void ShowToastNotification(
        string channelName, string videoTitle, string kindLabel, string videoUrl,
        string channelId = "")
    {
        try
        {
            var settings = SettingsService.Instance.Settings;

            var builder = new ToastContentBuilder()
                .AddText($"📺 {channelName}  [{kindLabel}]")
                .AddText(videoTitle)
                .AddArgument("url", videoUrl)
                .AddArgument("channelId", channelId);

            // トースト通知は常にサイレント（音は PlayNotificationSound で制御）
            builder.AddAudio(null, silent: true);
            builder.Show();

            // 通知音ON なら再生
            if (settings.NotificationSound)
                PlayNotificationSound();

            LoggerService.Instance.Info($"通知送信: {videoTitle}");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning($"通知送信失敗: {ex.Message}");
        }
    }

    // ===== 通知音再生 =====
    // Resources\notification.wav があればそれを、なければ Windows の notify.wav を使用
    private static void PlayNotificationSound()
    {
        try
        {
            // カスタムWAV優先
            var exeDir  = System.IO.Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            var custom  = System.IO.Path.Combine(exeDir, "Resources", "notification.wav");

            if (System.IO.File.Exists(custom))
            {
                using var player = new System.Media.SoundPlayer(custom);
                player.Play();
                return;
            }

            // Windows デフォルト: %SystemRoot%\Media\notify.wav
            var sysMedia = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Media", "notify.wav");

            if (System.IO.File.Exists(sysMedia))
            {
                using var player = new System.Media.SoundPlayer(sysMedia);
                player.Play();
            }
            else
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
        catch { }
    }

    public void SendTestNotification()
    {
        var settings = SettingsService.Instance.Settings;
        try
        {
            if (settings.ShowDesktopNotification)
            {
                var builder = new ToastContentBuilder()
                    .AddText("📺 YTNotifier  [テスト]")
                    .AddText("通知テスト：正常に動作しています")
                    .AddArgument("url", "https://www.youtube.com")
                    .AddAudio(null, silent: true);
                builder.Show();
            }

            if (settings.NotificationSound)
                PlayNotificationSound();

            LoggerService.Instance.Success("テスト通知を送信しました");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning($"テスト通知失敗: {ex.Message}");
        }
    }

    public async Task<bool> ManualCheckAsync()
    {
        if (_isChecking) return false;
        await CheckAllChannelsAsync();
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
