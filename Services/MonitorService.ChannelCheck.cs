using YTNotifier.Models;

namespace YTNotifier.Services;

// 部分クラス: 巡回1件の処理（取得・結果の反映・カーソル更新）
public partial class MonitorService
{
    private const int GracePeriodIntervalSeconds = 30;

    /// <summary>pending ライブ/プレミアエントリの破棄閾値（日数）。ScheduledAt がこの日数以上前のエントリは起動時に削除する</summary>
    private const int StalePendingEntryDays = 14;

    private async Task CheckChannelAsync(ChannelInfo channel)
    {
        bool quotaExceeded = false;
        try
        {
            channel.State.LastCheckedAt = DateTime.Now;

            // このチェックで消費する API ユニットの計上カテゴリ（既定＝通常巡回）。
            // PrepareChannelAsync 内の初回 Channels.list はこの時点の値（Normal）で計上される。
            UsageStatsStore.CurrentApiUnitCategory = ApiUnitCategory.Normal;

            var debugSvc = !string.IsNullOrEmpty(channel.TestDataPath)
                ? DebugServiceLoader.GetService()
                : null;

            ChannelScanResult scanResult;
            IReadOnlyList<string> trackedVideoIds;

            if (debugSvc != null)
            {
                ActivateGracePeriods(channel);
                scanResult = BuildDebugScanResult(channel, debugSvc);
                trackedVideoIds = new List<string>();
            }
            else
            {
                await PrepareChannelAsync(channel);
                ActivateGracePeriods(channel);

                // 計上カテゴリの代入は async メソッドの内側では呼び出し元に反映されないため、ここ（本体）で行う。
                (trackedVideoIds, var trackedUnitCategory) = CollectTrackedVideoIds(channel);
                if (trackedUnitCategory.HasValue)
                    UsageStatsStore.CurrentApiUnitCategory = trackedUnitCategory.Value;

                scanResult = await _youtubeClient.CheckLatestVideosAsync(
                    channel.ChannelId, channel.State.LastCheckedVideoId,
                    channel.State.UploadsPlaylistId, trackedVideoIds,
                    lastVideoPublishedAt: channel.State.LastCheckedVideoPublishedAt,
                    videoKindCache: channel.State.VideoKindCache);
            }

            UpdateRecentUploadsSnapshot(channel, scanResult);

            PruneVideoKindCache(channel, scanResult, trackedVideoIds);

            DetectLatestVideoChanges(channel, scanResult);

            DetectNoVideosChange(channel, scanResult);

            if (scanResult.Videos.Count == 0 && scanResult.PendingTransitioned.Count == 0)
            {
                CompleteNoNewVideoCheck(channel, scanResult);
                return;
            }

            if (RebuildActiveVideoLists(channel, scanResult.AllScanned))
                ChannelUpdated?.Invoke();

            UpdateLatestVideoInfo(channel, scanResult.Videos);

            var newCandidates = BuildNewVideoNotifyCandidates(channel, scanResult.Videos);

            AdvanceCursorByNewVideos(channel, scanResult.Videos);

            var (pendingNotifyLive, pendingNotifyPremiere, confirmedTransitions) =
                BuildPendingTransitionCandidates(channel, scanResult.PendingTransitioned, newCandidates);

            AdvanceCursorByConfirmedTransitions(channel, confirmedTransitions, scanResult.Videos, scanResult.AllScanned);

            // 通知送信（await）の前に、進めた巡回カーソルを未保存として印す。
            // 通知送信中に30秒周期の保存が走っても、進んだカーソルが保存されるようにする（終了時の再通知を防ぐ）。
            SettingsService.Instance.Channels.UpdateChannelSilent(channel);

            await SendNotificationsAsync(channel, newCandidates, pendingNotifyLive, pendingNotifyPremiere);
        }
        catch (QuotaExceededException)
        {
            HandleQuotaExceeded();
            quotaExceeded = true;
        }
        catch (PlaylistNotFoundException ex)
        {
            try
            {
                if (!await ConfirmChannelBannedAsync(channel))
                {
                    AppLogger.Log(LogMsg.CheckFailed, channel.ChannelName, ex.Message);
                    NetworkCheckRequested?.Invoke();
                }
            }
            catch (QuotaExceededException)
            {
                HandleQuotaExceeded();
                quotaExceeded = true;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.CheckFailed, channel.ChannelName, ex.Message);
            NetworkCheckRequested?.Invoke();
        }
        finally
        {
            bool hadGrace = TickGracePeriods(channel);
            DateTime? suspended;
            lock (_quotaLock) suspended = _quotaSuspendedUntil;
            // クォータ超過後にリセット時刻到達で _quotaSuspendedUntil が別スレッドにクリアされた場合も
            // 次回リセット時刻まで待機させる（直前のリセット時刻はすでに過ぎているので +1日分を取得）
            if (quotaExceeded && !suspended.HasValue)
                suspended = QuotaResetTimeHelper.GetNextQuotaResetTime();
            RevertExpiredPendingEntries(channel, channel.State.LastCheckedAt);
            channel.State.NextCheckAt = suspended
                ?? (hadGrace ? channel.State.LastCheckedAt.AddSeconds(GracePeriodIntervalSeconds) : CalcNextCheckAt(channel, channel.State.LastCheckedAt));
            // どの経路（正常・早期終了・例外）でも、最後の更新（次回チェック時刻）の後に未保存として印す。
            SettingsService.Instance.Channels.UpdateChannelSilent(channel);
        }
    }

