
namespace YTNotifier.Services;

public interface IYouTubeApiClient
{
    Task<string?> GetUploadsPlaylistIdAsync(string channelId);
    Task<ChannelInfo?> FetchChannelInfoAsync(string input);
    Task<(List<VideoInfo> videos, List<VideoInfo> pendingTransitioned)> CheckLatestVideosAsync(
        string channelId, string lastVideoId,
        string uploadsPlaylistId = "", IReadOnlyList<string>? pendingUpcomingVideoIds = null,
        int maxResults = 10);
    Task<(string? videoId, VideoKind kind)?> FetchLatestAllowedVideoAsync(
        string channelId, bool allowVideo, bool allowShort, bool allowLive,
        string uploadsPlaylistId = "");
}
