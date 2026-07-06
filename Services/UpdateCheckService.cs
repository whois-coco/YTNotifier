using System.Net.Http;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using YTNotifier.Constants;

namespace YTNotifier.Services;

internal static class UpdateCheckService
{
    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

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
            var json = await _http.GetStringAsync(AppConstants.GitHubReleasesApiUrl);
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
}
