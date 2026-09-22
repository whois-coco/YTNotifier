using System.Windows;
using System.Windows.Controls;
using YTNotifier.Constants;
using YTNotifier.Services;

namespace YTNotifier.Views;

// 部分クラス: バックアップ / インポート
public partial class SettingsWindow : Window
{
    private const string BackupFileFilterSave = "YTNotifierバックアップ (*.ytbk)|*.ytbk";
    private const string BackupFilePrefix     = "YTNotifier_";

    private void ExportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "バックアップの保存先を選択",
            Filter = BackupFileFilterSave,
            FileName = $"{BackupFilePrefix}{DateTime.Now:yyyyMMdd}.ytbk",
            DefaultExt = ".ytbk",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var path = SettingsService.Instance.Backup.ExportBackup(dlg.FileName, includeState: true);
            BackupStatusText.Text = $"✅ エクスポート完了: {System.IO.Path.GetFileName(path)}";
            MainWindow.SetDynamicBrush(BackupStatusText, TextBlock.ForegroundProperty, "SuccessBrush");
            AppLogger.Log(LogMsg.SettingBackupExported, null, System.IO.Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            BackupStatusText.Text = $"❌ エラー: {ex.Message}";
            MainWindow.SetDynamicBrush(BackupStatusText, TextBlock.ForegroundProperty, "ErrorBrush");
        }
    }

    private void ImportBackup_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "復元するバックアップファイルを選択",
            Filter = AppConstants.BackupFileFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;

        if (ConfirmDialog.Show(this, "復元の確認", "現在の設定がバックアップで上書きされます。\n続行しますか？", "上書きする") != true) return;

        var currentTraceLogEnabled = SettingsService.Instance.Settings.TraceLogEnabled;
        var (success, message) = SettingsService.Instance.Backup.ImportBackup(dlg.FileName);
        BackupStatusText.Text = success ? $"✅ {message}" : $"❌ {message}";
        MainWindow.SetDynamicBrush(BackupStatusText, TextBlock.ForegroundProperty, success ? "SuccessBrush" : "ErrorBrush");
        if (success)
        {
            SettingsService.Instance.Settings.TraceLogEnabled = currentTraceLogEnabled;
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.SettingBackupImported, null, System.IO.Path.GetFileName(dlg.FileName));
            LoadSettings();
            App.ApplyTheme(SettingsService.Instance.Settings.Theme);
        }
    }
}