    /// <summary>
    /// 追跡対象の動画ID（待機中・配信中）の一覧と、その状況に応じた API 使用量の計上カテゴリを決めて返す。
    /// 待機中・配信中の動画がなければカテゴリは null（呼び出し側で設定済みの値を維持する）。
    /// 計上カテゴリは非同期フロー単位の値のため、ここでは代入せず呼び出し側（CheckChannelAsync 本体）で代入する。
    /// </summary>
    private static (List<string> TrackedVideoIds, ApiUnitCategory? UnitCategory) CollectTrackedVideoIds(ChannelInfo channel)
    {
        lock (_pendingListLock)
        {
            var trackedVideoIds = channel.State.PendingLives.Select(p => p.VideoId)
                .Concat(channel.State.PendingPremieres.Select(p => p.VideoId))
                .Concat(channel.State.ActiveLives.Select(p => p.VideoId))
                .Concat(channel.State.ActivePremieres.Select(p => p.VideoId))
                .ToList();

            ApiUnitCategory? unitCategory = null;
            if (channel.State.ActiveLives.Count > 0 || channel.State.ActivePremieres.Count > 0)
                unitCategory = ApiUnitCategory.LiveStatus;
            else if (channel.State.PendingLives.Count > 0 || channel.State.PendingPremieres.Count > 0)
                unitCategory = ApiUnitCategory.PendingTrack;

            return (trackedVideoIds, unitCategory);
        }
    }

    /// <summary>
    /// デバッグチャンネルの取得結果を組み立てる。スキャン結果は空、プレイリストは空でない扱い。
    /// </summary>
    private static ChannelScanResult BuildDebugScanResult(ChannelInfo channel, IDebugChannelService debugSvc)
    {
        var (videos, pendingTransitioned) = debugSvc.GetNextCheckResult(channel);
        return new ChannelScanResult(
            videos,
            pendingTransitioned,
            new List<VideoInfo>(),
            new List<VideoInfo>(),
            PlaylistEmpty: false);
    }

    /// <summary>
    /// チャンネルの最新投稿スナップショット（RecentUploads）を更新する。動画時間の追随更新を含む。
    /// </summary>
    private static void UpdateRecentUploadsSnapshot(ChannelInfo channel, ChannelScanResult scanResult)
    {
        // チャンネルの最新投稿スナップショット（RecentUploads）を更新する。
        // allScanned は新着有無に関わらず取得済み・追加API呼び出しなしのため毎回のチェックで実行。
        // 空（デバッグチャンネル、または問い合わせ結果なし）の場合は既存の RecentUploads を維持する。
        if (scanResult.AllScanned.Count > 0)
        {
            channel.State.RecentUploads = scanResult.AllScanned
                .Select(v => new RecentUploadEntry
                {
                    VideoId      = v.VideoId,
                    Title        = v.Title,
                    ThumbnailUrl = v.ThumbnailUrl,
                    Kind         = v.Kind,
                    Duration     = v.Duration,
                    PublishedAt  = v.PublishedAt
                })
                .ToList();

            // 進行中に検出したライブ／プレミアは検出時点の動画時間が未確定（0秒→null）のため
            // LatestDuration が空のまま残る。スキャン結果に確定値が現れたら追随して更新する。
            // 動画時間は不変のため、確定値 → null への巻き戻しは行わない。
            if (!string.IsNullOrEmpty(channel.State.LatestVideoId))
            {
                var latestScanned = scanResult.AllScanned.FirstOrDefault(v => v.VideoId == channel.State.LatestVideoId);
                if (latestScanned?.Duration != null && latestScanned.Duration != channel.State.LatestDuration)
                    channel.State.LatestDuration = latestScanned.Duration;
            }
        }
    }

