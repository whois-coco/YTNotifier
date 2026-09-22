using System.Windows;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

// 部分クラス: アップデート確認・適用
public partial class SettingsWindow : Window
{
    private async void ShowReleaseNotes_Click(object sender, RoutedEventArgs e)
    {
        ShowReleaseNotesBtn.IsEnabled = false;
        try
        {
            var tag = AppConstants.AppVersion;
            var notes = await UpdateCheckService.GetReleaseNotesByTagAsync(tag);
            if (notes != null && !string.IsNullOrEmpty(notes.Body))
                new ReleaseNotesWindow(this, notes.Tag, notes.Body).Show();
            else
                System.Windows.MessageBox.Show(this, "リリースノートの取得に失敗しました。",
                    "リリースノート", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ShowReleaseNotesBtn.IsEnabled = true;
        }
    }

    private const string UpdateDialogTitle             = "アップデート";
    private const string UpdateCheckFailedMessage       = "通信に失敗しました。時間をおいて再実行を試してください。";
    private const string AlreadyLatestVersionMessage    = "最新版をお使いです。";
    private const string UpdateFoundMessageFormat       = "新しいバージョン {0} が見つかりました。更新しますか？";
    private const string UpdateConfirmButtonLabel       = "更新する";
    private const string DownloadOrVerifyFailedMessage  = "ダウンロードまたは検証に失敗しました。しばらくしてから再度お試しください。";
    private const string ApplyFailedMessageFormat       = "アップデートの適用に失敗しました。\n{0}";

    private async void CheckForUpdateBtn_Click(object sender, RoutedEventArgs e)
    {
        CheckForUpdateBtn.IsEnabled = false;
        try
        {
            var result = await UpdateCheckService.CheckWithStatusAsync();
            if (result.Status == UpdateCheckStatus.Failed)
            {
                System.Windows.MessageBox.Show(this, UpdateCheckFailedMessage,
                    UpdateDialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (result.Status == UpdateCheckStatus.UpToDate)
            {
                System.Windows.MessageBox.Show(this, AlreadyLatestVersionMessage,
                    UpdateDialogTitle, MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (ConfirmDialog.Show(this, UpdateDialogTitle,
                    string.Format(UpdateFoundMessageFormat, result.LatestTag), UpdateConfirmButtonLabel) != true)
                return;

            // ここから先は設定画面が閉じられても処理を継続するため、
            // ダイアログの親は常に生存しているメイン画面にする
            var mainWindowOwner = System.Windows.Application.Current.MainWindow;

            var assetInfo = await UpdateCheckService.GetLatestAssetAsync();
            if (assetInfo == null)
            {
                System.Windows.MessageBox.Show(mainWindowOwner, DownloadOrVerifyFailedMessage,
                    UpdateDialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var verifiedPath = await SelfUpdateService.DownloadAndVerifyAsync(assetInfo);
            if (verifiedPath == null)
            {
                System.Windows.MessageBox.Show(mainWindowOwner, DownloadOrVerifyFailedMessage,
                    UpdateDialogTitle, MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            SelfUpdateService.ApplyAndRestart(verifiedPath); // 成功時はここでプロセスが終了する
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(System.Windows.Application.Current.MainWindow,
                string.Format(ApplyFailedMessageFormat, ex.Message),
                UpdateDialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            CheckForUpdateBtn.IsEnabled = true;
        }
    }
}
