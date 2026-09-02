using System.Linq;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// YouTube Data API v3 クォータ計算ヘルパー
/// 無料枠: 10,000 ユニット/日
/// チェック1回あたりのコスト:
///   playlistItems.list = 1ユニット（毎回）
///   videos.list        = 1ユニット（新着検知時のみ）
///   playlistItems.list（UUSH Short判定） = 1ユニット（新着検知した動画のうち
///                                          再生時間180秒以下のときのみ）
/// 1チャンネルが1日に「新着検知」を起こす回数には想定上限があり、その値が
/// DailyUploadLimitPerChannel である。最大値見積もりのため、
/// 新着検知した動画はすべてUUSH判定（+1ユニット）が発生すると仮定する。よって
///   1チャンネルの1日の推定消費量 = チェック回数 + 2 * min(チェック回数, DailyUploadLimitPerChannel)
/// </summary>
public static class ApiQuotaHelper
{
    public const int    DailyLimit                 = 10_000; // ユニット/日
    public const int    DailyUploadLimitPerChannel  = 10;     // 1チャンネルが1日に新着を出すと想定する上限本数（動画+Short合計）。見積もり用の想定値
    public const double QuotaDisableThresholdPct    = 95.0;   // この%以上で手動チェックボタンを無効化
    public const int    QuotaWarnHighThresholdPct   = 90;     // この%以上で高警告色
    public const int    QuotaWarnLowThresholdPct    = 85;     // この%以上で低警告色
    public const int    MinutesPerDay               = 1_440;

    /// <summary>チェック間隔の推奨候補（分）</summary>
    private static readonly int[] CheckIntervalCandidates = { 1, 5, 10, 30, 60 };

    /// <summary>
    /// チャンネルリストと間隔指定がクォータ内に収まるか検証し、
    /// 収まらない場合は推奨間隔を返す。
    /// 監視モード（集中/低頻度）を正確に反映する。
    /// </summary>
    public static (bool safe, int recommendedMinutes) ValidateInterval(
        int intervalMinutes,
        IEnumerable<YTNotifier.Models.ChannelInfo> channels)
    {
        var channelList = channels.ToList();
        if (channelList.Count == 0) return (true, intervalMinutes);

        var daily = EstimateDailyUnitsForChannels(intervalMinutes, channelList);
        if (daily <= DailyLimit) return (true, intervalMinutes);

        // 推奨間隔を候補から探す（小さい順に試して収まる最小値を返す）
        var candidates = CheckIntervalCandidates;
        foreach (var candidate in candidates)
        {
            if (EstimateDailyUnitsForChannels(candidate, channelList) <= DailyLimit)
                return (false, candidate);
        }
        return (false, 60);
    }

    /// <summary>
    /// 1チャンネル分の1日推定消費ユニット数を、監視モード別パラメータから計算する。
    /// 全チャンネル合算（EstimateDailyUnitsForChannels）と上級設定のプレビューで
    /// 同じ計算式を共有するための単一ソース。
    /// </summary>
    public static int EstimateDailyUnitsForMode(
        YTNotifier.Models.MonitorMode mode,
        int globalIntervalMinutes,
        int focusWindowMinutes,
        int focusIntervalMinutes,
        int lowFreqIntervalMinutes)
    {
        int checks;
        switch (mode)
        {
            case YTNotifier.Models.MonitorMode.LowFreq:
            {
                var lowInterval = Math.Max(1, lowFreqIntervalMinutes);
                checks = MinutesPerDay / lowInterval;
                break;
            }
            case YTNotifier.Models.MonitorMode.Focus:
            {
                var window   = focusWindowMinutes;
                var interval = Math.Max(1, focusIntervalMinutes);
                checks = window / interval;
                break;
            }
            default: // Normal
            {
                var globalInterval = Math.Max(1, globalIntervalMinutes);
                checks = MinutesPerDay / globalInterval;
                break;
            }
        }
        // videos.list（新着検知）+ UUSH判定playlistItems.list（新着すべてがShort候補と仮定した最大値見積もり）
        return checks + 2 * Math.Min(checks, DailyUploadLimitPerChannel);
    }

