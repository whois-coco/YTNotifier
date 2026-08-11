using Newtonsoft.Json;
using System.IO;
using System.Reflection;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 外部要約DLL（YTS.dll）が存在する場合、要約処理をそちらへ委譲するブリッジ。
/// 開発時の検証用のため Debug ビルドのみ有効で、Release ビルドでは常に利用不可を返す。
/// </summary>
public static class ExternalSummaryBridge
{
#if DEBUG
    private const string ExternalDllFileName = "YTS.dll";
    private const string ExternalTypeName    = "YTS.SummaryBridge";
    private const string ExternalMethodName  = "SummarizeAsync";

    /// <summary>実行ファイルと同じフォルダに YTS.dll が存在するか</summary>
    public static bool IsAvailable
        => File.Exists(Path.Combine(AppContext.BaseDirectory, ExternalDllFileName));

    /// <summary>
    /// YTS.dll 経由で要約を試みる。型解決・呼び出し・デシリアライズのいずれかで失敗した場合、
    /// または結果が null／空の場合は null を返す（例外を外に投げない。呼び出し元は
    /// null を「フォールバックすべきサイン」として扱う）。
    /// </summary>
    public static async Task<GeminiSummaryEntry?> TrySummarizeAsync(string videoId, string videoUrl, string apiKey)
    {
        try
        {
            var dllPath = Path.Combine(AppContext.BaseDirectory, ExternalDllFileName);
            var assembly = Assembly.LoadFrom(dllPath);
            var type     = assembly.GetType(ExternalTypeName);
            var method   = type?.GetMethod(ExternalMethodName);
            if (method == null) return null;

            var task = (Task<string?>?)method.Invoke(null, new object[] { videoId, videoUrl, apiKey });
            if (task == null) return null;

            var json = await task;
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonConvert.DeserializeObject<GeminiSummaryEntry>(json);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.ExternalSummaryBridgeFailed, null, videoId, ex.Message);
            return null;
        }
    }
#else
    /// <summary>Release ビルドでは YTS.dll 経路を持たないため常に false</summary>
    public static bool IsAvailable => false;

    /// <summary>Release ビルドでは YTS.dll 経路を持たないため常に null（フォールバックすべきサイン）を返す</summary>
    public static Task<GeminiSummaryEntry?> TrySummarizeAsync(string videoId, string videoUrl, string apiKey)
        => Task.FromResult<GeminiSummaryEntry?>(null);
#endif
}
