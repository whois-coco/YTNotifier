using Google.Apis.Services;
using YTNotifier.Constants;
using YTNotifier.Models;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace YTNotifier.Services;

public enum VideoKind { Video, Short, Live, Premiere }

/// <summary>APIキー有効性テストの結果</summary>
public enum ApiKeyTestResult { Valid, Invalid, NetworkError }


public static class YouTubeApiConstants
{
    public const int HttpStatusOk            = 200;
    public const int HttpStatusServerError   = 500;
    public const ulong SubscriberMillion     = 1_000_000;
    public const ulong SubscriberManUnit     = 10_000;
    public const ulong SubscriberThousand    = 1_000;
}

public class VideoInfo
{
    public string    VideoId      { get; set; } = string.Empty;
    public string    Title        { get; set; } = string.Empty;
    public string?   ThumbnailUrl { get; set; }
    public VideoKind Kind         { get; set; } = VideoKind.Video;
    /// <summary>liveBroadcastContent == "upcoming" の時 true（待機所状態）</summary>
    public bool      IsUpcoming      { get; set; } = false;
    /// <summary>liveBroadcastContent == "live" の時 true（今まさに配信中）</summary>
    public bool      IsCurrentlyLive { get; set; } = false;
    /// <summary>配信予定時刻（upcoming の場合のみ設定）</summary>
    public DateTime? ScheduledStartTime { get; set; } = null;
    /// <summary>配信実際の開始時刻（配信中・アーカイブの場合に設定）</summary>
    public DateTime? ActualStartTime    { get; set; } = null;
    /// <summary>動画の再生時間（取得できない場合は null）</summary>
    public TimeSpan? Duration    { get; set; } = null;
    public string KindLabel => Kind switch
    {
        VideoKind.Short    => "Short",
        VideoKind.Live     => "ライブ",
        VideoKind.Premiere => "プレミア",
        _                  => "動画"
    };
}

/// <summary>YouTube API の 1 日クォータ上限に達したことを示す例外</summary>
public sealed class QuotaExceededException : Exception
{
    public QuotaExceededException() : base("APIクォータ上限に達しました") { }
}

public partial class YouTubeApiClient : IYouTubeApiClient
{
    private const string ChannelIdPrefix      = "UC";
    private const string UploadPlaylistPrefix = "UU";
    private const string ShortsPlaylistPrefix = "UUSH";
    private const string ShortsUrlBase        = "https://www.youtube.com/shorts/";
    private const string LbcLive              = "live";
    private const string LbcUpcoming          = "upcoming";
    private const string StatusProcessed      = "processed";
    private const string StatusUploaded        = "uploaded";

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
        if ((int)ex.HttpStatusCode >= YouTubeApiConstants.HttpStatusServerError)
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

