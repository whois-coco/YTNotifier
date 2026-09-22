using System;
using System.Diagnostics;

namespace YTNotifier.Services;

internal static class BrowserLaunchHelper
{
    private const string HttpsScheme = "https://";
    private const string HttpScheme  = "http://";

    /// <summary>
    /// 既定のブラウザでURLを開く。http/https以外の先頭のURLは無視する。
    /// </summary>
    internal static void OpenUrl(string url)
    {
        if (!url.StartsWith(HttpsScheme, StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith(HttpScheme,  StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }
}
