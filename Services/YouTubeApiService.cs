using Google.Apis.Services;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace YTNotifier.Services;

public class YouTubeApiClient : IYouTubeApiClient
{
    public static IYouTubeApiClient Instance { get; } = new YouTubeApiClient();

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
    /// 動画種別判定フェーズ1〜3（純粋関数 — 単体テスト対象）。
    /// Complete=false の場合はフェーズ4（外部 HTTP/API）に進む必要がある。
    ///
    /// フェーズ1: liveBroadcastContent == "live" / "upcoming"
    ///   uploadStatus == "processed" → Premiere、それ以外 → Live
    /// フェーズ2: liveStreamingDetails != null
    ///   scheduledEndTime あり → Premiere（プレミア終了後）
    ///   なし → Live（ライブアーカイブ）
    /// フェーズ3: duration > 180秒 → Video
    /// </summary>
    public static (VideoKind? Kind, bool Complete) ClassifyVideoPhase123(
        Google.Apis.YouTube.v3.Data.Video v)
    {
        var lbc = v.Snippet?.LiveBroadcastContent;
        var lsd = v.LiveStreamingDetails;

        if (lbc == "live")
            return (v.Status?.UploadStatus == "processed" ? VideoKind.Premiere : VideoKind.Live, true);
        if (lbc == "upcoming")
            return (v.Status?.UploadStatus == "processed" ? VideoKind.Premiere : VideoKind.Live, true);

        if (lsd != null)
            return (lsd.ScheduledEndTimeDateTimeOffset != null ? VideoKind.Premiere : VideoKind.Live, true);

        var secs = ParseDurationSeconds(v.ContentDetails?.Duration ?? "");
        if (secs > 180)
            return (VideoKind.Video, true);

        return (null, false);
    }

    private static async Task<VideoKind> ClassifyVideoAsync(
        Google.Apis.YouTube.v3.Data.Video v,
        GoogleYouTubeService svc)
    {
        var (kind, complete) = ClassifyVideoPhase123(v);
        if (complete) return kind!.Value;

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

    public static double ParseDurationSeconds(string iso)
    {
        try { return System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds; }
        catch { return 0; }
    }

    /// <summary>
    /// プレイリストアイテムと kindMap から VideoInfo リストを構築する（単体テスト対象）。
    /// lastVideoId と一致した時点で走査を止める（それより古い動画は対象外）。
    /// </summary>
    public static List<VideoInfo> BuildVideoInfoList(
        IEnumerable<Google.Apis.YouTube.v3.Data.PlaylistItem> items,
        string lastVideoId,
        IReadOnlyDictionary<string, (VideoKind Kind, bool IsUpcoming)> kindMap)
    {
        var result = new List<VideoInfo>();
        foreach (var item in items)
        {
            var vid = item.ContentDetails?.VideoId;
            if (vid == null) continue;
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

    /// <summary>
    /// 新着動画と pending プレミア公開の遷移を返す
    /// - videos: lastVideoId より新しい動画リスト（新着順）
    /// - pendingTransitioned: upcoming → live に遷移した pending 動画リスト（空 = 遷移なし）
    /// </summary>
    public async Task<(List<VideoInfo> videos, List<VideoInfo> pendingTransitioned)> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", IReadOnlyList<string>? pendingUpcomingVideoIds = null,
        int maxResults = 10)
    {
        var result = new List<VideoInfo>();
        if (channelId.Length < 2 && string.IsNullOrEmpty(uploadsPlaylistId)) return (result, new());
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
        catch { return (result, new()); }

        if (plResp?.Items == null || plResp.Items.Count == 0) return (result, new());

        var items = plResp.Items
            .Where(i => i.ContentDetails?.VideoId != null)
            .ToList();

        if (items.Count == 0) return (result, new());

        bool hasPending = pendingUpcomingVideoIds?.Count > 0;
        var firstId = items[0].ContentDetails!.VideoId;

        // 新着なし && pending なし → 即リターン
        if (firstId == lastVideoId && !hasPending)
            return (result, new());

        // 新着なし && pending あり → pending の状態だけ確認（1回の videos.list で全ID一括チェック）
        if (firstId == lastVideoId && hasPending)
        {
            var visiblePending = pendingUpcomingVideoIds!
                .Where(id => items.Any(i => i.ContentDetails!.VideoId == id))
                .ToList();
            if (visiblePending.Count == 0) return (result, new());

            var pKindMap = await GetVideoKindsAsync(svc, visiblePending);
            var transitioned = new List<VideoInfo>();
            foreach (var pid in visiblePending)
            {
                if (pKindMap.TryGetValue(pid, out var pkv) && !pkv.IsUpcoming)
                {
                    var pi = items.First(i => i.ContentDetails!.VideoId == pid);
                    var thumb = pi.Snippet?.Thumbnails?.Medium?.Url ?? pi.Snippet?.Thumbnails?.Default__?.Url;
                    transitioned.Add(new VideoInfo
                    {
                        VideoId      = pid,
                        Title        = pi.Snippet?.Title ?? string.Empty,
                        ThumbnailUrl = thumb,
                        Kind         = pkv.Kind,
                        IsUpcoming   = false
                    });
                }
            }
            return (result, transitioned);
        }

        // 新着あり → kindMap を一括構築してメインループ
        var kindMap = await GetVideoKindsAsync(svc, items.Select(i => i.ContentDetails!.VideoId));
        result = BuildVideoInfoList(items, lastVideoId, kindMap);

        // pending が遷移済みかチェック（kindMap に含まれるもの全て一括判定）
        var transitionedList = new List<VideoInfo>();
        if (hasPending)
        {
            foreach (var pid in pendingUpcomingVideoIds!)
            {
                if (kindMap.TryGetValue(pid, out var pkv) && !pkv.IsUpcoming)
                {
                    var pi = items.FirstOrDefault(i => i.ContentDetails!.VideoId == pid);
                    if (pi != null)
                    {
                        var thumb = pi.Snippet?.Thumbnails?.Medium?.Url ?? pi.Snippet?.Thumbnails?.Default__?.Url;
                        transitionedList.Add(new VideoInfo
                        {
                            VideoId      = pid,
                            Title        = pi.Snippet?.Title ?? string.Empty,
                            ThumbnailUrl = thumb,
                            Kind         = pkv.Kind,
                            IsUpcoming   = false
                        });
                    }
                }
            }
        }

        return (result, transitionedList);
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
            if (channelId.Length < 2 && string.IsNullOrEmpty(uploadsPlaylistId)) return null;
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
