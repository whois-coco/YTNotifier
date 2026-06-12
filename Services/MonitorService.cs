using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;
using Timer = System.Threading.Timer;
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
    public event Action? QuotaUpdated;

    public void NotifyQuotaUpdated() => QuotaUpdated?.Invoke();
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

        // 全チャンネルの NextCheckAt を初期化（即時チェック）
        foreach (var ch in SettingsService.Instance.Channels.Where(c => c.IsEnabled))
            ch.NextCheckAt = DateTime.MinValue;

        // タイマーは1分固定で回し、NextCheckAt を過ぎたチャンネルのみチェック
        _timer = new Timer(async _ => await CheckAllChannelsAsync(), null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
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
                SettingsService.Instance.UpdateChannelSilent(ch);
                Instance.ChannelUpdated?.Invoke();
            }

            // アイコンクリックと同じ挙動：有効な種別の最新動画を開く
            await YTNotifier.Views.MainWindow.OpenChannelLatestVideoFromToastAsync(ch);
        });
    }

    // ===== チャンネル一括チェック =====
    private async Task CheckAllChannelsAsync(bool forceAll = false)
    {
        if (_isChecking) return;
        _isChecking = true;

        var now = DateTime.Now;
        var allChannels = SettingsService.Instance.Channels.Where(c => c.IsEnabled).ToList();

        // 通常は NextCheckAt を過ぎたチャンネルのみ。手動チェック(forceAll)時は全チャンネル
        var channels = forceAll
            ? allChannels
            : allChannels.Where(c => c.NextCheckAt <= now).ToList();

        if (channels.Count == 0) { _isChecking = false; return; }

        var label = _isStartupCheck ? "起動時チェック" : "定期チェック";
        LoggerService.Instance.Info($"{label}開始 ({channels.Count}/{allChannels.Count}チャンネル)");

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
            // UploadsPlaylistIdが未取得の場合はAPIから取得して保存
            if (string.IsNullOrEmpty(channel.UploadsPlaylistId))
            {
                var pid = await _youtubeClient.GetUploadsPlaylistIdAsync(channel.ChannelId);
                if (!string.IsNullOrEmpty(pid))
                {
                    channel.UploadsPlaylistId = pid;
                    SettingsService.Instance.SaveChannelsSilent();
                }
            }

            var videos = await _youtubeClient.CheckLatestVideosAsync(
                channel.ChannelId, channel.LastCheckedVideoId,
                channel.UploadsPlaylistId, channel.PendingUpcomingVideoId);

            channel.LastCheckedAt = DateTime.Now;

            if (videos.Count == 0)
            {
                LoggerService.Instance.Info("新着なし", channel.ChannelName, YTNotifier.Models.LogCategory.NoNew);
                SettingsService.Instance.UpdateChannelSilent(channel);
                return;
            }

            // 新着動画を順に確認し、通知対象の最初の動画を探す
            VideoInfo? notifyVideo = null;
            foreach (var video in videos)
            {
                // 待機所スキップ判定
                if (video.IsUpcoming)
                {
                    bool globalOff  = !SettingsService.Instance.Settings.GlobalNotifyUpcoming;
                    bool channelOff = !channel.NotifyUpcoming;
                    if (globalOff || channelOff)
                    {
                        LoggerService.Instance.Info(
                            $"待機所スキップ（live待ち）: {video.Title}",
                            channel.ChannelName, YTNotifier.Models.LogCategory.NoNew);
                        channel.LastCheckedAt          = DateTime.Now;
                        channel.PendingUpcomingVideoId = video.VideoId;
                        SettingsService.Instance.UpdateChannelSilent(channel);
                        return;
                    }
                }

                // upcoming待ちの動画が live になった場合はペンディングをクリア
                if (!video.IsUpcoming && video.VideoId == channel.PendingUpcomingVideoId)
                    channel.PendingUpcomingVideoId = string.Empty;

                // 種別フィルター確認
                bool allowed = video.Kind switch
                {
                    VideoKind.Short    => channel.NotifyShort,
                    VideoKind.Live     => channel.NotifyLive,
                    VideoKind.Premiere => channel.NotifyVideo,
                    _                  => channel.NotifyVideo
                };

                if (allowed)
                {
                    notifyVideo = video;
                    break; // 通知対象の最初の動画が見つかった
                }
                else
                {
                    // フィルター対象外の種別はスキップ（既読にして次へ）
                    LoggerService.Instance.Info(
                        $"種別フィルタースキップ [{video.KindLabel}]: {video.Title}",
                        channel.ChannelName, YTNotifier.Models.LogCategory.NoNew);
                }
            }

            // 最新動画の既読更新（通知対象外でも更新）
            var latestVideo = videos[0];
            channel.LastCheckedVideoId = latestVideo.VideoId;
            switch (latestVideo.Kind)
            {
                case VideoKind.Video:   channel.LastVideoId = latestVideo.VideoId; break;
                case VideoKind.Live:    channel.LastLiveId  = latestVideo.VideoId; break;
                case VideoKind.Short:   channel.LastShortId = latestVideo.VideoId; break;
            }
            SettingsService.Instance.UpdateChannelSilent(channel);

            if (notifyVideo == null) return;

            var settings  = SettingsService.Instance.Settings;
            var videoUrl = $"https://www.youtube.com/watch?v={notifyVideo!.VideoId}";

            LoggerService.Instance.Success(
                $"新着{notifyVideo.KindLabel}: {notifyVideo.Title}", channel.ChannelName, YTNotifier.Models.LogCategory.NewFound);

            // 未読フラグをセット
            channel.HasUnread = true;
            SettingsService.Instance.UpdateChannelSilent(channel);
            ChannelUpdated?.Invoke();

            if (settings.ShowDesktopNotification)
                NotificationService.ShowVideoNotification(channel.ChannelName, notifyVideo.Title, notifyVideo.KindLabel, videoUrl, channel.ChannelId, notifyVideo.Kind, channel.ThumbnailUrl, notifyVideo.ThumbnailUrl);
            else if (settings.NotificationSound)
                NotificationService.PlaySound(notifyVideo.Kind);
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"チェック失敗: {ex.Message}", channel.ChannelName, YTNotifier.Models.LogCategory.CheckError);
            // 通信エラーの可能性 → UIのネットワーク状態を即時チェック
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (System.Windows.Application.Current?.MainWindow is YTNotifier.Views.MainWindow mw)
                    mw.CheckNetworkState();
            });
        }
        finally
        {
            // モードに応じて次回チェック時刻を設定
            channel.NextCheckAt = CalcNextCheckAt(channel);
            SettingsService.Instance.UpdateChannelSilent(channel);
        }
    }

    // ===== 次回チェック時刻を計算 =====
    public static DateTime CalcNextCheckAt(ChannelInfo ch)
    {
        var now         = DateTime.Now;
        var globalMins  = SettingsService.Instance.Settings.CheckIntervalMinutes;

        switch (ch.MonitorMode)
        {
            case MonitorMode.LowFreq:
                return now.AddMinutes(ch.LowFreqIntervalMinutes);

            case MonitorMode.Focus:
            {
                // 有効スロット一覧（FocusSlotsが空なら旧フィールドから1スロット構成）
                var slots = ch.FocusSlots.Where(s => s.IsEnabled).ToList();
                if (slots.Count == 0)
                {
                    slots = new List<FocusSlot>
                    {
                        new FocusSlot
                        {
                            Days = ch.FocusDays, Hour = ch.FocusHour, Minute = ch.FocusMinute,
                            WindowMinutes = ch.FocusWindowMinutes, IntervalMinutes = ch.FocusIntervalMinutes,
                            IsEnabled = true
                        }
                    };
                }

                // いずれかのスロットの時間帯内なら、そのスロットの間隔で次回
                foreach (var slot in slots)
                {
                    var anchor      = now.Date.AddHours(slot.Hour).AddMinutes(slot.Minute);
                    var windowStart = anchor.AddMinutes(-slot.WindowMinutes);
                    var windowEnd   = anchor.AddMinutes(slot.WindowMinutes);
                    if (now >= windowStart && now <= windowEnd && IsSlotDayMatch(slot.Days, anchor.Date))
                        return now.AddMinutes(slot.IntervalMinutes);
                }

                // 時間帯外: 全スロットの「次の有効な開始時刻」のうち最も早いものまで待機
                DateTime? earliest = null;
                foreach (var slot in slots)
                {
                    // 今日から最大8日先まで曜日一致する日を探索
                    for (int d = 0; d < 8; d++)
                    {
                        var date   = now.Date.AddDays(d);
                        if (!IsSlotDayMatch(slot.Days, date)) continue;
                        var start  = date.AddHours(slot.Hour).AddMinutes(slot.Minute)
                                         .AddMinutes(-slot.WindowMinutes);
                        if (start <= now) continue; // 既に過ぎた開始時刻はスキップ
                        if (earliest == null || start < earliest) earliest = start;
                        break; // このスロットの最短は確定
                    }
                }
                return earliest ?? now.AddMinutes(globalMins);
            }

            default: // Normal
                return now.AddMinutes(globalMins);
        }
    }

    /// <summary>曜日ビットマスク判定 (bit0=Sun..bit6=Sat、0=全曜日)</summary>
    private static bool IsSlotDayMatch(int daysMask, DateTime date)
        => daysMask == 0 || (daysMask & (1 << (int)date.DayOfWeek)) != 0;

    // 全体設定の間隔変更時に Normal チャンネルの NextCheckAt をリセット
    public void ResetNormalChannels()
    {
        foreach (var ch in SettingsService.Instance.Channels
            .Where(c => c.IsEnabled && c.MonitorMode == MonitorMode.Normal))
            ch.NextCheckAt = DateTime.MinValue;
    }

    public void SendTestNotification() => NotificationService.ShowTestNotification();

    public async Task<bool> ManualCheckAsync()
    {
        if (_isChecking) return false;
        await CheckAllChannelsAsync(forceAll: true);
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
