using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: BAN／自主削除の存在確認
public partial class MonitorService
{
    /// <summary>BAN中チャンネルの自動存在確認の間隔（分）。日次（最大24時間）より短く、実例の復旧ラグ（約5時間）より十分短い</summary>
    private const int BannedChannelRecheckIntervalMinutes = 60;

    private DateTime      _lastBannedRecheckAt = DateTime.MinValue;

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
            if (ApplyBanResult(channel, isBanned)) changed = true;
        }

        if (!changed)
            AppLogger.Log(allAliveMsg);
        AppLogger.Log(completedMsg);
    }

    /// <summary>監視中のBAN中チャンネルだけを対象に、一定間隔で存在確認を行い復活を検知する。開始／完了ログは出さない。</summary>
    private async Task RecheckBannedChannelsAsync(List<ChannelInfo> monitoredChannels, DateTime now)
    {
        if ((now - _lastBannedRecheckAt).TotalMinutes < BannedChannelRecheckIntervalMinutes) return;
        _lastBannedRecheckAt = now;

        var targets = monitoredChannels
            .Where(c => c.State.IsBanned && string.IsNullOrEmpty(c.TestDataPath))
            .ToList();
        if (targets.Count == 0) return;

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

        foreach (var channel in targets)
        {
            if (!banResults.TryGetValue(channel.ChannelId, out var isBanned)) continue;
            ApplyBanResult(channel, isBanned);
        }
    }

    /// <summary>存在確認の判定結果をチャンネルへ反映する。状態が変化した場合のみ保存・通知・ログを行い true を返す。</summary>
    private bool ApplyBanResult(ChannelInfo channel, bool isBanned)
    {
        if (isBanned == channel.State.IsBanned) return false;

        channel.State.IsBanned = isBanned;
        SettingsService.Instance.Channels.UpdateChannelSilent(channel);
        ChannelUpdated?.Invoke();
        AppLogger.Log(isBanned ? LogMsg.ChannelBanned : LogMsg.ChannelBanRecovered, channel.ChannelName);

        if (!isBanned && SettingsService.Instance.Settings.ShowDesktopNotification)
        {
            NotificationService.ShowChannelRecoveredNotification(channel.ChannelName, channel.ChannelId);
        }

        return true;
    }

    /// <summary>巡回中に404を受けたチャンネル1件だけ存在確認し、BANなら確定して true を返す。存在する・判定不能は false。</summary>
    private async Task<bool> ConfirmChannelBannedAsync(ChannelInfo channel)
    {
        var banResults = await _youtubeClient.CheckChannelsBannedAsync(new[] { channel.ChannelId });
        if (!banResults.TryGetValue(channel.ChannelId, out var isBanned) || !isBanned) return false;

        ApplyBanResult(channel, true);
        return true;
    }
}