    /// <summary>
    /// スロット単位（複数スロット）の1日推定ユニット数。
    /// 実際には1回のチェックで全種別を処理するため、
    /// Normal/LowFreq スロットの最小間隔をベースに算出し、
    /// Focus スロットはウィンドウ内でベースより高頻度な分だけ加算する。
    /// </summary>
    public static int EstimateDailyUnitsForFocusSlots(
        IEnumerable<YTNotifier.Models.FocusSlot> slots,
        int globalIntervalMinutes = 5)
    {
        var enabledSlots = slots.Where(s => s.IsEnabled).ToList();
        if (enabledSlots.Count == 0) return 0;

        // Normal/LowFreq スロットの最小間隔をベース間隔とする
        int baseInterval = int.MaxValue;
        foreach (var slot in enabledSlots)
        {
            switch (slot.SlotMode)
            {
                case YTNotifier.Models.MonitorMode.Normal:
                    var ni = slot.SlotNormalIntervalMinutes > 0
                        ? slot.SlotNormalIntervalMinutes
                        : Math.Max(1, globalIntervalMinutes);
                    if (ni < baseInterval) baseInterval = ni;
                    break;
                case YTNotifier.Models.MonitorMode.LowFreq:
                    var li = Math.Max(1, slot.SlotLowFreqIntervalMinutes);
                    if (li < baseInterval) baseInterval = li;
                    break;
            }
        }

        double totalChecks;
        if (baseInterval == int.MaxValue)
        {
            // Normal/LowFreq スロットなし（Focus スロットのみ）
            totalChecks = 0;
            foreach (var slot in enabledSlots.Where(s => s.SlotMode == YTNotifier.Models.MonitorMode.Focus))
            {
                var interval   = ToIntervalMinutes(slot.IntervalMinutes);
                int activeDays = slot.Days == 0 ? 7 : CountBits(slot.Days);
                totalChecks += (slot.WindowMinutes / interval) * activeDays / 7.0;
            }
        }
        else
        {
            // ベース: 1日中 baseInterval 間隔でチェック
            totalChecks = MinutesPerDay / baseInterval;

            // Focus スロット: ウィンドウ内でベースより高頻度な分のみ追加
            foreach (var slot in enabledSlots.Where(s => s.SlotMode == YTNotifier.Models.MonitorMode.Focus))
            {
                var focusInterval = ToIntervalMinutes(slot.IntervalMinutes);
                if (focusInterval >= baseInterval) continue;

                int activeDays        = slot.Days == 0 ? 7 : CountBits(slot.Days);
                var additionalChecks  = slot.WindowMinutes / (double)focusInterval
                                       - slot.WindowMinutes / (double)baseInterval;
                totalChecks += additionalChecks * activeDays / 7.0;
            }
        }

        var checks = (int)Math.Round(totalChecks);
        return checks + 2 * Math.Min(checks, DailyUploadLimitPerChannel);
    }

    /// <summary>IntervalMinutes == 0 は30秒（0.5分）を意味する</summary>
    private static double ToIntervalMinutes(int intervalMinutes)
        => intervalMinutes == 0 ? 0.5 : Math.Max(1, intervalMinutes);

    private static int CountBits(int v)
    {
        int c = 0;
        for (int i = 0; i < 7; i++) if ((v & (1 << i)) != 0) c++;
        return c;
    }

    /// <summary>全チャンネルの監視モードを考慮した1日の推定消費ユニット数を計算する</summary>
    public static int EstimateDailyUnitsForChannels(
        int globalIntervalMinutes,
        IEnumerable<YTNotifier.Models.ChannelInfo> channels)
    {
        int total = 0;
        foreach (var ch in channels.Where(c => c.IsEnabled && !c.IsDormant))
        {
            // 時間指定モードでFocusSlotsがあればスロット単位の計算を使用
            if (ch.MonitorMode == YTNotifier.Models.MonitorMode.Focus
                && ch.FocusSlots.Any(s => s.IsEnabled))
            {
                total += EstimateDailyUnitsForFocusSlots(ch.FocusSlots, globalIntervalMinutes);
                continue;
            }
            total += EstimateDailyUnitsForMode(
                ch.MonitorMode, globalIntervalMinutes,
                ch.FocusWindowMinutes, ch.FocusIntervalMinutes, ch.LowFreqIntervalMinutes);
        }
        return total;
    }

