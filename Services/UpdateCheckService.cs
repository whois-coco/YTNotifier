using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using YTNotifier.Constants;

namespace YTNotifier.Services;

internal static class UpdateCheckService
{
    private const string GitHubReleasesApiUrl = "https://api.github.com/repos/whois-coco/YTNotifier/releases/latest";

    /// <summary>リリース確認のタイムアウト（秒）</summary>
    private const int ReleaseCheckTimeoutSeconds = 10;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(ReleaseCheckTimeoutSeconds) };

    static UpdateCheckService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppConstants.AppName, AppConstants.AppVersion));
    }

    /// <summary>
    /// GitHub Releases の最新タグを取得し、現在バージョンより新しければそのタグ文字列を返す。
    /// 最新版であるか取得失敗の場合は null を返す。
    /// </summary>
    public static async Task<string?> CheckAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(GitHubReleasesApiUrl);
            var tag = JObject.Parse(json)["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tag)) return null;

            var latest  = ParseVersion(tag.TrimStart('v'));
            var current = ParseVersion(AppConstants.AppVersion);
            return latest > current ? tag : null;
        }
        catch
        {
            return null;
        }
    }

    private static Version ParseVersion(string s)
    {
        return Version.TryParse(s, out var v) ? v : new Version(0, 0);
    }

    /// <summary>candidate が baseline より新しいバージョンかどうかを判定する（"1.2.3" 形式の文字列を比較）。</summary>
    public static bool IsNewerVersion(string candidate, string baseline) =>
        ParseVersion(candidate) > ParseVersion(baseline);

    internal sealed record ReleaseNotes(string Tag, string? Body);

    /// <summary>
    /// 最新リリースのタグと本文（Markdown形式のリリースノート）を取得する。取得失敗時は null。
    /// </summary>
    public static async Task<ReleaseNotes?> GetLatestReleaseNotesAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(GitHubReleasesApiUrl);
            var release = JObject.Parse(json);
            var tag = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tag)) return null;

            var body = release["body"]?.ToString();
            return new ReleaseNotes(tag, body);
        }
        catch
        {
            return null;
        }
    }

    internal sealed record UpdateAssetInfo(string Tag, string DownloadUrl, string? Sha256Digest);

    /// <summary>
    /// 最新リリースの情報を取得し、拡張子が .exe の最初のアセットのダウンロードURLと
    /// ハッシュ値（digest フィールド、"sha256:xxxx" 形式）を返す。取得失敗時は null。
    /// </summary>
    public static async Task<UpdateAssetInfo?> GetLatestAssetAsync()
    {
        try
        {
            var json = await _http.GetStringAsync(GitHubReleasesApiUrl);
            var release = JObject.Parse(json);
            var tag = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tag)) return null;

            var asset = release["assets"]?
                .FirstOrDefault(a => (a["name"]?.ToString() ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
            var downloadUrl = asset?["browser_download_url"]?.ToString();
            if (string.IsNullOrEmpty(downloadUrl)) return null;

            var digest = asset?["digest"]?.ToString();
            return new UpdateAssetInfo(tag, downloadUrl, digest);
        }
        catch
        {
            return null;
        }
    }
}
