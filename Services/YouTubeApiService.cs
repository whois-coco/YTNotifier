using Google.Apis.Services;
using YTNotifier.Constants;
using YTNotifier.Models;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace YTNotifier.Services;

/// <summary>YouTube API の 1 日クォータ上限に達したことを示す例外</summary>
public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException() : base("APIクォータ上限に達しました") { }
}

/// <summary>アップロードプレイリストが存在しない（404 playlistNotFound）ことを示す例外</summary>
public sealed class PlaylistNotFoundException : Exception
{
    public PlaylistNotFoundException(Exception inner) : base(inner.Message, inner) { }
}

public class YouTubeApiClient : IYouTubeApiClient
{
    private const string ChannelIdPrefix      = "UC";
    private const string UploadPlaylistPrefix = "UU";
    private const string ShortsPlaylistPrefix = "UUSH";
    private const string ReasonPlaylistNotFound = "playlistNotFound";
    private const int    ShortsCheckMaxResults = 1;
    private const string LbcLive              = "live";
    private const string LbcUpcoming          = "upcoming";
    private const string StatusProcessed      = "processed";
    private const string StatusUploaded        = "uploaded";

    /// <summary>投稿からこの時間が経過するまで、種別判定の結果をキャッシュへ書き込まない（時間）。
    /// UUSHプレイリストへの反映遅れで、投稿直後のショートを通常動画と誤判定した結果が確定するのを防ぐ</summary>
    private const int VideoKindCacheMinAgeHours = 24;

    /// <summary>YouTube チャンネル ID の固定長（UC + 22文字）</summary>
    private const int ChannelIdLength = 24;

    /// <summary>この秒数を超える動画は無条件で通常動画と判定する（YouTube Shortsの上限尺）</summary>
    private const int ShortsMaxDurationSeconds = 180;

    private const int HttpStatusServerError   = 500;
    private const ulong SubscriberMillion     = 1_000_000;
    private const ulong SubscriberManUnit     = 10_000;
    private const ulong SubscriberThousand    = 1_000;

    /// <summary>外部への問い合わせを同時に走らせる本数の上限</summary>
    private const int MaxConcurrentRequests = 8;

    /// <summary>問い合わせ全体で共有する同時実行数の制限。
    /// チャンネル追加ウィンドウ等がこのクラスを別途生成するため static で1つだけ持つ</summary>
    private static readonly SemaphoreSlim _requestLimiter = new(MaxConcurrentRequests, MaxConcurrentRequests);

    /// <summary>同時実行数の制限を取得したうえで問い合わせを1本実行する。
    /// 制限を取得したまま別の問い合わせの完了を待つ（入れ子にする）ことは禁止</summary>
    private static async Task<T> ThrottleAsync<T>(Func<Task<T>> request)
    {
        await _requestLimiter.WaitAsync().ConfigureAwait(false);
        try
        {
            return await request().ConfigureAwait(false);
        }
        finally
        {
            _requestLimiter.Release();
        }
    }

    private static bool IsQuotaExceededError(Google.GoogleApiException ex)
    {
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason ?? "";
        return reason is "quotaExceeded" or "dailyLimitExceeded";
    }

    private static string ClassifyApiException(Google.GoogleApiException ex)
    {
        var reason = ex.Error?.Errors?.FirstOrDefault()?.Reason ?? "";
        if (reason is "quotaExceeded" or "dailyLimitExceeded")
            return "APIクォータ上限に達しました（本日の残り枠が不足しています）";
        if (reason is "keyInvalid" or "forbidden" || (int)ex.HttpStatusCode == 403)
            return $"APIキーが無効または権限がありません（{reason})";
        if ((int)ex.HttpStatusCode >= HttpStatusServerError)
            return $"YouTube サーバーエラー（HTTP {(int)ex.HttpStatusCode}）";
        return $"YouTube API エラー（HTTP {(int)ex.HttpStatusCode}: {ex.Message}）";
    }

