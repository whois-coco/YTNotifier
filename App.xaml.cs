using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using YTNotifier.Constants;
using YTNotifier.Services;
using YTNotifier.Views;

namespace YTNotifier;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIconService;
    private bool _errorShown = false;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ===== 多重起動防止 =====
        _mutex = new Mutex(true, "YTNotifier_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            System.Windows.MessageBox.Show(
                "YTNotifier はすでに起動しています。\nタスクトレイを確認してください。",
                "多重起動", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // トースト通知用 AppID 設定（WPFで ToastContentBuilder.Show() を使うために必要）
        Microsoft.Toolkit.Uwp.Notifications.ToastNotificationManagerCompat.History.Clear();

        base.OnStartup(e);

        try
        {
            // 起動前にデータ破損・消失を検知して自動復元（ユーザー意識不要）
            // TryAutoRestore が成功した場合、ImportBackup 内で Load() を呼ぶため二重呼び出し不要
            var restoreReason = SettingsService.Instance.TryAutoRestore();
            if (restoreReason == null)
                SettingsService.Instance.Load();
            else
                AppLogger.Log(LogMsg.AutoRestored, null, restoreReason);
        }
        catch (Exception ex) { ShowFatalError("設定ファイルの読み込みに失敗しました", ex); Shutdown(); return; }

        try { ApplyTheme(SettingsService.Instance.Settings.IsDarkMode); }
        catch (Exception ex) { ShowFatalError("テーマの適用に失敗しました", ex); Shutdown(); return; }

        // システムトレイアイコン初期化
        _trayIconService = new TrayIconService(ShowMainWindow, ExitApp);
        _trayIconService.Initialize();

        // APIキー未設定の初回起動時はセットアップウィンドウを先に表示
        if (string.IsNullOrEmpty(SettingsService.Instance.Settings.ApiKey))
        {
            var setup = new ApiKeySetupWindow();
            setup.ShowDialog();
            if (!setup.ApiKeySaved)
            {
                Shutdown();
                return;
            }
        }

        try
        {
            _mainWindow = new MainWindow();
            _mainWindow.Show();
        }
        catch (Exception ex)
        {
            // 内部例外も含めて全てログに記録
            var sb = new System.Text.StringBuilder();
            var e2 = ex;
            while (e2 != null)
            {
                sb.AppendLine($"[{e2.GetType().FullName}] {e2.Message}");
                sb.AppendLine(e2.StackTrace);
                sb.AppendLine("---");
                e2 = e2.InnerException;
            }
            LogError("ウィンドウ初期化エラー（詳細）", sb.ToString());
            ShowFatalError("ウィンドウの初期化に失敗しました", ex);
            Shutdown();
        }
    }

    // ===== System.Windows.Forms.NotifyIcon 初期化 =====

    // ===== 公開メソッド =====

    public void ShowMainWindow()
    {
        if (_mainWindow == null) return;
        Dispatcher.Invoke(() =>
        {
            _mainWindow.Show();
            _mainWindow.WindowState = System.Windows.WindowState.Normal;
            _mainWindow.Activate();
            _mainWindow.Focus();
        });
    }

    public static void ApplyTheme(bool isDark)
    {
        var uri = new Uri(isDark
            ? "pack://application:,,,/Themes/DarkTheme.xaml"
            : "pack://application:,,,/Themes/LightTheme.xaml",
            UriKind.Absolute);

        // Self-Contained 発行時も確実にリソースを読み込む
        var dict = new ResourceDictionary();
        dict.Source = uri;

        // 既存のテーマを削除
        var toRemove = Current.Resources.MergedDictionaries
            .Where(d => d.Source != null &&
                (d.Source.OriginalString.Contains("DarkTheme") ||
                 d.Source.OriginalString.Contains("LightTheme")))
            .ToList();
        foreach (var r in toRemove)
            Current.Resources.MergedDictionaries.Remove(r);

        // 先頭に挿入（CommonStyles より前に配置して確実に上書き）
        Current.Resources.MergedDictionaries.Insert(0, dict);
    }

    private void ExitApp()
    {
        MonitorService.Instance.Stop();
        _trayIconService?.Dispose();
        Shutdown();
    }

    // ===== エラーハンドラ =====

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        LogAndShow("UIスレッドエラー", e.Exception);
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        LogError("致命的エラー",
            (e.ExceptionObject as Exception)?.ToString() ?? e.ExceptionObject?.ToString() ?? "不明");
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        e.SetObserved();
        LogError("非同期エラー", e.Exception.ToString());
    }

    private void ShowFatalError(string context, Exception ex)
    {
        LogError(context, ex.ToString());
        System.Windows.MessageBox.Show($"{context}\n\n{ex.GetType().Name}: {ex.Message}",
            "起動エラー", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void LogAndShow(string kind, Exception ex)
    {
        LogError(kind, ex.ToString());
        if (_errorShown) return;
        _errorShown = true;
        try
        {
            System.Windows.MessageBox.Show(
                $"{kind}:\n{ex.GetType().Name}: {ex.Message}\n\nログ: {GetLogDir()}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { _errorShown = false; }
    }

    private static void LogError(string kind, string detail)
    {
        try
        {
            var dir = GetLogDir();
            Directory.CreateDirectory(dir);
            File.AppendAllText(
                Path.Combine(dir, $"{DateTime.Now:yyyy-MM-dd}_crash.log"),
                $"[{DateTime.Now:HH:mm:ss}] [{kind}]\n{detail}\n\n");
        }
        catch { }
    }

    private static string GetLogDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        AppConstants.AppName, AppConstants.DirLogs);

    protected override void OnExit(ExitEventArgs e)
    {
        MonitorService.Instance.Stop();
        // 変更があれば bkup/auto_backup.ytbk へ自動保存
        try { SettingsService.Instance.SaveAutoBackupIfDirty(); } catch { }
        try { SettingsService.Instance.SaveChannelsSilent(); } catch { }
        _trayIconService?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
