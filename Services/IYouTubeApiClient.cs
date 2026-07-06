
namespace YTNotifier.Services;

public interface IYouTubeApiClient
{
    Task<string?> GetUploadsPlaylistIdAsync(string channelId);
    Task<ApiKeyTestResult> TestApiKeyAsync(string apiKey, string channelId);
    Task<ChannelInfo?> FetchChannelInfoAsync(string input);
    Task<(List<VideoInfo> Videos, List<VideoInfo> PendingTransitioned, List<VideoInfo> AllScanned)> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", IReadOnlyList<string>? pendingUpcomingVideoIds = null,
        int maxResults = 50);
    Task<(string? videoId, VideoKind kind)?> FetchLatestAllowedVideoAsync(
        string channelId, bool allowVideo, bool allowShort, bool allowLive,
        string uploadsPlaylistId = "");
    Task<Dictionary<string, DateTime?>> GetActualEndTimesAsync(IEnumerable<string> videoIds);
}
