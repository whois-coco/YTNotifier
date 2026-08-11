using Jint;
using Newtonsoft.Json;
using System.IO;
using System.Reflection;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>要約スクリプト（Plugins\summary.js）が存在する場合、要約処理をそちらへ委譲するブリッジ</summary>
public static class SummaryScriptService
{
    private const string PluginsDirName      = "Plugins";
    private const string ScriptFileName      = "summary.js";
    private const string SummarizeFuncName   = "summarize";
    private const string HttpRequestFuncName = "httpRequest";
    private const string LogFuncName         = "log";

    /// <summary>スクリプト実行全体の時間の上限（分）。通信の待ち時間も含む</summary>
    private const int ScriptTimeoutMinutes = 5;

    /// <summary>
    /// 再帰の深さの上限。同梱スクリプトの再帰は深さ8で自制しており、
    /// 正常動作に当たらない余裕を取った値
    /// </summary>
    private const int MaxRecursionDepth = 64;

    /// <summary>
    /// スクリプトが使えるメモリの上限（64MB）。通信の受信上限が10MB
    /// （ScriptHttpGateway.MaxResponseBytes）で、文字列は UTF-16 で約2倍になるため
    /// 1本で約20MB相当。それを数本同時に保持できる余裕を取った値
    /// </summary>
    private const long MaxScriptMemoryBytes = 64 * 1024 * 1024;

    /// <summary>検出ログに記録するスクリプトの最終更新日時の書式</summary>
    private const string LastWriteTimeFormat = "yyyy-MM-dd HH:mm:ss";

    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath
            ?? Assembly.GetExecutingAssembly().Location) ?? "";

    /// <summary>実行ファイルと同じフォルダに Plugins\summary.js が存在するか</summary>
    public static bool IsAvailable
        => File.Exists(Path.Combine(ExeDir, PluginsDirName, ScriptFileName));

    /// <summary>
    /// 要約スクリプトが置かれている場合、その場所と最終更新日時をログに記録する（起動時に1回呼ばれる想定）。
    /// スクリプトが無い場合は何もしない。日時の取得に失敗しても例外を外に投げない（起動を妨げない）。
    /// </summary>
    public static void LogPluginDetection()
    {
        try
        {
            var scriptPath = Path.Combine(ExeDir, PluginsDirName, ScriptFileName);
            if (!File.Exists(scriptPath)) return;

            var lastWriteTime = File.GetLastWriteTime(scriptPath).ToString(LastWriteTimeFormat);
            AppLogger.Log(LogMsg.PluginDetected, null, scriptPath, lastWriteTime);
        }
        catch
        {
            // 検出ログの記録に失敗しても起動を妨げない
        }
    }

    /// <summary>
    /// Plugins\summary.js 経由で要約を試みる。読込・実行・デシリアライズのいずれかで失敗した場合、
    /// タイムアウトした場合、または結果が null／空の場合は null を返す（例外を外に投げない。
    /// 呼び出し元は null を「フォールバックすべきサイン」として扱う）。
    /// </summary>
    public static async Task<GeminiSummaryEntry?> TrySummarizeAsync(string videoUrl, string apiKey)
    {
        var videoId = videoUrl.StartsWith(YouTubeConstants.WatchUrlBase)
            ? videoUrl.Substring(YouTubeConstants.WatchUrlBase.Length)
            : string.Empty;

        try
        {
            var scriptTask  = Task.Run(() => RunScript(videoUrl, apiKey));
            var timeoutTask = Task.Delay(TimeSpan.FromMinutes(ScriptTimeoutMinutes));
            var completed   = await Task.WhenAny(scriptTask, timeoutTask);
            if (completed != scriptTask)
            {
                AppLogger.Log(LogMsg.SummaryScriptFailed, null, videoId, "タイムアウト");
                return null;
            }

            var json = await scriptTask;
            if (string.IsNullOrWhiteSpace(json))
            {
                AppLogger.Log(LogMsg.SummaryScriptFailed, null, videoId, "スクリプトの戻り値が空");
                return null;
            }

            var entry = JsonConvert.DeserializeObject<GeminiSummaryEntry>(json);
            if (entry == null || string.IsNullOrEmpty(entry.Headline))
            {
                AppLogger.Log(LogMsg.SummaryScriptFailed, null, videoId, "headlineが空");
                return null;
            }

            return entry;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.SummaryScriptFailed, null, videoId, ex.Message);
            return null;
        }
    }

    /// <summary>Jint の Engine を新規生成してスクリプトを実行する。UIスレッド以外（Task.Run内）から呼ばれる前提</summary>
    private static string? RunScript(string videoUrl, string apiKey)
    {
        var scriptPath = Path.Combine(ExeDir, PluginsDirName, ScriptFileName);
        var scriptText = File.ReadAllText(scriptPath, System.Text.Encoding.UTF8);

        var gateway = new ScriptHttpGateway(apiKey);
        var logGateway = new ScriptLogGateway();
        var engine  = new Engine(options => options
            .TimeoutInterval(TimeSpan.FromMinutes(ScriptTimeoutMinutes))
            .LimitRecursion(MaxRecursionDepth)
            .LimitMemory(MaxScriptMemoryBytes));
        engine.SetValue(HttpRequestFuncName, new Func<string, string, string, string, string>(gateway.HttpRequest));
        engine.SetValue(LogFuncName, new Action<string>(logGateway.Log));
        engine.Execute(scriptText);

        var result = engine.Invoke(SummarizeFuncName, videoUrl);
        return result.IsNull() || result.IsUndefined() ? null : result.ToString();
    }
}
