using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using Microsoft.Toolkit.Uwp.Notifications;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// トースト通知・サウンド再生を一元管理するサービス
/// </summary>
public static class NotificationService
{
    private const string BaseUrl     = "https://www.youtube.com";
    private const string DirResources = "Resources";
    private const string FileAppIcon  = "app.png";

    /// <summary>トースト通知用画像の一時保存先ディレクトリ名（%TEMP% 配下）</summary>
    private const string DirToastTempImages = "YTNotifier_ToastImages";

    /// <summary>トースト通知用サムネイル一時ファイルの拡張子</summary>
    private const string ToastThumbnailTempExtension = ".jpg";

    /// <summary>トースト通知用アイコン一時ファイルの拡張子</summary>
    private const string ToastIconTempExtension = ".png";

    /// <summary>トースト通知用一時画像ファイルの削除待機時間（ミリ秒）。通知プラットフォームの画像読込猶予</summary>
    private const int ToastTempImageCleanupDelayMs = 10000;

    private static string ToFileUri(string path) => "file:///" + path.Replace("\\", "/");

    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

    // ===== トースト通知画像の一時ダウンロード =====
    // ディスクへの永続保存（指示書053）を避けつつ、パッケージ化されていないアプリでは
    // トースト通知が https:// のリモート画像を読み込めないため、表示直前だけ一時フォルダへ書き出す
    private static readonly System.Net.Http.HttpClient _toastImageHttp =
        new() { Timeout = TimeSpan.FromSeconds(5) };

    private static readonly string ToastTempImageDir =
        Path.Combine(Path.GetTempPath(), DirToastTempImages);

    private static async Task<string?> DownloadToTempFileAsync(string url, string extension)
    {
        try
        {
            Directory.CreateDirectory(ToastTempImageDir);
            var bytes = await _toastImageHttp.GetByteArrayAsync(url).ConfigureAwait(false);
            var path  = Path.Combine(ToastTempImageDir, $"{Guid.NewGuid():N}{extension}");
            await File.WriteAllBytesAsync(path, bytes).ConfigureAwait(false);
            return path;
        }
        catch { return null; }
    }

    private static void ScheduleTempFileCleanup(string path)
    {
        _ = Task.Delay(ToastTempImageCleanupDelayMs).ContinueWith(_ =>
        {
            try { File.Delete(path); } catch { }
        });
    }