    /// <summary>
    /// 種別キャッシュの掃除。今回のスキャン結果にも追跡対象にも含まれない動画IDを削除する。
    /// </summary>
    private static void PruneVideoKindCache(ChannelInfo channel, ChannelScanResult scanResult, IReadOnlyList<string> trackedVideoIds)
    {
        // 種別キャッシュから、今回のスキャン結果にも追跡対象にも含まれない動画IDを削除する。
        // allScannedBasic が空（デバッグチャンネル・問い合わせ結果なし）の場合はキャッシュを維持する
        if (scanResult.AllScannedBasic.Count > 0)
        {
            var kindCacheKeepIds = new HashSet<string>(scanResult.AllScannedBasic.Select(v => v.VideoId));
            kindCacheKeepIds.UnionWith(trackedVideoIds);
            foreach (var kindCacheStaleId in channel.State.VideoKindCache.Keys.Where(k => !kindCacheKeepIds.Contains(k)).ToList())
                channel.State.VideoKindCache.TryRemove(kindCacheStaleId, out _);
        }
    }

    /// <summary>
    /// 表示中の最新動画の削除・復帰・改題を検知する。
    /// 復帰時は取得結果の Videos へ新着候補として追加する（後続の新着処理に乗せるための副作用）。
    /// </summary>
    private void DetectLatestVideoChanges(ChannelInfo channel, ChannelScanResult scanResult)
    {
        // 表示中の最新動画が削除・非公開になった/復帰したかの判定
        // allScannedBasic は新着有無に関わらず取得済み・追加API呼び出しなしのため毎回のチェックで実行
        if (!string.IsNullOrEmpty(channel.State.LatestVideoId) && scanResult.AllScannedBasic.Count > 0)
        {
            var scannedEntry = scanResult.AllScannedBasic.FirstOrDefault(v => v.VideoId == channel.State.LatestVideoId);
            if (!channel.State.LatestVideoDeleted && scannedEntry == null)
            {
                channel.State.LatestVideoDeleted = true;
                ChannelUpdated?.Invoke();
                AppLogger.Log(LogMsg.LatestVideoDeletedDetected, channel.ChannelName, channel.State.LatestVideoId);
            }
            else if (channel.State.LatestVideoDeleted && scannedEntry != null && channel.State.LatestKind.HasValue)
            {
                // 動画IDを変えずに再公開されたケース。新着候補に差し込み、既存の新着処理
                // （通知送信・タイトル更新・LatestVideoDeleted 解除・カーソル更新）にそのまま乗せる。
                scanResult.Videos.Add(new VideoInfo
                {
                    VideoId         = scannedEntry.VideoId,
                    Title           = scannedEntry.Title,
                    ThumbnailUrl    = scannedEntry.ThumbnailUrl,
                    PublishedAt     = scannedEntry.PublishedAt,
                    Kind            = channel.State.LatestKind.Value,
                    IsUpcoming      = false,
                    IsCurrentlyLive = false,
                    // 再公開は意図的な再通知のため、通知済み記録との照合を飛ばす
                    SkipDuplicateCheck = true
                });
                AppLogger.Log(LogMsg.LatestVideoRecovered, channel.ChannelName, channel.State.LatestVideoId);
            }
            else if (!channel.State.LatestVideoDeleted && scannedEntry != null
                     && !string.IsNullOrEmpty(scannedEntry.Title) && scannedEntry.Title != channel.State.LatestTitle)
            {
                // 動画IDは同じままYouTube側でタイトルのみ変更されたケース
                var oldTitle = channel.State.LatestTitle;
                channel.State.LatestTitle = scannedEntry.Title;
                ChannelUpdated?.Invoke();
                AppLogger.Log(LogMsg.LatestVideoTitleChanged, channel.ChannelName, channel.State.LatestVideoId, oldTitle!, scannedEntry.Title);
            }
        }
    }

    /// <summary>
    /// 投稿の全消失・復帰を検知する。
    /// </summary>
    private void DetectNoVideosChange(ChannelInfo channel, ChannelScanResult scanResult)
    {
        // 過去に動画があったチャンネルの投稿が全て確認できなくなった/復帰したかの判定
        if (!string.IsNullOrEmpty(channel.State.LastCheckedVideoId))
        {
            if (!channel.State.NoVideosFound && scanResult.PlaylistEmpty)
            {
                channel.State.NoVideosFound = true;
                ChannelUpdated?.Invoke();
                AppLogger.Log(LogMsg.NoVideosDetected, channel.ChannelName);
            }
            else if (channel.State.NoVideosFound && scanResult.AllScannedBasic.Count > 0)
            {
                channel.State.NoVideosFound = false;
                ChannelUpdated?.Invoke();
                AppLogger.Log(LogMsg.NoVideosRecovered, channel.ChannelName);
            }
        }
    }

