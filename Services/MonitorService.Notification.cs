using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: 通知候補の判定と送信
public partial class MonitorService
{
    /// <summary>チャンネルごとの upcoming キュー上限数</summary>
    private const int MaxPendingQueueSize = 10;

    /// <summary>
    /// 通知の送信。送信順は 動画 → Short → ライブ → 待機遷移ライブ → 待機遷移プレミア。
    /// </summary>
    private async Task SendNotificationsAsync(
        ChannelInfo channel, NewVideoNotifyCandidates newCandidates, VideoInfo? pendingNotifyLive, VideoInfo? pendingNotifyPremiere)
    {
        // ===== 通知送信（種別ごとに独立）=====
        // 待機所通知・開始通知はスケジューラーが担当するため、ここでは送らない
        if (newCandidates.Video   != null) await NotifyAsync(channel, newCandidates.Video);
        if (newCandidates.Short   != null) await NotifyAsync(channel, newCandidates.Short);
        foreach (var live in newCandidates.Lives) await NotifyAsync(channel, live);
        if (pendingNotifyLive     != null) await NotifyAsync(channel, pendingNotifyLive);
        if (pendingNotifyPremiere != null) await NotifyAsync(channel, pendingNotifyPremiere);
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
        // カーソル更新（AdvanceCursorByNewVideos の cursorVideo 反映）より前に呼ばれるため、ここで判定して問題ない。
        bool isFirstCheck              = string.IsNullOrEmpty(channel.State.LastCheckedVideoId);
        bool firstViewArchiveNotified  = false;

        foreach (var video in videos)
        {
            switch (video.Kind)
            {
                case VideoKind.Live:
                    if (video.IsUpcoming)
                        QueueUpcomingEntry(channel, channel.State.PendingLives, video);
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
                            channel.State.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

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
                        QueueUpcomingEntry(channel, channel.State.PendingPremieres, video);
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
            channel.State.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

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
            channel.State.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

        if (channel.PremiereUpcomingNotifyMode == YTNotifier.Models.UpcomingNotifyMode.WaitingRoomOnly)
        {
            AppLogger.Log(LogMsg.PremiereStartSkipped, channel.ChannelName, video.Title);
            return null;
        }
        return FilterByKind(channel, video, channel.NotifyVideo);
    }

    /// <summary>タイトルが共通NGワード・チャンネル個別NGワードのいずれかに部分一致するか判定する（大文字小文字は区別しない）。</summary>
    private static bool MatchesNgWord(ChannelInfo channel, string title)
    {
        if (string.IsNullOrEmpty(title)) return false;

        bool ContainsAny(IEnumerable<string> words) =>
            words.Any(w => !string.IsNullOrWhiteSpace(w) && title.Contains(w, StringComparison.OrdinalIgnoreCase));

        return ContainsAny(SettingsService.Instance.Settings.NgWords) || ContainsAny(channel.NgWords);
    }

    /// <summary>通知フィルターを適用し、スキップ時はログを出力する。</summary>
    private static VideoInfo? FilterByKind(ChannelInfo channel, VideoInfo video, bool enabled)
    {
        if (!enabled)
        {
            AppLogger.Log(LogMsg.KindFilterSkipped, channel.ChannelName, video.KindLabel, video.Title);
            return null;
        }
        if (MatchesNgWord(channel, video.Title))
        {
            AppLogger.Log(LogMsg.NgWordFilterSkipped, channel.ChannelName, video.Title);
            return null;
        }
        return video;
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
                wasLive     = channel.State.PendingLives.Any(p => p.VideoId == video.VideoId);
                wasPremiere = channel.State.PendingPremieres.Any(p => p.VideoId == video.VideoId);
            }

            if (wasLive)
            {
                lock (_pendingListLock)
                    channel.State.PendingLives.RemoveAll(p => p.VideoId == video.VideoId);

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
                    channel.State.PendingPremieres.RemoveAll(p => p.VideoId == video.VideoId);

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
        // 強制終了などでカーソルが巻き戻った場合に備え、通知済みの組み合わせはここで弾く
        if (!video.SkipDuplicateCheck
            && NotifiedRecordService.Instance.IsNotified(video.VideoId, video.Kind, video.IsUpcoming))
        {
            AppLogger.Log(LogMsg.NotifyDuplicateSkipped, channel.ChannelName, video.Title);
            return;
        }

        var settings = SettingsService.Instance.Settings;
        var videoUrl = YouTubeConstants.WatchUrlBase + video.VideoId;

        AppLogger.Log(LogMsg.NewVideo, channel.ChannelName, video.KindLabel, video.Title);

        channel.HasUnread = true;
        if (!video.IsUpcoming)
        {
            switch (video.Kind)
            {
                case VideoKind.Video:
                    channel.State.LastVideoTitle      = video.Title;
                    channel.State.LastVideoNotifiedAt = DateTime.Now;
                    break;
                case VideoKind.Short:
                    channel.State.LastShortNotifiedId = video.VideoId;
                    channel.State.LastShortTitle      = video.Title;
                    channel.State.LastShortNotifiedAt = DateTime.Now;
                    break;
                case VideoKind.Live:
                    channel.State.LastLiveNotifiedTitle = video.Title;
                    channel.State.LastLiveNotifiedAt    = DateTime.Now;
                    break;
                case VideoKind.Premiere:
                    channel.State.LastPremiereNotifiedTitle = video.Title;
                    channel.State.LastPremiereNotifiedAt    = DateTime.Now;
                    break;
            }
        }
        SettingsService.Instance.Channels.UpdateChannelSilent(channel);
        ChannelUpdated?.Invoke();

        if (settings.ShowDesktopNotification)
            await NotificationService.ShowVideoNotificationAsync(channel.ChannelName, video.Title, video.KindLabel, videoUrl, channel.ChannelId, video.Kind, channel.ThumbnailUrl, video.ThumbnailUrl);
        else if (settings.NotificationSound)
            NotificationService.PlaySound(video.Kind);

        // トースト・音のどちらの経路も「通知1回」として記録する
        NotifiedRecordService.Instance.Record(video.VideoId, video.Kind, video.IsUpcoming);
    }
}
