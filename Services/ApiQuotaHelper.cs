using System.Linq;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// YouTube Data API v3 クォータ計算ヘルパー
/// 無料枠: 10,000 ユニット/日
/// チェック1回あたりのコスト:
///   playlistItems.list = 1ユニット × チャンネル数
///   videos.list        = 1ユニット × 新着数（最大チャンネル数と仮定）
/// = 最大 2ユニット × チャンネル数 / チェック
/// </summary>
public static class ApiQuotaHelper
{
    public const int DailyLimit      = 10_000; // ユニット/日
    public const int UnitsPerCheck   = 2;      // playlistItems.list + videos.list
    private const int MinutesPerDay  = 1_440;

    /// <summary>1日の推定消費ユニット数を計算する</summary>
    public static int EstimateDailyUnits(int intervalMinutes, int channelCount)
    {
        if (intervalMinutes <= 0 || channelCount <= 0) return 0;
        var checksPerDay = MinutesPerDay / intervalMinutes;
        return checksPerDay * channelCount * UnitsPerCheck;
    }

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
        var candidates = AppConstants.CheckIntervalCandidates;
        foreach (var candidate in candidates)
        {
            if (EstimateDailyUnitsForChannels(candidate, channelList) <= DailyLimit)
                return (false, candidate);
        }
        return (false, 60);
    }

    /// <summary>後方互換：チャンネル数だけ分かる場合の簡易検証（全チャンネル通常モード仮定）</summary>
    public static (bool safe, int recommendedMinutes) ValidateInterval(
        int intervalMinutes, int channelCount)
    {
        if (channelCount <= 0) return (true, intervalMinutes);

        var daily = EstimateDailyUnits(intervalMinutes, channelCount);
        if (daily <= DailyLimit) return (true, intervalMinutes);

        var maxChecks = DailyLimit / (channelCount * UnitsPerCheck);
        if (maxChecks <= 0) return (false, 60);

        var minInterval = (int)Math.Ceiling((double)MinutesPerDay / maxChecks);
        var candidates   = AppConstants.CheckIntervalCandidates;
        var recommended  = candidates.FirstOrDefault(c => c >= minInterval);
        return (false, recommended == 0 ? 60 : recommended);
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
        switch (mode)
        {
            case YTNotifier.Models.MonitorMode.LowFreq:
                var lowInterval = Math.Max(1, lowFreqIntervalMinutes);
                return (MinutesPerDay / lowInterval) * UnitsPerCheck;

            case YTNotifier.Models.MonitorMode.Focus:
                var window   = focusWindowMinutes;
                var interval = Math.Max(1, focusIntervalMinutes);
                return (window / interval) * UnitsPerCheck;

            default: // Normal
                var globalInterval = Math.Max(1, globalIntervalMinutes);
                return (MinutesPerDay / globalInterval) * UnitsPerCheck;
        }
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

        int total;
        if (baseInterval == int.MaxValue)
        {
            // Normal/LowFreq スロットなし（Focus スロットのみ）
            total = 0;
            foreach (var slot in enabledSlots.Where(s => s.SlotMode == YTNotifier.Models.MonitorMode.Focus))
            {
                var interval   = Math.Max(1, slot.IntervalMinutes);
                int activeDays = slot.Days == 0 ? 7 : CountBits(slot.Days);
                total += (int)Math.Round((slot.WindowMinutes / (double)interval) * UnitsPerCheck * activeDays / 7.0);
            }
        }
        else
        {
            // ベース: 1日中 baseInterval 間隔でチェック
            total = (MinutesPerDay / baseInterval) * UnitsPerCheck;

            // Focus スロット: ウィンドウ内でベースより高頻度な分のみ追加
            foreach (var slot in enabledSlots.Where(s => s.SlotMode == YTNotifier.Models.MonitorMode.Focus))
            {
                var focusInterval = Math.Max(1, slot.IntervalMinutes);
                if (focusInterval >= baseInterval) continue;

                int activeDays     = slot.Days == 0 ? 7 : CountBits(slot.Days);
                var additionalChecks = slot.WindowMinutes / (double)focusInterval
                                     - slot.WindowMinutes / (double)baseInterval;
                total += (int)Math.Round(additionalChecks * UnitsPerCheck * activeDays / 7.0);
            }
        }

        return total;
    }

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
        foreach (var ch in channels.Where(c => c.IsEnabled))
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
    public static int SafeMinInterval(int channelCount)
    {
        var (_, rec) = ValidateInterval(1, channelCount);
        return rec;
    }
}
