using Google.GenAI;
using Google.GenAI.Types;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>Gemini API（gemini-3.1-flash-lite）を使った動画要約サービス</summary>
public static class GeminiSummaryService
{
    /// <summary>Gemini 要約に使用するモデル名</summary>
    private const string GeminiModelName = "gemini-3.1-flash-lite";

    /// <summary>Gemini へ動画を渡す際の固定 mime_type</summary>
    private const string GeminiVideoMimeType = "video/mp4";

    /// <summary>Gemini API リクエストのタイムアウト（分）</summary>
    private const int GeminiRequestTimeoutMinutes = 3;

    /// <summary>Gemini 要約リクエストのプロンプト（1行目=主題、2行目以降=詳細要約）</summary>
    private const string GeminiSummaryPromptText =
        "この動画の内容を要約してください。1行目に「ざっくり言うとどんな動画か」が一言でわかる見出しを、" +
        "2行目以降に箇条書き（「・」始まり）で3〜5行程度の要約を記載してください。" +
        "1行目は「要約しますと」「この動画の内容は以下の通りです」のような前置きを含めず、内容そのものを直接書いてください。";

    /// <summary>Gemini 要約リクエストの thinking トークン予算（0=思考無効化で高速化）</summary>
    private const int GeminiThinkingBudget = 0;

    /// <summary>音声中心の要約実験用：動画フレームのサンプリング頻度（fps）を極小化</summary>
    private const double GeminiAudioOnlyFps = 0.1;

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
                Timeout = GeminiRequestTimeoutMinutes * 60 * 1000
            });

            var content = new Content
            {
                Parts = new List<Part>
                {
                    new() { FileData = new FileData
                    {
                        FileUri  = $"{YouTubeConstants.WatchUrlBase}{videoId}",
                        MimeType = GeminiVideoMimeType
                    }, VideoMetadata = new VideoMetadata { Fps = GeminiAudioOnlyFps } },
                    new() { Text = GeminiSummaryPromptText }
                }
            };

            var config = new GenerateContentConfig
            {
                ThinkingConfig  = new ThinkingConfig { ThinkingBudget = GeminiThinkingBudget },
                MediaResolution = MediaResolution.MediaResolutionLow
            };

            var textBuilder = new System.Text.StringBuilder();
            await foreach (var chunk in client.Models.GenerateContentStreamAsync(GeminiModelName, content, config))
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
