using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: 手動実行（一括・1件・BAN再確認）
public partial class MonitorService
{
    /// <summary>カード「🔄 最新情報取得」で他チェック完了を待つ最大時間（秒）</summary>
    private const int ChannelManualCheckMaxWaitSeconds = 90;
    /// <summary>上記待機中に _isChecking を再取得しにいく間隔（ミリ秒）</summary>
    private const int ChannelManualCheckPollIntervalMs = 250;

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

    /// <summary>BAN中の1チャンネルの存在を再確認する。解除された場合は続けて動画チェックまで行う。
    /// 他のチェックが実行中の場合は完了を待ってから実行する。</summary>
    public async Task<bool> RecheckBannedChannelAsync(ChannelInfo channel)
    {
        if (channel is null || channel.IsDormant || !channel.State.IsBanned
            || !string.IsNullOrEmpty(channel.TestDataPath)) return false;

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
            Dictionary<string, bool> banResults;
            try
            {
                banResults = await _youtubeClient.CheckChannelsBannedAsync(new[] { channel.ChannelId });
            }
            catch (QuotaExceededException)
            {
                HandleQuotaExceeded();
                return false;
            }

            if (banResults.TryGetValue(channel.ChannelId, out var isBanned)
                && ApplyBanResult(channel, isBanned))
            {
                await CheckChannelAsync(channel);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isChecking, 0);
        }
        return true;
    }
}
