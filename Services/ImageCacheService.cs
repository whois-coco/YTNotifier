using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace YTNotifier.Services;

/// <summary>
/// チャンネルアイコン・動画サムネイルのディスクキャッシュ管理
/// </summary>
public static class ImageCacheService
{
    private static string IconCacheDir =>
        Path.Combine(SettingsService.Instance.AppDataDir, "icons");

    private static string ThumbCacheDir =>
        Path.Combine(SettingsService.Instance.AppDataDir, "thumbcache");

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
    public static string? GetOrDownloadThumbnail(string url)
    {
        try
        {
            Directory.CreateDirectory(ThumbCacheDir);

            // 1週間以上古いキャッシュを削除
            foreach (var old in Directory.GetFiles(ThumbCacheDir, "*.jpg"))
                if (File.GetLastWriteTime(old) < DateTime.Now.AddDays(-7))
                    try { File.Delete(old); } catch { }

            var filePath = Path.Combine(ThumbCacheDir, GetHashFileName(url, ".jpg"));
            if (File.Exists(filePath)) return filePath;

            using var http = new System.Net.Http.HttpClient();
            http.Timeout   = TimeSpan.FromSeconds(5);
            var bytes      = http.GetByteArrayAsync(url).GetAwaiter().GetResult();
            File.WriteAllBytes(filePath, bytes);
            return filePath;
        }
        catch { return null; }
    }
}