    /// <summary>
    /// 配信中・公開中リスト（ActiveLives / ActivePremieres）をスキャン結果で組み直す。
    /// リストのクリアと追加は待機リスト用ロック内で行う。組み直し前後で件数が変わったら true。
    /// </summary>
    private static bool RebuildActiveVideoLists(ChannelInfo channel, List<VideoInfo> allScanned)
    {
        // ActiveLives / ActivePremieres をスキャン結果で上書き
        var liveCountBefore     = channel.State.ActiveLives.Count;
        var premiereCountBefore = channel.State.ActivePremieres.Count;
        lock (_pendingListLock)
        {
            channel.State.ActiveLives.Clear();
            channel.State.ActivePremieres.Clear();
            foreach (var scannedVideo in allScanned.Where(v => v.IsCurrentlyLive))
            {
                var entry = new YTNotifier.Models.PendingVideoEntry { VideoId = scannedVideo.VideoId, Title = scannedVideo.Title, ThumbnailUrl = scannedVideo.ThumbnailUrl, ActualStartTime = scannedVideo.ActualStartTime };
                if (scannedVideo.Kind == VideoKind.Live)           channel.State.ActiveLives.Add(entry);
                else if (scannedVideo.Kind == VideoKind.Premiere)  channel.State.ActivePremieres.Add(entry);
            }
        }

        return channel.State.ActiveLives.Count != liveCountBefore || channel.State.ActivePremieres.Count != premiereCountBefore;
    }

    /// <summary>
    /// 新着なし時の完了処理。配信中・公開中リストの件数が変わった場合と、それ以外とで、
    /// イベント発火の有無が異なる（経路A：イベント発火→ログ／経路B：ログのみ）。
    /// </summary>
    private void CompleteNoNewVideoCheck(ChannelInfo channel, ChannelScanResult scanResult)
    {
        // デバッグチャンネルは allScanned が空固定のためバッジ更新をスキップ
        // allScanned が空（問い合わせ結果なし）の場合もスキップ
        var isDebugChannel = !string.IsNullOrEmpty(channel.TestDataPath);
        if (!isDebugChannel && scanResult.AllScanned.Count > 0)
        {
            if (RebuildActiveVideoLists(channel, scanResult.AllScanned))
            {
                ChannelUpdated?.Invoke();
                AppLogger.Log(LogMsg.NoNew, channel.ChannelName);
                return;
            }
        }

        AppLogger.Log(LogMsg.NoNew, channel.ChannelName);
    }

    /// <summary>
    /// 最新動画情報の更新。通知ON種別のうち先頭の非 upcoming 動画を反映する。
    /// </summary>
    private static void UpdateLatestVideoInfo(ChannelInfo channel, List<VideoInfo> videos)
    {
        // 通知ON種別の中でプレイリスト最新の非 upcoming 動画を LatestTitle / LatestKind に保存
        var latestFound = videos
            .Where(v => !v.IsUpcoming)
            .FirstOrDefault(v =>
                (v.Kind == VideoKind.Video    && channel.NotifyVideo) ||
                (v.Kind == VideoKind.Premiere && channel.NotifyVideo) ||
                (v.Kind == VideoKind.Short    && channel.NotifyShort) ||
                (v.Kind == VideoKind.Live     && channel.NotifyLive));
        if (latestFound != null)
        {
            channel.State.LatestTitle    = latestFound.Title;
            channel.State.LatestKind     = latestFound.Kind;
            channel.State.LatestVideoId  = latestFound.VideoId;
            channel.State.LatestDuration = latestFound.Duration;
            channel.State.LatestThumbnailUrl = latestFound.ThumbnailUrl;
            channel.State.LatestVideoDeleted = false;
        }
    }

    /// <summary>
    /// 巡回カーソルの更新。新着動画のうち先頭の非 upcoming 動画まで進める。
    /// </summary>
    private static void AdvanceCursorByNewVideos(ChannelInfo channel, List<VideoInfo> videos)
    {
        // upcoming 動画はカーソルを進めない（公開済み動画が upcoming より古い位置にあっても検出できるよう）
        var cursorVideo = videos.FirstOrDefault(v => !v.IsUpcoming);
        if (cursorVideo != null)
        {
            channel.State.LastCheckedVideoId = cursorVideo.VideoId;
            channel.State.LastCheckedVideoPublishedAt = cursorVideo.PublishedAt;
        }
    }

