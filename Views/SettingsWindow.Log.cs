using System.Windows;
using System.Windows.Controls;
using YTNotifier.Services;

namespace YTNotifier.Views;

// 部分クラス: ログ
public partial class SettingsWindow : Window
{
    private void ShowActivityLog_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.ActivityLogWindowOpened);
        var win = new ActivityLogWindow { Owner = this };
        win.Show();
    }

    private void RefreshLogStats()
    {
        var (count, bytes, oldest, newest) = LoggerService.Instance.GetLogStats();
        if (count == 0) { LogStatsText.Text = "ログファイルはありません"; return; }
        var sizeMegabytes = bytes / 1024.0 / 1024.0;
        var sizeStr = sizeMegabytes >= 1 ? $"{sizeMegabytes:F1} MB" : $"{bytes / 1024.0:F0} KB";
        LogStatsText.Text = $"ファイル数: {count} 件  合計サイズ: {sizeStr}\n最古: {oldest:yyyy/MM/dd}  最新: {newest:yyyy/MM/dd}";
    }

    private void TraceLogToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(TraceLogToggle, (settings, enabled) => settings.TraceLogEnabled = enabled, LogMsg.SettingTraceLogEnabled);

    private void AutoCleanLogsToggle_Changed(object sender, RoutedEventArgs e)
        => ApplyToggleSetting(AutoCleanLogsToggle, (settings, enabled) => settings.AutoCleanLogs = enabled, LogMsg.SettingAutoCleanLogs);

    private void LogRetentionComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        if (LogRetentionComboBox.SelectedItem is ComboBoxItem item && int.TryParse(item.Tag?.ToString(), out var days))
        {
            SettingsService.Instance.Settings.LogRetentionDays = days;
            SettingsService.Instance.MarkDirty();
            AppLogger.Log(LogMsg.SettingLogRetention, null, days == -1 ? "無制限" : days.ToString());
        }
    }

    private void CleanLogsNow_Click(object sender, RoutedEventArgs e)
    {
        var days = SettingsService.Instance.Settings.LogRetentionDays;
        if (days == -1) { LogCleanResultText.Text = "保持期間が「無制限」のため削除しません"; return; }
        var (deleted, freed) = LoggerService.Instance.CleanOldLogs(days);
        var freedMegabytes = freed / 1024.0 / 1024.0;
        var sizeStr = freedMegabytes >= 1 ? $"{freedMegabytes:F1} MB" : $"{freed / 1024.0:F0} KB";
        LogCleanResultText.Text = deleted > 0 ? $"✅ {deleted} 件削除（{sizeStr} 解放）" : "削除対象のログファイルはありませんでした";
        AppLogger.Log(LogMsg.LogManualDeleted, null, deleted, sizeStr);
        RefreshLogStats();
    }
}
