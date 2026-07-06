using System;
using System.IO;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// チャンネルアイコン・動画サムネイルのディスクキャッシュ管理
/// </summary>
public static class ImageCacheService
{
    private static readonly System.Net.Http.HttpClient _http =
        new() { Timeout = TimeSpan.FromSeconds(5) };
    private static readonly System.Threading.SemaphoreSlim _thumbWriteLock = new(1, 1);

    private static string IconCacheDir =>
        Path.Combine(SettingsService.Instance.AppDataDir, AppConstants.DirIcons);

    private const string DirThumbCache = "thumbcache";
    private static string ThumbCacheDir =>
        Path.Combine(SettingsService.Instance.AppDataDir, DirThumbCache);

    private static string GetKindSuffix(VideoKind kind) => kind switch
    {
        VideoKind.Short     => "short",
        VideoKind.Live      => "live",
        VideoKind.Premiere  => "premiere",
        _                   => "video",
    };

    /// <summary>
    /// チャンネルアイコンのキャッシュパスを返す（ファイル存在チェックなし）
    /// ダウンロードは MainWindow のアイコンローダーが担当
    /// </summary>
    public static string GetIconDiskPath(string url, string channelId)
    {
        Directory.CreateDirectory(IconCacheDir);
        return Path.Combine(IconCacheDir, $"{channelId}.png");
    }

    /// <summary>
    /// 動画サムネイルのキャッシュパスを返す（ファイルが存在する場合のみ）。ダウンロードは行わない
    /// </summary>
    public static string? GetThumbnailDiskPathIfExists(string channelId, VideoKind videoKind)
    {
        var filePath = Path.Combine(ThumbCacheDir, $"{channelId}_{GetKindSuffix(videoKind)}.jpg");
        return File.Exists(filePath) ? filePath : null;
    }

    /// <summary>
    /// 動画サムネイルをダウンロードしてキャッシュパスを返す（ヒーロー画像用）
    /// チャンネルごと・種別ごとに最新1件を上書き保持する
    /// </summary>
    public static async Task<string?> GetOrDownloadThumbnailAsync(string url, string channelId, VideoKind videoKind)
    {
        try
        {
            Directory.CreateDirectory(ThumbCacheDir);

            var filePath = Path.Combine(ThumbCacheDir, $"{channelId}_{GetKindSuffix(videoKind)}.jpg");

            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            await _thumbWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                File.WriteAllBytes(filePath, bytes);
            }
            finally { _thumbWriteLock.Release(); }
            return filePath;
        }
        catch { return null; }
    }
}
