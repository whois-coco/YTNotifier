using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

internal static class UpdateCheckService
{
    private const string GitHubReleasesApiBaseUrl = "https://api.github.com/repos/whois-coco/YTNotifier/releases";

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
        var result = await CheckWithStatusAsync();
        return result.Status == UpdateCheckStatus.UpdateFound ? result.LatestTag : null;
    }

    /// <summary>
    /// GitHub Releases の最新タグを取得し、新バージョンあり／最新版／通信失敗の3状態を区別して返す。
    /// </summary>
    public static async Task<UpdateCheckResult> CheckWithStatusAsync()
    {
        try
        {
            var json = await _http.GetStringAsync($"{GitHubReleasesApiBaseUrl}/latest");
            var tag = JObject.Parse(json)["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tag)) return new UpdateCheckResult(UpdateCheckStatus.Failed, null);

            var latest  = ParseVersion(tag.TrimStart('v'));
            var current = ParseVersion(AppConstants.AppVersion);
            return latest > current
                ? new UpdateCheckResult(UpdateCheckStatus.UpdateFound, tag)
                : new UpdateCheckResult(UpdateCheckStatus.UpToDate, null);
        }
        catch
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, null);
        }
    }

    private static Version ParseVersion(string versionText)
    {
        return Version.TryParse(versionText, out var parsedVersion) ? parsedVersion : new Version(0, 0);
    }

    /// <summary>candidate が baseline より新しいバージョンかどうかを判定する（"1.2.3" 形式の文字列を比較）。</summary>
    public static bool IsNewerVersion(string candidate, string baseline) =>
        ParseVersion(candidate) > ParseVersion(baseline);

    /// <summary>
    /// 最新リリースのタグと本文（Markdown形式のリリースノート）を取得する。取得失敗時は null。
    /// </summary>
    public static async Task<ReleaseNotes?> GetLatestReleaseNotesAsync()
    {
        try
        {
            var json = await _http.GetStringAsync($"{GitHubReleasesApiBaseUrl}/latest");
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

    /// <summary>
    /// 指定タグ（例: "v0.9.4"）のリリースのタグと本文（Markdown形式のリリースノート）を取得する。取得失敗時は null。
    /// </summary>
    public static async Task<ReleaseNotes?> GetReleaseNotesByTagAsync(string tag)
    {
        try
        {
            var json = await _http.GetStringAsync($"{GitHubReleasesApiBaseUrl}/tags/{tag}");
            var release = JObject.Parse(json);
            var tagName = release["tag_name"]?.ToString();
            if (string.IsNullOrEmpty(tagName)) return null;

            var body = release["body"]?.ToString();
            return new ReleaseNotes(tagName, body);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 最新リリースの情報を取得し、拡張子が .exe の最初のアセットのダウンロードURLと
    /// ハッシュ値（digest フィールド、"sha256:xxxx" 形式）を返す。取得失敗時は null。
    /// </summary>
    public static async Task<UpdateAssetInfo?> GetLatestAssetAsync()
    {
        try
        {
            var json = await _http.GetStringAsync($"{GitHubReleasesApiBaseUrl}/latest");
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