    // Short 判定の HEAD リクエスト専用クライアント（ソケット枯渇防止のため static 共有）
    private static readonly System.Net.Http.HttpClient _shortCheckClient = CreateShortCheckClient();
    private static System.Net.Http.HttpClient CreateShortCheckClient()
    {
        var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };
        var client  = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return client;
    }

    private GoogleYouTubeService GetService()
    {
        var apiKeys = SettingsService.Instance.Settings.ApiKeys;
        var apiKey = apiKeys.Count > 0 ? apiKeys[0] : string.Empty;
        if (_ytService == null || _currentApiKey != apiKey)
        {
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
            var resp = await req.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1); // channels.list = 1unit
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
            var testService = new GoogleYouTubeService(new BaseClientService.Initializer
            {
                ApiKey = apiKey,
                ApplicationName = AppConstants.AppName
            });
            var req = testService.Channels.List("id");
            req.Id = channelId;
            await req.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1); // channels.list = 1unit
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
            bool isChannelId = input.StartsWith(ChannelIdPrefix) && input.Length == AppConstants.ChannelIdLength;
            bool isUrl       = input.StartsWith("http");
            bool isHandleName = !isHandle && !isChannelId && !isUrl; // "@" なしのハンドル名

            if (isHandle || isHandleName)
            {
                var handle = isHandle ? input : "@" + input;
                var req = svc.Channels.List("snippet,statistics");
                req.ForHandle = handle;
                var resp = await req.ExecuteAsync();
                if (resp.Items?.Count > 0) return MapChannel(resp.Items[0]);
            }
            if (isChannelId)
            {
                var req = svc.Channels.List("snippet,statistics");
                req.Id = input;
                var resp = await req.ExecuteAsync();
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

    private static ChannelInfo MapChannel(Google.Apis.YouTube.v3.Data.Channel ch)
    {
        var subs = ch.Statistics?.SubscriberCount;
        return new ChannelInfo
        {
            ChannelId       = ch.Id,
            ChannelName     = ch.Snippet.Title,
            ChannelHandle   = ch.Snippet.CustomUrl ?? string.Empty,
            ThumbnailUrl    = ch.Snippet.Thumbnails?.Default__?.Url ?? string.Empty,
            SubscriberCount = subs.HasValue ? FormatSubscribers(subs.Value) : "非公開",
        };
    }

    private static string FormatSubscribers(ulong count) => count switch
    {
        >= YouTubeApiConstants.SubscriberMillion  => $"{count / (double)YouTubeApiConstants.SubscriberMillion:F1}M",
        >= YouTubeApiConstants.SubscriberManUnit  => $"{count / YouTubeApiConstants.SubscriberManUnit}万",
        >= YouTubeApiConstants.SubscriberThousand => $"{count / (double)YouTubeApiConstants.SubscriberThousand:F1}K",
        _            => count.ToString()
    };

    private static string? ExtractFromUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            foreach (var seg in uri.Segments)
            {
                var s = seg.TrimEnd('/');
                if (s.StartsWith("@")) return s;
                if (s.StartsWith(ChannelIdPrefix) && s.Length == AppConstants.ChannelIdLength) return s;
            }
        }
        catch { }
        return null;
    }

    // ===== 動画種別を一括判定 =====
    private static async Task<Dictionary<string, (VideoKind Kind, bool IsUpcoming, bool IsCurrentlyLive, DateTime? ScheduledStartTime, DateTime? ActualStartTime, TimeSpan? Duration)>> GetVideoKindsAsync(
        GoogleYouTubeService svc, IEnumerable<string> ids)
    {
        var result = new Dictionary<string, (VideoKind Kind, bool IsUpcoming, bool IsCurrentlyLive, DateTime? ScheduledStartTime, DateTime? ActualStartTime, TimeSpan? Duration)>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;

        try
        {
            var req  = svc.Videos.List("snippet,contentDetails,liveStreamingDetails,status");
            req.Id   = string.Join(",", idList);
            var resp = await req.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1);
            if (resp.Items == null) return result;

            var tasks = resp.Items.Select(async v =>
                (v.Id,
                 kind:             await ClassifyVideoAsync(v, svc, v.Snippet?.ChannelTitle),
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
    /// フェーズ4: HEAD リクエスト → Short or 通常動画（フォールバック: UUSHプレイリスト）
    /// </summary>
    private static async Task<VideoKind> ClassifyVideoAsync(
        Google.Apis.YouTube.v3.Data.Video v,
        GoogleYouTubeService svc,
        string? channelName = null)
    {
        var (kind, complete) = ClassifyVideoPhase123(v);
        if (complete) return kind!.Value;

        // ── フェーズ4: HEAD リクエスト ────────────────────────────────
        var videoId = v.Id;
        try
        {
            var url = ShortsUrlBase + videoId;
            using var res = await _shortCheckClient.SendAsync(
                new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Head, url));
            var status = (int)res.StatusCode;
            if (status == YouTubeApiConstants.HttpStatusOk) return VideoKind.Short;
            if (status is 302 or 303) return VideoKind.Video;
            // 4xx/5xx → フォールバックへ
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.ShortHeadFailed, channelName, videoId, ClassifyNetworkException(ex));
        }

        // ── フォールバック: UUSH プレイリスト ─────────────────────────
        try
        {
            var channelId = v.Snippet?.ChannelId ?? "";
            if (channelId.Length > 2)
            {
                var shortsId = ShortsPlaylistPrefix + channelId[2..];
                var plReq    = svc.PlaylistItems.List("contentDetails");
                plReq.PlaylistId = shortsId;
                plReq.MaxResults  = 50;
                plReq.VideoId     = videoId;
                var plResp = await plReq.ExecuteAsync();
                SettingsService.Instance.AddApiUnits(1);
                if (plResp.Items?.Any(i => i.ContentDetails?.VideoId == videoId) == true)
                    return VideoKind.Short;
            }
        }
        catch (Google.GoogleApiException gex)
        {
            AppLogger.Log(LogMsg.UushFallbackFailed, null, videoId, ClassifyApiException(gex));
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.UushFallbackFailed, null, videoId, ClassifyNetworkException(ex));
        }

        return VideoKind.Video;
    }

    /// <summary>
    /// フェーズ1〜3 の同期判定（単体テスト対象）。
    /// Phase4 が必要な場合は complete=false / kind=null を返す。
    /// </summary>
    public static (VideoKind? kind, bool complete) ClassifyVideoPhase123(
        Google.Apis.YouTube.v3.Data.Video v)
    {
        var lbc = v.Snippet?.LiveBroadcastContent;
        var lsd = v.LiveStreamingDetails;

        // ── フェーズ1: リアルタイム・予定 ──────────────────────────
        if (lbc == LbcLive || lbc == LbcUpcoming)
        {
            var kind = v.Status?.UploadStatus == StatusProcessed
                ? VideoKind.Premiere
                : VideoKind.Live;
            return (kind, true);
        }

        // ── フェーズ2: アーカイブ判定 ────────────────────────────────
        // ライブ: 配信終了後にYouTubeが処理してから公開 → publishedAt > actualEndTime
        // プレミア: 事前アップロード済みでプレミア開始と同時に公開 → publishedAt ≈ actualStartTime
        if (lsd != null)
        {
            if (lsd.ScheduledEndTimeDateTimeOffset.HasValue)
                return (VideoKind.Live, true);

            if (v.Status?.UploadStatus == StatusUploaded)
                return (VideoKind.Live, true);

            var published  = v.Snippet?.PublishedAtDateTimeOffset;
            var actualEnd  = lsd.ActualEndTimeDateTimeOffset;
            if (published.HasValue && actualEnd.HasValue && published.Value > actualEnd.Value)
                return (VideoKind.Live, true);

            return (VideoKind.Premiere, true);
        }

        // ── フェーズ3: duration > 180秒 → 通常動画 ─────────────────
        if (ParseDurationSeconds(v.ContentDetails?.Duration ?? "") > 180)
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
        IReadOnlyDictionary<string, (VideoKind Kind, bool IsUpcoming, bool IsCurrentlyLive, DateTime? ScheduledStartTime, DateTime? ActualStartTime, TimeSpan? Duration)> kindMap)
    {
        var result = new List<VideoInfo>();
        foreach (var item in items)
        {
            var vid = item.ContentDetails?.VideoId;
            if (vid == null) continue;
            if (vid == lastVideoId) break;

            var (kind, isUpcoming, isCurrentlyLive, scheduledStart, actualStart, duration) = kindMap.TryGetValue(vid, out var kv)
                ? kv : (VideoKind.Video, false, false, (DateTime?)null, (DateTime?)null, (TimeSpan?)null);
            var thumb = item.Snippet?.Thumbnails?.Medium?.Url
                        ?? item.Snippet?.Thumbnails?.Default__?.Url;

            result.Add(new VideoInfo
            {
                VideoId            = vid,
                Title              = item.Snippet?.Title ?? string.Empty,
                ThumbnailUrl       = thumb,
                Kind               = kind,
                IsUpcoming         = isUpcoming,
                IsCurrentlyLive    = isCurrentlyLive,
                ScheduledStartTime = scheduledStart,
                ActualStartTime    = actualStart,
                Duration           = duration
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
        var d = duration.Value;
        var totalHours = (int)d.TotalHours;
        return totalHours > 0
            ? $"{totalHours:D2}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes:D2}:{d.Seconds:D2}";
    }

    /// <summary>
    /// 新着動画を複数件返す（通知フィルタ対応のため最大件数まで走査）
    /// lastVideoId より新しい動画を新着順で返す
    /// </summary>
    public async Task<(List<VideoInfo> Videos, List<VideoInfo> PendingTransitioned, List<VideoInfo> AllScanned)> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", IReadOnlyList<string>? pendingUpcomingVideoIds = null,
        int maxResults = 50)
    {
        var empty = (new List<VideoInfo>(), new List<VideoInfo>(), new List<VideoInfo>());
        if (string.IsNullOrEmpty(channelId) || channelId.Length < 2)
        {
            AppLogger.Log(LogMsg.InvalidChannelId, null, channelId);
            return empty;
        }
        var playlistId = !string.IsNullOrEmpty(uploadsPlaylistId)
            ? uploadsPlaylistId : UploadPlaylistPrefix + channelId[2..];

        Google.Apis.YouTube.v3.Data.PlaylistItemListResponse? plResp = null;
        var svc = GetService();
        try
        {
            var plReq = svc.PlaylistItems.List("snippet,contentDetails");
            plReq.PlaylistId = playlistId;
            plReq.MaxResults  = maxResults;
            plResp = await plReq.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1);
        }
        catch (Google.GoogleApiException gex)
        {
            if (IsQuotaExceededError(gex)) throw new QuotaExceededException();
            AppLogger.Log(LogMsg.PlaylistFailed, null, playlistId, ClassifyApiException(gex));
            throw;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.PlaylistFailed, null, playlistId, ClassifyNetworkException(ex));
            throw;
        }

        if (plResp?.Items == null || plResp.Items.Count == 0) return empty;

        var items = plResp.Items
            .Where(i => i.ContentDetails?.VideoId != null)
            .ToList();

        if (items.Count == 0) return empty;

        bool hasPending = pendingUpcomingVideoIds?.Count > 0;
        var firstId = items[0].ContentDetails!.VideoId;

        // 新着なし & pending もない → 即リターン
        if (firstId == lastVideoId && !hasPending)
            return empty;

        var kindMap = await GetVideoKindsAsync(svc, items.Select(i => i.ContentDetails!.VideoId));

        // kindMap にない pending 動画を個別取得（10件超の投稿で押し出された場合）
        if (hasPending)
        {
            var missing = pendingUpcomingVideoIds!.Where(p => !kindMap.ContainsKey(p)).ToList();
            if (missing.Count > 0)
                foreach (var (k, v) in await GetVideoKindsAsync(svc, missing))
                    kindMap[k] = v;
        }

        // スキャン全件リスト（allScanned: kindMap が確定した全アイテム）
        var allScanned = items
            .Where(i => kindMap.ContainsKey(i.ContentDetails!.VideoId!))
            .Select(i =>
            {
                var vid = i.ContentDetails!.VideoId!;
                var kv  = kindMap[vid];
                return new VideoInfo
                {
                    VideoId            = vid,
                    Title              = i.Snippet?.Title ?? string.Empty,
                    ThumbnailUrl       = i.Snippet?.Thumbnails?.Medium?.Url ?? i.Snippet?.Thumbnails?.Default__?.Url,
                    Kind               = kv.Kind,
                    IsCurrentlyLive    = kv.IsCurrentlyLive,
                    IsUpcoming         = kv.IsUpcoming,
                    ScheduledStartTime = kv.ScheduledStartTime,
                    Duration           = kv.Duration
                };
            })
            .ToList();

        // 新着動画リスト（lastVideoId より新しいもの）
        var videos = firstId != lastVideoId
            ? BuildVideoInfoList(items, lastVideoId, kindMap)
            : new List<VideoInfo>();

        // pending 遷移チェック: upcoming → live/active になったものだけ返す
        var pendingTransitioned = new List<VideoInfo>();
        if (hasPending)
        {
            var itemMap = items.ToDictionary(i => i.ContentDetails!.VideoId!, i => i);
            foreach (var pendingId in pendingUpcomingVideoIds!)
            {
                if (videos.Any(v => v.VideoId == pendingId)) continue; // 新着に含まれている
                if (!kindMap.TryGetValue(pendingId, out var kv) || kv.IsUpcoming) continue;
                if (!itemMap.TryGetValue(pendingId, out var pi)) continue; // 取得範囲外
                var thumb = pi.Snippet?.Thumbnails?.Medium?.Url ?? pi.Snippet?.Thumbnails?.Default__?.Url;
                pendingTransitioned.Add(new VideoInfo
                {
                    VideoId            = pendingId,
                    Title              = pi.Snippet?.Title ?? string.Empty,
                    ThumbnailUrl       = thumb,
                    Kind               = kv.Kind,
                    IsUpcoming         = false,
                    IsCurrentlyLive    = kv.IsCurrentlyLive,
                    ScheduledStartTime = kv.ScheduledStartTime,
                    ActualStartTime    = kv.ActualStartTime,
                    Duration           = kv.Duration
                });
            }
        }

        return (videos, pendingTransitioned, allScanned);
    }

    // ===== クリック用: 有効な種別の中で最新のIDを取得 =====
    // notifyVideo/notifyShort/notifyLive の組み合わせに従い
    // 有効な種別の中で最も新しい動画IDを返す
    public async Task<(string? videoId, VideoKind kind)?> FetchLatestAllowedVideoAsync(
        string channelId, bool allowVideo, bool allowShort, bool allowLive,
        string uploadsPlaylistId = "")
    {
        var svc = GetService();
        try
        {
            var playlistId = !string.IsNullOrEmpty(uploadsPlaylistId)
                ? uploadsPlaylistId
                : "UU" + channelId[2..];
            var plReq = svc.PlaylistItems.List("snippet,contentDetails");
            plReq.PlaylistId = playlistId;
            plReq.MaxResults = 25;

            var plResp = await plReq.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1);
            if (plResp.Items == null) return null;

            var videoIds = plResp.Items
                .Where(i => i.ContentDetails?.VideoId != null)
                .Select(i => i.ContentDetails!.VideoId)
                .Distinct()
                .ToList();

            if (videoIds.Count == 0) return null;

            var kindMap = await GetVideoKindsAsync(svc, videoIds);

            // 投稿順（新→旧）に走査して有効な種別の最初のものを返す
            foreach (var id in videoIds)
            {
                var kind = kindMap.TryGetValue(id, out var kv) ? kv.Kind : VideoKind.Video;
                bool allowed = kind switch
                {
                    VideoKind.Video    => allowVideo,
                    VideoKind.Short    => allowShort,
                    VideoKind.Live     => allowLive,
                    VideoKind.Premiere => allowVideo,  // プレミアは動画フィルタに準拠
                    _               => false
                };
                if (allowed)
                    return (id, kind);
            }
            return null;
        }
        catch (QuotaExceededException)
        {
            return null;
        }
        catch (Google.GoogleApiException gex)
        {
            AppLogger.Log(LogMsg.LatestVideoFailed, null, ClassifyApiException(gex));
            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.LatestVideoFailed, null, ClassifyNetworkException(ex));
            return null;
        }
    }
    // ===== 配信終了チェック =====
    public async Task<Dictionary<string, DateTime?>> GetActualEndTimesAsync(IEnumerable<string> videoIds)
    {
        var result = new Dictionary<string, DateTime?>();
        var idList = videoIds.Distinct().ToList();
        if (idList.Count == 0) return result;
        try
        {
            var svc = GetService();
            var req = svc.Videos.List("liveStreamingDetails");
            req.Id  = string.Join(",", idList);
            var resp = await req.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1);
            if (resp.Items == null) return result;
            foreach (var item in resp.Items)
                result[item.Id] = item.LiveStreamingDetails?.ActualEndTimeDateTimeOffset?.DateTime;
        }
        catch (Google.GoogleApiException gex) { AppLogger.Log(LogMsg.CheckFailed, null, "ActiveLives", ClassifyApiException(gex)); }
        catch (Exception ex)                  { AppLogger.Log(LogMsg.CheckFailed, null, "ActiveLives", ClassifyNetworkException(ex)); }
        return result;
    }
}

