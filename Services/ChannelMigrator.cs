using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 旧形式で保存されたチャンネル一覧を現行形式へ移行する処理の集まり。
/// 状態を持たずロックも取らない（呼び出し側が必要なロックを保持した状態で呼ぶ）。
/// 各メソッドは変更が発生した場合に true を返す。
/// </summary>
internal static class ChannelMigrator
{
    /// <summary>
    /// 期限切れ（GraceRemaining == -1）の猶予エントリを掃除する。
    /// </summary>
    public static bool CleanupExpiredGraceEntries(List<ChannelInfo> channels)
    {
        bool cleaned = false;
        foreach (var ch in channels)
        {
            if (ch.State.PendingLives.RemoveAll(p => p.GraceRemaining == -1) > 0) cleaned = true;
            if (ch.State.PendingPremieres.RemoveAll(p => p.GraceRemaining == -1) > 0) cleaned = true;
        }
        return cleaned;
    }

    /// <summary>
    /// NotifyUpcoming が false（旧デフォルト）のチャンネルを null（グローバルに従う）に変換する。
    /// </summary>
    public static bool MigrateNotifyUpcomingToNullable(List<ChannelInfo> channels)
    {
        bool migrated = false;
        foreach (var ch in channels.Where(c => c.NotifyUpcoming == false))
        {
            ch.NotifyUpcoming = null;
            migrated = true;
        }
        return migrated;
    }

    /// <summary>
    /// NotifyUpcoming（bool?）→ UpcomingNotifyMode への移行。
    /// Settings.UpcomingMigrated が false の場合のみ実行する。
    /// </summary>
    public static bool MigrateUpcomingNotifyMode(List<ChannelInfo> channels, AppSettings settings, Action saveSettings)
    {
        if (settings.UpcomingMigrated) return false;

        bool globalUpcoming = settings.GlobalNotifyUpcoming;
        foreach (var ch in channels)
        {
            var effective = ch.NotifyUpcoming ?? globalUpcoming;
            ch.UpcomingNotifyMode       = effective
                ? Models.UpcomingNotifyMode.WaitingRoomOnly
                : Models.UpcomingNotifyMode.LiveStartOnly;
            ch.UpcomingNotifyLeadMinutes = 0;
            ch.NotifyUpcoming            = null;
        }
        settings.UpcomingMigrated = true;
        saveSettings();
        return true;
    }

    /// <summary>
    /// 共通 UpcomingNotifyMode / UpcomingNotifyLeadMinutes を
    /// Premiere 用・Live 用の別フィールドへ複製する。
    /// Settings.UpcomingSplitMigrated が false の場合のみ実行する。
    /// </summary>
    public static bool MigrateUpcomingNotifySplit(List<ChannelInfo> channels, AppSettings settings, Action saveSettings)
    {
        if (settings.UpcomingSplitMigrated) return false;

        foreach (var ch in channels)
        {
            ch.PremiereUpcomingNotifyMode        = ch.UpcomingNotifyMode;
            ch.PremiereUpcomingNotifyLeadMinutes = ch.UpcomingNotifyLeadMinutes;
            ch.LiveUpcomingNotifyMode            = ch.UpcomingNotifyMode;
            ch.LiveUpcomingNotifyLeadMinutes     = ch.UpcomingNotifyLeadMinutes;
        }
        settings.UpcomingSplitMigrated = true;
        saveSettings();
        return true;
    }

    /// <summary>
    /// 旧来の単一 MonitorMode（Normal/LowFreq/Focus 単一スロット）を
    /// 動画/Short/ライブ別の3スロット形式（FocusSlots）に一括移行する（初回のみ）。
    /// ChannelDetailWindow コンストラクタの変換ロジックと同じ規則を使用。
    /// また、並び順（0=動画/1=Short/2=ライブ）で種別を決めていた旧データは NotifyKind を並び順どおりに補正する。
    /// </summary>
    public static bool MigrateChannelsToFocusSlots(List<ChannelInfo> channels)
    {
        bool migrated = false;
        VideoKind[] defaultKinds = { VideoKind.Video, VideoKind.Short, VideoKind.Live };

        // Days == 0（旧・全曜日）を 127（全ビット）に変換
        foreach (var ch in channels)
        {
            foreach (var slot in ch.FocusSlots.Where(s => s.SlotMode == MonitorMode.Focus && s.Days == 0))
            {
                slot.Days = AppConstants.AllDaysMask;
                migrated = true;
            }
        }

        // 並び順で種別を決めていた旧データ（3件で、いずれかの種別が NotifyKind に含まれない）の NotifyKind を補正
        // 新方式では各種別が必ず1件以上あるため、新方式で保存したデータはこの条件に当たらない
        foreach (var ch in channels)
        {
            if (ch.FocusSlots.Count != AppConstants.KindSlotCount) continue;
            var hasMissingKind = AppConstants.KindSlotKinds.Any(k => ch.FocusSlots.All(s => s.NotifyKind != k));
            if (!hasMissingKind) continue;

            for (int kindSlotIndex = 0; kindSlotIndex < AppConstants.KindSlotCount; kindSlotIndex++)
                ch.FocusSlots[kindSlotIndex].NotifyKind = AppConstants.KindSlotKinds[kindSlotIndex];
            migrated = true;
        }

        foreach (var ch in channels)
        {
            if (ch.FocusSlots.Count > 0) continue;

            bool[] kindEnabled = { ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive };
            var slots = new List<FocusSlot>();
            for (int kindSlotIndex = 0; kindSlotIndex < AppConstants.KindSlotCount; kindSlotIndex++)
            {
                var slot = ch.CreateDefaultFocusSlot(defaultKinds[kindSlotIndex]);
                slot.IsEnabled = kindEnabled[kindSlotIndex];
                slots.Add(slot);
            }

            ch.FocusSlots  = slots;
            ch.MonitorMode = MonitorMode.Focus;
            migrated = true;
        }

        return migrated;
    }
}
