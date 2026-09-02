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

    /// <summary>カード「🔄 最新情報取得」で他チェック完了を待つ最大時間（秒）</summary>
    private const int ChannelManualCheckMaxWaitSeconds = 90;
    /// <summary>上記待機中に _isChecking を再取得しにいく間隔（ミリ秒）</summary>
    private const int ChannelManualCheckPollIntervalMs = 250;

    /// <summary>ライブ/プレミア配信開始後に継続チェックする猶予回数</summary>
    private const int GracePeriodAttempts = 10;

    /// <summary>待機所通知タイミングのデフォルト（leadMinutes=0 の既存データ向けフォールバック）</summary>
    private const int DefaultUpcomingLeadMinutes = 5;

    /// <summary>チャンネルごとの upcoming キュー上限数</summary>
    private const int MaxPendingQueueSize = 10;

    /// <summary>チャンネルカードに「配信予定」を表示する開始予定時刻までの上限（分）</summary>
    private const int UpcomingDisplayWindowMinutes = 30;

    /// <summary>pending ライブ/プレミアエントリの破棄閾値（日数）。ScheduledAt がこの日数以上前のエントリは起動時に削除する</summary>
    private const int StalePendingEntryDays = 14;

    /// <summary>ライブ配信中・プレミア公開中の終了検知チェック間隔（分）</summary>
    private const int ActiveLiveCheckIntervalMinutes = 15;

    /// <summary>スケジューラーで想定外エラーが発生した際の再試行待機時間（秒）</summary>
    private const int SchedulerErrorRetryDelaySeconds = 30;

    private static readonly TimeZoneInfo _pacificTz =
        TimeZoneInfo.FindSystemTimeZoneById("Pacific Standard Time");

    /// <summary>次回クォータリセット時刻をローカル時刻で返す（DST対応）</summary>
    private static DateTime GetNextQuotaResetTime()
    {
        var nowPt          = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        var nextMidnightPt = DateTime.SpecifyKind(nowPt.Date.AddDays(1), DateTimeKind.Unspecified);
        var nextMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(nextMidnightPt, _pacificTz);
        return TimeZoneInfo.ConvertTimeFromUtc(nextMidnightUtc, TimeZoneInfo.Local);
    }

    /// <summary>本日の太平洋時間深夜0時（クォータリセット時刻）をローカル時刻で返す（DST対応）</summary>
    private static DateTime GetTodayQuotaResetTime()
    {
        var nowPt           = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _pacificTz);
        var todayMidnightPt = DateTime.SpecifyKind(nowPt.Date, DateTimeKind.Unspecified);
        var todayMidnightUtc = TimeZoneInfo.ConvertTimeToUtc(todayMidnightPt, _pacificTz);
        return TimeZoneInfo.ConvertTimeFromUtc(todayMidnightUtc, TimeZoneInfo.Local);
    }

    private readonly IYouTubeApiClient _youtubeClient;
    private Timer? _timer;
    private volatile bool _isRunning      = false;
    private int           _isChecking     = 0;
    private volatile bool _isStartupCheck = true;
    private volatile bool _startupIsNewDay = false;
    private readonly object _quotaLock = new();
    private DateTime?       _quotaSuspendedUntil = null;

    // ===== 待機所・開始通知スケジューラー =====
    private readonly object              _schedulerLock = new();
    private CancellationTokenSource      _schedulerCts  = new();
    private Task?                        _schedulerTask;

    // PendingLives/PendingPremieres/ActiveLives/ActivePremieres の構造変更（Add/Remove/Clear系）と、
    // 監視スレッド外（スケジューラー・UI・状態保存）からの列挙・参照を直列化するロック。
    // 注意: このロックを保持したまま SettingsService のロックを取るメソッド（Save系/UpdateChannel系）を呼ばないこと
    internal static readonly object _pendingListLock = new();

    public event Action<bool>? StatusChanged;
    public event Action? ChannelUpdated;
    public event Action? QuotaUpdated;
    public event Action? NetworkCheckRequested;

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
        _startupIsNewDay = isNewDay;

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
            finally { ScheduleNextTick(); }
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
    public void WakeUpScheduler(string? channelName = null)
    {
        CancellationTokenSource old;
        lock (_schedulerLock)
        {
            old          = _schedulerCts;
            _schedulerCts = new CancellationTokenSource();
        }
        old.Cancel();
        AppLogger.Log(LogMsg.SchedulerWakeUp, channelName);
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
            CancellationToken token;
            lock (_schedulerLock) token = _schedulerCts.Token;

            try
            {
                var action = FindNextSchedulerAction();

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
            catch (Exception ex)
            {
                AppLogger.Log(LogMsg.SchedulerError, null, ex.Message);
                try { await Task.Delay(TimeSpan.FromSeconds(SchedulerErrorRetryDelaySeconds), token); }
                catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>全チャンネルのキューを走査して最も早い次アクションを返す</summary>
    private static SchedulerAction? FindNextSchedulerAction()
    {
        SchedulerAction? earliest = null;
        var now = DateTime.Now;

        foreach (var ch in SettingsService.Instance.GetEnabledChannelsSnapshot())
        {
            List<(PendingVideoEntry entry, bool isLive)> entries;
            lock (_pendingListLock)
                entries = ch.PendingLives.Select(e => (e, true))
                    .Concat(ch.PendingPremieres.Select(e => (e, false)))
                    .ToList();
            foreach (var (entry, isLive) in entries)
            {
                if (!entry.ScheduledAt.HasValue) continue;
                if (entry.GraceRemaining != 0) continue; // 集中監視中は除外

                var scheduledAt = entry.ScheduledAt.Value;
                var mode    = isLive ? ch.LiveUpcomingNotifyMode         : ch.PremiereUpcomingNotifyMode;
                var leadMin = isLive ? ch.LiveUpcomingNotifyLeadMinutes  : ch.PremiereUpcomingNotifyLeadMinutes;

                // 待機所通知アクション（LiveStartOnly 以外）
                if (!entry.UpcomingNotified && mode != UpcomingNotifyMode.LiveStartOnly)
                {
                    var effectiveLead = leadMin > 0 ? leadMin : DefaultUpcomingLeadMinutes;
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
            bool isPendingLive, isPendingPremiere;
            lock (_pendingListLock)
            {
                isPendingLive     = ch.PendingLives.Any(p => p.VideoId == entry.VideoId);
                isPendingPremiere = ch.PendingPremieres.Any(p => p.VideoId == entry.VideoId);
            }
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
            bool proceed;
            lock (_pendingListLock)
            {
                var isPendingLive     = ch.PendingLives.Any(p => p.VideoId == entry.VideoId);
                var isPendingPremiere = ch.PendingPremieres.Any(p => p.VideoId == entry.VideoId);
                proceed = (isPendingLive || isPendingPremiere) && entry.GraceRemaining == 0;
                if (proceed) entry.GraceRemaining = GracePeriodAttempts;
            }
            if (!proceed) return;

            ch.NextCheckAt = DateTime.Now;
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

            // 定時全巡回（クォータリセット＋1分以降の最初のチェックで実行、起動時チェック中を除く、太平洋時間基準で1日1回）
            var pacificDayKey   = AppConstants.GetQuotaDayKey();
            var triggerTime     = GetTodayQuotaResetTime().AddMinutes(1);
            var isDailyFullScan = false;
            if (now >= triggerTime
                && !_isStartupCheck
                && SettingsService.Instance.Settings.LastDailyFullScanDate != pacificDayKey)
            {
                SettingsService.Instance.Settings.LastDailyFullScanDate = pacificDayKey;
                SettingsService.Instance.SaveSettings();
                foreach (var ch in allChannels)
                    ch.NextCheckAt = DateTime.MinValue;
                isDailyFullScan = true;
            }

            // BAN／自主削除判定のトリガー：定時全チェック、または当日初回の起動時チェック（どちらか早い方で1日1回）
            var runBanScan = isDailyFullScan || (_isStartupCheck && _startupIsNewDay);
            if (runBanScan && !isDailyFullScan)
            {
                // 起動時トリガー経由の場合、同日の定時全チェック（太平洋時間トリガー）での二重実行を防ぐ
                SettingsService.Instance.Settings.LastDailyFullScanDate = pacificDayKey;
                SettingsService.Instance.SaveSettings();
            }

            if (runBanScan)
            {
                await CheckChannelsAliveAsync(allChannels, LogMsg.ChannelListBanCheckStarted, LogMsg.ChannelListBanCheckCompleted, LogMsg.ChannelListAllAlive);
                await CheckChannelsAliveAsync(SettingsService.Instance.Channels.Where(c => c.IsDormant).ToList(), LogMsg.DormantListBanCheckStarted, LogMsg.DormantListBanCheckCompleted, LogMsg.DormantListAllAlive);
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

    private async Task CheckChannelsAliveAsync(
        List<ChannelInfo> channels,
        LogMsg startedMsg, LogMsg completedMsg, LogMsg allAliveMsg)
    {
        // TestDataPath 設定済み（デバッグ用テストチャンネル）は実API呼び出し対象から除外する
        var targets = channels.Where(c => string.IsNullOrEmpty(c.TestDataPath)).ToList();
        if (targets.Count == 0) return;

        AppLogger.Log(startedMsg);

        Dictionary<string, bool> banResults;
        try
        {
            banResults = await _youtubeClient.CheckChannelsBannedAsync(
                targets.Select(c => c.ChannelId).ToList());
        }
        catch (QuotaExceededException)
        {
            HandleQuotaExceeded();
            return;
        }

        var changed = false;
        foreach (var channel in targets)
        {
            if (!banResults.TryGetValue(channel.ChannelId, out var isBanned)) continue;
            if (isBanned == channel.IsBanned) continue;

            changed = true;
            channel.IsBanned = isBanned;
            SettingsService.Instance.UpdateChannelSilent(channel);
            ChannelUpdated?.Invoke();
            AppLogger.Log(isBanned ? LogMsg.ChannelBanned : LogMsg.ChannelBanRecovered, channel.ChannelName);
        }

        if (!changed)
            AppLogger.Log(allAliveMsg);
        AppLogger.Log(completedMsg);
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
            List<VideoInfo> allScannedBasic;
            bool playlistEmpty;

            var debugSvc = !string.IsNullOrEmpty(channel.TestDataPath)
                ? DebugServiceLoader.GetService()
                : null;

            if (debugSvc != null)
            {
                ActivateGracePeriods(channel);
                (videos, pendingTransitioned) = debugSvc.GetNextCheckResult(channel);
                allScanned = new List<VideoInfo>();
                allScannedBasic = new List<VideoInfo>();
                playlistEmpty = false;
            }
            else
            {
                await PrepareChannelAsync(channel);
                ActivateGracePeriods(channel);

                List<string> pendingIds;
                lock (_pendingListLock)
                    pendingIds = channel.PendingLives.Select(p => p.VideoId)
                        .Concat(channel.PendingPremieres.Select(p => p.VideoId))
                        .Concat(channel.ActiveLives.Select(p => p.VideoId))
                        .Concat(channel.ActivePremieres.Select(p => p.VideoId))
                        .ToList();

                (videos, pendingTransitioned, allScanned, allScannedBasic, playlistEmpty) = await _youtubeClient.CheckLatestVideosAsync(
                    channel.ChannelId, channel.LastCheckedVideoId,
                    channel.UploadsPlaylistId, pendingIds,
                    lastVideoPublishedAt: channel.LastCheckedVideoPublishedAt);
            }

            // チャンネルの最新投稿スナップショット（RecentUploads）を更新する。
            // allScanned は新着有無に関わらず取得済み・追加API呼び出しなしのため毎回のチェックで実行。
            // 空（デバッグチャンネル、または問い合わせ結果なし）の場合は既存の RecentUploads を維持する。
            if (allScanned.Count > 0)
            {
                channel.RecentUploads = allScanned
                    .Select(v => new RecentUploadEntry
                    {
                        VideoId      = v.VideoId,
                        Title        = v.Title,
                        ThumbnailUrl = v.ThumbnailUrl,
                        Kind         = v.Kind,
                        Duration     = v.Duration,
                        PublishedAt  = v.PublishedAt
                    })
                    .ToList();
                SettingsService.Instance.UpdateChannelSilent(channel);
            }

            // 表示中の最新動画が削除・非公開になった/復帰したかの判定
            // allScannedBasic は新着有無に関わらず取得済み・追加API呼び出しなしのため毎回のチェックで実行
            if (!string.IsNullOrEmpty(channel.LatestVideoId) && allScannedBasic.Count > 0)
            {
                var scannedEntry = allScannedBasic.FirstOrDefault(v => v.VideoId == channel.LatestVideoId);
                if (!channel.LatestVideoDeleted && scannedEntry == null)
                {
                    channel.LatestVideoDeleted = true;
                    SettingsService.Instance.UpdateChannelSilent(channel);
                    ChannelUpdated?.Invoke();
                    AppLogger.Log(LogMsg.LatestVideoDeletedDetected, channel.ChannelName, channel.LatestVideoId);
                }
                else if (channel.LatestVideoDeleted && scannedEntry != null && channel.LatestKind.HasValue)
                {
                    // 動画IDを変えずに再公開されたケース。新着候補に差し込み、既存の新着処理
                    // （通知送信・タイトル更新・LatestVideoDeleted 解除・カーソル更新）にそのまま乗せる。
                    videos.Add(new VideoInfo
                    {
                        VideoId         = scannedEntry.VideoId,
                        Title           = scannedEntry.Title,
                        ThumbnailUrl    = scannedEntry.ThumbnailUrl,
                        PublishedAt     = scannedEntry.PublishedAt,
                        Kind            = channel.LatestKind.Value,
                        IsUpcoming      = false,
                        IsCurrentlyLive = false
                    });
                    AppLogger.Log(LogMsg.LatestVideoRecovered, channel.ChannelName, channel.LatestVideoId);
                }
                else if (!channel.LatestVideoDeleted && scannedEntry != null
                         && !string.IsNullOrEmpty(scannedEntry.Title) && scannedEntry.Title != channel.LatestTitle)
                {
                    // 動画IDは同じままYouTube側でタイトルのみ変更されたケース
                    var oldTitle = channel.LatestTitle;
                    channel.LatestTitle = scannedEntry.Title;
                    SettingsService.Instance.UpdateChannelSilent(channel);
                    ChannelUpdated?.Invoke();
                    AppLogger.Log(LogMsg.LatestVideoTitleChanged, channel.ChannelName, channel.LatestVideoId, oldTitle!, scannedEntry.Title);
                }
            }

            // 過去に動画があったチャンネルの投稿が全て確認できなくなった/復帰したかの判定
            if (!string.IsNullOrEmpty(channel.LastCheckedVideoId))
            {
                if (!channel.NoVideosFound && playlistEmpty)
                {
                    channel.NoVideosFound = true;
                    SettingsService.Instance.UpdateChannelSilent(channel);
                    ChannelUpdated?.Invoke();
                    AppLogger.Log(LogMsg.NoVideosDetected, channel.ChannelName);
                }
                else if (channel.NoVideosFound && allScannedBasic.Count > 0)
                {
                    channel.NoVideosFound = false;
                    SettingsService.Instance.UpdateChannelSilent(channel);
                    ChannelUpdated?.Invoke();
                    AppLogger.Log(LogMsg.NoVideosRecovered, channel.ChannelName);
                }
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

                    lock (_pendingListLock)
                    {
                        channel.ActiveLives.Clear();
                        channel.ActivePremieres.Clear();
                        foreach (var v in allScanned.Where(v => v.IsCurrentlyLive))
                        {
                            var entry = new YTNotifier.Models.PendingVideoEntry { VideoId = v.VideoId, Title = v.Title, ThumbnailUrl = v.ThumbnailUrl, ActualStartTime = v.ActualStartTime };
                            if (v.Kind == VideoKind.Live)          channel.ActiveLives.Add(entry);
                            else if (v.Kind == VideoKind.Premiere) channel.ActivePremieres.Add(entry);
                        }
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
            lock (_pendingListLock)
            {
                channel.ActiveLives.Clear();
                channel.ActivePremieres.Clear();
                foreach (var v in allScanned.Where(v => v.IsCurrentlyLive))
                {
                    var entry = new YTNotifier.Models.PendingVideoEntry { VideoId = v.VideoId, Title = v.Title, ThumbnailUrl = v.ThumbnailUrl, ActualStartTime = v.ActualStartTime };
                    if (v.Kind == VideoKind.Live)           channel.ActiveLives.Add(entry);
                    else if (v.Kind == VideoKind.Premiere)  channel.ActivePremieres.Add(entry);
                }
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
                channel.LatestThumbnailUrl = latestFound.ThumbnailUrl;
                channel.LatestVideoDeleted = false;
            }

            var newCandidates = BuildNewVideoNotifyCandidates(channel, videos);

            // upcoming 動画はカーソルを進めない（公開済み動画が upcoming より古い位置にあっても検出できるよう）
            var cursorVideo = videos.FirstOrDefault(v => !v.IsUpcoming);
            if (cursorVideo != null)
            {
                channel.LastCheckedVideoId = cursorVideo.VideoId;
                channel.LastCheckedVideoPublishedAt = cursorVideo.PublishedAt;
            }

            SettingsService.Instance.UpdateChannelSilent(channel);

            var (pendingNotifyLive, pendingNotifyPremiere, confirmedTransitions) =
                BuildPendingTransitionCandidates(channel, pendingTransitioned, newCandidates);

            // ③ 確認済み遷移動画（wasLive/wasPremiereで実在確認済み）の VideoId で LastCheckedVideoId を進める
            // 配信中のライブ/プレミア自身は毎回 pendingTransitioned に含まれ続けるが wasLive/wasPremiere が
            // 既に false（PendingLives/PendingPremieres から削除済み）のため confirmedTransitions には含まれず、
            // カーソルを巻き戻さない（007修正）
            var transitionedIds = confirmedTransitions
                .Where(v => !videos.Any(x => x.VideoId == v.VideoId) && v.VideoId != channel.LastCheckedVideoId)
                .Select(v => v.VideoId)
                .ToHashSet();
            if (transitionedIds.Count > 0)
            {
                var best = allScanned.FirstOrDefault(s => transitionedIds.Contains(s.VideoId));
                if (best != null)
                {
                    channel.LastCheckedVideoId = best.VideoId;
                    channel.LastCheckedVideoPublishedAt = best.PublishedAt;
                }
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
            NetworkCheckRequested?.Invoke();
        }
        finally
        {
            bool hadGrace = TickGracePeriods(channel);
            DateTime? suspended;
            lock (_quotaLock) suspended = _quotaSuspendedUntil;
            // クォータ超過後にリセット時刻到達で _quotaSuspendedUntil が別スレッドにクリアされた場合も
            // 次回リセット時刻まで待機させる（直前のリセット時刻はすでに過ぎているので +1日分を取得）
            if (quotaExceeded && !suspended.HasValue)
                suspended = GetNextQuotaResetTime();
            RevertExpiredPendingEntries(channel, channel.LastCheckedAt);
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
        var staleCutoff = DateTime.Now.AddDays(-StalePendingEntryDays);
        lock (_pendingListLock)
        {
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
    }

    /// <summary>
    /// 開始予定時刻を過ぎた pending エントリの猶予期間を起動する。
    /// API 呼び出し前に実行することで、LastCheckedVideoId に吸収済みのエントリにも機能する。
    /// </summary>
    private static void ActivateGracePeriods(ChannelInfo channel)
    {
        lock (_pendingListLock)
        {
            foreach (var entry in channel.PendingLives.Concat(channel.PendingPremieres))
            {
                if (entry.ScheduledAt.HasValue
                    && entry.ScheduledAt.Value <= DateTime.Now
                    && entry.GraceRemaining == 0)
                {
                    entry.GraceRemaining = GracePeriodAttempts;
                    AppLogger.Log(LogMsg.GracePeriodStarted, channel.ChannelName, entry.VideoId, GracePeriodAttempts);
                }
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
        // カーソル未設定＝まだ一度も中身を見ていない初回チェック。BuildNewVideoNotifyCandidates は
        // カーソル更新（CheckChannelAsync の cursorVideo 反映）より前に呼ばれるため、ここで判定して問題ない。
        bool isFirstCheck              = string.IsNullOrEmpty(channel.LastCheckedVideoId);
        bool firstViewArchiveNotified  = false;

        foreach (var video in videos)
        {
            switch (video.Kind)
            {
                case VideoKind.Live:
                    if (video.IsUpcoming)
                        QueueUpcomingEntry(channel, channel.PendingLives, video);
                    else if (video.IsCurrentlyLive)
                    {
                        // 配信中：pending から削除し、開始日に関わらず通知する
                        // （⑥ 013修正で当日フィルタ撤去。最新のライブ活動を通知。二重通知はカーソルで防止）
                        // publishedLiveSeen は「通知対象の配信中ライブを見た」ときだけ立てる（⑦）
                        publishedLiveSeen = true;
                        var liveCandidate = ResolveLiveCandidate(channel, video);
                        if (liveCandidate != null) notifyLives.Add(liveCandidate);
                    }
                    else
                    {
                        // アーカイブ（配信終了済み）。まず pending の後始末のみ行う。
                        lock (_pendingListLock)
                            channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

                        // 初回チェック（まだ一度も中身を見ていないチャンネル）に限り、最新1件を通知する（①例外）。
                        // 既に見ているチャンネルで配信終了→アーカイブ化したものは通知しない（①本体・02:42バグ修正）。
                        // 通知する場合も KindLabel は「アーカイブ」になる（②）。NotifyLive フィルタのみ適用し、
                        // 待機所モード（WaitingRoomOnly）は配信開始通知向けの設定のためここでは適用しない。
                        if (isFirstCheck && !firstViewArchiveNotified)
                        {
                            firstViewArchiveNotified = true;
                            var archiveCandidate = FilterByKind(channel, video, channel.NotifyLive);
                            if (archiveCandidate != null) notifyLives.Add(archiveCandidate);
                        }
                        else
                        {
                            AppLogger.Log(LogMsg.ArchivedLiveNotNotified, channel.ChannelName, video.Title);
                        }
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

        lock (_pendingListLock)
        {
            // 既存エントリは情報を更新して終了（日付チェック不要）
            var existing = list.FirstOrDefault(p => p.VideoId == video.VideoId);
            if (existing != null)
            {
                bool hasChanged = existing.ScheduledAt != scheduled
                                || existing.Title != video.Title
                                || existing.ThumbnailUrl != video.ThumbnailUrl;

                existing.ScheduledAt  = scheduled;
                existing.Title        = video.Title;
                existing.ThumbnailUrl = video.ThumbnailUrl;

                if (hasChanged)
                {
                    AppLogger.Log(LogMsg.UpcomingQueueUpdated, channel.ChannelName,
                        scheduled?.ToString("MM/dd HH:mm") ?? "-", video.Title);
                }
                return;
            }

            if (list.Count >= MaxPendingQueueSize)
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
        }

        // スケジューラーに新規キュー追加を通知
        Instance.WakeUpScheduler(channel.ChannelName);
    }

    /// <summary>
    /// ライブ配信（配信開始済み）の通知候補を解決し、pending から削除する。
    /// UpcomingNotifyMode に従い開始通知を送るかを決定する。
    /// </summary>
    private static VideoInfo? ResolveLiveCandidate(ChannelInfo channel, VideoInfo video)
    {
        lock (_pendingListLock)
            channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

        if (channel.LiveUpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
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
        lock (_pendingListLock)
            channel.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

        if (channel.PremiereUpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
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
    private static (VideoInfo? live, VideoInfo? premiere, List<VideoInfo> confirmedTransitions) BuildPendingTransitionCandidates(
        ChannelInfo channel,
        List<VideoInfo> pendingTransitioned,
        NewVideoNotifyCandidates newCandidates)
    {
        VideoInfo? pendingNotifyLive     = null;
        VideoInfo? pendingNotifyPremiere = null;
        var confirmedTransitions = new List<VideoInfo>();

        foreach (var video in pendingTransitioned)
        {
            // Phase2 の再分類に頼らず、追跡元リスト（PendingLives / PendingPremieres）で種別を確定する
            bool wasLive, wasPremiere;
            lock (_pendingListLock)
            {
                wasLive     = channel.PendingLives.Any(p => p.VideoId == video.VideoId);
                wasPremiere = channel.PendingPremieres.Any(p => p.VideoId == video.VideoId);
            }

            if (wasLive)
            {
                lock (_pendingListLock)
                    channel.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

                confirmedTransitions.Add(video);

                if (!video.IsCurrentlyLive)
                {
                    // 待機所から配信開始を捕まえられないまま終了（アーカイブ化）したものは通知しない（③ 013修正）。
                    // カーソルは confirmedTransitions 経由で前進させ、pending は上で削除済み。
                    AppLogger.Log(LogMsg.ArchivedLiveNotNotified, channel.ChannelName, video.Title);
                }
                else if (newCandidates.LatestLiveSeen)
                {
                    AppLogger.Log(LogMsg.OldLiveDiscardedNew, channel.ChannelName, video.Title);
                }
                else if (pendingNotifyLive == null)
                {
                    if (channel.LiveUpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
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
                lock (_pendingListLock)
                    channel.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

                confirmedTransitions.Add(video);

                if (newCandidates.LatestPremiereSeen)
                {
                    AppLogger.Log(LogMsg.OldPremiereDiscardedNew, channel.ChannelName, video.Title);
                }
                else if (pendingNotifyPremiere == null)
                {
                    if (channel.PremiereUpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
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

        return (pendingNotifyLive, pendingNotifyPremiere, confirmedTransitions);
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
        lock (_pendingListLock)
        {
            foreach (var entry in ch.PendingLives.Concat(ch.PendingPremieres))
            {
                if (entry.GraceRemaining <= 0) continue;
                anyGrace = true;
                entry.GraceRemaining--;
                if (entry.GraceRemaining == 0) entry.GraceRemaining = -1;
            }
        }
        return anyGrace;
    }

    /// <summary>
    /// 時間指定スロットの監視ウィンドウが終了してもライブ/プレミア開始が確認できなかった
    /// pending エントリ（猶予期間終了済み = GraceRemaining == -1）の予約状態を解除する。
    /// </summary>
    private static void RevertExpiredPendingEntries(ChannelInfo ch, DateTime now)
    {
        if (HasActiveTimeSlotWindow(ch, now)) return;

        void Revert(List<PendingVideoEntry> entries)
        {
            var expired = entries.Where(e => e.GraceRemaining == -1).ToList();
            foreach (var e in expired)
            {
                entries.Remove(e);
                AppLogger.Log(LogMsg.PendingWindowExpired, ch.ChannelName, e.VideoId);
            }
        }

        lock (_pendingListLock)
        {
            Revert(ch.PendingLives);
            Revert(ch.PendingPremieres);
        }
    }

    /// <summary>現在、有効な時間指定スロットの監視ウィンドウ内かどうかを判定する</summary>
    private static bool HasActiveTimeSlotWindow(ChannelInfo ch, DateTime now)
    {
        foreach (var slot in ch.FocusSlots)
        {
            if (!slot.IsEnabled || slot.SlotMode != MonitorMode.Focus) continue;
            var anchor    = now.Date.AddHours(slot.Hour).AddMinutes(slot.Minute);
            var windowEnd = anchor.AddMinutes(slot.WindowMinutes);
            if (now >= anchor && now <= windowEnd && IsSlotDayMatch(slot.Days, anchor.Date))
                return true;
        }
        return false;
    }

    // ===== 次回チェック時刻を計算（副作用なし）=====
    public static DateTime CalcNextCheckAt(ChannelInfo ch, DateTime? baseTime = null)
    {
        var now        = baseTime ?? DateTime.Now;
        var globalMins = SettingsService.Instance.Settings.CheckIntervalMinutes;

        if (ch.ActiveLives.Count > 0 || ch.ActivePremieres.Count > 0)
            return now.AddMinutes(ActiveLiveCheckIntervalMinutes);

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

    // ===== チャンネルの「今表示・クリックで開くべき対象」を判定（副作用なし）=====
    public static ChannelCardStatus ResolveCardStatus(ChannelInfo ch)
    {
        var statusWindow = DateTime.Now.AddMinutes(UpcomingDisplayWindowMinutes);

        List<YTNotifier.Models.PendingVideoEntry>  activeLiveEntries;
        List<YTNotifier.Models.PendingVideoEntry>  activePremiereEntries;
        YTNotifier.Models.PendingVideoEntry?       pendingLiveDisplay;
        YTNotifier.Models.PendingVideoEntry?       pendingPremiereDisplay;
        lock (_pendingListLock)
        {
            activeLiveEntries     = ch.NotifyLive  ? ch.ActiveLives.ToList()     : new List<YTNotifier.Models.PendingVideoEntry>();
            activePremiereEntries = ch.NotifyVideo ? ch.ActivePremieres.ToList() : new List<YTNotifier.Models.PendingVideoEntry>();

            pendingLiveDisplay = ch.NotifyLive
                ? ch.PendingLives
                    .Where(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= statusWindow)
                    .Where(p => !activeLiveEntries.Any(a => a.VideoId == p.VideoId)) // 012修正：配信中と同一動画は予定側に出さない
                    .OrderBy(p => p.ScheduledAt)
                    .FirstOrDefault()
                : null;

            pendingPremiereDisplay = ch.NotifyVideo
                ? ch.PendingPremieres
                    .Where(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= statusWindow)
                    .Where(p => !activePremiereEntries.Any(a => a.VideoId == p.VideoId)) // 012修正：公開中と同一動画は予定側に出さない
                    .OrderBy(p => p.ScheduledAt)
                    .FirstOrDefault()
                : null;
        }

        var result = new ChannelCardStatus
        {
            ActiveLiveEntries       = activeLiveEntries,
            ActivePremiereEntries   = activePremiereEntries,
            PendingLiveDisplay      = pendingLiveDisplay,
            PendingPremiereDisplay  = pendingPremiereDisplay,
        };

        // ① 進行中（配信中ライブ／公開中プレミア）の中で開始時刻が最も新しいものを優先
        var activeCombined = activeLiveEntries
            .Select(e => (Entry: e, Kind: VideoKind.Live))
            .Concat(activePremiereEntries.Select(e => (Entry: e, Kind: VideoKind.Premiere)))
            .ToList();
        if (activeCombined.Count > 0)
        {
            var newest = activeCombined
                .OrderByDescending(x => x.Entry.ActualStartTime ?? DateTime.MinValue)
                .First();
            result.ClickTargetVideoId = newest.Entry.VideoId;
            result.ClickTargetKind    = newest.Kind;
            return result;
        }

        // ② 30分以内の予約（ライブ・プレミア）で、早い方（同時刻はライブ優先）
        if (pendingLiveDisplay != null || pendingPremiereDisplay != null)
        {
            if (pendingLiveDisplay != null && pendingPremiereDisplay != null)
            {
                if (pendingPremiereDisplay.ScheduledAt < pendingLiveDisplay.ScheduledAt)
                {
                    result.ClickTargetVideoId = pendingPremiereDisplay.VideoId;
                    result.ClickTargetKind    = VideoKind.Premiere;
                }
                else
                {
                    result.ClickTargetVideoId = pendingLiveDisplay.VideoId;
                    result.ClickTargetKind    = VideoKind.Live;
                }
            }
            else if (pendingLiveDisplay != null)
            {
                result.ClickTargetVideoId = pendingLiveDisplay.VideoId;
                result.ClickTargetKind    = VideoKind.Live;
            }
            else
            {
                result.ClickTargetVideoId = pendingPremiereDisplay!.VideoId;
                result.ClickTargetKind    = VideoKind.Premiere;
            }
            return result;
        }

        // ③ 種別ごとの最新（削除・非公開が確定しているものは対象外）
        if (!string.IsNullOrEmpty(ch.LatestVideoId) && ch.LatestKind.HasValue && !ch.LatestVideoDeleted && !ch.NoVideosFound)
        {
            result.ClickTargetVideoId = ch.LatestVideoId;
            result.ClickTargetKind    = ch.LatestKind;
            return result;
        }

        // ④ 該当なし
        return result;
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

            resumeAt             = GetNextQuotaResetTime();
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

    /// <summary>指定した1チャンネルだけを即時チェックする（チャンネルカードの「最新情報取得」用）。
    /// 他のチェックが実行中の場合は完了を待ってから実行する。</summary>
    public async Task<bool> ManualCheckChannelAsync(ChannelInfo channel)
    {
        if (channel is null || channel.IsDormant) return false;

        lock (_quotaLock)
        {
            if (_quotaSuspendedUntil.HasValue && DateTime.Now < _quotaSuspendedUntil.Value)
                return false;
        }

        var waitUntil = DateTime.Now.AddSeconds(ChannelManualCheckMaxWaitSeconds);
        while (Interlocked.CompareExchange(ref _isChecking, 1, 0) != 0)
        {
            if (DateTime.Now >= waitUntil) return false;
            await Task.Delay(ChannelManualCheckPollIntervalMs);
        }
        try
        {
            await CheckChannelAsync(channel);
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
        return true;
    }

    public void Dispose()
    {
        _timer?.Dispose();
        CancellationTokenSource schedulerCts;
        lock (_schedulerLock) schedulerCts = _schedulerCts;
        schedulerCts.Cancel();
    }
}
