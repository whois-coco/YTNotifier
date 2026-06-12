using Google.Apis.Services;
using YTNotifier.Models;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace YTNotifier.Services;

public enum VideoKind { Video, Short, Live, Premiere }

public class VideoInfo
{
    public string    VideoId      { get; set; } = string.Empty;
    public string    Title        { get; set; } = string.Empty;
    public string?   ThumbnailUrl { get; set; }
    public VideoKind Kind         { get; set; } = VideoKind.Video;
    /// <summary>liveBroadcastContent == "upcoming" の時 true（待機所状態）</summary>
    public bool      IsUpcoming   { get; set; } = false;
    public string KindLabel => Kind switch
    {
        VideoKind.Short    => "Short",
        VideoKind.Live     => "ライブ",
        VideoKind.Premiere => "プレミア",
        _                  => "動画"
    };
}

public partial class YouTubeApiClient
{
    private GoogleYouTubeService? _ytService;
    private string _currentApiKey = string.Empty;

    private GoogleYouTubeService GetService()
    {
        var apiKey = SettingsService.Instance.Settings.ApiKey;
        if (_ytService == null || _currentApiKey != apiKey)
        {
            _ytService = new GoogleYouTubeService(new BaseClientService.Initializer
            {
                ApiKey = apiKey,
                ApplicationName = "YTNotifier"
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
        catch { return null; }
    }

    public async Task<ChannelInfo?> FetchChannelInfoAsync(string input)
    {
        var svc = GetService();
        input = input.Trim();
        try
        {
            if (input.StartsWith("@") || (!input.StartsWith("UC") && !input.StartsWith("http")))
            {
                var handle = input.StartsWith("@") ? input : "@" + input;
                var req = svc.Channels.List("snippet,statistics");
                req.ForHandle = handle;
                var resp = await req.ExecuteAsync();
                if (resp.Items?.Count > 0) return MapChannel(resp.Items[0]);
            }
            if (input.StartsWith("UC") && input.Length > 20)
            {
                var req = svc.Channels.List("snippet,statistics");
                req.Id = input;
                var resp = await req.ExecuteAsync();
                if (resp.Items?.Count > 0) return MapChannel(resp.Items[0]);
            }
            if (input.StartsWith("http"))
            {
                var extracted = ExtractFromUrl(input);
                if (extracted != null) return await FetchChannelInfoAsync(extracted);
            }
            return null;
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"チャンネル情報取得失敗: {ex.Message}");
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
        >= 1_000_000 => $"{count / 1_000_000.0:F1}M",
        >= 10_000    => $"{count / 10_000}万",
        >= 1_000     => $"{count / 1000.0:F1}K",
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
                if (s.StartsWith("UC") && s.Length > 20) return s;
            }
        }
        catch { }
        return null;
    }

    // ===== 動画種別を一括判定 =====
    private static async Task<Dictionary<string, (VideoKind Kind, bool IsUpcoming)>> GetVideoKindsAsync(
        GoogleYouTubeService svc, IEnumerable<string> ids)
    {
        var result = new Dictionary<string, (VideoKind Kind, bool IsUpcoming)>();
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
                 kind: await ClassifyVideoAsync(v, svc),
                 isUpcoming: v.Snippet?.LiveBroadcastContent == "upcoming"));
            foreach (var (id, kind, isUpcoming) in await Task.WhenAll(tasks))
                result[id] = (kind, isUpcoming);
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 動画種別判定（フェーズ1〜4）
    ///
    /// フェーズ1: リアルタイム・予定の判定
    ///   liveBroadcastContent == "live"
    ///     concurrentViewers != null → ライブ配信
    ///     concurrentViewers == null → プレミア公開（公開中）
    ///   liveBroadcastContent == "upcoming"
    ///     scheduledEndTime != null  → プレミア公開（公開前）
    ///     scheduledEndTime == null  → ライブ配信（配信前）
    ///
    /// フェーズ2: アーカイブ判定
    ///   liveStreamingDetails != null
    ///     actualEndTime < publishedAt or scheduledEndTime == null → ライブアーカイブ
    ///     それ以外 → プレミア公開（終了後）
    ///
    /// フェーズ3: duration > 180秒 → 通常動画
    ///
    /// フェーズ4: HEAD リクエスト → Short or 通常動画（フォールバック: UUSHプレイリスト）
    /// </summary>
    private static async Task<VideoKind> ClassifyVideoAsync(
        Google.Apis.YouTube.v3.Data.Video v,
        GoogleYouTubeService svc)
    {
        var lbc = v.Snippet?.LiveBroadcastContent;
        var lsd = v.LiveStreamingDetails;

        // ── フェーズ1: リアルタイム・予定 ────────────────────────────
        if (lbc == "live")
        {
            // uploadStatus == "processed" → プレミア公開中（事前アップロード済み）
            // それ以外（"uploaded"等） → ライブ配信中
            return v.Status?.UploadStatus == "processed"
                ? VideoKind.Premiere
                : VideoKind.Live;
        }
        if (lbc == "upcoming")
        {
            // uploadStatus == "processed" → プレミア公開予約（動画ファイル処理済み）
            // それ以外 → ライブ配信予約（ストリームキー待機中）
            return v.Status?.UploadStatus == "processed"
                ? VideoKind.Premiere
                : VideoKind.Live;
        }

        // ── フェーズ2: アーカイブ判定 ─────────────────────────────────
        if (lsd != null)
        {
            var actualEnd    = lsd.ActualEndTimeDateTimeOffset;
            var actualStart  = lsd.ActualStartTimeDateTimeOffset;
            var publishedAt  = v.Snippet?.PublishedAtDateTimeOffset;

            // publishedAt ≒ actualStartTime（差2秒以内）→ プレミア公開
            bool isPremiere = actualStart.HasValue && publishedAt.HasValue
                && Math.Abs((publishedAt.Value - actualStart.Value).TotalSeconds) < 2.0;

            if (isPremiere)
                return VideoKind.Premiere;

            // それ以外でliveStreamingDetailsがある → ライブ配信アーカイブ
            return VideoKind.Live;
        }

        // ── フェーズ3: duration > 180秒 → 通常動画 ───────────────────
        var secs = ParseDurationSeconds(v.ContentDetails?.Duration ?? "");
        if (secs > 180)
            return VideoKind.Video;

        // ── フェーズ4: HEAD リクエスト ────────────────────────────────
        var videoId = v.Id;
        try
        {
            using var handler = new System.Net.Http.HttpClientHandler
                { AllowAutoRedirect = false };
            using var http = new System.Net.Http.HttpClient(handler)
                { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            var url    = $"https://www.youtube.com/shorts/{videoId}";
            var res    = await http.SendAsync(
                new System.Net.Http.HttpRequestMessage(
                    System.Net.Http.HttpMethod.Head, url));
            var status = (int)res.StatusCode;
            if (status == 200)           return VideoKind.Short;
            if (status is 302 or 303)    return VideoKind.Video;
            // 4xx/5xx → フォールバックへ
        }
        catch { /* タイムアウト等 → フォールバックへ */ }

        // ── フォールバック: UUSH プレイリスト ─────────────────────────
        try
        {
            var channelId = v.Snippet?.ChannelId ?? "";
            if (channelId.Length > 2)
            {
                var shortsId = "UUSH" + channelId[2..];
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
        catch { }

        return VideoKind.Video;
    }

    private static double ParseDurationSeconds(string iso)
    {
        try { return System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds; }
        catch { return 0; }
    }

    // ===== 新着チェック（通知用） =====
    public async Task<VideoInfo?> CheckLatestVideoAsync(string channelId, string lastVideoId,
        string uploadsPlaylistId = "", string pendingUpcomingVideoId = "")
    {
        var svc = GetService();

        // 保存済みのプレイリストIDを優先、なければUC→UU変換（フォールバック）
        var playlistId = !string.IsNullOrEmpty(uploadsPlaylistId)
            ? uploadsPlaylistId
            : "UU" + channelId[2..];
        var plReq = svc.PlaylistItems.List("snippet,contentDetails");
        plReq.PlaylistId = playlistId;
        plReq.MaxResults = 5;

        Google.Apis.YouTube.v3.Data.PlaylistItemListResponse plResp;
        try
        {
            plResp = await plReq.ExecuteAsync();
            SettingsService.Instance.AddApiUnits(1); // PlaylistItems.list = 1unit
        }
        catch { return null; }

        if (plResp.Items == null || plResp.Items.Count == 0) return null;

        var items = plResp.Items
            .Where(i => i.ContentDetails?.VideoId != null)
            .ToList();

        if (items.Count == 0) return null;

        // 先頭IDが前回と同じでも、upcoming待ち中なら再判定（live化を検知するため）
        var firstId = items[0].ContentDetails!.VideoId;
        if (firstId == lastVideoId && firstId != pendingUpcomingVideoId) return null;

        // 全IDの種別を一括取得（IsUpcomingも含む）
        var kindMap = await GetVideoKindsAsync(svc,
            items.Select(i => i.ContentDetails!.VideoId));

        foreach (var item in items)
        {
            var vid = item.ContentDetails!.VideoId;
            if (vid == lastVideoId) break;

            var (kind, isUpcoming) = kindMap.TryGetValue(vid, out var kv)
                ? kv : (VideoKind.Video, false);
            var thumb = item.Snippet?.Thumbnails?.Medium?.Url
                        ?? item.Snippet?.Thumbnails?.Default__?.Url;

            return new VideoInfo
            {
                VideoId      = vid,
                Title        = item.Snippet?.Title ?? string.Empty,
                ThumbnailUrl = thumb,
                Kind         = kind,
                IsUpcoming   = isUpcoming
            };
        }

        return null;
    }

    /// <summary>
    /// 新着動画を複数件返す（通知フィルタ対応のため最大件数まで走査）
    /// lastVideoId より新しい動画を新着順で返す
    /// </summary>
    public async Task<List<VideoInfo>> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", string pendingUpcomingVideoId = "",
        int maxResults = 10)
    {
        var result = new List<VideoInfo>();
        var playlistId = !string.IsNullOrEmpty(uploadsPlaylistId)
            ? uploadsPlaylistId : "UU" + channelId[2..];

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
        catch { return result; }

        if (plResp?.Items == null || plResp.Items.Count == 0) return result;

        var items = plResp.Items
            .Where(i => i.ContentDetails?.VideoId != null)
            .ToList();

        if (items.Count == 0) return result;

        // pending upcoming がある場合は同IDでも再判定
        var firstId = items[0].ContentDetails!.VideoId;
        if (firstId == lastVideoId && firstId != pendingUpcomingVideoId)
            return result;

        var kindMap = await GetVideoKindsAsync(svc,
            items.Select(i => i.ContentDetails!.VideoId));

        foreach (var item in items)
        {
            var vid = item.ContentDetails!.VideoId;
            if (vid == lastVideoId) break;

            var (kind, isUpcoming) = kindMap.TryGetValue(vid, out var kv)
                ? kv : (VideoKind.Video, false);
            var thumb = item.Snippet?.Thumbnails?.Medium?.Url
                        ?? item.Snippet?.Thumbnails?.Default__?.Url;

            result.Add(new VideoInfo
            {
                VideoId      = vid,
                Title        = item.Snippet?.Title ?? string.Empty,
                ThumbnailUrl = thumb,
                Kind         = kind,
                IsUpcoming   = isUpcoming
            });
        }
        return result;
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
        catch (Exception ex)
        {
            LoggerService.Instance.Error($"最新動画取得失敗: {ex.Message}");
            return null;
        }
    }
}
