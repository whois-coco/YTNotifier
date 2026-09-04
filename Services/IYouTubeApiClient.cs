
namespace YTNotifier.Services;

public interface IYouTubeApiClient
{
    Task<string?> GetUploadsPlaylistIdAsync(string channelId);
    Task<ApiKeyTestResult> TestApiKeyAsync(string apiKey, string channelId);
    Task<ChannelInfo?> FetchChannelInfoAsync(string input);
    Task<(List<VideoInfo> Videos, List<VideoInfo> PendingTransitioned, List<VideoInfo> AllScanned, List<VideoInfo> AllScannedBasic, bool PlaylistEmpty)> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", IReadOnlyList<string>? pendingUpcomingVideoIds = null,
        int maxResults = 50, DateTime? lastVideoPublishedAt = null,
        System.Collections.Concurrent.ConcurrentDictionary<string, VideoKind>? videoKindCache = null);
    Task<Dictionary<string, bool>> CheckChannelsBannedAsync(IReadOnlyList<string> channelIds);
}
