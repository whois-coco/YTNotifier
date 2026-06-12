using System;
using System.IO;
using Microsoft.Toolkit.Uwp.Notifications;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// トースト通知・サウンド再生を一元管理するサービス
/// </summary>
public static class NotificationService
{
    private static string ExeDir =>
        Path.GetDirectoryName(Environment.ProcessPath
            ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "";

    // ============================================================
    // トースト通知
    // ============================================================

    /// <summary>動画新着通知を表示する</summary>
    public static void ShowVideoNotification(
        string channelName, string videoTitle, string kindLabel, string videoUrl,
        string channelId = "", VideoKind kind = VideoKind.Video,
        string? channelThumbnailUrl = null, string? videoThumbnailUrl = null)
    {
        try
        {
            var settings = SettingsService.Instance.Settings;

            var builder = new ToastContentBuilder()
                .AddArgument("url", videoUrl)
                .AddArgument("channelId", channelId);

            if (settings.ToastStyle == ToastStyle.Thumbnail)
            {
                // ─── サムネイル通知 ──────────────────────────────────────
                if (!string.IsNullOrEmpty(videoThumbnailUrl))
                {
                    var heroPath = ImageCacheService.GetOrDownloadThumbnail(videoThumbnailUrl);
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

            LoggerService.Instance.Info(
                $"通知送信: {videoTitle}", null, LogCategory.Notify);
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning(
                $"通知送信失敗: {ex.Message}", null, LogCategory.Notify);
        }
    }

    /// <summary>テスト通知を表示する（通知スタイルに従う）</summary>
    public static void ShowTestNotification()
    {
        var settings = SettingsService.Instance.Settings;
        try
        {
            if (settings.ShowDesktopNotification)
            {
                var iconPath = Path.Combine(ExeDir, "Resources", "app.png");
                var builder  = new ToastContentBuilder()
                    .AddArgument("url", "https://www.youtube.com");

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
                    builder.AddAttributionText("YTNotifier");
                    builder.AddText("[テスト]");
                    builder.AddText("通知テスト：正常に動作しています");
                }
                else
                {
                    if (File.Exists(iconPath))
                        builder.AddAppLogoOverride(
                            new Uri("file:///" + iconPath.Replace("\\", "/")),
                            ToastGenericAppLogoCrop.Circle);
                    builder.AddText("YTNotifier  [テスト]");
                    builder.AddText("通知テスト：正常に動作しています");
                }

                builder.AddAudio(null, silent: true);
                builder.Show();
            }

            if (settings.NotificationSound)
                PlayTestSound();

            LoggerService.Instance.Success("テスト通知を送信しました");
        }
        catch (Exception ex)
        {
            LoggerService.Instance.Warning($"テスト通知失敗: {ex.Message}");
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
            var soundsDir = Path.Combine(ExeDir, "Sounds");
            var kindFile  = kind switch
            {
                VideoKind.Short    => "short.wav",
                VideoKind.Live     => "live.wav",
                VideoKind.Premiere => "live.wav",
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
            var testPath = Path.Combine(ExeDir, "Sounds", "test.wav");
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
