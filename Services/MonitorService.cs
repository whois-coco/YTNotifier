using System.Windows;
using Application = System.Windows.Application;
using Timer = System.Threading.Timer;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

public class MonitorService : IDisposable
{
    private static readonly Lazy<MonitorService> _lazy = new(() => new MonitorService());
    public static MonitorService Instance => _lazy.Value;

    // FocusSlots 未設定時のフォールバック生成を避けるための空リスト定数
    private static readonly IReadOnlyList<FocusSlot> _emptySlots = Array.Empty<FocusSlot>();

    private readonly IYouTubeApiClient _youtubeClient;
    private Timer? _timer;
    private volatile bool _isRunning      = false;
    private int           _isChecking     = 0;
    private volatile bool _isStartupCheck = true;
    private readonly object _quotaLock = new();
    private DateTime?       _quotaSuspendedUntil = null;

    public event Action<bool>? StatusChanged;
    public event Action? ChannelUpdated;
    public event Action? QuotaUpdated;

    public void NotifyQuotaUpdated() => QuotaUpdated?.Invoke();
    public bool IsRunning => _isRunning;
    public DateTime? QuotaSuspendedUntil { get { lock (_quotaLock) return _quotaSuspendedUntil; } }

    private MonitorService(IYouTubeApiClient? youtubeClient = null)
    {
        _youtubeClient = youtubeClient ?? new YouTubeApiClient();
        NotificationService.RegisterToastActivation();
    }

    /// <summary>次の :01 秒までの遅延を計算する（最大60秒・遅延最小化）</summary>
    private static TimeSpan CalcAlignedDelay()
    {
        var sec = 61 - DateTime.Now.Second;
        if (sec > 60) sec -= 60;
        return TimeSpan.FromSeconds(sec);
    }

