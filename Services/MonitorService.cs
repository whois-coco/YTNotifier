using System.Diagnostics;
using System.Windows;
using Application = System.Windows.Application;
using Timer = System.Threading.Timer;
using Microsoft.Toolkit.Uwp.Notifications;

namespace YTNotifier.Services;

public class MonitorService : IDisposable
{
    private static MonitorService? _instance;
    public static MonitorService Instance => _instance ??= new MonitorService();

    private readonly IYouTubeApiClient _youtubeClient = YouTubeApiClient.Instance;
    private Timer? _timer;
    private bool _isRunning    = false;
    private int  _isChecking; // 0=idle, 1=running — Interlocked で操作
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
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0) return;
        try
        {
            var now = DateTime.Now;
            var allChannels = SettingsService.Instance.Channels.Where(c => c.IsEnabled).ToList();

            // 通常は NextCheckAt を過ぎたチャンネルのみ。手動チェック(forceAll)時は全チャンネル
            var channels = forceAll
                ? allChannels
                : allChannels.Where(c => c.NextCheckAt <= now).ToList();

            if (channels.Count == 0) return;

            var label = _isStartupCheck ? "起動時チェック" : "定期チェック";
            LoggerService.Instance.Info($"{label}開始 ({channels.Count}/{allChannels.Count}チャンネル)");

            var tasks = channels.Select(ch => CheckChannelAsync(ch)).ToList();
            await Task.WhenAll(tasks);

            _isStartupCheck = false;
            SettingsService.Instance.SaveSettingsSilent();  // クォータを一括保存
            SettingsService.Instance.SaveChannelsSilent();  // 全チャンネル状態を一括保存
            LoggerService.Instance.Info($"{label}完了");
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
    }

    private async Task CheckChannelAsync(ChannelInfo channel)
    {
        try
        {
            List<VideoInfo> videos;
            List<VideoInfo> pendingTransitionedList;

            if (channel.IsTestChannel)
            {
                (videos, pendingTransitionedList) = TestChannelService.GetNextCheckResult(channel);
            }
            else
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
                (videos, pendingTransitionedList) = await _youtubeClient.CheckLatestVideosAsync(
                    channel.ChannelId, channel.LastCheckedVideoId,
                    channel.UploadsPlaylistId, channel.PendingUpcomingVideoIds);
            }

            channel.LastCheckedAt = DateTime.Now;

            if (videos.Count == 0 && pendingTransitionedList.Count == 0)
            {
                LoggerService.Instance.Info("新着なし", channel.ChannelName, YTNotifier.Models.LogCategory.NoNew);
                SettingsService.Instance.UpdateChannelInMemory(channel);
                return;
            }

            var settings = SettingsService.Instance.Settings;

            // ───── pending プレミア公開の live 遷移処理 ─────
            string? checkpointOverrideId = null;
            VideoInfo? checkpointTransition = null;
            if (pendingTransitionedList.Count > 0)
            {
                foreach (var pendingTransitioned in pendingTransitionedList)
                {
                    // pending が新着リストの先頭（最新）なら、除去後も先頭IDをチェックポイントとして維持
                    if (videos.Count > 0 && videos[0].VideoId == pendingTransitioned.VideoId)
                    {
                        checkpointOverrideId = pendingTransitioned.VideoId;
                        checkpointTransition = pendingTransitioned;
                    }
                    channel.PendingUpcomingVideoIds.Remove(pendingTransitioned.VideoId);
                    videos.RemoveAll(v => v.VideoId == pendingTransitioned.VideoId);

                    bool pendingAllowed = NotificationFilter.IsAllowed(pendingTransitioned, channel);

                    if (pendingAllowed)
                    {
                        LoggerService.Instance.Success(
                            $"新着{pendingTransitioned.KindLabel}: {pendingTransitioned.Title}",
                            channel.ChannelName, YTNotifier.Models.LogCategory.NewFound);
                        channel.HasUnread = true;
                        SettingsService.Instance.UpdateChannelInMemory(channel);
                        ChannelUpdated?.Invoke();

                        var pendingUrl = $"https://www.youtube.com/watch?v={pendingTransitioned.VideoId}";
                        if (settings.ShowDesktopNotification)
                            await ShowToastNotificationAsync(
                                channel.ChannelName, pendingTransitioned.Title, pendingTransitioned.KindLabel,
                                pendingUrl, channel.ChannelId, pendingTransitioned.Kind,
                                channel.ThumbnailUrl, pendingTransitioned.ThumbnailUrl);
                        else if (settings.NotificationSound)
                            PlayNotificationSound(pendingTransitioned.Kind);
                    }
                }

                // pending が新着の最新だった場合: チェックポイントを更新して終了
                if (videos.Count == 0 && checkpointTransition != null)
                {
                    channel.LastCheckedVideoId = checkpointTransition.VideoId;
                    switch (checkpointTransition.Kind)
                    {
                        case VideoKind.Video:
                        case VideoKind.Premiere: channel.LastVideoId = checkpointTransition.VideoId; break;
                        case VideoKind.Live:     channel.LastLiveId  = checkpointTransition.VideoId; break;
                        case VideoKind.Short:    channel.LastShortId = checkpointTransition.VideoId; break;
                    }
                    SettingsService.Instance.UpdateChannelInMemory(channel);
                    return;
                }
            }

            // ───── 通常の新着動画処理 ─────
            if (videos.Count == 0) return;

            // 新着動画を順に確認し、通知対象の最初の動画を探す
            VideoInfo? notifyVideo = null;
            foreach (var video in videos)
            {
                // 待機所スキップ判定（return せず後続の新着も処理する）
                if (NotificationFilter.ShouldSkipUpcoming(video, channel, settings))
                {
                    LoggerService.Instance.Info(
                        $"待機所スキップ（live待ち）: {video.Title}",
                        channel.ChannelName, YTNotifier.Models.LogCategory.NoNew);
                    if (!channel.PendingUpcomingVideoIds.Contains(video.VideoId))
                        channel.PendingUpcomingVideoIds.Add(video.VideoId);
                    continue;
                }

                // upcoming待ちの動画が live になった場合はペンディングをクリア
                if (!video.IsUpcoming)
                    channel.PendingUpcomingVideoIds.Remove(video.VideoId);

                // 種別フィルター確認
                bool allowed = NotificationFilter.IsAllowed(video, channel);

                if (allowed)
                {
                    notifyVideo = video;
                    break;
                }
                else
                {
                    LoggerService.Instance.Info(
                        $"種別フィルタースキップ [{video.KindLabel}]: {video.Title}",
                        channel.ChannelName, YTNotifier.Models.LogCategory.NoNew);
                }
            }

            // 最新動画の既読更新（通知対象外でも更新）
            // upcoming でもチェックポイントは進める（遷移は pending パスが追跡する）
            var latestVideo = videos[0];
            channel.LastCheckedVideoId = checkpointOverrideId ?? latestVideo.VideoId;
            if (!latestVideo.IsUpcoming)
            {
                switch (latestVideo.Kind)
                {
                    case VideoKind.Video:
                    case VideoKind.Premiere: channel.LastVideoId = latestVideo.VideoId; break;
                    case VideoKind.Live:     channel.LastLiveId  = latestVideo.VideoId; break;
                    case VideoKind.Short:    channel.LastShortId = latestVideo.VideoId; break;
                }
            }
            SettingsService.Instance.UpdateChannelInMemory(channel);

            if (notifyVideo == null) return;

            var videoUrl = $"https://www.youtube.com/watch?v={notifyVideo!.VideoId}";

            LoggerService.Instance.Success(
                $"新着{notifyVideo.KindLabel}: {notifyVideo.Title}", channel.ChannelName, YTNotifier.Models.LogCategory.NewFound);

            // 未読フラグをセット
            channel.HasUnread = true;
            SettingsService.Instance.UpdateChannelInMemory(channel);
            ChannelUpdated?.Invoke();

            if (settings.ShowDesktopNotification)
                await ShowToastNotificationAsync(channel.ChannelName, notifyVideo.Title, notifyVideo.KindLabel, videoUrl, channel.ChannelId, notifyVideo.Kind, channel.ThumbnailUrl, notifyVideo.ThumbnailUrl);
            else if (settings.NotificationSound)
                PlayNotificationSound(notifyVideo.Kind);
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
            SettingsService.Instance.UpdateChannelInMemory(channel);
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

    // ===== トースト通知送信 =====
    private static async Task ShowToastNotificationAsync(
        string channelName, string videoTitle, string kindLabel, string videoUrl,
        string channelId = "", VideoKind kind = VideoKind.Video,
        string? channelThumbnailUrl = null, string? videoThumbnailUrl = null)
    {
        try
        {
            var settings = SettingsService.Instance.Settings;

            var builder = new ToastContentBuilder()
                .AddArgument("url", videoUrl)
                .AddArgument("channelId", channelId);

            if (settings.ToastStyle == YTNotifier.Models.ToastStyle.Thumbnail)
            {
                // ─── サムネイル通知 ──────────────────────────────────────
                // ヒーロー画像: 動画サムネイルをディスクキャッシュ経由で参照
                if (!string.IsNullOrEmpty(videoThumbnailUrl))
                {
                    var heroPath = await GetOrDownloadThumbnailAsync(videoThumbnailUrl);
                    if (!string.IsNullOrEmpty(heroPath))
                    {
                        try { builder.AddHeroImage(new Uri("file:///" + heroPath.Replace("\\", "/"))); }
                        catch { }
                    }
                }

                // チャンネルアイコン（丸型・ディスクキャッシュから）
                if (!string.IsNullOrEmpty(channelThumbnailUrl))
                {
                    var iconPath = SettingsService.Instance.GetIconDiskPath(channelThumbnailUrl);
                    if (System.IO.File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);
                }

                // Attribution: チャンネル名
                builder.AddAttributionText(channelName);

                // 種別 → タイトル
                builder.AddText(kindLabel);
                builder.AddText(videoTitle);
            }
            else
            {
                // ─── デフォルト通知 ──────────────────────────────────────
                if (!string.IsNullOrEmpty(channelThumbnailUrl))
                {
                    var iconPath = SettingsService.Instance.GetIconDiskPath(channelThumbnailUrl);
                    if (System.IO.File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);
                }

                builder.AddText($"{channelName}  [{kindLabel}]");
                builder.AddText(videoTitle);
            }

            builder.AddAudio(null, silent: true);
            builder.Show();

            if (settings.NotificationSound)
                PlayNotificationSound(kind);

            LoggerService.Instance.Info($"通知送信: {videoTitle}", null, YTNotifier.Models.LogCategory.Notify);
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning($"通知送信失敗: {ex.Message}", null, YTNotifier.Models.LogCategory.Notify);
        }
    }

    /// <summary>動画サムネイルをディスクにキャッシュしてパスを返す（ヒーロー画像用）</summary>
    private static async Task<string?> GetOrDownloadThumbnailAsync(string url)
    {
        try
        {
            var cacheDir = System.IO.Path.Combine(
                SettingsService.Instance.AppDataDir, "thumbcache");
            System.IO.Directory.CreateDirectory(cacheDir);

            // 1週間以上古いキャッシュを削除
            foreach (var old in System.IO.Directory.GetFiles(cacheDir, "*.jpg"))
                if (System.IO.File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7))
                    try { System.IO.File.Delete(old); } catch { }

            var hash     = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url));
            var fileName = Convert.ToHexString(hash).ToLower() + ".jpg";
            var filePath = System.IO.Path.Combine(cacheDir, fileName);

            if (System.IO.File.Exists(filePath)) return filePath;

            using var http = new System.Net.Http.HttpClient();
            http.Timeout   = TimeSpan.FromSeconds(5);
            var bytes      = await http.GetByteArrayAsync(url);
            System.IO.File.WriteAllBytes(filePath, bytes);
            return filePath;
        }
        catch { return null; }
    }


    // ===== 通知音再生 =====
    /// <summary>
    /// テスト通知専用の音再生。
    /// Sounds\test.wav があればそれを優先、なければ通常の PlayNotificationSound にフォールバック
    /// </summary>
    private static void PlayTestNotificationSound()
    {
        try
        {
            var exeDir   = System.IO.Path.GetDirectoryName(
                Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
            var testPath = System.IO.Path.Combine(exeDir, "Sounds", "test.wav");

            if (System.IO.File.Exists(testPath))
            {
                using var player = new System.Media.SoundPlayer(testPath);
                player.Play();
                return;
            }
        }
        catch { }

        // test.wav がない場合は通常の通知音
        PlayNotificationSound();
    }

    // Sounds\video.wav / short.wav / live.wav があれば種別ごとに再生、
    // 該当ファイルがなければ Windows の notify.wav、それもなければ Asterisk を使用
    private static void PlayNotificationSound(VideoKind kind = VideoKind.Video)
    {
        try
        {
            var exeDir = System.IO.Path.GetDirectoryName(
                Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

            var soundsDir = System.IO.Path.Combine(exeDir, "Sounds");
            var kindFile  = kind switch
            {
                VideoKind.Short => "short.wav",
                VideoKind.Live  => "live.wav",
                _               => "video.wav"
            };
            var customPath = System.IO.Path.Combine(soundsDir, kindFile);

            if (System.IO.File.Exists(customPath))
            {
                using var player = new System.Media.SoundPlayer(customPath);
                player.Play();
                return;
            }

            // Sounds フォルダなし or 該当ファイルなし → Windows の notify.wav
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
                var exeDir   = System.IO.Path.GetDirectoryName(Environment.ProcessPath
                    ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";
                var iconPath = System.IO.Path.Combine(exeDir, "Resources", "app.png");

                var builder = new ToastContentBuilder()
                    .AddArgument("url", "https://www.youtube.com");

                if (settings.ToastStyle == YTNotifier.Models.ToastStyle.Thumbnail)
                {
                    // HeroImage スタイル: サムネイル位置にアイコンを代用
                    if (System.IO.File.Exists(iconPath))
                    {
                        try { builder.AddHeroImage(new Uri("file:///" + iconPath.Replace("\\", "/"))); }
                        catch { }
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);
                    }
                    builder.AddAttributionText("YTNotifier");
                    builder.AddText("[テスト]");
                    builder.AddText("通知テスト：正常に動作しています");
                }
                else
                {
                    // Standard スタイル
                    if (System.IO.File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);

                    builder.AddText("YTNotifier  [テスト]");
                    builder.AddText("通知テスト：正常に動作しています");
                }

                builder.AddAudio(null, silent: true);
                builder.Show();
            }

            if (settings.NotificationSound)
                PlayTestNotificationSound();

            LoggerService.Instance.Success("テスト通知を送信しました");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning($"テスト通知失敗: {ex.Message}");
        }
    }

    public async Task<bool> ManualCheckAsync()
    {
        if (_isChecking != 0) return false;
        await CheckAllChannelsAsync(forceAll: true);
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
