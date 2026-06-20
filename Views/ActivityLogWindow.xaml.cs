using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using YTNotifier.Constants;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ActivityLogWindow : Window
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    private System.Collections.Specialized.NotifyCollectionChangedEventHandler? _handler;

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
        LogList.ItemsSource = LoggerService.Instance.TodayEntries;

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
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
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
