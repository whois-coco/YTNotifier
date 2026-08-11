using System;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media.Imaging;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// チャンネルアイコン・動画サムネイルのメモリキャッシュ管理（ディスク保存は行わない）
/// </summary>
public static class ImageCacheService
{
    private const string DirThumbCache = "thumbcache";
    private const string DirIcons      = "icons";

    /// <summary>アイコン・サムネイル各キャッシュの件数上限（MainWindow の旧 IconCacheMaxEntries を踏襲）</summary>
    private const int CacheMaxEntries = 300;

    /// <summary>キャッシュエントリの有効期間（日）</summary>
    private const int CacheExpiryDays = 30;

    private sealed class CacheEntry
    {
        public required BitmapImage Bitmap    { get; init; }
        public required DateTime    CachedAt  { get; init; }
    }

    private static readonly System.Net.Http.HttpClient _http =
        new() { Timeout = TimeSpan.FromSeconds(5) };

    // キー: アイコン=URL、サムネイル=チャンネルID+動画種別
    private static readonly Dictionary<string, CacheEntry> _iconCache      = new();
    private static readonly Dictionary<string, CacheEntry> _thumbnailCache = new();
    private static readonly object _lock = new();

    private static string GetKindSuffix(VideoKind kind) => kind switch
    {
        VideoKind.Short     => "short",
        VideoKind.Live      => "live",
        VideoKind.Premiere  => "premiere",
        _                   => "video",
    };

    private static string GetThumbnailCacheKey(string channelId, VideoKind kind, string videoId) =>
        $"{channelId}_{GetKindSuffix(kind)}_{videoId}";

    private static BitmapImage? DecodeImage(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.StreamSource = stream;
            bmp.CacheOption  = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch { return null; }
    }

    private static bool TryGetValid(Dictionary<string, CacheEntry> cache, string key, out BitmapImage? bitmap)
    {
        lock (_lock)
        {
            if (cache.TryGetValue(key, out var entry) &&
                (DateTime.Now - entry.CachedAt).TotalDays < CacheExpiryDays)
            {
                bitmap = entry.Bitmap;
                return true;
            }
            cache.Remove(key);
            bitmap = null;
            return false;
        }
    }

    /// <summary>
    /// 有効期限（CacheExpiryDays）を過ぎた＝もう参照されていない古いエントリをキャッシュから除去する。
    /// 呼び出し側で _lock を保持していること。
    /// </summary>
    private static void PurgeExpired(Dictionary<string, CacheEntry> cache)
    {
        var now = DateTime.Now;
        var expiredKeys = cache
            .Where(kv => (now - kv.Value.CachedAt).TotalDays >= CacheExpiryDays)
            .Select(kv => kv.Key)
            .ToList();
        foreach (var k in expiredKeys) cache.Remove(k);
    }

    /// <summary>チャンネルアイコンのキャッシュを参照する（ダウンロードは行わない）</summary>
    public static bool TryGetCachedIcon(string url, out BitmapImage? bitmap) =>
        TryGetValid(_iconCache, url, out bitmap);

    /// <summary>動画サムネイルのキャッシュを参照する（ダウンロードは行わない）</summary>
    public static bool TryGetCachedThumbnail(string channelId, VideoKind kind, string videoId, out BitmapImage? bitmap) =>
        TryGetValid(_thumbnailCache, GetThumbnailCacheKey(channelId, kind, videoId), out bitmap);

    /// <summary>チャンネルアイコンをキャッシュから取得、なければダウンロードしてメモリキャッシュへ格納する</summary>
    public static async Task<BitmapImage?> GetOrDownloadIconAsync(string url)
    {
        if (TryGetValid(_iconCache, url, out var cached)) return cached;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            var bmp = DecodeImage(bytes);
            if (bmp == null) return null;

            lock (_lock)
            {
                if (_iconCache.Count >= CacheMaxEntries)
                    _iconCache.Remove(_iconCache.Keys.First());
                _iconCache[url] = new CacheEntry { Bitmap = bmp, CachedAt = DateTime.Now };
            }
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>動画サムネイルをキャッシュから取得、なければダウンロードしてメモリキャッシュへ格納する</summary>
    public static async Task<BitmapImage?> GetOrDownloadThumbnailAsync(string url, string channelId, VideoKind kind, string videoId)
    {
        var key = GetThumbnailCacheKey(channelId, kind, videoId);
        if (TryGetValid(_thumbnailCache, key, out var cached)) return cached;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url).ConfigureAwait(false);
            var bmp = DecodeImage(bytes);
            if (bmp == null) return null;

            lock (_lock)
            {
                PurgeExpired(_thumbnailCache);
                if (_thumbnailCache.Count >= CacheMaxEntries)
                    _thumbnailCache.Remove(_thumbnailCache.Keys.First());
                _thumbnailCache[key] = new CacheEntry { Bitmap = bmp, CachedAt = DateTime.Now };
            }
            return bmp;
        }
        catch { return null; }
    }

    /// <summary>
    /// 画像ディスクキャッシュ廃止（指示書053）に伴う一時的なクリーンアップ処理。
    /// 旧バージョンで作成された icons / thumbcache ディレクトリが残っていれば削除する。
    /// </summary>
    public static void CleanupLegacyDiskCache(string appDataDir)
    {
        foreach (var dir in new[]
        {
            Path.Combine(appDataDir, DirIcons),
            Path.Combine(appDataDir, DirThumbCache),
        })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { }
        }
    }
}
