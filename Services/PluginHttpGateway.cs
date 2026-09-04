using Newtonsoft.Json.Linq;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace YTNotifier.Services;

/// <summary>
/// プラグインへ公開する、接続先を限定した通信の道具。
/// 許可した接続先（Gemini API・YouTube）以外へは一切つながらない。
/// </summary>
public sealed class PluginHttpGateway
{
    /// <summary>要約用APIキーを付与する唯一の接続先（Gemini API）</summary>
    private const string GeminiHost = "generativelanguage.googleapis.com";

    /// <summary>字幕の取得先（YouTube）。APIキーは付与しない</summary>
    private const string YouTubeHost = "www.youtube.com";

    private static readonly string[] AllowedHosts =
    {
        GeminiHost,
        YouTubeHost,
    };
    private const string AllowedScheme    = "https";
    private const string ApiKeyHeaderName = "x-goog-api-key";
    private const string ContentTypeHeaderName = "Content-Type";

    /// <summary>受信量の上限（10MB）。要約結果のテキスト応答に対して十分な余裕を見た値</summary>
    private const long MaxResponseBytes = 10 * 1024 * 1024;

    /// <summary>
    /// 仕事1回あたりの通信回数の上限。同梱スクリプトが使うのは4回
    /// （訪問者情報・字幕トラック一覧・字幕本文・Gemini）で、その倍の余裕を取った値
    /// </summary>
    private const int MaxCallCount = 8;

    /// <summary>通信タイムアウト。既存 Gemini 要約（GeminiSummaryService.GeminiRequestTimeoutMinutes）と同じ3分</summary>
    private const int HttpTimeoutMinutes = 3;

    private static readonly HttpClient HttpClient =
        new(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromMinutes(HttpTimeoutMinutes)
        };

    private readonly string _apiKey;

    private int _callCount;

    public PluginHttpGateway(string apiKey) => _apiKey = apiKey;

    /// <summary>
    /// プラグインから呼ばれる唯一の外部アクセス手段。許可ホスト以外・許可外スキーム・URL として
    /// 解釈できない文字列、および通信回数の上限に達した以降の呼び出しは、例外を投げずに
    /// status=0 の結果を返す。
    /// 同期的に完結させるため、UIスレッド以外から呼ばれる前提（保証は PluginBridge 側で行う）。
    /// </summary>
    public string HttpRequest(string method, string url, string headersJson, string bodyText)
    {
        if (_callCount >= MaxCallCount) return BuildResult(0, "");
        _callCount++;

        try
        {
            var uri = new Uri(url);
            var hostAllowed = Array.Exists(AllowedHosts,
                h => string.Equals(uri.Host, h, StringComparison.OrdinalIgnoreCase));
            if (!string.Equals(uri.Scheme, AllowedScheme, StringComparison.OrdinalIgnoreCase) || !hostAllowed)
            {
                return BuildResult(0, "");
            }

            using var request = new HttpRequestMessage(new HttpMethod(method), uri);

            HttpContent? content = null;
            if (!string.IsNullOrEmpty(bodyText))
                content = new StringContent(bodyText, Encoding.UTF8);

            if (!string.IsNullOrEmpty(headersJson))
            {
                var headers = JObject.Parse(headersJson);
                foreach (var prop in headers.Properties())
                {
                    if (string.Equals(prop.Name, ApiKeyHeaderName, StringComparison.OrdinalIgnoreCase))
                        continue; // 呼び出し側からのAPIキー指定は無視し、本体が付与する値を優先する

                    if (string.Equals(prop.Name, ContentTypeHeaderName, StringComparison.OrdinalIgnoreCase) && content != null)
                        content.Headers.ContentType = MediaTypeHeaderValue.Parse(prop.Value.ToString());
                    else
                        request.Headers.TryAddWithoutValidation(prop.Name, prop.Value.ToString());
                }
            }

            if (content != null) request.Content = content;

            // 要約用APIキーは Gemini 宛のときだけ付与する（YouTube へは渡さない）
            if (string.Equals(uri.Host, GeminiHost, StringComparison.OrdinalIgnoreCase))
                request.Headers.TryAddWithoutValidation(ApiKeyHeaderName, _apiKey);

            using var response = HttpClient.Send(request);
            using var stream    = response.Content.ReadAsStream();
            using var buffer    = new MemoryStream();

            var chunk = new byte[8192];
            int read;
            while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > MaxResponseBytes) return BuildResult(0, "");
            }

            var body = Encoding.UTF8.GetString(buffer.ToArray());
            return BuildResult((int)response.StatusCode, body);
        }
        catch
        {
            return BuildResult(0, "");
        }
    }

    private static string BuildResult(int status, string body)
    {
        var result = new JObject { ["status"] = status, ["body"] = body };
        return result.ToString(Newtonsoft.Json.Formatting.None);
    }
}