    /// <summary>
    /// 確認済み遷移動画による巡回カーソルの更新。
    /// </summary>
    private static void AdvanceCursorByConfirmedTransitions(
        ChannelInfo channel, List<VideoInfo> confirmedTransitions, List<VideoInfo> videos, List<VideoInfo> allScanned)
    {
        // ③ 確認済み遷移動画（wasLive/wasPremiereで実在確認済み）の VideoId で LastCheckedVideoId を進める
        // 配信中のライブ/プレミア自身は毎回 pendingTransitioned に含まれ続けるが wasLive/wasPremiere が
        // 既に false（PendingLives/PendingPremieres から削除済み）のため confirmedTransitions には含まれず、
        // カーソルを巻き戻さない（007修正）
        var transitionedIds = confirmedTransitions
            .Where(v => !videos.Any(x => x.VideoId == v.VideoId) && v.VideoId != channel.State.LastCheckedVideoId)
            .Select(v => v.VideoId)
            .ToHashSet();
        if (transitionedIds.Count > 0)
        {
            var best = allScanned.FirstOrDefault(s => transitionedIds.Contains(s.VideoId));
            if (best != null)
            {
                channel.State.LastCheckedVideoId = best.VideoId;
                channel.State.LastCheckedVideoPublishedAt = best.PublishedAt;
            }
        }
    }

    /// <summary>
    /// UploadsPlaylistId の取得と v1→v2 マイグレーションを行う。
    /// </summary>
    private async Task PrepareChannelAsync(ChannelInfo channel)
    {
        if (string.IsNullOrEmpty(channel.State.UploadsPlaylistId))
        {
            var uploadsPlaylistId = await _youtubeClient.GetUploadsPlaylistIdAsync(channel.ChannelId);
            if (!string.IsNullOrEmpty(uploadsPlaylistId))
                channel.State.UploadsPlaylistId = uploadsPlaylistId;
        }

        // 旧スカラーフィールド → PendingLives/Premieres マイグレーション（初回のみ）
        var staleCutoff = DateTime.Now.AddDays(-StalePendingEntryDays);
        lock (_pendingListLock)
        {
            channel.State.PendingLives.RemoveAll(p => !p.ScheduledAt.HasValue || p.ScheduledAt.Value < staleCutoff);
            channel.State.PendingPremieres.RemoveAll(p => !p.ScheduledAt.HasValue || p.ScheduledAt.Value < staleCutoff);

            if (channel.State.PendingLives.Count == 0 && channel.State.NextLiveCheckAt.HasValue
                && !string.IsNullOrEmpty(channel.State.LastLiveId))
            {
                channel.State.PendingLives.Add(new PendingVideoEntry
                {
                    VideoId        = channel.State.LastLiveId,
                    ScheduledAt    = channel.State.NextLiveCheckAt,
                    GraceRemaining = channel.State.LiveGraceRemaining
                });
                channel.State.NextLiveCheckAt    = null;
                channel.State.LiveGraceRemaining = 0;
                channel.State.LastLiveId         = string.Empty;
            }
            if (channel.State.PendingPremieres.Count == 0 && channel.State.NextPremiereCheckAt.HasValue
                && !string.IsNullOrEmpty(channel.State.LastPremiereId))
            {
                channel.State.PendingPremieres.Add(new PendingVideoEntry
                {
                    VideoId     = channel.State.LastPremiereId,
                    ScheduledAt = channel.State.NextPremiereCheckAt
                });
                channel.State.NextPremiereCheckAt = null;
                channel.State.LastPremiereId      = string.Empty;
            }
        }
    }

    /// <summary>
    /// 開始予定時刻を過ぎた pending エントリの猶予期間を起動する。
    /// API 呼び出し前に実行することで、LastCheckedVideoId に吸収済みのエントリにも機能する。
    /// </summary>
    private static void ActivateGracePeriods(ChannelInfo channel)
    {
        lock (_pendingListLock)
        {
            foreach (var entry in channel.State.PendingLives.Concat(channel.State.PendingPremieres))
            {
                if (entry.ScheduledAt.HasValue
                    && entry.ScheduledAt.Value <= DateTime.Now
                    && entry.GraceRemaining == 0)
                {
                    entry.GraceRemaining = GracePeriodAttempts;
                    AppLogger.Log(LogMsg.GracePeriodStarted, channel.ChannelName, entry.VideoId, GracePeriodAttempts);
                }
            }
        }
    }
}
