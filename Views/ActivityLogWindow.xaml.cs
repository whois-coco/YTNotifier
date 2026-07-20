using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ActivityLogWindow : Window
{
    private const string WindowsExplorer = "explorer.exe";
    private const string DefaultLogFilterTag = "Info";

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _handler;
    private ICollectionView? _logView;

    public ActivityLogWindow()
    {
        InitializeComponent();
        Loaded  += OnLoaded;
        Closed  += OnClosed;
    }

    private void ScrollToBottom()
    {
        if (LogList.Items.Count > 0)
            LogList.ScrollIntoView(LogList.Items[LogList.Items.Count - 1]);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        WindowCornerHelper.Apply(this);

        _logView = CollectionViewSource.GetDefaultView(LoggerService.Instance.TodayEntries);
        LogList.ItemsSource = _logView;
        LogFilterComboBox.SelectedIndex = 0;   // "情報"。SelectionChangedでApplyLogFilterが呼ばれる

        _handler = (_, _) =>
            Dispatcher.BeginInvoke(ScrollToBottom,
                System.Windows.Threading.DispatcherPriority.Background);
        LoggerService.Instance.TodayEntries.CollectionChanged += _handler;

        Dispatcher.BeginInvoke(ScrollToBottom,
            System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        if (_handler != null)
            LoggerService.Instance.TodayEntries.CollectionChanged -= _handler;
    }

    private void LogFilterComboBox_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (LogFilterComboBox.SelectedItem is ComboBoxItem item)
            ApplyLogFilter(item.Tag?.ToString() ?? DefaultLogFilterTag);
    }

    private void ApplyLogFilter(string tag)
    {
        if (_logView == null) return;
        _logView.Filter = obj => obj is LogEntry entry && IsVisibleForFilter(entry.Level, tag);
        _logView.Refresh();
    }

    private static bool IsVisibleForFilter(LogLevel level, string tag)
    {
        return tag switch
        {
            "Warning" => level is LogLevel.Warning or LogLevel.Error,
            "Error"   => level == LogLevel.Error,
            _         => level is LogLevel.System or LogLevel.Info or LogLevel.Warning or LogLevel.Error
        };
    }

    private void SaveLogToFile_Click(object sender, RoutedEventArgs e)
    {
        var count = LoggerService.Instance.SaveTodayLogToFile();
        AppLogger.Log(LogMsg.ActivityLogSavedToFile, null, count);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        LoggerService.Instance.ClearTodayLog();
        AppLogger.Log(LogMsg.ActivityLogCleared);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e)
    {
        AppLogger.Log(LogMsg.LogFolderOpened);
        var dir = Path.Combine(SettingsService.Instance.AppDataDir, AppConstants.DirLogs);
        Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo(WindowsExplorer, dir) { UseShellExecute = true });
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    private void TitleBar_Minimize(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void TitleBar_Close(object sender, RoutedEventArgs e)
        => Close();
}
