using System;
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

    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

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
                await YTNotifier.Views.MainWindow.OpenChannelLatestVideoFromToastAsync(ch, toastUrl);
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
                if (!string.IsNullOrEmpty(videoThumbnailUrl))
                {
                    var heroPath = await ImageCacheService.GetOrDownloadThumbnailAsync(videoThumbnailUrl).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(heroPath))
                        try { builder.AddHeroImage(new Uri("file:///" + heroPath.Replace("\\", "/"))); }
                        catch { }
                }
                if (!string.IsNullOrEmpty(channelThumbnailUrl))
                {
                    var iconPath = ImageCacheService.GetIconDiskPath(channelThumbnailUrl);
                    if (File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);
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
                    var iconPath = ImageCacheService.GetIconDiskPath(channelThumbnailUrl);
                    if (File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);
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

            AppLogger.Log(LogMsg.NotificationSent, null, videoTitle);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.NotifyFailed, null, ex.Message);
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
                        try { builder.AddHeroImage(new Uri("file:///" + iconPath.Replace("\\", "/"))); }
                        catch { }
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
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
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
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
            var testPath = Path.Combine(ExeDir, AppConstants.DirSounds, "test.wav");
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
