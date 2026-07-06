using Newtonsoft.Json;
using System.IO;
using System.Text;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// Gemini 要約結果を動画IDごとに gemini_summary_cache.json へ保存/読み込みするサービス。
/// </summary>
public static class GeminiSummaryCacheService
{
    private static string GetPath(string appDataDir)
        => Path.Combine(appDataDir, AppConstants.FileGeminiSummaryCache);

    private static Dictionary<string, GeminiSummaryEntry> Load(string appDataDir)
    {
        try
        {
            var path = GetPath(appDataDir);
            if (!File.Exists(path)) return new();
            var json = File.ReadAllText(path, Encoding.UTF8);
            return JsonConvert.DeserializeObject<Dictionary<string, GeminiSummaryEntry>>(json) ?? new();
        }
        catch
        {
            return new();
        }
    }

    /// <summary>動画IDに対応するキャッシュ済み要約を返す。なければ null</summary>
    public static GeminiSummaryEntry? Get(string appDataDir, string videoId)
    {
        if (string.IsNullOrEmpty(videoId)) return null;
        var cache = Load(appDataDir);
        return cache.TryGetValue(videoId, out var entry) ? entry : null;
    }

    /// <summary>動画IDをキーに要約結果を保存する</summary>
    public static void Save(string appDataDir, string videoId, GeminiSummaryEntry entry)
    {
        if (string.IsNullOrEmpty(videoId)) return;
        try
        {
            var cache = Load(appDataDir);
            cache[videoId] = entry;
            var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
            var path = GetPath(appDataDir);
            var tmp  = path + ".tmp";
            File.WriteAllText(tmp, json, Encoding.UTF8);
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }
}