    private static string ClassifyNetworkException(Exception ex) => ex switch
    {
        TaskCanceledException  => "リクエストがタイムアウトしました",
        System.Net.Http.HttpRequestException => $"ネットワークエラー: {ex.Message}",
        _ => ex.Message
    };

    private GoogleYouTubeService? _ytService;
    private string _currentApiKey = string.Empty;

    private GoogleYouTubeService GetService()
    {
        var apiKey = SettingsService.Instance.Settings.ApiKey;
        if (_ytService == null || _currentApiKey != apiKey)
        {
            // 差し替え前の通信オブジェクトを破棄する（破棄で例外が出ても新規生成は止めない）
            try { _ytService?.Dispose(); }
            catch { }

            _ytService = new GoogleYouTubeService(new BaseClientService.Initializer
            {
                ApiKey = apiKey,
                ApplicationName = AppConstants.AppName
            });
            _currentApiKey = apiKey;
        }
        return _ytService;
    }

    // ===== チャンネル情報取得 =====

    /// <summary>
    /// channels.list からアップロードプレイリストIDを取得する（1ユニット）
    /// UC→UU変換より確実（トピックチャンネル対応）
    /// </summary>
    public async Task<string?> GetUploadsPlaylistIdAsync(string channelId)
    {
        try
        {
            var svc = GetService();
            var req = svc.Channels.List("contentDetails");
            req.Id = channelId;
            var resp = await ThrottleAsync(() => req.ExecuteAsync());
            SettingsService.Instance.UsageStats.AddApiUnits(1); // channels.list = 1unit
            return resp.Items?.FirstOrDefault()?
                .ContentDetails?.RelatedPlaylists?.Uploads;
        }
        catch (Google.GoogleApiException gex)
        {
            AppLogger.Log(LogMsg.UploadsPlaylistFailed, null, channelId, ClassifyApiException(gex));
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.UploadsPlaylistFailed, null, channelId, ClassifyNetworkException(ex));
            return null;
        }
    }

    /// <summary>
    /// 指定されたAPIキーの有効性を channels.list（1ユニット）でテストする
    /// 初回設定ウィザードなど、まだ設定に保存されていないキーを検証する用途
    /// </summary>
    public async Task<ApiKeyTestResult> TestApiKeyAsync(string apiKey, string channelId)
    {
        try
        {
            using var testService = new GoogleYouTubeService(new BaseClientService.Initializer
            {
                ApiKey = apiKey,
                ApplicationName = AppConstants.AppName
            });
            var req = testService.Channels.List("id");
            req.Id = channelId;
            await ThrottleAsync(() => req.ExecuteAsync());
            SettingsService.Instance.UsageStats.AddApiUnits(1); // channels.list = 1unit
            return ApiKeyTestResult.Valid;
        }
        catch (Google.GoogleApiException)
        {
            return ApiKeyTestResult.Invalid;
        }
        catch (Exception)
        {
            return ApiKeyTestResult.NetworkError;
        }
    }

    public async Task<ChannelInfo?> FetchChannelInfoAsync(string input)
    {
        var svc = GetService();
        input = input.Trim();
        try
        {
            bool isHandle    = input.StartsWith("@");
            bool isChannelId = input.StartsWith(ChannelIdPrefix) && input.Length == ChannelIdLength;
            bool isUrl       = input.StartsWith("http");
            bool isHandleName = !isHandle && !isChannelId && !isUrl; // "@" なしのハンドル名

            if (isHandle || isHandleName)
            {
                var handle = isHandle ? input : "@" + input;
                var req = svc.Channels.List("snippet,statistics");
                req.ForHandle = handle;
                var resp = await ThrottleAsync(() => req.ExecuteAsync());
                SettingsService.Instance.UsageStats.AddApiUnits(1); // channels.list = 1unit
                if (resp.Items?.Count > 0) return MapChannel(resp.Items[0]);
            }
            if (isChannelId)
            {
                var req = svc.Channels.List("snippet,statistics");
                req.Id = input;
                var resp = await ThrottleAsync(() => req.ExecuteAsync());
                SettingsService.Instance.UsageStats.AddApiUnits(1); // channels.list = 1unit
                if (resp.Items?.Count > 0) return MapChannel(resp.Items[0]);
            }
            if (isUrl)
            {
                var extracted = ExtractFromUrl(input);
                if (extracted != null) return await FetchChannelInfoAsync(extracted);
            }
            return null;
        }
        catch (Google.GoogleApiException gex)
        {
            AppLogger.Log(LogMsg.ChannelInfoFailed, null, ClassifyApiException(gex));
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.ChannelInfoFailed, null, ClassifyNetworkException(ex));
            throw;
        }
    }

    private static ChannelInfo? MapChannel(Google.Apis.YouTube.v3.Data.Channel ch)
    {
        if (ch.Snippet == null) return null;   // snippet 欠落の異常応答は取得失敗（=見つからない）として扱う

        var subscriberCount = ch.Statistics?.SubscriberCount;
        return new ChannelInfo
        {
            ChannelId       = ch.Id,
            ChannelName     = ch.Snippet.Title,
            ChannelHandle   = ch.Snippet.CustomUrl ?? string.Empty,
            ThumbnailUrl    = ch.Snippet.Thumbnails?.Default__?.Url ?? string.Empty,
            SubscriberCount = subscriberCount.HasValue ? FormatSubscribers(subscriberCount.Value) : "非公開",
        };
    }

    private static string FormatSubscribers(ulong count) => count switch
    {
        >= SubscriberMillion  => $"{count / (double)SubscriberMillion:F1}M",
        >= SubscriberManUnit  => $"{count / SubscriberManUnit}万",
        >= SubscriberThousand => $"{count / (double)SubscriberThousand:F1}K",
        _            => count.ToString()
    };

    private static string? ExtractFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            foreach (var seg in uri.Segments)
            {
                var segmentText = seg.TrimEnd('/');
                if (segmentText.StartsWith("@")) return segmentText;
                if (segmentText.StartsWith(ChannelIdPrefix) && segmentText.Length == ChannelIdLength) return segmentText;
            }
        }
        catch { }
        return null;
    }

    // ===== 動画種別を一括判定 =====
    private static async Task<Dictionary<string, (VideoKind Kind, bool IsUpcoming, bool IsCurrentlyLive, DateTime? ScheduledStartTime, DateTime? ActualStartTime, TimeSpan? Duration)>> GetVideoKindsAsync(
        GoogleYouTubeService svc, IEnumerable<string> ids,
        System.Collections.Concurrent.ConcurrentDictionary<string, VideoKind>? videoKindCache = null)
    {
        var result = new Dictionary<string, (VideoKind Kind, bool IsUpcoming, bool IsCurrentlyLive, DateTime? ScheduledStartTime, DateTime? ActualStartTime, TimeSpan? Duration)>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;

        try
        {
            var req  = svc.Videos.List("snippet,contentDetails,liveStreamingDetails,status");
            req.Id   = string.Join(",", idList);
            var resp = await ThrottleAsync(() => req.ExecuteAsync());
            SettingsService.Instance.UsageStats.AddApiUnits(1);
            if (resp.Items == null) return result;

            var tasks = resp.Items.Select(async v =>
                (v.Id,
                 kind:             await ClassifyVideoAsync(v, svc, videoKindCache),
                 isUpcoming:       v.Snippet?.LiveBroadcastContent == LbcUpcoming,
                 isCurrentlyLive:  v.Snippet?.LiveBroadcastContent == LbcLive,
                 scheduledStart:   v.LiveStreamingDetails?.ScheduledStartTimeDateTimeOffset?.DateTime,
                 actualStart:      v.LiveStreamingDetails?.ActualStartTimeDateTimeOffset?.LocalDateTime,
                 duration:         ParseDuration(v.ContentDetails?.Duration ?? "")));
            foreach (var (id, kind, isUpcoming, isCurrentlyLive, scheduledStart, actualStart, duration) in await Task.WhenAll(tasks))
                result[id] = (kind, isUpcoming, isCurrentlyLive, scheduledStart, actualStart, duration);
        }
        catch (Google.GoogleApiException gex)
        {
            if (IsQuotaExceededError(gex)) throw new QuotaExceededException();
            AppLogger.Log(LogMsg.VideoKindFailed, null, ClassifyApiException(gex));
            throw;
        }
        catch (QuotaExceededException) { throw; }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.VideoKindFailed, null, ClassifyNetworkException(ex));
            throw;
        }

        return result;
    }

    /// <summary>
    /// 動画種別判定（フェーズ1〜4）
    ///
    /// フェーズ1: リアルタイム・予定の判定
    ///   liveBroadcastContent == "live" / "upcoming"
    ///     uploadStatus == "processed" → プレミア公開
    ///     それ以外                    → ライブ配信
    ///
    /// フェーズ2: アーカイブ判定
    ///   liveStreamingDetails != null
    ///     scheduledEndTime あり                              → ライブ配信アーカイブ
    ///     scheduledEndTime なし かつ publishedAt > actualEndTime → ライブ配信アーカイブ（処理後公開）
    ///     それ以外                                           → プレミア公開
    ///
    /// フェーズ3: duration > 180秒 → 通常動画
    ///
    /// フェーズ4: 無条件でUUプレイリストへの存在確認
    /// </summary>
    private static async Task<VideoKind> ClassifyVideoAsync(
        Google.Apis.YouTube.v3.Data.Video video,
        GoogleYouTubeService svc,
        System.Collections.Concurrent.ConcurrentDictionary<string, VideoKind>? videoKindCache = null)
    {
        var (kind, complete) = ClassifyVideoPhase123(video);
        if (complete) return kind!.Value;

        var videoId = video.Id;

        // フェーズ4は動画1本につき1ユニットを消費する。種別は一度確定すれば変わらないため、
        // 確定済みの結果があれば問い合わせを省略する
        if (videoKindCache != null && videoKindCache.TryGetValue(videoId, out var cachedKind))
            return cachedKind;

        // 投稿直後はUUSHプレイリストへの反映が遅れることがあり、ショートを通常動画と誤判定しうる。
        // 一定時間が経過するまでは判定結果を確定させない（毎回判定し直して自己修復させる）
        var videoPublishedAt = video.Snippet?.PublishedAtDateTimeOffset;
        var isKindCacheable  = videoKindCache != null
                               && videoPublishedAt.HasValue
                               && videoPublishedAt.Value <= DateTimeOffset.UtcNow.AddHours(-VideoKindCacheMinAgeHours);

        // ── UUSH（Short専用）プレイリストへの存在確認 ─────────────────
        try
        {
            var channelId = video.Snippet?.ChannelId ?? "";
            if (channelId.Length > ChannelIdPrefix.Length)
            {
                var shortsId = ShortsPlaylistPrefix + channelId[ChannelIdPrefix.Length..];
                var plReq    = svc.PlaylistItems.List("id");
                plReq.PlaylistId = shortsId;
                plReq.MaxResults  = ShortsCheckMaxResults;
                plReq.VideoId     = videoId;
                var plResp = await ThrottleAsync(() => plReq.ExecuteAsync());
                SettingsService.Instance.UsageStats.AddApiUnits(1);
                var resolvedKind = plResp.Items?.Count > 0 ? VideoKind.Short : VideoKind.Video;
                if (isKindCacheable) videoKindCache![videoId] = resolvedKind;
                return resolvedKind;
            }
        }
        catch (Google.GoogleApiException gex)
        {
            if (gex.Error?.Errors?.FirstOrDefault()?.Reason == ReasonPlaylistNotFound)
            {
                SettingsService.Instance.UsageStats.AddApiUnits(1);
                if (isKindCacheable) videoKindCache![videoId] = VideoKind.Video;
                return VideoKind.Video;
            }
            AppLogger.Log(LogMsg.UushFallbackFailed, null, videoId, ClassifyApiException(gex));
            SettingsService.Instance.UsageStats.AddApiUnits(1);
            return VideoKind.Video;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.UushFallbackFailed, null, videoId, ClassifyNetworkException(ex));
            SettingsService.Instance.UsageStats.AddApiUnits(1);
            return VideoKind.Video;
        }

        return VideoKind.Video;
    }

    /// <summary>
    /// フェーズ1〜3 の同期判定（単体テスト対象）。
    /// Phase4 が必要な場合は complete=false / kind=null を返す。
    /// </summary>
    public static (VideoKind? kind, bool complete) ClassifyVideoPhase123(
        Google.Apis.YouTube.v3.Data.Video video)
    {
        var liveBroadcastContent = video.Snippet?.LiveBroadcastContent;
        var liveStreamingDetails = video.LiveStreamingDetails;

        // ── フェーズ1: リアルタイム・予定 ──────────────────────────
        if (liveBroadcastContent == LbcLive || liveBroadcastContent == LbcUpcoming)
        {
            var kind = video.Status?.UploadStatus == StatusProcessed
                ? VideoKind.Premiere
                : VideoKind.Live;
            return (kind, true);
        }

        // ── フェーズ2: アーカイブ判定 ────────────────────────────────
        // ライブ: 配信終了後にYouTubeが処理してから公開 → publishedAt > actualEndTime
        // プレミア: 事前アップロード済みでプレミア開始と同時に公開 → publishedAt ≈ actualStartTime
        if (liveStreamingDetails != null)
        {
            if (liveStreamingDetails.ScheduledEndTimeDateTimeOffset.HasValue)
                return (VideoKind.Live, true);

            if (video.Status?.UploadStatus == StatusUploaded)
                return (VideoKind.Live, true);

            var published  = video.Snippet?.PublishedAtDateTimeOffset;
            var actualEnd  = liveStreamingDetails.ActualEndTimeDateTimeOffset;
            if (published.HasValue && actualEnd.HasValue && published.Value > actualEnd.Value)
                return (VideoKind.Live, true);

            return (VideoKind.Premiere, true);
        }

        // ── フェーズ3: duration > 180秒 → 通常動画 ─────────────────
        if (ParseDurationSeconds(video.ContentDetails?.Duration ?? "") > ShortsMaxDurationSeconds)
            return (VideoKind.Video, true);

        return (null, false); // フェーズ4 が必要
    }

    /// <summary>
    /// プレイリストアイテムと種別マップから VideoInfo リストを構築する（単体テスト対象）。
    /// lastVideoId と一致したアイテムで走査を停止する（一致アイテム自体は含まない）。
    /// </summary>
    public static List<VideoInfo> BuildVideoInfoList(
        IEnumerable<Google.Apis.YouTube.v3.Data.PlaylistItem> items,
        string lastVideoId,
        IReadOnlyDictionary<string, (VideoKind Kind, bool IsUpcoming, bool IsCurrentlyLive, DateTime? ScheduledStartTime, DateTime? ActualStartTime, TimeSpan? Duration)> kindMap,
        DateTime? lastVideoPublishedAt = null)
    {
        var result = new List<VideoInfo>();
        foreach (var item in items)
        {
            var videoId = item.ContentDetails?.VideoId;
            if (videoId == null) continue;
            if (videoId == lastVideoId) break;

            var publishedAt = item.Snippet?.PublishedAtDateTimeOffset?.DateTime;
            // カーソルの動画自体が削除・非公開等でプレイリストから消えている場合、
            // ID一致では停止位置を検出できない。投稿日時が基準線（カーソル動画自身の投稿日時）
            // 以前であれば、ID不一致でも既読とみなして停止する（何本連続で消えていても対応可能）。
            if (lastVideoPublishedAt.HasValue && publishedAt.HasValue && publishedAt.Value <= lastVideoPublishedAt.Value)
                break;

            var (kind, isUpcoming, isCurrentlyLive, scheduledStart, actualStart, duration) = kindMap.TryGetValue(videoId, out var kindEntry)
                ? kindEntry : (VideoKind.Video, false, false, (DateTime?)null, (DateTime?)null, (TimeSpan?)null);
            var thumb = item.Snippet?.Thumbnails?.Medium?.Url
                        ?? item.Snippet?.Thumbnails?.Default__?.Url;

            result.Add(new VideoInfo
            {
                VideoId            = videoId,
                Title              = item.Snippet?.Title ?? string.Empty,
                ThumbnailUrl       = thumb,
                Kind               = kind,
                IsUpcoming         = isUpcoming,
                IsCurrentlyLive    = isCurrentlyLive,
                ScheduledStartTime = scheduledStart,
                ActualStartTime    = actualStart,
                Duration           = duration,
                PublishedAt        = publishedAt
            });
        }
        return result;
    }

    public static double ParseDurationSeconds(string iso)
    {
        try { return System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds; }
        catch { return 0; }
    }

    /// <summary>ISO 8601 の動画時間をパースする。取得できない（0秒）場合は null を返す</summary>
    public static TimeSpan? ParseDuration(string iso)
    {
        try
        {
            var span = System.Xml.XmlConvert.ToTimeSpan(iso);
            return span.TotalSeconds > 0 ? span : null;
        }
        catch { return null; }
    }

    /// <summary>動画時間を HH:MM:SS 表記に変換する（1時間未満は MM:SS）</summary>
    public static string? FormatDurationHms(TimeSpan? duration)
    {
        if (duration == null) return null;
        var durationValue = duration.Value;
        var totalHours = (int)durationValue.TotalHours;
        return totalHours > 0
            ? $"{totalHours:D2}:{durationValue.Minutes:D2}:{durationValue.Seconds:D2}"
            : $"{durationValue.Minutes:D2}:{durationValue.Seconds:D2}";
    }

    /// <summary>投稿日時の表示書式（ローカル時刻へ変換して "yyyy/MM/dd HH:mm:ss"）</summary>
    private const string PublishedAtDisplayFormat = "yyyy/MM/dd HH:mm:ss";

    /// <summary>投稿日時（UTC壁時計・Unspecified）をローカル時刻の "yyyy/MM/dd HH:mm:ss" に変換する。null は null。</summary>
    public static string? FormatPublishedAt(DateTime? publishedAt)
    {
        if (publishedAt == null) return null;
        var local = DateTime.SpecifyKind(publishedAt.Value, DateTimeKind.Utc).ToLocalTime();
        return local.ToString(PublishedAtDisplayFormat);
    }

    /// <summary>
    /// 新着動画を複数件返す（通知フィルタ対応のため最大件数まで走査）
    /// lastVideoId より新しい動画を新着順で返す
    /// </summary>
    public async Task<ChannelScanResult> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", IReadOnlyList<string>? pendingUpcomingVideoIds = null,
        int maxResults = 50, DateTime? lastVideoPublishedAt = null,
        System.Collections.Concurrent.ConcurrentDictionary<string, VideoKind>? videoKindCache = null)
    {
        if (string.IsNullOrEmpty(channelId) || channelId.Length < ChannelIdPrefix.Length)
        {
            AppLogger.Log(LogMsg.InvalidChannelId, null, channelId);
            return ChannelScanResult.CreateEmpty();
        }
        var playlistId = !string.IsNullOrEmpty(uploadsPlaylistId)
            ? uploadsPlaylistId : UploadPlaylistPrefix + channelId[ChannelIdPrefix.Length..];

        Google.Apis.YouTube.v3.Data.PlaylistItemListResponse? plResp = null;
        var svc = GetService();
        try
        {
            var plReq = svc.PlaylistItems.List("snippet,contentDetails");
            plReq.PlaylistId = playlistId;
            plReq.MaxResults  = maxResults;
            plResp = await ThrottleAsync(() => plReq.ExecuteAsync());
            SettingsService.Instance.UsageStats.AddApiUnits(1);
        }
        catch (Google.GoogleApiException gex)
        {
            if (IsQuotaExceededError(gex)) throw new QuotaExceededException();
            AppLogger.Log(LogMsg.PlaylistFailed, null, playlistId, ClassifyApiException(gex));
            if (gex.Error?.Errors?.FirstOrDefault()?.Reason == ReasonPlaylistNotFound)
                throw new PlaylistNotFoundException(gex);
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.PlaylistFailed, null, playlistId, ClassifyNetworkException(ex));
            throw;
        }

        if (plResp?.Items == null || plResp.Items.Count == 0) return ChannelScanResult.CreateEmpty(playlistEmpty: true);

        var items = plResp.Items
            .Where(i => i.ContentDetails?.VideoId != null)
            .ToList();

        if (items.Count == 0) return ChannelScanResult.CreateEmpty();

        var allScannedBasic = items.Select(i => new VideoInfo
        {
            VideoId      = i.ContentDetails!.VideoId!,
            Title        = i.Snippet?.Title ?? string.Empty,
            ThumbnailUrl = i.Snippet?.Thumbnails?.Medium?.Url ?? i.Snippet?.Thumbnails?.Default__?.Url,
            PublishedAt  = i.Snippet?.PublishedAtDateTimeOffset?.DateTime
        }).ToList();

        bool hasPending = pendingUpcomingVideoIds?.Count > 0;
        var firstId = items[0].ContentDetails!.VideoId;

        // 新着なし & pending もない → videos/pendingTransitioned/allScanned は空のまま即リターン。
        // allScannedBasic のみ削除・非公開判定/復帰判定用に返す（追加API呼び出しなし）
        if (firstId == lastVideoId && !hasPending)
            return new ChannelScanResult(new List<VideoInfo>(), new List<VideoInfo>(), new List<VideoInfo>(), allScannedBasic, PlaylistEmpty: false);

        var kindMap = await GetVideoKindsAsync(svc, items.Select(i => i.ContentDetails!.VideoId), videoKindCache);

        // kindMap にない pending 動画を個別取得（10件超の投稿で押し出された場合）
        if (hasPending)
        {
            var missing = pendingUpcomingVideoIds!.Where(p => !kindMap.ContainsKey(p)).ToList();
            if (missing.Count > 0)
                foreach (var (kindVideoId, kindInfo) in await GetVideoKindsAsync(svc, missing, videoKindCache))
                    kindMap[kindVideoId] = kindInfo;
        }

        // スキャン全件リスト（allScanned: kindMap が確定した全アイテム）
        var allScanned = items
            .Where(i => kindMap.ContainsKey(i.ContentDetails!.VideoId!))
            .Select(i =>
            {
                var videoId = i.ContentDetails!.VideoId!;
                var kindEntry = kindMap[videoId];
                return new VideoInfo
                {
                    VideoId            = videoId,
                    Title              = i.Snippet?.Title ?? string.Empty,
                    ThumbnailUrl       = i.Snippet?.Thumbnails?.Medium?.Url ?? i.Snippet?.Thumbnails?.Default__?.Url,
                    Kind               = kindEntry.Kind,
                    IsCurrentlyLive    = kindEntry.IsCurrentlyLive,
                    IsUpcoming         = kindEntry.IsUpcoming,
                    ScheduledStartTime = kindEntry.ScheduledStartTime,
                    ActualStartTime    = kindEntry.ActualStartTime,
                    Duration           = kindEntry.Duration,
                    PublishedAt         = i.Snippet?.PublishedAtDateTimeOffset?.DateTime
                };
            })
            .ToList();

        // 新着動画リスト（lastVideoId より新しいもの）
        var videos = firstId != lastVideoId
            ? BuildVideoInfoList(items, lastVideoId, kindMap, lastVideoPublishedAt)
            : new List<VideoInfo>();

        // pending 遷移チェック: upcoming → live/active になったものだけ返す
        var pendingTransitioned = new List<VideoInfo>();
        if (hasPending)
        {
            var itemMap = items.ToDictionary(i => i.ContentDetails!.VideoId!, i => i);
            foreach (var pendingId in pendingUpcomingVideoIds!)
            {
                if (videos.Any(v => v.VideoId == pendingId)) continue; // 新着に含まれている
                if (!kindMap.TryGetValue(pendingId, out var kindEntry) || kindEntry.IsUpcoming) continue;
                if (!itemMap.TryGetValue(pendingId, out var pendingItem)) continue; // 取得範囲外
                var thumb = pendingItem.Snippet?.Thumbnails?.Medium?.Url ?? pendingItem.Snippet?.Thumbnails?.Default__?.Url;
                pendingTransitioned.Add(new VideoInfo
                {
                    VideoId            = pendingId,
                    Title              = pendingItem.Snippet?.Title ?? string.Empty,
                    ThumbnailUrl       = thumb,
                    Kind               = kindEntry.Kind,
                    IsUpcoming         = false,
                    IsCurrentlyLive    = kindEntry.IsCurrentlyLive,
                    ScheduledStartTime = kindEntry.ScheduledStartTime,
                    ActualStartTime    = kindEntry.ActualStartTime,
                    Duration           = kindEntry.Duration
                });
            }
        }

        return new ChannelScanResult(videos, pendingTransitioned, allScanned, allScannedBasic, PlaylistEmpty: false);
    }

    // ===== チャンネルBAN／自主削除判定 =====
    /// <summary>
    /// channels.list(id) で複数チャンネルのBAN／自主削除を一括判定する（最大50件ごとに1ユニット）
    /// 各チャンクの items に含まれるIDは false（生存）、含まれないIDは true（BAN確定）として戻り値に格納する
    /// クォータ超過は QuotaExceededException をスロー。判定不能なチャンクのIDは戻り値の Dictionary に含めない
    /// </summary>
    public async Task<Dictionary<string, bool>> CheckChannelsBannedAsync(IReadOnlyList<string> channelIds)
    {
        var result = new Dictionary<string, bool>();
        for (var offset = 0; offset < channelIds.Count; offset += YouTubeConstants.BanCheckChunkSize)
        {
            var chunk = channelIds.Skip(offset).Take(YouTubeConstants.BanCheckChunkSize).ToList();
            try
            {
                var svc = GetService();
                var req = svc.Channels.List("id");
                req.Id = string.Join(",", chunk);
                var resp = await ThrottleAsync(() => req.ExecuteAsync());
                SettingsService.Instance.UsageStats.AddApiUnits(1); // channels.list = 1unit（複数ID一括指定でも1ユニット）

                var aliveIds = new HashSet<string>(resp.Items?.Select(i => i.Id) ?? Enumerable.Empty<string>());
                foreach (var id in chunk)
                    result[id] = !aliveIds.Contains(id);
            }
            catch (Google.GoogleApiException gex)
            {
                if (IsQuotaExceededError(gex)) throw new QuotaExceededException();
                var reason = gex.Error?.Errors?.FirstOrDefault()?.Reason ?? "";
                if (reason is "keyInvalid" or "forbidden" || (int)gex.HttpStatusCode == 403)
                    continue;
                AppLogger.Log(LogMsg.ChannelBanCheckFailed, null, string.Join(",", chunk), ClassifyApiException(gex));
            }
            catch (Exception ex)
            {
                AppLogger.Log(LogMsg.ChannelBanCheckFailed, null, string.Join(",", chunk), ClassifyNetworkException(ex));
            }
        }
        return result;
    }
}

