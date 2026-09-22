using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 最新動画スナップショット（表示用キャッシュ・recent_uploads.json.gz）の読み書きを担当するストア。
/// </summary>
public sealed class RecentUploadStore
{
    private readonly SettingsFileStore _fileStore;
    private readonly SettingsService _core;

    internal RecentUploadStore(SettingsFileStore fileStore, SettingsService core)
    {
        _fileStore = fileStore;
        _core      = core;
    }

    /// <summary>
    /// recent_uploads.json のシリアライズ設定。
    /// 人間が編集しない純粋な表示キャッシュのため、詰めて（Formatting.None）書き出す。
    /// </summary>
    private static readonly JsonSerializerSettings _recentUploadsSerializerSettings = new()
    {
        Formatting        = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
    };

    /// <summary>
    /// RecentUploadEntry 1件を、項目名を持たない配列
    /// [videoId, title, thumbnailUrl, kindの数値, duration, publishedAt] に変換する。
    /// 各値の書式は既定の Newtonsoft シリアライズと同じにする。
    /// </summary>
    private static Newtonsoft.Json.Linq.JArray RecentUploadEntryToArray(RecentUploadEntry entry) => new()
    {
        entry.VideoId,
        entry.Title,
        entry.ThumbnailUrl,
        (int)entry.Kind,
        entry.Duration?.ToString(),
        entry.PublishedAt,
    };

    /// <summary>RecentUploadEntryToArray の逆変換。</summary>
    private static RecentUploadEntry RecentUploadEntryFromArray(Newtonsoft.Json.Linq.JArray array) => new()
    {
        VideoId      = (string?)array[0] ?? string.Empty,
        Title        = (string?)array[1] ?? string.Empty,
        ThumbnailUrl = (string?)array[2],
        Kind         = (VideoKind)((int?)array[3] ?? 0),
        Duration     = (string?)array[4] is string durationText ? TimeSpan.Parse(durationText) : null,
        PublishedAt  = (DateTime?)array[5],
    };

    /// <summary>
    /// 最新動画スナップショット（表示用キャッシュ）を recent_uploads.json.gz へ保存する。
    /// state.json とは切り離し、バックアップ対象外の再生成可能ファイルとして扱う。
    /// 項目名を持たない配列形式にした上で gzip 圧縮して書き出す。
    /// </summary>
    internal void SaveRecentUploadsInternal()
    {
        try
        {
            var json = _core.Channels.ReadChannelsLocked(channels =>
            {
                var snapshot = new Dictionary<string, List<Newtonsoft.Json.Linq.JArray>>();
                foreach (var ch in channels)
                {
                    if (ch.State.RecentUploads.Count > 0)
                        snapshot[ch.ChannelId] = ch.State.RecentUploads.Select(RecentUploadEntryToArray).ToList();
                }
                return JsonConvert.SerializeObject(snapshot, _recentUploadsSerializerSettings);
            });
            var bytes = System.Text.Encoding.UTF8.GetBytes(json);
            using var compressedStream = new MemoryStream();
            using (var gzip = new GZipStream(compressedStream, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(bytes, 0, bytes.Length);
            SettingsFileStore.WriteAtomic(_fileStore.RecentUploadsPath, compressedStream.ToArray());
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveRecentUploads", ex.Message); }
    }

    /// <summary>
    /// recent_uploads.json.gz を読み込み、各チャンネルの表示用スナップショットへ反映する。
    /// 破損・空・null の場合はバックアップからの復元は行わず、そのまま戻る（次回チェックで自己修復するため）。
    /// </summary>
    internal void LoadRecentUploads()
    {
        try
        {
            if (!File.Exists(_fileStore.RecentUploadsPath))
                return;

            string json;
            using (var fileStream = File.OpenRead(_fileStore.RecentUploadsPath))
            using (var gzip = new GZipStream(fileStream, CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip, System.Text.Encoding.UTF8))
                json = reader.ReadToEnd();

            if (string.IsNullOrWhiteSpace(json))
                return;

            var snapshot = JsonConvert.DeserializeObject<Dictionary<string, List<Newtonsoft.Json.Linq.JArray>>>(json);
            if (snapshot == null || snapshot.Count == 0)
                return;

            _core.Channels.ReadChannelsLocked(channels =>
            {
                foreach (var ch in channels)
                {
                    ch.State.RecentUploads = snapshot.TryGetValue(ch.ChannelId, out var uploads) && uploads != null
                        ? uploads.Select(RecentUploadEntryFromArray).ToList()
                        : new();
                }
            });
        }
        catch (Exception ex) { _fileStore.WriteSaveError("LoadRecentUploads", ex.Message); }
    }
}
