using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>URLのSHA1ハッシュでキャッシュファイル名を生成する</summary>
    private static string GetHashFileName(string url, string extension)
    {
        using var sha1 = SHA1.Create();
        var hash = sha1.ComputeHash(Encoding.UTF8.GetBytes(url));
        return BitConverter.ToString(hash).Replace("-", "").ToLower() + extension;
    }

    /// <summary>
    /// チャンネルアイコンのキャッシュパスを返す（ファイル存在チェックなし）
    /// ダウンロードは MainWindow のアイコンローダーが担当
    /// </summary>
    public static string GetIconDiskPath(string url)
    {
        Directory.CreateDirectory(IconCacheDir);
        return Path.Combine(IconCacheDir, GetHashFileName(url, ".png"));
    }

    /// <summary>
    /// 動画サムネイルをダウンロードしてキャッシュパスを返す（ヒーロー画像用）
    /// 1週間以上古いキャッシュを自動削除する
    /// </summary>
    public static async Task<string?> GetOrDownloadThumbnailAsync(string url)
    {
        try
        {
            Directory.CreateDirectory(ThumbCacheDir);

            // 1週間以上古いキャッシュを削除
            foreach (var old in Directory.GetFiles(ThumbCacheDir, "*.jpg"))
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-AppConstants.ThumbnailCacheDays))
                    try { File.Delete(old); } catch { }

            var filePath = Path.Combine(ThumbCacheDir, GetHashFileName(url, ".jpg"));
            if (File.Exists(filePath)) return filePath;

            using var response = await _http.GetAsync(url).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var bytes = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);

            await _thumbWriteLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!File.Exists(filePath))
                    File.WriteAllBytes(filePath, bytes);
            }
            finally { _thumbWriteLock.Release(); }
            return filePath;
        }
        catch { return null; }
    }
}
