using Google.GenAI;
using Google.GenAI.Types;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>Gemini 動画要約の結果</summary>
public class GeminiSummaryResult
{
    public bool    Success      { get; init; }
    public string? Headline     { get; init; }
    public string? Detail       { get; init; }
    public string? ErrorMessage { get; init; }
}

/// <summary>Gemini API（gemini-2.5-flash）を使った動画要約サービス</summary>
public static class GeminiSummaryService
{
    private static string ClassifyClientError(ClientError ex)
    {
        if (ex.StatusCode == 403) return $"Gemini APIキーが無効または権限がありません（HTTP {ex.StatusCode}）";
        if (ex.StatusCode == 429) return "Gemini APIクォータの上限に達しました";
        return $"Gemini API エラー（HTTP {ex.StatusCode}: {ex.Message}）";
    }

    private static string ClassifyServerError(ServerError ex)
        => $"Gemini サーバーエラー（HTTP {ex.StatusCode}）";

    private static string ClassifyNetworkException(Exception ex) => ex switch
    {
        TaskCanceledException                 => "リクエストがタイムアウトしました",
        System.Net.Http.HttpRequestException  => $"ネットワークエラー: {ex.Message}",
        _                                      => ex.Message
    };

    /// <summary>動画IDを元に Gemini API へ要約をリクエストする（onChunk が渡された場合、受信済みテキストを都度通知する）</summary>
    public static async Task<GeminiSummaryResult> SummarizeVideoAsync(string apiKey, string videoId, Action<string>? onChunk = null)
    {
        try
        {
            AppLogger.Log(LogMsg.GeminiSummaryRequested, null, videoId);

            using var client = new Client(apiKey: apiKey, httpOptions: new HttpOptions
            {
                Timeout = AppConstants.GeminiRequestTimeoutMinutes * 60 * 1000
            });

            var content = new Content
            {
                Parts = new List<Part>
                {
                    new() { FileData = new FileData
                    {
                        FileUri  = $"{YouTubeConstants.WatchUrlBase}{videoId}",
                        MimeType = AppConstants.GeminiVideoMimeType
                    }, VideoMetadata = new VideoMetadata { Fps = AppConstants.GeminiAudioOnlyFps } },
                    new() { Text = AppConstants.GeminiSummaryPromptText }
                }
            };

            var config = new GenerateContentConfig
            {
                ThinkingConfig  = new ThinkingConfig { ThinkingBudget = AppConstants.GeminiThinkingBudget },
                MediaResolution = MediaResolution.MediaResolutionLow
            };

            var textBuilder = new System.Text.StringBuilder();
            await foreach (var chunk in client.Models.GenerateContentStreamAsync(AppConstants.GeminiModelName, content, config))
            {
                if (string.IsNullOrEmpty(chunk.Text)) continue;
                textBuilder.Append(chunk.Text);
                onChunk?.Invoke(textBuilder.ToString());
            }

            var text = textBuilder.ToString();
            if (string.IsNullOrWhiteSpace(text))
                return new GeminiSummaryResult { Success = false, ErrorMessage = "要約結果が空でした" };

            var lines    = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var headline = lines.Length > 0 ? lines[0] : text;
            var detail   = lines.Length > 1 ? string.Join("\n", lines[1..]) : string.Empty;

            AppLogger.Log(LogMsg.GeminiSummarySucceeded, null, videoId);
            return new GeminiSummaryResult { Success = true, Headline = headline, Detail = detail };
        }
        catch (ClientError cex)
        {
            var msg = ClassifyClientError(cex);
            AppLogger.Log(LogMsg.GeminiSummaryFailed, null, videoId, msg);
            return new GeminiSummaryResult { Success = false, ErrorMessage = msg };
        }
        catch (ServerError sex)
        {
            var msg = ClassifyServerError(sex);
            AppLogger.Log(LogMsg.GeminiSummaryFailed, null, videoId, msg);
            return new GeminiSummaryResult { Success = false, ErrorMessage = msg };
        }
        catch (Exception ex)
        {
            var msg = ClassifyNetworkException(ex);
            AppLogger.Log(LogMsg.GeminiSummaryFailed, null, videoId, msg);
            return new GeminiSummaryResult { Success = false, ErrorMessage = msg };
        }
    }
}
