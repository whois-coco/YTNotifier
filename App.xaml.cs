using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using YTNotifier.Services;
using YTNotifier.Views;

namespace YTNotifier;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private MainWindow? _mainWindow;
    private NotifyIcon? _notifyIcon;       // System.Windows.Forms.NotifyIcon
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

        try { SettingsService.Instance.Load(); }
        catch (Exception ex) { ShowFatalError("設定ファイルの読み込みに失敗しました", ex); Shutdown(); return; }

        try { ApplyTheme(SettingsService.Instance.Settings.IsDarkMode); }
        catch (Exception ex) { ShowFatalError("テーマの適用に失敗しました", ex); Shutdown(); return; }

        // システムトレイアイコン初期化
        InitNotifyIcon();

        try
        {
            _mainWindow = new MainWindow();
            _mainWindow.Show();
        }
        catch (Exception ex) { ShowFatalError("ウィンドウの初期化に失敗しました", ex); Shutdown(); }
    }

    // ===== System.Windows.Forms.NotifyIcon 初期化 =====
    private void InitNotifyIcon()
    {
        try
        {
            var icon = LoadIcon("app.ico");

            _notifyIcon = new NotifyIcon
            {
                Icon = icon,
                Text = "YTNotifier - YouTube通知",
                Visible = true
            };

            // コンテキストメニュー
            var menu = new ContextMenuStrip();
            menu.Items.Add("🖥  ウィンドウを開く",  null, (_, _) => ShowMainWindow());
            menu.Items.Add("🔄  今すぐチェック",    null, async (_, _) => await MonitorService.Instance.ManualCheckAsync());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("▶  監視開始",           null, (_, _) => MonitorService.Instance.Start());
            menu.Items.Add("⏸  監視停止",           null, (_, _) => MonitorService.Instance.Stop());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("✖  終了",               null, (_, _) => ExitApp());

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();

            // 監視ステータス変化でアイコン・ツールチップを更新
            MonitorService.Instance.StatusChanged += OnMonitorStatusChanged;
        }
        catch (Exception ex)
        {
            LogError("トレイアイコン初期化失敗", ex.ToString());
        }
    }

    private static Icon LoadIcon(string fileName)
    {
        // Resources フォルダのアイコンをストリームから読み込む
        var uri = new Uri($"pack://application:,,,/Resources/{fileName}");
        var sri = GetResourceStream(uri);
        if (sri != null)
            return new Icon(sri.Stream);

        // フォールバック: 実行ファイルの隣の Resources フォルダ
        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var path = Path.Combine(exeDir, "Resources", fileName);
        if (File.Exists(path))
            return new Icon(path);

        // さらにフォールバック: システムデフォルトアイコン
        return SystemIcons.Application;
    }

    private void OnMonitorStatusChanged(bool isRunning)
    {
        Dispatcher.Invoke(() =>
        {
            if (_notifyIcon == null) return;
            _notifyIcon.Text = isRunning ? "YTNotifier - 監視中" : "YTNotifier - 停止中";
            try
            {
                _notifyIcon.Icon = LoadIcon(isRunning ? "app.ico" : "app_warn.ico");
            }
            catch { }
        });
    }

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
        _notifyIcon?.Dispose();
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
        "YTNotifier", "logs");

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