    /// <summary>
    /// スロット一覧の推定ユニット数をモード別（通常・低頻度・時間指定）に集計して返す。
    /// 設定画面（全チャンネル集計）とチャンネル詳細画面（編集中スロットのプレビュー）で
    /// 同じ計算式を共有するための単一ソース。
    /// </summary>
    public static (int normal, int lowFreq, int focus) EstimateDailyUnitsByModeForSlots(
        IEnumerable<YTNotifier.Models.FocusSlot> slots,
        int globalIntervalMinutes)
    {
        int normal = 0, lowFreq = 0, focus = 0;
        var globalInterval = Math.Max(1, globalIntervalMinutes);

        foreach (var slot in slots.Where(s => s.IsEnabled))
        {
            int checks;
            switch (slot.SlotMode)
            {
                case YTNotifier.Models.MonitorMode.Normal:
                    var ni = slot.SlotNormalIntervalMinutes > 0
                        ? slot.SlotNormalIntervalMinutes
                        : globalInterval;
                    checks  = MinutesPerDay / Math.Max(1, ni);
                    normal += checks + 2 * Math.Min(checks, DailyUploadLimitPerChannel);
                    break;

                case YTNotifier.Models.MonitorMode.LowFreq:
                    var li = Math.Max(1, slot.SlotLowFreqIntervalMinutes);
                    checks   = MinutesPerDay / li;
                    lowFreq += checks + 2 * Math.Min(checks, DailyUploadLimitPerChannel);
                    break;

                case YTNotifier.Models.MonitorMode.Focus:
                    var fi         = ToIntervalMinutes(slot.IntervalMinutes);
                    int activeDays = slot.Days == 0 ? 7 : CountBits(slot.Days);
                    checks  = (int)Math.Round((slot.WindowMinutes / (double)fi) * activeDays / 7.0);
                    focus  += checks + 2 * Math.Min(checks, DailyUploadLimitPerChannel);
                    break;
            }
        }

        return (normal, lowFreq, focus);
    }

    /// <summary>
    /// 全チャンネルの推定ユニット数をモード別（通常・低頻度・時間指定）に集計して返す。
    /// 各スロットを独立して計算した値の合計のため、EstimateDailyUnitsForChannels とは
    /// 若干異なる場合があるが、割合の算出（セグメントバー表示）用として使用する。
    /// </summary>
    public static (int normal, int lowFreq, int focus) EstimateDailyUnitsByMode(
        int globalIntervalMinutes,
        IEnumerable<YTNotifier.Models.ChannelInfo> channels)
    {
        int normal = 0, lowFreq = 0, focus = 0;
        foreach (var ch in channels.Where(c => c.IsEnabled && !c.IsDormant))
        {
            var (n, l, f) = EstimateDailyUnitsByModeForSlots(ch.FocusSlots, globalIntervalMinutes);
            normal  += n;
            lowFreq += l;
            focus   += f;
        }
        return (normal, lowFreq, focus);
    }

    /// <summary>
    /// チャンネル生存確認バッチ（channels.list）の1日の消費量。
    /// 50件ごとに1ユニット（BanCheckChunkSize）。チャンネル一覧・休眠リストは別々に区切られるため、
    /// それぞれ独立して切り上げ計算してから合算する。チェック間隔・監視モードに関係なく1日1回発生する固定コスト。
    /// </summary>
    public static int EstimateDailyUnitsForBanCheck(int activeChannelCount, int dormantChannelCount)
    {
        int Chunks(int count) => count <= 0 ? 0 : (int)Math.Ceiling(count / (double)YouTubeConstants.BanCheckChunkSize);
        return Chunks(activeChannelCount) + Chunks(dormantChannelCount);
    }
}
