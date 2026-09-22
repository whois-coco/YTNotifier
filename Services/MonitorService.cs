using Application = System.Windows.Application;
using Timer = System.Threading.Timer;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: 起動・停止・タイマー・一括巡回・クォータ超過
public partial class MonitorService
{
    private static readonly Lazy<MonitorService> _lazy = new(() => new MonitorService());
    public static MonitorService Instance => _lazy.Value;

    /// <summary>ライブ/プレミア配信開始後に継続チェックする猶予回数</summary>
    private const int GracePeriodAttempts = 10;

    /// <summary>クォータ超過による監視停止・再開予定時刻のログ表示フォーマット</summary>
    private const string QuotaResumeTimeFormat = "M/d HH:mm";

    /// <summary>次のタイマー起動を何秒に合わせるか（:01 秒・:31 秒。:00 秒ちょうどを避ける）</summary>
    private const int AlignedTickSecond = 1;

    /// <summary>タイマーの発火間隔（秒）。時間指定スロットの最短間隔（30秒）に合わせる</summary>
    private const int TickIntervalSeconds = 30;

    /// <summary>
    /// 次回チェック予定時刻をこの秒数だけ手前まで「到来済み」とみなす。
    /// 予定時刻はチェック開始時刻＋間隔で決まり、タイマー発火時刻とは1秒未満ずれるため、
    /// わずかに間に合わず1回分（TickIntervalSeconds）余計に待たされるのを防ぐ
    /// </summary>
    private const int DueCheckToleranceSeconds = 5;

    /// <summary>クォータリセットから何分後に定時全巡回を始めるか</summary>
    private const int DailyFullScanDelayMinutes = 1;

    private readonly IYouTubeApiClient _youtubeClient;
    private Timer? _timer;
    private volatile bool _isRunning      = false;
    private int           _isChecking     = 0;
    private volatile bool _isStartupCheck = true;
    private volatile bool _startupIsNewDay = false;
    private readonly object _quotaLock = new();
    private DateTime?       _quotaSuspendedUntil = null;

    // PendingLives/PendingPremieres/ActiveLives/ActivePremieres の構造変更（Add/Remove/Clear系）と、
    // 監視スレッド外（スケジューラー・UI・状態保存）からの列挙・参照を直列化するロック。
    // 注意: このロックを保持したまま SettingsService のロックを取るメソッド（Save系/UpdateChannel系）を呼ばないこと
    private static readonly object _pendingListLock = new();

    // ===== 他クラスからロックを保持したまま処理を実行するための入口 =====

    /// <summary>待機リスト用ロックを保持したまま処理を実行し、その結果を返す</summary>
    internal static TResult RunUnderPendingListLock<TResult>(Func<TResult> action)
    {
        lock (_pendingListLock) return action();
    }

    /// <summary>待機リスト用ロックを保持したまま処理を実行する</summary>
    internal static void RunUnderPendingListLock(Action action)
    {
        lock (_pendingListLock) action();
    }

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

    /// <summary>次の :01 秒または :31 秒までの遅延を計算する（最大30秒・遅延最小化）</summary>
    private static TimeSpan CalcAlignedDelay()
    {
        var sec = TickIntervalSeconds + AlignedTickSecond - (DateTime.Now.Second % TickIntervalSeconds);
        if (sec > TickIntervalSeconds) sec -= TickIntervalSeconds;
        return TimeSpan.FromSeconds(sec);
    }

    /// <summary>次の :01 秒または :31 秒にタイマーを再スケジュールする</summary>
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

        // クォータ超過による監視停止を再起動後も維持する（007）
        var quotaResumeAt  = SettingsService.Instance.MonitorState.AppState.QuotaSuspendedUntil;
        var quotaSuspended = quotaResumeAt.HasValue && now < quotaResumeAt.Value;
        if (quotaSuspended)
        {
            lock (_quotaLock) _quotaSuspendedUntil = quotaResumeAt;
        }
        else if (quotaResumeAt.HasValue)
        {
            SettingsService.Instance.MonitorState.PersistQuotaSuspension(null); // 期限切れ → 破棄
        }

        foreach (var ch in SettingsService.Instance.Channels.GetEnabledChannelsSnapshot())
        {
            if (quotaSuspended)
                ch.State.NextCheckAt = quotaResumeAt!.Value;
            else if (isNewDay)
                ch.State.NextCheckAt = DateTime.MinValue;
            else if (ch.State.NextCheckAt == DateTime.MinValue)
                ch.State.NextCheckAt = CalcNextCheckAt(ch);
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
        if (quotaSuspended)
            AppLogger.Log(LogMsg.QuotaStillSuspended, null, quotaResumeAt!.Value.ToString(QuotaResumeTimeFormat));
        StatusChanged?.Invoke(!quotaSuspended);
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
                SettingsService.Instance.MonitorState.PersistQuotaSuspension(null);
                AppLogger.Log(LogMsg.QuotaResumed);
                StatusChanged?.Invoke(true); // 再開を UI に通知
            }
            var allChannels = SettingsService.Instance.Channels.GetEnabledChannelsSnapshot();

