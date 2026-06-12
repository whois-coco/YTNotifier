using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;

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

        try
        {
            // 起動前にデータ破損・消失を検知して自動復元（ユーザー意識不要）
            var restoreReason = SettingsService.Instance.TryAutoRestore();
            SettingsService.Instance.Load();
            // 復元後にログ出力（LoggerServiceはLoad後に使用可能）
            if (restoreReason != null)
            {
                LoggerService.Instance.Warning(
                    $"自動復元を実行しました（理由: {restoreReason}）",
                    null, YTNotifier.Models.LogCategory.System);
            }
        }
        catch (Exception ex) { ShowFatalError("設定ファイルの読み込みに失敗しました", ex); Shutdown(); return; }

        try { ApplyTheme(SettingsService.Instance.Settings.IsDarkMode); }
        catch (Exception ex) { ShowFatalError("テーマの適用に失敗しました", ex); Shutdown(); return; }

        // システムトレイアイコン初期化
        InitNotifyIcon();

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
            ApplyTrayMenuTheme(menu);

            menu.Items.Add("🖥  ウィンドウを開く",  null, (_, _) => ShowMainWindow());
            menu.Items.Add("🔄  今すぐチェック",    null, async (_, _) => await MonitorService.Instance.ManualCheckAsync());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("▶  監視開始",           null, (_, _) => MonitorService.Instance.Start());
            menu.Items.Add("⏸  監視停止",           null, (_, _) => MonitorService.Instance.Stop());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("✖  終了",               null, (_, _) => ExitApp());

            // 各アイテムにスタイル適用
            foreach (ToolStripItem item in menu.Items)
                ApplyTrayMenuItemTheme(item);

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

    private static void ApplyTrayMenuTheme(ContextMenuStrip menu)
    {
        // ダーク背景（メインウィンドウのサイドバー色に近い #1E2130）
        menu.BackColor         = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
        menu.ForeColor         = System.Drawing.Color.FromArgb(0xE2, 0xE8, 0xF0);
        menu.Font              = new System.Drawing.Font("Yu Gothic UI", 9.5f);
        menu.ShowImageMargin   = false;
        menu.ShowCheckMargin   = false;
        menu.Padding           = new System.Windows.Forms.Padding(4, 4, 4, 4);
        menu.Renderer          = new TrayMenuRenderer();
        menu.Opening          += (_, _) =>
        {
            foreach (ToolStripItem item in menu.Items)
                ApplyTrayMenuItemTheme(item);
        };
    }

    private static void ApplyTrayMenuItemTheme(ToolStripItem item)
    {
        if (item is ToolStripMenuItem mi)
        {
            mi.BackColor = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
            mi.ForeColor = System.Drawing.Color.FromArgb(0xE2, 0xE8, 0xF0);
            mi.Padding   = new System.Windows.Forms.Padding(8, 4, 8, 4);
        }
        else if (item is ToolStripSeparator sep)
        {
            sep.BackColor = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
            sep.ForeColor = System.Drawing.Color.FromArgb(0x2D, 0x34, 0x4F);
        }
    }

    // カスタムレンダラー（ホバー色・区切り線をカスタマイズ）
    private class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayMenuColorTable()) { }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            var y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(System.Drawing.Color.FromArgb(0x2D, 0x34, 0x4F));
            g.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                using var brush = new System.Drawing.SolidBrush(
                    System.Drawing.Color.FromArgb(0x2D, 0x3A, 0x5A));
                var rc = new System.Drawing.Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                e.Graphics.FillRectangle(brush, rc);
            }
            else
            {
                using var brush = new System.Drawing.SolidBrush(
                    System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30));
                e.Graphics.FillRectangle(brush, e.Item.Bounds);
            }
        }
    }

    private class TrayMenuColorTable : ProfessionalColorTable
    {
        public override System.Drawing.Color MenuBorder
            => System.Drawing.Color.FromArgb(0x2D, 0x34, 0x4F);
        public override System.Drawing.Color ToolStripDropDownBackground
            => System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
        public override System.Drawing.Color ImageMarginGradientBegin
            => System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
        public override System.Drawing.Color ImageMarginGradientMiddle
            => System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
        public override System.Drawing.Color ImageMarginGradientEnd
            => System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
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
        "YTNotifier", "logs");  // logs フォルダ

    protected override void OnExit(ExitEventArgs e)
    {
        MonitorService.Instance.Stop();
        // 変更があれば bkup/auto_backup.ytbk へ自動保存
        try { SettingsService.Instance.SaveAutoBackupIfDirty(); } catch (Exception ex) { LoggerService.Instance.Warning($"終了時バックアップ失敗: {ex.Message}"); }
        try { SettingsService.Instance.SaveChannelsSilent(); }    catch (Exception ex) { LoggerService.Instance.Warning($"終了時保存失敗: {ex.Message}"); }
        _notifyIcon?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
