using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: 次回時刻・カード状態・猶予期限の計算
public partial class MonitorService
{
    /// <summary>チャンネルカードに「配信予定」を表示する開始予定時刻までの上限（分）</summary>
    private const int UpcomingDisplayWindowMinutes = 30;

    /// <summary>ライブ配信中・プレミア公開中の終了検知チェック間隔（分）</summary>
    private const int ActiveLiveCheckIntervalMinutes = 15;

    /// <summary>時間指定スロットで間隔0＝30秒を意味する</summary>
    private const int FocusSlotFastIntervalSeconds = 30;

    /// <summary>次の時間指定枠を今日から何日先まで探すか</summary>
    private const int NextFocusWindowSearchDays = 8;

    /// <summary>
    /// 猶予カウンタを1回分進める。猶予中だったエントリが1件以上あった場合 true を返す。
    /// CheckChannelAsync の finally で CalcNextCheckAt より先に呼ぶこと。
    /// </summary>
    private static bool TickGracePeriods(ChannelInfo ch)
    {
        bool anyGrace = false;
        lock (_pendingListLock)
        {
            foreach (var entry in ch.State.PendingLives.Concat(ch.State.PendingPremieres))
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
            foreach (var expiredEntry in expired)
            {
                entries.Remove(expiredEntry);
                AppLogger.Log(LogMsg.PendingWindowExpired, ch.ChannelName, expiredEntry.VideoId);
            }
        }

        lock (_pendingListLock)
        {
            Revert(ch.State.PendingLives);
            Revert(ch.State.PendingPremieres);
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

        if (ch.State.ActiveLives.Count > 0 || ch.State.ActivePremieres.Count > 0)
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
                                    ? now.AddSeconds(FocusSlotFastIntervalSeconds)
                                    : now.AddMinutes(slot.IntervalMinutes);
                                if (earliest == null || slotNext < earliest) earliest = slotNext;
                                continue;
                            }
                            DateTime? nextWindow = null;
                            for (int dayOffset = 0; dayOffset < NextFocusWindowSearchDays; dayOffset++)
                            {
                                var date  = now.Date.AddDays(dayOffset);
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
            activeLiveEntries     = ch.NotifyLive  ? ch.State.ActiveLives.ToList()     : new List<YTNotifier.Models.PendingVideoEntry>();
            activePremiereEntries = ch.NotifyVideo ? ch.State.ActivePremieres.ToList() : new List<YTNotifier.Models.PendingVideoEntry>();

            pendingLiveDisplay = ch.NotifyLive
                ? ch.State.PendingLives
                    .Where(p => p.ScheduledAt.HasValue && p.ScheduledAt.Value <= statusWindow)
                    .Where(p => !activeLiveEntries.Any(a => a.VideoId == p.VideoId)) // 012修正：配信中と同一動画は予定側に出さない
                    .OrderBy(p => p.ScheduledAt)
                    .FirstOrDefault()
                : null;

            pendingPremiereDisplay = ch.NotifyVideo
                ? ch.State.PendingPremieres
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
        if (!string.IsNullOrEmpty(ch.State.LatestVideoId) && ch.State.LatestKind.HasValue && !ch.State.LatestVideoDeleted && !ch.State.NoVideosFound)
        {
            result.ClickTargetVideoId = ch.State.LatestVideoId;
            result.ClickTargetKind    = ch.State.LatestKind;
            return result;
        }

        // ④ 該当なし
        return result;
    }

    /// <summary>曜日ビットマスク判定 (bit0=Sun..bit6=Sat)</summary>
    private static bool IsSlotDayMatch(int daysMask, DateTime date)
        => (daysMask & (1 << (int)date.DayOfWeek)) != 0;
}
