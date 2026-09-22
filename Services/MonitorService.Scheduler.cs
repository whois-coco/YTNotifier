using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: 待機所・開始通知スケジューラー
public partial class MonitorService
{
    /// <summary>待機所通知タイミングのデフォルト（leadMinutes=0 の既存データ向けフォールバック）</summary>
    private const int DefaultUpcomingLeadMinutes = 5;

    /// <summary>スケジューラーで想定外エラーが発生した際の再試行待機時間（秒）</summary>
    private const int SchedulerErrorRetryDelaySeconds = 30;

    // ===== 待機所・開始通知スケジューラー =====
    private readonly object              _schedulerLock = new();
    private CancellationTokenSource      _schedulerCts  = new();
    private Task?                        _schedulerTask;

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

        foreach (var ch in SettingsService.Instance.Channels.GetEnabledChannelsSnapshot())
        {
            if (ch.State.IsBanned) continue;

            List<(PendingVideoEntry entry, bool isLive)> entries;
            lock (_pendingListLock)
                entries = ch.State.PendingLives.Select(e => (e, true))
                    .Concat(ch.State.PendingPremieres.Select(e => (e, false)))
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
        if (ch.State.IsBanned) return;

        var entry = action.Entry;

        if (action.IsNotification)
        {
            bool isPendingLive, isPendingPremiere;
            lock (_pendingListLock)
            {
                isPendingLive     = ch.State.PendingLives.Any(p => p.VideoId == entry.VideoId);
                isPendingPremiere = ch.State.PendingPremieres.Any(p => p.VideoId == entry.VideoId);
            }
            if (!isPendingLive && !isPendingPremiere) return;
            if (entry.UpcomingNotified) return;

            if (entry.ScheduledAt.HasValue && entry.ScheduledAt.Value < DateTime.Now)
            {
                entry.UpcomingNotified = true;
                SettingsService.Instance.Channels.UpdateChannelSilent(ch);
                return;
            }

            entry.UpcomingNotified = true;
            SettingsService.Instance.Channels.UpdateChannelSilent(ch);

            var kind     = isPendingLive ? VideoKind.Live : VideoKind.Premiere;
            var notifyOn = kind == VideoKind.Live ? ch.NotifyLive : ch.NotifyVideo;
            if (!notifyOn) return;
            if (MatchesNgWord(ch, entry.Title))
            {
                AppLogger.Log(LogMsg.NgWordFilterSkipped, ch.ChannelName, entry.Title);
                return;
            }

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
                var isPendingLive     = ch.State.PendingLives.Any(p => p.VideoId == entry.VideoId);
                var isPendingPremiere = ch.State.PendingPremieres.Any(p => p.VideoId == entry.VideoId);
                proceed = (isPendingLive || isPendingPremiere) && entry.GraceRemaining == 0;
                if (proceed) entry.GraceRemaining = GracePeriodAttempts;
            }
            if (!proceed) return;

            ch.State.NextCheckAt = DateTime.Now;
            SettingsService.Instance.Channels.UpdateChannelSilent(ch);
            AppLogger.Log(LogMsg.SchedulerGracePeriodStarted, ch.ChannelName, entry.VideoId);
        }
    }
}