            // 定時全巡回（クォータリセット＋1分以降の最初のチェックで実行、起動時チェック中を除く、太平洋時間基準で1日1回）
            var pacificDayKey   = AppConstants.GetQuotaDayKey();
            var triggerTime     = QuotaResetTimeHelper.GetTodayQuotaResetTime().AddMinutes(DailyFullScanDelayMinutes);
            var isDailyFullScan = false;
            if (now >= triggerTime
                && !_isStartupCheck
                && SettingsService.Instance.Settings.LastDailyFullScanDate != pacificDayKey)
            {
                SettingsService.Instance.Settings.LastDailyFullScanDate = pacificDayKey;
                SettingsService.Instance.SaveSettings();
                foreach (var ch in allChannels)
                    ch.State.NextCheckAt = DateTime.MinValue;
                isDailyFullScan = true;
            }

            // BAN／自主削除判定のトリガー：定時全チェック、または当日初回の起動時チェック（どちらか早い方で1日1回）
            var runBanScan = isDailyFullScan || forceAll || (_isStartupCheck && _startupIsNewDay);
            if (!isDailyFullScan && _isStartupCheck && _startupIsNewDay)
            {
                // 起動時トリガー経由の場合、同日の定時全チェック（太平洋時間トリガー）での二重実行を防ぐ
                SettingsService.Instance.Settings.LastDailyFullScanDate = pacificDayKey;
                SettingsService.Instance.SaveSettings();
            }

            if (runBanScan)
            {
                await CheckChannelsAliveAsync(allChannels, LogMsg.ChannelListBanCheckStarted, LogMsg.ChannelListBanCheckCompleted, LogMsg.ChannelListAllAlive);
                await CheckChannelsAliveAsync(SettingsService.Instance.Channels.GetChannelsSnapshot().Where(c => c.IsDormant).ToList(), LogMsg.DormantListBanCheckStarted, LogMsg.DormantListBanCheckCompleted, LogMsg.DormantListAllAlive);
                _lastBannedRecheckAt = now;
            }
            else
            {
                await RecheckBannedChannelsAsync(allChannels, now);
            }

            var dueThreshold = now.AddSeconds(DueCheckToleranceSeconds);
            var channels = allChannels
                .Where(c => !c.State.IsBanned && (forceAll || c.State.NextCheckAt <= dueThreshold))
                .ToList();

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

    public void ResetNormalChannels(int newIntervalMinutes)
    {
        var nextCheckAt = DateTime.Now.AddMinutes(newIntervalMinutes);
        foreach (var ch in SettingsService.Instance.Channels.GetEnabledChannelsSnapshot())
        {
            var hasNormal = ch.MonitorMode == MonitorMode.Normal
                || (ch.MonitorMode == MonitorMode.Focus
                    && ch.FocusSlots.Any(s => s.IsEnabled && s.SlotMode == MonitorMode.Normal));
            if (hasNormal) ch.State.NextCheckAt = nextCheckAt;
        }
    }

    private void HandleQuotaExceeded()
    {
        DateTime resumeAt;
        lock (_quotaLock)
        {
            if (_quotaSuspendedUntil.HasValue) return; // 既に処理済み（複数並列タスクの重複呼び出し防止）

            resumeAt             = QuotaResetTimeHelper.GetNextQuotaResetTime();
            _quotaSuspendedUntil = resumeAt;
        }

        // ロック外で副作用を実行（デッドロック防止）
        foreach (var ch in SettingsService.Instance.Channels.GetEnabledChannelsSnapshot())
        {
            ch.State.NextCheckAt = resumeAt;
            SettingsService.Instance.Channels.UpdateChannelSilent(ch);
        }

        SettingsService.Instance.MonitorState.PersistQuotaSuspension(resumeAt);

        AppLogger.Log(LogMsg.QuotaExceeded, null, resumeAt.ToString(QuotaResumeTimeFormat));

        // UI に停止状態を通知（IsRunning は true のままなので明示的に false を送る）
        StatusChanged?.Invoke(false);

        Application.Current?.Dispatcher.InvokeAsync(() =>
            NotificationService.ShowQuotaExceededNotification(resumeAt));
    }

    public void InvokeChannelUpdated() => ChannelUpdated?.Invoke();
    public void SendTestNotification() => NotificationService.ShowTestNotification();
}
