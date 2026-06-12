namespace YTNotifier.Services;

/// <summary>通知送信可否の判定ロジック（純粋関数 — 単体テスト対象）</summary>
public static class NotificationFilter
{
    /// <summary>チャンネルの種別フィルター設定に基づいて通知すべきか判定する</summary>
    public static bool IsAllowed(VideoInfo video, ChannelInfo channel)
        => video.Kind switch
        {
            VideoKind.Short    => channel.NotifyShort,
            VideoKind.Live     => channel.NotifyLive,
            VideoKind.Premiere => channel.NotifyVideo,
            _                  => channel.NotifyVideo
        };

    /// <summary>upcoming動画をスキップすべきか判定する（true = スキップ）</summary>
    public static bool ShouldSkipUpcoming(VideoInfo video, ChannelInfo channel, AppSettings settings)
        => video.IsUpcoming && (!settings.GlobalNotifyUpcoming || !channel.NotifyUpcoming);
}