    /// <summary>次の :00 秒にタイマーを再スケジュールする</summary>
    private void ScheduleNextTick()
    {
        if (!_isRunning) return;
        try { _timer?.Change(CalcAlignedDelay(), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    public void Start()
    {
        if (_isRunning) return;
        _isRunning      = true;
        _isStartupCheck = true;

        foreach (var ch in SettingsService.Instance.GetEnabledChannelsSnapshot())
            ch.NextCheckAt = DateTime.MinValue;

        _timer = new Timer(async _ =>
        {
            try { await CheckAllChannelsAsync(); }
            catch (Exception ex) { AppLogger.Log(LogMsg.CheckFailed, null, ex.Message); }
            finally { ScheduleNextTick(); }
        }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        AppLogger.Log(LogMsg.MonitorStarted);
        StatusChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _timer?.Dispose();
        _timer = null;
        _isRunning = false;
        AppLogger.Log(LogMsg.MonitorStopped);
        StatusChanged?.Invoke(false);
    }

    public void RestartWithNewInterval()
    {
        if (!_isRunning) return;
        try { _timer?.Change(CalcAlignedDelay(), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // ===== チャンネル一括チェック =====
    private async Task<bool> CheckAllChannelsAsync(bool forceAll = false)
    {
        if (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0) return false;
        try
        {
            var now = DateTime.Now;

            // クォータ停止中はスキップ（16:00 リセット後は自動解除）
            bool resumed = false;
            lock (_quotaLock)
            {
                if (_quotaSuspendedUntil.HasValue)
                {
                    if (now < _quotaSuspendedUntil.Value) return false;
                    _quotaSuspendedUntil = null;
                    resumed = true;
                }
            }
            if (resumed)
            {
                AppLogger.Log(LogMsg.QuotaResumed);
                StatusChanged?.Invoke(true); // 再開を UI に通知
            }
            var allChannels = SettingsService.Instance.GetEnabledChannelsSnapshot();

            var channels = forceAll
                ? allChannels
                : allChannels.Where(c => c.NextCheckAt <= now).ToList();

            if (channels.Count == 0) return true;

            var label = _isStartupCheck ? "起動時チェック" : "定期チェック";
            AppLogger.Log(LogMsg.CheckStarted, null, label, channels.Count, allChannels.Count);

            var tasks = channels.Select(ch => CheckChannelAsync(ch)).ToList();
            await Task.WhenAll(tasks);

            _isStartupCheck = false;
            AppLogger.Log(LogMsg.CheckCompleted, null, label);
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
        return true;
    }

    // 新着ループの通知候補を1つのレコードにまとめる
    // Video にはプレミア公開後の動画も含む（NotifyVideo で一元管理）
    private record NewVideoNotifyCandidates(
        VideoInfo? Video,
        VideoInfo? Short,
        VideoInfo? Live,
        VideoInfo? UpcomingLive,
        VideoInfo? UpcomingPremiere,
        bool LatestLiveSeen,
        bool LatestPremiereSeen);

    private async Task CheckChannelAsync(ChannelInfo channel)
    {
        bool quotaExceeded = false;
        try
        {
            channel.LastCheckedAt = DateTime.Now;

            List<VideoInfo> videos;
            List<VideoInfo> pendingTransitioned;

            var debugSvc = !string.IsNullOrEmpty(channel.TestDataPath)
                ? DebugServiceLoader.GetService()
                : null;

            if (debugSvc != null)
            {
                ActivateGracePeriods(channel);
                (videos, pendingTransitioned) = debugSvc.GetNextCheckResult(channel);
            }
            else
            {
                await PrepareChannelAsync(channel);
                ActivateGracePeriods(channel);

                var pendingIds = channel.PendingLives.Select(p => p.VideoId)
                    .Concat(channel.PendingPremieres.Select(p => p.VideoId))
                    .ToList();

                (videos, pendingTransitioned) = await _youtubeClient.CheckLatestVideosAsync(
                    channel.ChannelId, channel.LastCheckedVideoId,
                    channel.UploadsPlaylistId, pendingIds);
            }

            if (videos.Count == 0 && pendingTransitioned.Count == 0)
            {
                AppLogger.Log(LogMsg.NoNew, channel.ChannelName);
                SettingsService.Instance.UpdateChannelSilent(channel);
                return;
            }

            var newCandidates = BuildNewVideoNotifyCandidates(channel, videos);

            // upcoming 動画はカーソルを進めない（公開済み動画が upcoming より古い位置にあっても検出できるよう）
            var cursorVideo = videos.FirstOrDefault(v => !v.IsUpcoming);
            if (cursorVideo != null)
                channel.LastCheckedVideoId = cursorVideo.VideoId;
            if (newCandidates.Video != null)
                channel.LastVideoId = newCandidates.Video.VideoId;

            SettingsService.Instance.UpdateChannelSilent(channel);

            var (pendingNotifyLive, pendingNotifyPremiere) =
                BuildPendingTransitionCandidates(channel, pendingTransitioned, newCandidates);

            SettingsService.Instance.UpdateChannelSilent(channel);

            // ===== 通知送信（種別ごとに独立）=====
            if (newCandidates.Video           != null) await NotifyAsync(channel, newCandidates.Video);
            if (newCandidates.Short           != null) await NotifyAsync(channel, newCandidates.Short);
            if (newCandidates.Live            != null) await NotifyAsync(channel, newCandidates.Live);
            if (newCandidates.UpcomingLive    != null) await NotifyAsync(channel, newCandidates.UpcomingLive);
            if (newCandidates.UpcomingPremiere != null) await NotifyAsync(channel, newCandidates.UpcomingPremiere);
            if (pendingNotifyLive             != null) await NotifyAsync(channel, pendingNotifyLive);
            if (pendingNotifyPremiere         != null) await NotifyAsync(channel, pendingNotifyPremiere);
        }
        catch (QuotaExceededException)
        {
            HandleQuotaExceeded();
            quotaExceeded = true;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.CheckFailed, channel.ChannelName, ex.Message);
            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                if (System.Windows.Application.Current?.MainWindow is YTNotifier.Views.MainWindow mw)
                    mw.CheckNetworkState();
            });
        }
        finally
        {
            bool hadGrace = TickGracePeriods(channel);
            DateTime? suspended;
            lock (_quotaLock) suspended = _quotaSuspendedUntil;
            // クォータ超過後にリセット時刻到達で _quotaSuspendedUntil が別スレッドにクリアされた場合も
            // 次回リセット時刻まで待機させる（直前のリセット時刻はすでに過ぎているので +1日分を取得）
            if (quotaExceeded && !suspended.HasValue)
                suspended = AppConstants.GetNextQuotaResetTime();
            channel.NextCheckAt = suspended
                ?? (hadGrace ? channel.LastCheckedAt.AddMinutes(1) : CalcNextCheckAt(channel, channel.LastCheckedAt));
            SettingsService.Instance.UpdateChannelSilent(channel);
        }
    }

    /// <summary>
    /// UploadsPlaylistId の取得と v1→v2 マイグレーションを行う。
    /// </summary>
    private async Task PrepareChannelAsync(ChannelInfo channel)
    {
        if (string.IsNullOrEmpty(channel.UploadsPlaylistId))
        {
            var pid = await _youtubeClient.GetUploadsPlaylistIdAsync(channel.ChannelId);
            if (!string.IsNullOrEmpty(pid))
            {
                channel.UploadsPlaylistId = pid;
                SettingsService.Instance.SaveChannelsSilent();
            }
        }

        // 旧スカラーフィールド → PendingLives/Premieres マイグレーション（初回のみ）
        if (channel.PendingLives.Count == 0 && channel.NextLiveCheckAt.HasValue
            && !string.IsNullOrEmpty(channel.LastLiveId))
        {
            channel.PendingLives.Add(new PendingVideoEntry
            {
                VideoId        = channel.LastLiveId,
                ScheduledAt    = channel.NextLiveCheckAt,
                GraceRemaining = channel.LiveGraceRemaining
            });
            channel.NextLiveCheckAt    = null;
            channel.LiveGraceRemaining = 0;
            channel.LastLiveId         = string.Empty;
        }
        if (channel.PendingPremieres.Count == 0 && channel.NextPremiereCheckAt.HasValue
            && !string.IsNullOrEmpty(channel.LastPremiereId))
        {
            channel.PendingPremieres.Add(new PendingVideoEntry
            {
                VideoId     = channel.LastPremiereId,
                ScheduledAt = channel.NextPremiereCheckAt
            });
            channel.NextPremiereCheckAt = null;
            channel.LastPremiereId      = string.Empty;
        }
    }

    /// <summary>
    /// 開始予定時刻を過ぎた pending エントリの猶予期間を起動する。
    /// API 呼び出し前に実行することで、LastCheckedVideoId に吸収済みのエントリにも機能する。
    /// </summary>
    private static void ActivateGracePeriods(ChannelInfo channel)
    {
        foreach (var entry in channel.PendingLives.Concat(channel.PendingPremieres))
        {
            if (entry.ScheduledAt.HasValue
                && entry.ScheduledAt.Value <= DateTime.Now
                && entry.GraceRemaining == 0)
            {
                entry.GraceRemaining = AppConstants.GracePeriodAttempts;
                AppLogger.Log(LogMsg.GracePeriodStarted, channel.ChannelName, entry.VideoId, AppConstants.GracePeriodAttempts);
            }
        }
    }

    /// <summary>
    /// 新着動画リスト（プレイリスト順・新しい順）を走査して種別ごとの通知候補を返す。
    /// 各種別で最新1件のみを対象とし、それより古いものは pending から破棄する。
    /// </summary>
    private static NewVideoNotifyCandidates BuildNewVideoNotifyCandidates(
        ChannelInfo channel, List<VideoInfo> videos)
    {
        VideoInfo? notifyVideo          = null;
        VideoInfo? notifyShort          = null;
        VideoInfo? notifyLive           = null;
        VideoInfo? notifyUpcomingLive    = null;
        VideoInfo? notifyUpcomingPremiere = null;
        bool upcomingLiveSeen           = false;
        bool publishedLiveSeen          = false;
        bool upcomingPremiereSeen       = false;
        bool publishedPremiereSeen      = false;
        var  settings                = SettingsService.Instance.Settings;

        foreach (var video in videos)
        {
            switch (video.Kind)
            {
                case VideoKind.Live:
                    if (video.IsUpcoming && !upcomingLiveSeen)
                    {
                        upcomingLiveSeen = true;
                        notifyUpcomingLive = ResolveLiveCandidate(channel, video, settings);
                    }
                    else if (!video.IsUpcoming && !publishedLiveSeen)
                    {
                        publishedLiveSeen = true;
                        notifyLive = ResolveLiveCandidate(channel, video, settings);
                    }
                    else
                    {
                        channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);
                        AppLogger.Log(LogMsg.OldLiveDiscarded, channel.ChannelName, video.Title);
                    }
                    break;

                case VideoKind.Premiere:
                    if (video.IsUpcoming && !upcomingPremiereSeen)
                    {
                        upcomingPremiereSeen = true;
                        notifyUpcomingPremiere = ResolvePremiereCandidate(channel, video, settings);
                    }
                    else if (!video.IsUpcoming && !publishedPremiereSeen)
                    {
                        // 公開済みプレミアは Video スロットで動画と競合（NotifyVideo で一元管理）
                        publishedPremiereSeen = true;
                        if (notifyVideo == null)
                            notifyVideo = ResolvePremiereCandidate(channel, video, settings);
                        else
                            ResolvePremiereCandidate(channel, video, settings); // pending 更新のみ
                    }
                    else
                    {
                        channel.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);
                        AppLogger.Log(LogMsg.OldPremiereDiscarded, channel.ChannelName, video.Title);
                    }
                    break;

                case VideoKind.Short:
                    if (notifyShort == null)
                        notifyShort = FilterByKind(channel, video, channel.NotifyShort);
                    break;

                default: // VideoKind.Video
                    if (notifyVideo == null)
                        notifyVideo = FilterByKind(channel, video, channel.NotifyVideo);
                    break;
            }
        }

        return new NewVideoNotifyCandidates(
            notifyVideo, notifyShort, notifyLive,
            notifyUpcomingLive, notifyUpcomingPremiere,
            publishedLiveSeen, publishedPremiereSeen);
    }

    /// <summary>ライブ配信の通知候補を解決し、pending リストを更新する。</summary>
    private static VideoInfo? ResolveLiveCandidate(
        ChannelInfo channel, VideoInfo video, AppSettings settings)
    {
        if (video.IsUpcoming)
        {
            var scheduled = video.ScheduledStartTime?.ToLocalTime();
            channel.PendingLives.RemoveAll(p => p.VideoId != video.VideoId);
            UpsertPendingEntry(channel.PendingLives, video.VideoId, scheduled);

            bool notifyUpcoming = channel.NotifyUpcoming ?? settings.GlobalNotifyUpcoming;
            if (!notifyUpcoming)
            {
                AppLogger.Log(LogMsg.LiveSkipped, channel.ChannelName, scheduled?.ToString("MM/dd HH:mm") ?? "-", video.Title);
                return null;
            }
            if (channel.LastLiveNotifiedId == video.VideoId)
            {
                AppLogger.Log(LogMsg.LiveReSkipped, channel.ChannelName, video.Title);
                return null;
            }
            return channel.NotifyLive ? video : null;
        }

        channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

        // upcoming 通知済みの場合はライブ開始通知をスキップ
        bool notifyUpcomingOnStart = channel.NotifyUpcoming ?? settings.GlobalNotifyUpcoming;
        if (notifyUpcomingOnStart && channel.LastLiveNotifiedId == video.VideoId)
        {
            channel.LastLiveNotifiedId = string.Empty;
            AppLogger.Log(LogMsg.LiveStartSkipped, channel.ChannelName, video.Title);
            return null;
        }
        return FilterByKind(channel, video, channel.NotifyLive);
    }

    /// <summary>プレミア公開の通知候補を解決し、pending リストを更新する。</summary>
    private static VideoInfo? ResolvePremiereCandidate(
        ChannelInfo channel, VideoInfo video, AppSettings settings)
    {
        if (video.IsUpcoming)
        {
            var scheduled = video.ScheduledStartTime?.ToLocalTime();
            channel.PendingPremieres.RemoveAll(p => p.VideoId != video.VideoId);
            UpsertPendingEntry(channel.PendingPremieres, video.VideoId, scheduled);

            bool notifyUpcoming = channel.NotifyUpcoming ?? settings.GlobalNotifyUpcoming;
            if (!notifyUpcoming)
            {
                AppLogger.Log(LogMsg.PremiereSkipped, channel.ChannelName, scheduled?.ToString("MM/dd HH:mm") ?? "-", video.Title);
                return null;
            }
            if (channel.LastPremiereNotifiedId == video.VideoId)
            {
                AppLogger.Log(LogMsg.PremiereReSkipped, channel.ChannelName, video.Title);
                return null;
            }
            return channel.NotifyVideo ? video : null;
        }

        channel.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

        // upcoming 通知済みの場合はプレミア開始通知をスキップ
        bool notifyUpcomingOnStart = channel.NotifyUpcoming ?? settings.GlobalNotifyUpcoming;
        if (notifyUpcomingOnStart && channel.LastPremiereNotifiedId == video.VideoId)
        {
            channel.LastPremiereNotifiedId = string.Empty;
            AppLogger.Log(LogMsg.PremiereStartSkipped, channel.ChannelName, video.Title);
            return null;
        }
        return FilterByKind(channel, video, channel.NotifyVideo);
    }

    /// <summary>pending リストに VideoId が存在すれば ScheduledAt を更新、なければ追加する。</summary>
    private static void UpsertPendingEntry(
        List<PendingVideoEntry> list, string videoId, DateTime? scheduledAt)
    {
        var entry = list.FirstOrDefault(p => p.VideoId == videoId);
        if (entry == null)
            list.Add(new PendingVideoEntry { VideoId = videoId, ScheduledAt = scheduledAt });
        else
            entry.ScheduledAt = scheduledAt;
    }

    /// <summary>通知フィルターを適用し、スキップ時はログを出力する。</summary>
    private static VideoInfo? FilterByKind(ChannelInfo channel, VideoInfo video, bool enabled)
    {
        if (enabled) return video;
        AppLogger.Log(LogMsg.KindFilterSkipped, channel.ChannelName, video.KindLabel, video.Title);
        return null;
    }

    /// <summary>
    /// pending 遷移リストを走査して通知候補を返す。
    /// 新着ループで既に新しいライブ/プレミアが見つかっていた場合は遷移を破棄する。
    /// </summary>
    private static (VideoInfo? live, VideoInfo? premiere) BuildPendingTransitionCandidates(
        ChannelInfo channel,
        List<VideoInfo> pendingTransitioned,
        NewVideoNotifyCandidates newCandidates)
    {
        VideoInfo? pendingNotifyLive     = null;
        VideoInfo? pendingNotifyPremiere = null;

        foreach (var video in pendingTransitioned)
        {
            // Phase2 の再分類に頼らず、追跡元リスト（PendingLives / PendingPremieres）で種別を確定する
            bool wasLive    = channel.PendingLives.Any(p => p.VideoId == video.VideoId);
            bool wasPremiere = channel.PendingPremieres.Any(p => p.VideoId == video.VideoId);

            if (wasLive)
            {
                channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

                if (newCandidates.LatestLiveSeen)
                {
                    AppLogger.Log(LogMsg.OldLiveDiscardedNew, channel.ChannelName, video.Title);
                }
                else if (pendingNotifyLive == null)
                {
                    var transSettings = SettingsService.Instance.Settings;
                    bool notifyUpcoming = channel.NotifyUpcoming ?? transSettings.GlobalNotifyUpcoming;
                    if (notifyUpcoming && channel.LastLiveNotifiedId == video.VideoId)
                    {
                        channel.LastLiveNotifiedId = string.Empty;
                        AppLogger.Log(LogMsg.LiveStartSkipped, channel.ChannelName, video.Title);
                    }
                    else
                    {
                        pendingNotifyLive = FilterByKind(channel, video, channel.NotifyLive);
                    }
                }
                else
                {
                    AppLogger.Log(LogMsg.OldLiveDiscardedTrans, channel.ChannelName, video.Title);
                }
            }
            else if (wasPremiere)
            {
                channel.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

                if (newCandidates.LatestPremiereSeen)
                {
                    AppLogger.Log(LogMsg.OldPremiereDiscardedNew, channel.ChannelName, video.Title);
                }
                else if (pendingNotifyPremiere == null)
                {
                    var transSettings = SettingsService.Instance.Settings;
                    bool notifyUpcoming = channel.NotifyUpcoming ?? transSettings.GlobalNotifyUpcoming;
                    if (notifyUpcoming && channel.LastPremiereNotifiedId == video.VideoId)
                    {
                        channel.LastPremiereNotifiedId = string.Empty;
                        AppLogger.Log(LogMsg.PremiereStartSkipped, channel.ChannelName, video.Title);
                    }
                    else
                    {
                        pendingNotifyPremiere = FilterByKind(channel, video, channel.NotifyVideo);
                    }
                }
                else
                {
                    AppLogger.Log(LogMsg.OldPremiereDiscardedTrans, channel.ChannelName, video.Title);
                }
            }
        }

        return (pendingNotifyLive, pendingNotifyPremiere);
    }

    private async Task NotifyAsync(ChannelInfo channel, VideoInfo video)
    {
        var settings = SettingsService.Instance.Settings;
        var videoUrl = YouTubeConstants.WatchUrlBase + video.VideoId;

        AppLogger.Log(LogMsg.NewVideo, channel.ChannelName, video.KindLabel, video.Title);

        if (video.Kind == VideoKind.Live)
            channel.LastLiveNotifiedId = video.VideoId;
        else if (video.Kind == VideoKind.Premiere)
            channel.LastPremiereNotifiedId = video.VideoId;

        channel.HasUnread = true;
        SettingsService.Instance.UpdateChannelSilent(channel);
        ChannelUpdated?.Invoke();

        if (settings.ShowDesktopNotification)
            await NotificationService.ShowVideoNotificationAsync(channel.ChannelName, video.Title, video.KindLabel, videoUrl, channel.ChannelId, video.Kind, channel.ThumbnailUrl, video.ThumbnailUrl);
        else if (settings.NotificationSound)
            NotificationService.PlaySound(video.Kind);
    }

    /// <summary>
    /// 猶予カウンタを1回分進める。猶予中だったエントリが1件以上あった場合 true を返す。
    /// CheckChannelAsync の finally で CalcNextCheckAt より先に呼ぶこと。
    /// </summary>
    private static bool TickGracePeriods(ChannelInfo ch)
    {
        bool anyGrace = false;
        foreach (var entry in ch.PendingLives.Concat(ch.PendingPremieres))
        {
            if (entry.GraceRemaining <= 0) continue;
            anyGrace = true;
            entry.GraceRemaining--;
            if (entry.GraceRemaining == 0) entry.GraceRemaining = -1;
        }
        return anyGrace;
    }

    // ===== 次回チェック時刻を計算（副作用なし）=====
    public static DateTime CalcNextCheckAt(ChannelInfo ch, DateTime? baseTime = null)
    {
        var now        = baseTime ?? DateTime.Now;
        var globalMins = SettingsService.Instance.Settings.CheckIntervalMinutes;

        switch (ch.MonitorMode)
        {
            case MonitorMode.LowFreq:
                return now.AddMinutes(ch.LowFreqIntervalMinutes);

            case MonitorMode.Focus:
            {
                var slots = ch.FocusSlots.Where(s => s.IsEnabled).ToList();
                if (slots.Count == 0) return now.AddMinutes(globalMins);

                DateTime? earliest = null;
                foreach (var slot in slots)
                {
                    DateTime slotNext;
                    switch (slot.SlotMode)
                    {
                        case MonitorMode.Normal:
                            var normalMins = slot.SlotNormalIntervalMinutes > 0
                                ? slot.SlotNormalIntervalMinutes : globalMins;
                            slotNext = now.AddMinutes(normalMins);
                            break;
                        case MonitorMode.LowFreq:
                            slotNext = now.AddMinutes(Math.Max(1, slot.SlotLowFreqIntervalMinutes));
                            break;
                        default: // Focus（時間指定）
                        {
                            var anchor    = now.Date.AddHours(slot.Hour).AddMinutes(slot.Minute);
                            var windowEnd = anchor.AddMinutes(slot.WindowMinutes);
                            if (now >= anchor && now <= windowEnd && IsSlotDayMatch(slot.Days, anchor.Date))
                            {
                                slotNext = now.AddMinutes(slot.IntervalMinutes);
                                if (earliest == null || slotNext < earliest) earliest = slotNext;
                                continue;
                            }
                            DateTime? nextWindow = null;
                            for (int d = 0; d < 8; d++)
                            {
                                var date  = now.Date.AddDays(d);
                                if (!IsSlotDayMatch(slot.Days, date)) continue;
                                var start = date.AddHours(slot.Hour).AddMinutes(slot.Minute);
                                if (start <= now) continue;
                                if (nextWindow == null || start < nextWindow) nextWindow = start;
                                break;
                            }
                            slotNext = nextWindow ?? now.AddMinutes(globalMins);
                            break;
                        }
                    }
                    if (earliest == null || slotNext < earliest) earliest = slotNext;
                }
                return earliest ?? now.AddMinutes(globalMins);
            }

            default: // Normal
                return now.AddMinutes(globalMins);
        }
    }

    /// <summary>曜日ビットマスク判定 (bit0=Sun..bit6=Sat)</summary>
    private static bool IsSlotDayMatch(int daysMask, DateTime date)
        => (daysMask & (1 << (int)date.DayOfWeek)) != 0;

    public void ResetNormalChannels(int newIntervalMinutes)
    {
        var nextCheckAt = DateTime.Now.AddMinutes(newIntervalMinutes);
        foreach (var ch in SettingsService.Instance.GetEnabledChannelsSnapshot())
        {
            var hasNormal = ch.MonitorMode == MonitorMode.Normal
                || (ch.MonitorMode == MonitorMode.Focus
                    && ch.FocusSlots.Any(s => s.IsEnabled && s.SlotMode == MonitorMode.Normal));
            if (hasNormal) ch.NextCheckAt = nextCheckAt;
        }
    }

    private void HandleQuotaExceeded()
    {
        DateTime resumeAt;
        lock (_quotaLock)
        {
            if (_quotaSuspendedUntil.HasValue) return; // 既に処理済み（複数並列タスクの重複呼び出し防止）

            resumeAt             = AppConstants.GetNextQuotaResetTime();
            _quotaSuspendedUntil = resumeAt;
        }

        // ロック外で副作用を実行（デッドロック防止）
        foreach (var ch in SettingsService.Instance.GetEnabledChannelsSnapshot())
        {
            ch.NextCheckAt = resumeAt;
            SettingsService.Instance.UpdateChannelSilent(ch);
        }

        AppLogger.Log(LogMsg.QuotaExceeded, null, resumeAt.ToString("M/d HH:mm"));

        // UI に停止状態を通知（IsRunning は true のままなので明示的に false を送る）
        StatusChanged?.Invoke(false);

        Application.Current?.Dispatcher.InvokeAsync(() =>
            NotificationService.ShowQuotaExceededNotification(resumeAt));
    }

    public void InvokeChannelUpdated() => ChannelUpdated?.Invoke();
    public void SendTestNotification() => NotificationService.ShowTestNotification();

    public Task<bool> ManualCheckAsync()
        => CheckAllChannelsAsync(forceAll: true);

    public void Dispose()
    {
        _timer?.Dispose();
    }
}
