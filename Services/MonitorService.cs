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

    private const int GracePeriodIntervalSeconds = 30;

    private readonly IYouTubeApiClient _youtubeClient;
    private Timer? _timer;
    private volatile bool _isRunning      = false;
    private int           _isChecking     = 0;
    private volatile bool _isStartupCheck = true;
    private readonly object _quotaLock = new();
    private DateTime?       _quotaSuspendedUntil = null;

    // ===== 待機所・開始通知スケジューラー =====
    private readonly object              _schedulerLock = new();
    private CancellationTokenSource      _schedulerCts  = new();
    private Task?                        _schedulerTask;

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

    /// <summary>次の :01 秒にタイマーを再スケジュールする</summary>
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

        var now      = DateTime.Now;
        var today    = now.ToString("yyyy-MM-dd");
        var settings = SettingsService.Instance.Settings;
        var isNewDay = settings.LastStartupCheckDate != today;

        foreach (var ch in SettingsService.Instance.GetEnabledChannelsSnapshot())
        {
            if (isNewDay)
                ch.NextCheckAt = DateTime.MinValue;
            else if (ch.NextCheckAt == DateTime.MinValue)
                ch.NextCheckAt = CalcNextCheckAt(ch);
        }

        if (isNewDay)
        {
            settings.LastStartupCheckDate = today;
            SettingsService.Instance.SaveSettings();
        }

        _timer = new Timer(async _ =>
        {
            try { await CheckAllChannelsAsync(); }
            catch (Exception ex) { AppLogger.Log(LogMsg.CheckFailed, null, ex.Message); }
            finally { LoggerService.Instance.FlushLog(); ScheduleNextTick(); }
        }, null, TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        CancellationTokenSource oldCts;
        lock (_schedulerLock)
        {
            oldCts        = _schedulerCts;
            _schedulerCts = new CancellationTokenSource();
        }
        oldCts.Cancel();
        oldCts.Dispose();
        _schedulerTask = Task.Run(RunUpcomingSchedulerAsync);
        AppLogger.Log(LogMsg.MonitorStarted, null, AppConstants.AppVersion);
        StatusChanged?.Invoke(true);
    }

    public void Stop()
    {
        if (!_isRunning) return;
        _timer?.Dispose();
        _timer = null;
        _isRunning = false;
        CancellationTokenSource schedulerCts;
        lock (_schedulerLock) schedulerCts = _schedulerCts;
        schedulerCts.Cancel();
        AppLogger.Log(LogMsg.MonitorStopped);
        StatusChanged?.Invoke(false);
    }

    public void RestartWithNewInterval()
    {
        if (!_isRunning) return;
        try { _timer?.Change(CalcAlignedDelay(), Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { }
    }

    // ===== 待機所・開始通知スケジューラー =====

    /// <summary>スケジューラーを起こして次アクションを再計算させる（新規キュー追加時に呼ぶ）</summary>
    public void WakeUpScheduler()
    {
        CancellationTokenSource old;
        lock (_schedulerLock)
        {
            old          = _schedulerCts;
            _schedulerCts = new CancellationTokenSource();
        }
        old.Cancel();
        AppLogger.Log(LogMsg.SchedulerWakeUp);
    }

    private record SchedulerAction(
        ChannelInfo      Channel,
        PendingVideoEntry Entry,
        DateTime          ActionAt,
        bool              IsNotification);

    private async Task RunUpcomingSchedulerAsync()
    {
        while (_isRunning)
        {
            var action = FindNextSchedulerAction();

            CancellationToken token;
            lock (_schedulerLock) token = _schedulerCts.Token;

            if (action == null)
            {
                try { await Task.Delay(Timeout.Infinite, token); }
                catch (OperationCanceledException) { }
                continue;
            }

            var delay = action.ActionAt - DateTime.Now;
            if (delay > TimeSpan.Zero)
            {
                try { await Task.Delay(delay, token); }
                catch (OperationCanceledException) { continue; }
            }

            if (!_isRunning) break;
            await ExecuteSchedulerActionAsync(action);
        }
    }

    /// <summary>全チャンネルのキューを走査して最も早い次アクションを返す</summary>
    private static SchedulerAction? FindNextSchedulerAction()
    {
        SchedulerAction? earliest = null;
        var now = DateTime.Now;

        foreach (var ch in SettingsService.Instance.GetEnabledChannelsSnapshot())
        {
            var leadMin = ch.UpcomingNotifyLeadMinutes;
            var mode    = ch.UpcomingNotifyMode;

            foreach (var entry in ch.PendingLives.Concat(ch.PendingPremieres).ToList())
            {
                if (!entry.ScheduledAt.HasValue) continue;
                if (entry.GraceRemaining != 0) continue; // 集中監視中は除外

                var scheduledAt = entry.ScheduledAt.Value;

                // 待機所通知アクション（LiveStartOnly 以外）
                if (!entry.UpcomingNotified && mode != UpcomingNotifyMode.LiveStartOnly)
                {
                    var effectiveLead = leadMin > 0 ? leadMin : AppConstants.DefaultUpcomingLeadMinutes;
                    var notifyAt = scheduledAt.AddMinutes(-effectiveLead);
                    if (notifyAt < now) notifyAt = now;
                    if (earliest == null || notifyAt < earliest.ActionAt)
                        earliest = new SchedulerAction(ch, entry, notifyAt, IsNotification: true);
                }

                // 集中監視起動アクション（開始時刻）
                var startAt = scheduledAt < now ? now : scheduledAt;
                if (earliest == null || startAt < earliest.ActionAt)
                    earliest = new SchedulerAction(ch, entry, startAt, IsNotification: false);
            }
        }

        return earliest;
    }

    /// <summary>スケジューラーアクションを実行する</summary>
    private async Task ExecuteSchedulerActionAsync(SchedulerAction action)
    {
        var ch    = action.Channel;
        var entry = action.Entry;

        if (action.IsNotification)
        {
            bool isPendingLive    = ch.PendingLives.Any(p => p.VideoId == entry.VideoId);
            bool isPendingPremiere = ch.PendingPremieres.Any(p => p.VideoId == entry.VideoId);
            if (!isPendingLive && !isPendingPremiere) return;
            if (entry.UpcomingNotified) return;

            if (entry.ScheduledAt.HasValue && entry.ScheduledAt.Value < DateTime.Now)
            {
                entry.UpcomingNotified = true;
                SettingsService.Instance.UpdateChannelSilent(ch);
                return;
            }

            entry.UpcomingNotified = true;
            SettingsService.Instance.UpdateChannelSilent(ch);

            var kind     = isPendingLive ? VideoKind.Live : VideoKind.Premiere;
            var notifyOn = kind == VideoKind.Live ? ch.NotifyLive : ch.NotifyVideo;
            if (!notifyOn) return;

            var videoInfo = new VideoInfo
            {
                VideoId            = entry.VideoId,
                Title              = entry.Title,
                ThumbnailUrl       = entry.ThumbnailUrl,
                Kind               = kind,
                IsUpcoming         = true,
                ScheduledStartTime = entry.ScheduledAt,
            };
            AppLogger.Log(LogMsg.SchedulerWaitingRoomNotify, ch.ChannelName, videoInfo.KindLabel, entry.Title);
            await NotifyAsync(ch, videoInfo);
        }
        else
        {
            bool isPendingLive    = ch.PendingLives.Any(p => p.VideoId == entry.VideoId);
            bool isPendingPremiere = ch.PendingPremieres.Any(p => p.VideoId == entry.VideoId);
            if (!isPendingLive && !isPendingPremiere) return;
            if (entry.GraceRemaining != 0) return;

            entry.GraceRemaining = AppConstants.GracePeriodAttempts;
            ch.NextCheckAt       = DateTime.Now;
            SettingsService.Instance.UpdateChannelSilent(ch);
            AppLogger.Log(LogMsg.SchedulerGracePeriodStarted, ch.ChannelName, entry.VideoId);
        }
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

            // 定時全巡回（クォータリセット＋1分の1分間のみ、太平洋時間基準で1日1回）
            var pacificDayKey   = AppConstants.GetQuotaDayKey();
            var triggerTime     = AppConstants.GetTodayQuotaResetTime().AddMinutes(1);
            var isDailyFullScan = false;
            if (now.Hour == triggerTime.Hour && now.Minute == triggerTime.Minute
                && SettingsService.Instance.Settings.LastDailyFullScanDate != pacificDayKey)
            {
                SettingsService.Instance.Settings.LastDailyFullScanDate = pacificDayKey;
                SettingsService.Instance.SaveSettings();
                foreach (var ch in allChannels)
                    ch.NextCheckAt = DateTime.MinValue;
                isDailyFullScan = true;
            }

            var channels = forceAll
                ? allChannels
                : allChannels.Where(c => c.NextCheckAt <= now).ToList();

            if (channels.Count == 0) return true;

            var label = isDailyFullScan ? "定時全チェック" : _isStartupCheck ? "起動時チェック" : "定期チェック";
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
        IReadOnlyList<VideoInfo> Lives,
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
            List<VideoInfo> allScanned;

            var debugSvc = !string.IsNullOrEmpty(channel.TestDataPath)
                ? DebugServiceLoader.GetService()
                : null;

            if (debugSvc != null)
            {
                ActivateGracePeriods(channel);
                (videos, pendingTransitioned) = debugSvc.GetNextCheckResult(channel);
                allScanned = new List<VideoInfo>();
            }
            else
            {
                await PrepareChannelAsync(channel);
                ActivateGracePeriods(channel);

                var pendingIds = channel.PendingLives.Select(p => p.VideoId)
                    .Concat(channel.PendingPremieres.Select(p => p.VideoId))
                    .Concat(channel.ActiveLives.Select(p => p.VideoId))
                    .Concat(channel.ActivePremieres.Select(p => p.VideoId))
                    .ToList();

                (videos, pendingTransitioned, allScanned) = await _youtubeClient.CheckLatestVideosAsync(
                    channel.ChannelId, channel.LastCheckedVideoId,
                    channel.UploadsPlaylistId, pendingIds);
            }

            if (videos.Count == 0 && pendingTransitioned.Count == 0)
            {
                // デバッグチャンネルは allScanned が空固定のためバッジ更新をスキップ
                // allScanned が空（問い合わせ結果なし）の場合もスキップ
                var isDebugChannel = !string.IsNullOrEmpty(channel.TestDataPath);
                if (!isDebugChannel && allScanned.Count > 0)
                {
                    var prevLiveCount     = channel.ActiveLives.Count;
                    var prevPremiereCount = channel.ActivePremieres.Count;

                    channel.ActiveLives.Clear();
                    channel.ActivePremieres.Clear();
                    foreach (var v in allScanned.Where(v => v.IsCurrentlyLive))
                    {
                        var entry = new YTNotifier.Models.PendingVideoEntry { VideoId = v.VideoId, Title = v.Title, ThumbnailUrl = v.ThumbnailUrl };
                        if (v.Kind == VideoKind.Live)          channel.ActiveLives.Add(entry);
                        else if (v.Kind == VideoKind.Premiere) channel.ActivePremieres.Add(entry);
                    }

                    if (channel.ActiveLives.Count != prevLiveCount || channel.ActivePremieres.Count != prevPremiereCount)
                    {
                        SettingsService.Instance.UpdateChannelSilent(channel);
                        ChannelUpdated?.Invoke();
                        AppLogger.Log(LogMsg.NoNew, channel.ChannelName);
                        return;
                    }
                }

                AppLogger.Log(LogMsg.NoNew, channel.ChannelName);
                SettingsService.Instance.UpdateChannelSilent(channel);
                return;
            }

            // ① ActiveLives / ActivePremieres をスキャン結果で上書き
            var liveCountBefore     = channel.ActiveLives.Count;
            var premiereCountBefore = channel.ActivePremieres.Count;
            channel.ActiveLives.Clear();
            channel.ActivePremieres.Clear();
            foreach (var v in allScanned.Where(v => v.IsCurrentlyLive))
            {
                var entry = new YTNotifier.Models.PendingVideoEntry { VideoId = v.VideoId, Title = v.Title, ThumbnailUrl = v.ThumbnailUrl };
                if (v.Kind == VideoKind.Live)           channel.ActiveLives.Add(entry);
                else if (v.Kind == VideoKind.Premiere)  channel.ActivePremieres.Add(entry);
            }
            if (channel.ActiveLives.Count != liveCountBefore || channel.ActivePremieres.Count != premiereCountBefore)
                ChannelUpdated?.Invoke();

            // 通知ON種別の中でプレイリスト最新の非 upcoming 動画を LatestTitle / LatestKind に保存
            var latestFound = videos
                .Where(v => !v.IsUpcoming)
                .FirstOrDefault(v =>
                    (v.Kind == VideoKind.Video    && channel.NotifyVideo) ||
                    (v.Kind == VideoKind.Premiere && channel.NotifyVideo) ||
                    (v.Kind == VideoKind.Short    && channel.NotifyShort) ||
                    (v.Kind == VideoKind.Live     && channel.NotifyLive));
            if (latestFound != null)
            {
                channel.LatestTitle    = latestFound.Title;
                channel.LatestKind     = latestFound.Kind;
                channel.LatestVideoId  = latestFound.VideoId;
                channel.LatestDuration = latestFound.Duration;
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

            // ③ pendingTransitioned の VideoId で LastCheckedVideoId を進める（新着と重複していない場合）
            var transitionedIds = pendingTransitioned
                .Where(v => !videos.Any(x => x.VideoId == v.VideoId) && v.VideoId != channel.LastCheckedVideoId)
                .Select(v => v.VideoId)
                .ToHashSet();
            if (transitionedIds.Count > 0)
            {
                var best = allScanned.FirstOrDefault(s => transitionedIds.Contains(s.VideoId));
                if (best != null)
                    channel.LastCheckedVideoId = best.VideoId;
            }

            SettingsService.Instance.UpdateChannelSilent(channel);

            // ===== 通知送信（種別ごとに独立）=====
            // 待機所通知・開始通知はスケジューラーが担当するため、ここでは送らない
            if (newCandidates.Video   != null) await NotifyAsync(channel, newCandidates.Video);
            if (newCandidates.Short   != null) await NotifyAsync(channel, newCandidates.Short);
            foreach (var live in newCandidates.Lives) await NotifyAsync(channel, live);
            if (pendingNotifyLive     != null) await NotifyAsync(channel, pendingNotifyLive);
            if (pendingNotifyPremiere != null) await NotifyAsync(channel, pendingNotifyPremiere);
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
                ?? (hadGrace ? channel.LastCheckedAt.AddSeconds(GracePeriodIntervalSeconds) : CalcNextCheckAt(channel, channel.LastCheckedAt));
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
                channel.UploadsPlaylistId = pid;
        }

        // 旧スカラーフィールド → PendingLives/Premieres マイグレーション（初回のみ）
        var staleCutoff = DateTime.Now.AddDays(-AppConstants.StalePendingEntryDays);
        channel.PendingLives.RemoveAll(p => !p.ScheduledAt.HasValue || p.ScheduledAt.Value < staleCutoff);
        channel.PendingPremieres.RemoveAll(p => !p.ScheduledAt.HasValue || p.ScheduledAt.Value < staleCutoff);

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
    /// upcoming は全件キューに積む。通知タイミングは GetUpcomingNotificationCandidates が担う。
    /// </summary>
    private static NewVideoNotifyCandidates BuildNewVideoNotifyCandidates(
        ChannelInfo channel, List<VideoInfo> videos)
    {
        VideoInfo? notifyVideo         = null;
        VideoInfo? notifyShort         = null;
        var notifyLives                = new List<VideoInfo>();
        bool publishedLiveSeen         = false;
        bool publishedPremiereSeen     = false;
        bool publishedLiveArchiveSeen  = false;

        foreach (var video in videos)
        {
            switch (video.Kind)
            {
                case VideoKind.Live:
                    if (video.IsUpcoming)
                        QueueUpcomingEntry(channel, channel.PendingLives, video);
                    else
                    {
                        publishedLiveSeen = true;
                        if (video.IsCurrentlyLive)
                        {
                            // 配信中：当日開始のもののみ通知（過去日から続く配信の誤通知防止）
                            if (video.ActualStartTime.HasValue && video.ActualStartTime.Value >= DateTime.Today)
                            {
                                var liveCandidate = ResolveLiveCandidate(channel, video);
                                if (liveCandidate != null) notifyLives.Add(liveCandidate);
                            }
                        }
                        else if (!publishedLiveArchiveSeen)
                        {
                            // アーカイブ（配信終了済み）：最新1件のみ通知
                            publishedLiveArchiveSeen = true;
                            var liveCandidate = ResolveLiveCandidate(channel, video);
                            if (liveCandidate != null) notifyLives.Add(liveCandidate);
                        }
                        else
                            ResolveLiveCandidate(channel, video); // pending 削除のみ
                    }
                    break;

                case VideoKind.Premiere:
                    if (video.IsUpcoming)
                        QueueUpcomingEntry(channel, channel.PendingPremieres, video);
                    else if (!publishedPremiereSeen)
                    {
                        publishedPremiereSeen = true;
                        if (notifyVideo == null)
                        {
                            notifyVideo = ResolvePremiereCandidate(channel, video);
                        }
                        else
                            ResolvePremiereCandidate(channel, video); // pending 削除のみ
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
            notifyVideo, notifyShort, notifyLives,
            publishedLiveSeen, publishedPremiereSeen);
    }

    /// <summary>
    /// upcoming 動画をキューに積む。既存エントリは情報を更新する。
    /// キュー上限（MaxPendingQueueSize）を超える場合はスキップ。
    /// </summary>
    private static void QueueUpcomingEntry(
        ChannelInfo channel, List<YTNotifier.Models.PendingVideoEntry> list, VideoInfo video)
    {
        var scheduled = video.ScheduledStartTime?.ToLocalTime();

        // 既存エントリは情報を更新して終了（日付チェック不要）
        var existing = list.FirstOrDefault(p => p.VideoId == video.VideoId);
        if (existing != null)
        {
            existing.ScheduledAt  = scheduled;
            existing.Title        = video.Title;
            existing.ThumbnailUrl = video.ThumbnailUrl;
            AppLogger.Log(LogMsg.UpcomingQueueUpdated, channel.ChannelName,
                scheduled?.ToString("MM/dd HH:mm") ?? "-", video.Title);
            return;
        }

        if (list.Count >= YTNotifier.Constants.AppConstants.MaxPendingQueueSize)
        {
            AppLogger.Log(LogMsg.UpcomingQueueFull, channel.ChannelName, video.Title);
            return;
        }
        list.Add(new YTNotifier.Models.PendingVideoEntry
        {
            VideoId      = video.VideoId,
            ScheduledAt  = scheduled,
            Title        = video.Title,
            ThumbnailUrl = video.ThumbnailUrl,
        });
        AppLogger.Log(LogMsg.UpcomingQueued, channel.ChannelName,
            scheduled?.ToString("MM/dd HH:mm") ?? "-", video.Title);

        // スケジューラーに新規キュー追加を通知
        Instance.WakeUpScheduler();
    }

    /// <summary>
    /// ライブ配信（配信開始済み）の通知候補を解決し、pending から削除する。
    /// UpcomingNotifyMode に従い開始通知を送るかを決定する。
    /// </summary>
    private static VideoInfo? ResolveLiveCandidate(ChannelInfo channel, VideoInfo video)
    {
        channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

        if (channel.UpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
        {
            AppLogger.Log(LogMsg.LiveStartSkipped, channel.ChannelName, video.Title);
            return null;
        }
        return FilterByKind(channel, video, channel.NotifyLive);
    }

    /// <summary>
    /// プレミア公開（配信開始済み）の通知候補を解決し、pending から削除する。
    /// UpcomingNotifyMode に従い開始通知を送るかを決定する。
    /// </summary>
    private static VideoInfo? ResolvePremiereCandidate(ChannelInfo channel, VideoInfo video)
    {
        channel.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

        if (channel.UpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
        {
            AppLogger.Log(LogMsg.PremiereStartSkipped, channel.ChannelName, video.Title);
            return null;
        }
        return FilterByKind(channel, video, channel.NotifyVideo);
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
                    if (channel.UpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
                    {
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
                    if (channel.UpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
                    {
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

        channel.HasUnread = true;
        if (!video.IsUpcoming)
        {
            switch (video.Kind)
            {
                case VideoKind.Video:
                    channel.LastVideoTitle      = video.Title;
                    channel.LastVideoNotifiedAt = DateTime.Now;
                    break;
                case VideoKind.Short:
                    channel.LastShortNotifiedId = video.VideoId;
                    channel.LastShortTitle      = video.Title;
                    channel.LastShortNotifiedAt = DateTime.Now;
                    break;
                case VideoKind.Live:
                    channel.LastLiveNotifiedTitle = video.Title;
                    channel.LastLiveNotifiedAt    = DateTime.Now;
                    break;
                case VideoKind.Premiere:
                    channel.LastPremiereNotifiedTitle = video.Title;
                    channel.LastPremiereNotifiedAt    = DateTime.Now;
                    break;
            }
        }
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

        if (ch.ActiveLives.Count > 0 || ch.ActivePremieres.Count > 0)
            return now.AddMinutes(AppConstants.ActiveLiveCheckIntervalMinutes);

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
                                // IntervalMinutes == 0 は30秒間隔を意味する
                                slotNext = slot.IntervalMinutes == 0
                                    ? now.AddSeconds(30)
                                    : now.AddMinutes(slot.IntervalMinutes);
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
        CancellationTokenSource schedulerCts;
        lock (_schedulerLock) schedulerCts = _schedulerCts;
        schedulerCts.Cancel();
    }
}