    // ===== タスクバー点滅 =====
    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public IntPtr hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }
    private const uint FLASHW_TRAY    = 2;
    private const uint FLASHW_TIMERNOFG = 12;

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pfwi);

    private static void FlashTaskbar()
    {
        var win = System.Windows.Application.Current?.MainWindow;
        if (win == null) return;
        var hwnd = new WindowInteropHelper(win).Handle;
        if (hwnd == IntPtr.Zero) return;
        var info = new FLASHWINFO
        {
            cbSize    = (uint)Marshal.SizeOf<FLASHWINFO>(),
            hwnd      = hwnd,
            dwFlags   = FLASHW_TRAY | FLASHW_TIMERNOFG,
            uCount    = 3,
            dwTimeout = 0
        };
        FlashWindowEx(ref info);
    }

    // ============================================================
    // トースト通知クリック処理の登録
    // ============================================================

    /// <summary>トーストクリック時の動画オープン処理（View 側が起動時に設定する）</summary>
    public static Func<ChannelInfo, string?, Task>? OpenVideoFromToast { get; set; }

    /// <summary>トースト通知クリックハンドラを登録する（起動時に1回呼ぶ）</summary>
    public static void RegisterToastActivation()
    {
        ToastNotificationManagerCompat.OnActivated += OnToastActivated;
    }

    private static void OnToastActivated(ToastNotificationActivatedEventArgsCompat e)
    {
        var args = ToastArguments.Parse(e.Argument);
        if (!args.TryGetValue("channelId", out var channelId) || string.IsNullOrEmpty(channelId))
            return;

        System.Windows.Application.Current?.Dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var ch = SettingsService.Instance.Channels
                    .FirstOrDefault(c => c.ChannelId == channelId);
                if (ch == null) return;

                if (ch.HasUnread)
                {
                    ch.HasUnread = false;
                    SettingsService.Instance.UpdateChannelSilent(ch);
                    MonitorService.Instance.InvokeChannelUpdated();
                }

                args.TryGetValue("videoId", out var toastVideoId);
                var toastUrl = !string.IsNullOrEmpty(toastVideoId)
                    ? YouTubeConstants.WatchUrlBase + toastVideoId
                    : null;
                if (OpenVideoFromToast != null)
                    await OpenVideoFromToast(ch, toastUrl);
            }
            catch (Exception ex) { AppLogger.Log(LogMsg.NotifyFailed, null, ex.Message); }
        });
    }

    // ============================================================
    // トースト通知
    // ============================================================

    /// <summary>動画新着通知を表示する</summary>
    public static async Task ShowVideoNotificationAsync(
        string channelName, string videoTitle, string kindLabel, string videoUrl,
        string channelId = "", VideoKind kind = VideoKind.Video,
        string? channelThumbnailUrl = null, string? videoThumbnailUrl = null)
    {
        var tempFiles = new List<string>();
        try
        {
            var settings = SettingsService.Instance.Settings;

            // ToastArguments は = を区切り文字に使うため URL をそのまま渡すと ?v= で解析が壊れる。
            // videoId のみ渡し、クリック時に URL を再構築する。
            var videoId = videoUrl.StartsWith(YouTubeConstants.WatchUrlBase)
                ? videoUrl.Substring(YouTubeConstants.WatchUrlBase.Length)
                : string.Empty;

            var builder = new ToastContentBuilder()
                .AddArgument("videoId", videoId)
                .AddArgument("channelId", channelId);

            if (settings.ToastStyle == ToastStyle.Thumbnail)
            {
                // ─── サムネイル通知 ──────────────────────────────────────
                // パッケージ化されていないアプリはリモート画像を直接読み込めないため、
                // 表示直前だけ一時フォルダへダウンロードし、表示後に削除する
                if (!string.IsNullOrEmpty(videoThumbnailUrl))
                {
                    var heroPath = await DownloadToTempFileAsync(videoThumbnailUrl, ToastThumbnailTempExtension).ConfigureAwait(false);
                    if (heroPath != null)
                    {
                        tempFiles.Add(heroPath);
                        try { builder.AddHeroImage(new Uri(ToFileUri(heroPath))); }
                        catch { }
                    }
                }
                if (!string.IsNullOrEmpty(channelThumbnailUrl))
                {
                    var iconPath = await DownloadToTempFileAsync(channelThumbnailUrl, ToastIconTempExtension).ConfigureAwait(false);
                    if (iconPath != null)
                    {
                        tempFiles.Add(iconPath);
                        try
                        {
                            builder.AddAppLogoOverride(
                                new Uri(ToFileUri(iconPath)),
                                ToastGenericAppLogoCrop.Circle);
                        }
                        catch { }
                    }
                }
                builder.AddAttributionText(channelName);
                builder.AddText(kindLabel);
                builder.AddText(videoTitle);
            }
            else
            {
                // ─── デフォルト通知 ──────────────────────────────────────
                if (!string.IsNullOrEmpty(channelThumbnailUrl))
                {
                    var iconPath = await DownloadToTempFileAsync(channelThumbnailUrl, ToastIconTempExtension).ConfigureAwait(false);
                    if (iconPath != null)
                    {
                        tempFiles.Add(iconPath);
                        try
                        {
                            builder.AddAppLogoOverride(
                                new Uri(ToFileUri(iconPath)),
                                ToastGenericAppLogoCrop.Circle);
                        }
                        catch { }
                    }
                }
                builder.AddText($"{channelName}  [{kindLabel}]");
                builder.AddText(videoTitle);
            }

            builder.AddAudio(null, silent: true);
            builder.Show();

            if (settings.NotificationSound)
                PlaySound(kind);

            if (settings.FlashTaskbar)
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(FlashTaskbar);

            AppLogger.Log(LogMsg.NotificationSent, channelName, videoTitle);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.NotifyFailed, null, ex.Message);
        }
        finally
        {
            foreach (var path in tempFiles)
                ScheduleTempFileCleanup(path);
        }
    }

    /// <summary>APIクォータ超過通知を表示する（ミュート状態に関わらず表示）</summary>
    public static void ShowQuotaExceededNotification(DateTime resumeAt)
    {
        try
        {
            var resumeStr = resumeAt.ToString("HH:mm");
            new ToastContentBuilder()
                .AddArgument("url", BaseUrl)
                .AddText("チェック回数の上限に到達しました。")
                .AddText($"{resumeStr} まで監視が止まります。")
                .AddAudio(null, silent: true)
                .Show();
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.NotifyFailed, null, ex.Message);
        }
    }

    /// <summary>テスト通知を表示する（通知スタイルに従う）</summary>
    public static void ShowTestNotification()
    {
        var settings  = SettingsService.Instance.Settings;
        var iconPath  = Path.Combine(ExeDir, DirResources, FileAppIcon);
        try
        {
            if (settings.ShowDesktopNotification)
            {
                var builder  = new ToastContentBuilder()
                    .AddArgument("url", BaseUrl);

                if (settings.ToastStyle == ToastStyle.Thumbnail)
                {
                    if (File.Exists(iconPath))
                    {
                        try { builder.AddHeroImage(new Uri(ToFileUri(iconPath))); }
                        catch { }
                        builder.AddAppLogoOverride(
                            new Uri(ToFileUri(iconPath)),
                            ToastGenericAppLogoCrop.Circle);
                    }
                    builder.AddAttributionText(AppConstants.AppName);
                    builder.AddText("[テスト]");
                    builder.AddText("通知テスト：正常に動作しています");
                }
                else
                {
                    if (File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri(ToFileUri(iconPath)),
                            ToastGenericAppLogoCrop.Circle);
                    builder.AddText($"{AppConstants.AppName}  [テスト]");
                    builder.AddText("通知テスト：正常に動作しています");
                }

                builder.AddAudio(null, silent: true);
                builder.Show();
            }

            if (settings.NotificationSound)
                PlayTestSound();

            AppLogger.Log(LogMsg.TestNotifySent);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.TestNotifyFailedNS, null, ex.Message);
        }
    }

    // ============================================================
    // サウンド再生
    // ============================================================

    /// <summary>テスト用サウンドファイル名（PlayTestSound 内で複数回参照するためローカル定数化）</summary>
    private const string TestSoundFileName = "test.wav";

    /// <summary>
    /// Sounds フォルダ配下のサブフォルダ名一覧（通知音セット候補）を取得する。
    /// Sounds フォルダが存在しない場合は空リストを返す。
    /// </summary>
    public static List<string> GetAvailableSoundSets()
    {
        try
        {
            var soundsDir = Path.Combine(ExeDir, AppConstants.DirSounds);
            if (!Directory.Exists(soundsDir))
                return new List<string>();

            return Directory.GetDirectories(soundsDir)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .Select(name => name!)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// 動画種別に応じた通知音を再生する
    /// Sounds\video.wav / short.wav / live.wav → なければ notify.wav → Asterisk
    /// </summary>
    public static void PlaySound(VideoKind kind = VideoKind.Video)
    {
        try
        {
            var soundsDir = Path.Combine(ExeDir, AppConstants.DirSounds);
            var kindFile  = kind switch
            {
                VideoKind.Short    => "short.wav",
                VideoKind.Live     => "live.wav",
                VideoKind.Premiere => "premiere.wav",
                _                  => "video.wav"
            };

            var soundSet = SettingsService.Instance.Settings.NotificationSoundSet;
            if (!string.IsNullOrEmpty(soundSet))
            {
                var setPath = Path.Combine(soundsDir, soundSet, kindFile);
                if (File.Exists(setPath))
                {
                    using var setPlayer = new System.Media.SoundPlayer(setPath);
                    setPlayer.Play();
                    return;
                }
            }

            var customPath = Path.Combine(soundsDir, kindFile);
            if (File.Exists(customPath))
            {
                using var player = new System.Media.SoundPlayer(customPath);
                player.Play();
                return;
            }

            var sysMedia = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                "Media", "notify.wav");
            if (File.Exists(sysMedia))
            {
                using var player = new System.Media.SoundPlayer(sysMedia);
                player.Play();
            }
            else
            {
                System.Media.SystemSounds.Asterisk.Play();
            }
        }
        catch { }
    }

    /// <summary>
    /// テスト用サウンドを再生する
    /// Sounds\test.wav があれば優先、なければ PlaySound() にフォールバック
    /// </summary>
    public static void PlayTestSound()
    {
        try
        {
            var soundsDir = Path.Combine(ExeDir, AppConstants.DirSounds);
            var soundSet  = SettingsService.Instance.Settings.NotificationSoundSet;
            if (!string.IsNullOrEmpty(soundSet))
            {
                var setTestPath = Path.Combine(soundsDir, soundSet, TestSoundFileName);
                if (File.Exists(setTestPath))
                {
                    using var player = new System.Media.SoundPlayer(setTestPath);
                    player.Play();
                    return;
                }
            }

            var testPath = Path.Combine(soundsDir, TestSoundFileName);
            if (File.Exists(testPath))
            {
                using var player = new System.Media.SoundPlayer(testPath);
                player.Play();
                return;
            }
        }
        catch { }
        PlaySound();
    }
}
