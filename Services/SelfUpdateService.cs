using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>新バージョンのダウンロード・SHA256検証・exe差し替え・再起動を行う。</summary>
internal static class SelfUpdateService
{
    private const string DownloadTempFileSuffix = ".update";
    private const string BackupFileSuffix       = ".old";

    /// <summary>ダウンロード用のタイムアウト（秒）。exe本体の取得には数十MB想定のため長めに確保する</summary>
    private const int DownloadTimeoutSeconds = 120;

    private static readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(DownloadTimeoutSeconds) };

    static SelfUpdateService()
    {
        _http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue(AppConstants.AppName, AppConstants.AppVersion));
    }

    private static string CurrentExePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetExecutingAssembly().Location;

    /// <summary>新バージョンをダウンロードし、SHA256 が digest と一致するか検証する。
    /// 成功時はダウンロード済み一時ファイルのパスを返す。失敗時は一時ファイルを削除し null を返す。</summary>
    public static async Task<string?> DownloadAndVerifyAsync(UpdateAssetInfo info)
    {
        var tempPath = CurrentExePath + DownloadTempFileSuffix;
        try
        {
            using (var response = await _http.GetAsync(info.DownloadUrl))
            {
                response.EnsureSuccessStatusCode();
                await using var fs = File.Create(tempPath);
                await response.Content.CopyToAsync(fs);
            }

            if (!VerifyHash(tempPath, info.Sha256Digest))
            {
                File.Delete(tempPath);
                return null;
            }
            return tempPath;
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            return null;
        }
    }

    private static bool VerifyHash(string filePath, string? expectedDigest)
    {
        // digest が取得できない場合は検証不能として失敗扱い（安全側）
        const string DigestPrefix = "sha256:";
        if (string.IsNullOrEmpty(expectedDigest)) return false;
        if (!expectedDigest.StartsWith(DigestPrefix, StringComparison.OrdinalIgnoreCase)) return false;

        using var sha256 = System.Security.Cryptography.SHA256.Create();
        using var fs = File.OpenRead(filePath);
        var hash = Convert.ToHexString(sha256.ComputeHash(fs));
        return string.Equals(hash, expectedDigest[DigestPrefix.Length..], StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>検証済みの一時ファイルを現在の exe と差し替えて再起動する。
    /// 成功した場合、このメソッドは戻らない（Environment.Exit で終了する）。
    /// 失敗した場合はリネームを可能な限り元に戻したうえで例外を投げる。</summary>
    public static void ApplyAndRestart(string verifiedTempFilePath)
    {
        var exePath    = CurrentExePath;
        var backupPath = exePath + BackupFileSuffix;

        (System.Windows.Application.Current as YTNotifier.App)?.PrepareForSelfReplace();

        try
        {
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(exePath, backupPath);
            File.Move(verifiedTempFilePath, exePath);
        }
        catch
        {
            // 途中失敗時はできる範囲で元に戻す
            try { if (!File.Exists(exePath) && File.Exists(backupPath)) File.Move(backupPath, exePath); } catch { }
            throw;
        }

        Process.Start(new ProcessStartInfo(exePath) { UseShellExecute = true });
        Environment.Exit(0);
    }

    /// <summary>前回の自己更新で残った退避ファイルが残っていれば削除する（失敗は無視）。</summary>
    public static void CleanupLeftoverBackup()
    {
        try
        {
            var backupPath = CurrentExePath + BackupFileSuffix;
            if (File.Exists(backupPath)) File.Delete(backupPath);
        }
        catch { }
    }
}
