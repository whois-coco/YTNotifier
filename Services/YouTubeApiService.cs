using Google.Apis.Services;
using YTNotifier.Models;
using GoogleYouTubeService = Google.Apis.YouTube.v3.YouTubeService;

namespace YTNotifier.Services;

public enum VideoKind { Video, Short, Live }

public class VideoInfo
{
    public string    VideoId      { get; set; } = string.Empty;
    public string    Title        { get; set; } = string.Empty;
    public string?   ThumbnailUrl { get; set; }
    public VideoKind Kind         { get; set; } = VideoKind.Video;
    public string KindLabel => Kind switch
    {
        VideoKind.Short => "Short",
        VideoKind.Live  => "ライブ",
        _               => "動画"
    };
}

public class YouTubeApiClient
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
    // snippet + contentDetails + liveStreamingDetails の3パートを取得することで
    // ライブアーカイブまで確実に判定できる
    private static async Task<Dictionary<string, VideoKind>> GetVideoKindsAsync(
        GoogleYouTubeService svc, IEnumerable<string> ids)
    {
        var result = new Dictionary<string, VideoKind>();
        var idList = ids.Distinct().ToList();
        if (idList.Count == 0) return result;

        try
        {
            var req  = svc.Videos.List("snippet,contentDetails,liveStreamingDetails");
            req.Id   = string.Join(",", idList);
            var resp = await req.ExecuteAsync();
            if (resp.Items == null) return result;

            foreach (var v in resp.Items)
                result[v.Id] = ClassifyVideo(v);
        }
        catch { }

        return result;
    }

    /// <summary>
    /// 動画種別判定（優先度順）
    ///   1. liveStreamingDetails が存在 → ライブ/アーカイブ
    ///   2. liveBroadcastContent が "live"/"upcoming" → ライブ
    ///   3. duration ≤ 60秒 → Short
    ///   4. それ以外 → 通常動画
    /// </summary>
    private static VideoKind ClassifyVideo(Google.Apis.YouTube.v3.Data.Video v)
    {
        // 1. liveStreamingDetails が存在 → ライブ/アーカイブ
        if (v.LiveStreamingDetails != null)
            return VideoKind.Live;

        // 2. liveBroadcastContent チェック
        var lbc = v.Snippet?.LiveBroadcastContent;
        if (lbc == "live" || lbc == "upcoming")
            return VideoKind.Live;

        var title       = v.Snippet?.Title ?? "";
        var description = v.Snippet?.Description ?? "";
        var tags        = (IEnumerable<string>)(v.Snippet?.Tags ?? []);
        var dur         = v.ContentDetails?.Duration ?? "";
        var secs        = ParseDurationSeconds(dur);

        // 3. Short判定（複合条件）
        //    - 180秒（3分）以内 かつ
        //    - タイトル・説明・タグに #Shorts/#shorts を含む
        //    または
        //    - 60秒以内（明確にShort尺）
        // 2分30秒（150秒）以内は無条件でShort扱い
        if (secs > 0 && secs <= 150)
            return VideoKind.Short;

        return VideoKind.Video;
    }

    private static double ParseDurationSeconds(string iso)
    {
        try { return System.Xml.XmlConvert.ToTimeSpan(iso).TotalSeconds; }
        catch { return 0; }
    }

    // ===== 新着チェック（通知用） =====
    public async Task<VideoInfo?> CheckLatestVideoAsync(string channelId, string lastVideoId)
    {
        var svc = GetService();

        var actReq = svc.Activities.List("snippet,contentDetails");
        actReq.ChannelId  = channelId;
        actReq.MaxResults = 5;
        actReq.PublishedAfterDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-2);

        var actResp = await actReq.ExecuteAsync();
        if (actResp.Items == null || actResp.Items.Count == 0) return null;

        var uploads = actResp.Items
            .Where(a => a.Snippet.Type == "upload" &&
                        a.ContentDetails.Upload?.VideoId != null)
            .OrderByDescending(a => a.Snippet.PublishedAtDateTimeOffset)
            .ToList();

        if (uploads.Count == 0) return null;

        // 先頭IDが前回と同じなら新着なし
        if (uploads[0].ContentDetails.Upload!.VideoId == lastVideoId) return null;

        // 全IDの種別を一括取得
        var kindMap = await GetVideoKindsAsync(svc,
            uploads.Select(u => u.ContentDetails.Upload!.VideoId));

        // 最新から走査し「前回既読IDより新しい最初の動画」を返す
        foreach (var act in uploads)
        {
            var vid  = act.ContentDetails.Upload!.VideoId;
            if (vid == lastVideoId) break;

            var kind  = kindMap.TryGetValue(vid, out var k) ? k : VideoKind.Video;
            var thumb = act.Snippet.Thumbnails?.Medium?.Url
                        ?? act.Snippet.Thumbnails?.Default__?.Url;

            return new VideoInfo
            {
                VideoId      = vid,
                Title        = act.Snippet.Title ?? string.Empty,
                ThumbnailUrl = thumb,
                Kind         = kind
            };
        }

        return null;
    }

    // ===== クリック用: 有効な種別の中で最新のIDを取得 =====
    // notifyVideo/notifyShort/notifyLive の組み合わせに従い
    // 有効な種別の中で最も新しい動画IDを返す
    public async Task<(string? videoId, VideoKind kind)?> FetchLatestAllowedVideoAsync(
        string channelId, bool allowVideo, bool allowShort, bool allowLive)
    {
        var svc = GetService();
        try
        {
            var actReq = svc.Activities.List("snippet,contentDetails");
            actReq.ChannelId  = channelId;
            actReq.MaxResults = 25;
            actReq.PublishedAfterDateTimeOffset = DateTimeOffset.UtcNow.AddDays(-90);

            var actResp = await actReq.ExecuteAsync();
            if (actResp.Items == null) return null;

            var videoIds = actResp.Items
                .Where(a => a.Snippet.Type == "upload" &&
                            a.ContentDetails.Upload?.VideoId != null)
                .OrderByDescending(a => a.Snippet.PublishedAtDateTimeOffset)
                .Select(a => a.ContentDetails.Upload!.VideoId)
                .Distinct()
                .ToList();

            if (videoIds.Count == 0) return null;

            var kindMap = await GetVideoKindsAsync(svc, videoIds);

            // 投稿順（新→旧）に走査して有効な種別の最初のものを返す
            foreach (var id in videoIds)
            {
                var kind = kindMap.TryGetValue(id, out var k) ? k : VideoKind.Video;
                bool allowed = kind switch
                {
                    VideoKind.Video => allowVideo,
                    VideoKind.Short => allowShort,
                    VideoKind.Live  => allowLive,
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
