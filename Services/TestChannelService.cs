using Newtonsoft.Json;

namespace YTNotifier.Services;

/// <summary>
/// テストチャンネル用サービス。
/// YouTube API を呼ばず、ローカルの JSON ファイルからチェック結果を生成する。
///
/// JSON フォーマット:
/// {
///   "channelName": "テストチャンネル",
///   "states": [
///     [ { "videoId":"v1","title":"MVプレミア","kind":"Premiere","isUpcoming":true } ],
///     [ { "videoId":"v2","title":"Short","kind":"Short","isUpcoming":false },
///       { "videoId":"v1","title":"MVプレミア","kind":"Premiere","isUpcoming":true } ]
///   ]
/// }
///
/// states は「チェックごとのプレイリスト状態」の配列（新着順）。
/// 各チェックで次のステートへ進み、末尾に達したらそのまま留まる。
/// </summary>
public static class TestChannelService
{
    public class TestChannelData
    {
        [JsonProperty("channelName")]
        public string ChannelName { get; set; } = "テストチャンネル";

        [JsonProperty("states")]
        public List<List<TestChannelItem>> States { get; set; } = new();
    }

    public class TestChannelItem
    {
        [JsonProperty("videoId")]
        public string VideoId { get; set; } = string.Empty;

        [JsonProperty("title")]
        public string Title { get; set; } = string.Empty;

        [JsonProperty("kind")]
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public VideoKind Kind { get; set; } = VideoKind.Video;

        [JsonProperty("isUpcoming")]
        public bool IsUpcoming { get; set; } = false;

        [JsonProperty("thumbnailUrl")]
        public string? ThumbnailUrl { get; set; }
    }

    public static TestChannelData? LoadTestData(string path)
    {
        try
        {
            if (!System.IO.File.Exists(path)) return null;
            return JsonConvert.DeserializeObject<TestChannelData>(
                System.IO.File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>
    /// 現在のステートからチェック結果を返し、ステートインデックスを進める。
    /// YouTubeApiService.CheckLatestVideosAsync と同じ比較ロジックを使用。
    /// </summary>
    public static (List<VideoInfo> videos, List<VideoInfo> pendingTransitioned)
        GetNextCheckResult(ChannelInfo channel)
    {
        var empty = (new List<VideoInfo>(), new List<VideoInfo>());

        var testData = LoadTestData(channel.TestDataPath);
        if (testData == null || testData.States.Count == 0)
        {
            LoggerService.Instance.Warning(
                $"テストデータを読み込めません: {channel.TestDataPath}",
                channel.ChannelName, LogCategory.CheckError);
            return empty;
        }

        var stateIndex = Math.Clamp(channel.TestStateIndex, 0, testData.States.Count - 1);
        var items      = testData.States[stateIndex];

        // 次のステートへ（末尾で停止）
        channel.TestStateIndex = Math.Min(stateIndex + 1, testData.States.Count - 1);

        if (items.Count == 0) return empty;

        var lastVideoId          = channel.LastCheckedVideoId;
        var pendingIds           = channel.PendingUpcomingVideoIds;
        bool hasPending          = pendingIds.Count > 0;
        var firstId              = items[0].VideoId;

        if (firstId == lastVideoId && !hasPending)
            return empty;

        var kindMap = items.ToDictionary(i => i.VideoId, i => (i.Kind, i.IsUpcoming));

        // 新着なし && pending あり → pending の遷移のみ確認
        if (firstId == lastVideoId && hasPending)
        {
            var transitioned = new List<VideoInfo>();
            foreach (var pid in pendingIds)
            {
                if (!items.Any(i => i.VideoId == pid)) continue;
                if (kindMap.TryGetValue(pid, out var pkv) && !pkv.IsUpcoming)
                {
                    var item = items.First(i => i.VideoId == pid);
                    transitioned.Add(new VideoInfo
                    {
                        VideoId      = pid,
                        Title        = item.Title,
                        ThumbnailUrl = item.ThumbnailUrl,
                        Kind         = pkv.Kind,
                        IsUpcoming   = false
                    });
                }
            }
            return (new List<VideoInfo>(), transitioned);
        }

        // 新着あり
        var result = new List<VideoInfo>();
        foreach (var item in items)
        {
            if (item.VideoId == lastVideoId) break;
            result.Add(new VideoInfo
            {
                VideoId      = item.VideoId,
                Title        = item.Title,
                ThumbnailUrl = item.ThumbnailUrl,
                Kind         = item.Kind,
                IsUpcoming   = item.IsUpcoming
            });
        }

        // pending の遷移チェック（kindMap 内に含まれるもの）
        var transitionedList = new List<VideoInfo>();
        if (hasPending)
        {
            foreach (var pid in pendingIds)
            {
                if (kindMap.TryGetValue(pid, out var pkv) && !pkv.IsUpcoming)
                {
                    var item = items.FirstOrDefault(i => i.VideoId == pid);
                    if (item != null)
                        transitionedList.Add(new VideoInfo
                        {
                            VideoId      = pid,
                            Title        = item.Title,
                            ThumbnailUrl = item.ThumbnailUrl,
                            Kind         = pkv.Kind,
                            IsUpcoming   = false
                        });
                }
            }
        }

        return (result, transitionedList);
    }
}
