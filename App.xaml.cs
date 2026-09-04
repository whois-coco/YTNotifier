using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Threading;
using YTNotifier.Constants;
using YTNotifier.Models;
using YTNotifier.Services;
using YTNotifier.Views;

namespace YTNotifier;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private MainWindow? _mainWindow;
    private TrayIconService? _trayIconService;
    private bool _isDuplicateInstance = false;
    private bool _errorShown = false;
    private int _flushed = 0;

    protected override void OnStartup(StartupEventArgs e)
    {
        // ===== 多重起動防止 =====
        _mutex = new Mutex(true, "YTNotifier_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            _isDuplicateInstance = true;
            System.Windows.MessageBox.Show(
                "YTNotifier はすでに起動しています。\nタスクトレイを確認してください。",
                "多重起動", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
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

            // 現行は画像をメモリキャッシュのみで扱う。旧バージョンが %APPDATA% に残した画像ディスクキャッシュを掃除する
            ImageCacheService.CleanupLegacyDiskCache(SettingsService.Instance.AppDataDir);

            // 現行は要約結果を保存しない。旧バージョンが残した要約結果キャッシュファイルを掃除する
            GeminiSummaryService.CleanupLegacySummaryCache(SettingsService.Instance.AppDataDir);
        }
        catch (Exception ex) { ShowFatalError("設定ファイルの読み込みに失敗しました", ex); Shutdown(); return; }

        try { ApplyTheme(SettingsService.Instance.Settings.Theme); }
        catch (Exception ex) { ShowFatalError("テーマの適用に失敗しました", ex); Shutdown(); return; }

        // プラグインが置かれている場合、別プロセス（PluginHost.exe）を起動して受け入れる
        PluginBridge.Instance.Start();

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

    private const string ThemePackPrefix = "pack://application:,,,/Themes/";
    private const string ThemeFileSuffix = ".xaml";

    private static string ThemeFileBaseName(AppTheme theme) => theme switch
    {
        AppTheme.Dark => "DarkTheme",
        AppTheme.Blue => "BlueTheme",
        AppTheme.Gray => "GrayTheme",
        AppTheme.Pink => "PinkTheme",
        AppTheme.MatteBlack => "MatteBlackTheme",
        _             => "LightTheme",
    };

    private static readonly string[] AllThemeFileBaseNames =
        { "LightTheme", "DarkTheme", "BlueTheme", "GrayTheme", "PinkTheme", "MatteBlackTheme" };

    public static void ApplyTheme(AppTheme theme)
    {
        var uri = new Uri(ThemePackPrefix + ThemeFileBaseName(theme) + ThemeFileSuffix, UriKind.Absolute);

        // Self-Contained 発行時も確実にリソースを読み込む
        var dict = new ResourceDictionary();
        dict.Source = uri;

        // 既存のテーマを削除
        var toRemove = Current.Resources.MergedDictionaries
            .Where(d => d.Source != null &&
                AllThemeFileBaseNames.Any(n => d.Source.OriginalString.Contains(n)))
            .ToList();
        foreach (var r in toRemove)
            Current.Resources.MergedDictionaries.Remove(r);

        // 先頭に挿入（CommonStyles より前に配置して確実に上書き）
        Current.Resources.MergedDictionaries.Insert(0, dict);

        // 差し色（アクセント色）オーバーレイをテーマ辞書の上から再適用
        ApplyAccentOverride(SettingsService.Instance.Settings.AccentColorOverride);

        // トレイメニュー配色を追従
        (Current as App)?._trayIconService?.RefreshTheme();
    }

    // ===== 差し色（アクセント色）オーバーレイ =====

    // 上書き対象の色キー名
    private const string AccentPrimaryColorKey       = "PrimaryColor";
    private const string AccentPrimaryDarkColorKey   = "PrimaryDarkColor";
    private const string AccentPrimaryLightColorKey  = "PrimaryLightColor";
    private const string AccentAccentColorKey        = "AccentColor";
    private const string AccentAccentLightColorKey   = "AccentLightColor";
    private const string AccentSidebarActiveColorKey = "SidebarActiveColor";
    private const string AccentTextOnColorColorKey   = "TextOnColorColor";

    // 対応ブラシキー名
    private const string AccentPrimaryBrushKey       = "PrimaryBrush";
    private const string AccentPrimaryDarkBrushKey   = "PrimaryDarkBrush";
    private const string AccentPrimaryLightBrushKey  = "PrimaryLightBrush";
    private const string AccentAccentBrushKey        = "AccentBrush";
    private const string AccentAccentLightBrushKey   = "AccentLightBrush";
    private const string AccentSidebarActiveBrushKey = "SidebarActiveBrush";
    private const string AccentTextOnColorBrushKey   = "TextOnColorBrush";

    // 現テーマ面色キー名
    private const string AccentSurfaceColorKey = "SurfaceColor";

    // 派生パラメータ
    private const double AccentDarkScale                  = 0.82;  // 濃い色は各チャンネルにこれを乗算
    private const double AccentLightMixRatio              = 0.18;  // 淡色は面色と選択色をこの比で補間
    private const double AccentTextDarkLuminanceThreshold = 0.60;  // 相対輝度がこれ以上なら暗文字

    private static readonly System.Windows.Media.Color AccentDarkTextColor  =
        System.Windows.Media.Color.FromRgb(0x1A, 0x1A, 0x1A);
    private static readonly System.Windows.Media.Color AccentLightTextColor =
        System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0xFF);

    private static ResourceDictionary? _accentOverlay;

    /// <summary>差し色オーバーレイ辞書を差し替える。hex が null/空・解釈不能ならテーマ既定へ戻す。</summary>
    public static void ApplyAccentOverride(string? hex)
    {
        if (_accentOverlay != null)
        {
            Current.Resources.MergedDictionaries.Remove(_accentOverlay);
            _accentOverlay = null;
        }

        if (!TryParseHex(hex, out var accent)) return;

        var primaryDark = Scale(accent, AccentDarkScale);
        var textOnColor = RelativeLuminance(accent) >= AccentTextDarkLuminanceThreshold
            ? AccentDarkTextColor
            : AccentLightTextColor;

        var dict = new ResourceDictionary();

        void Put(string colorKey, string brushKey, System.Windows.Media.Color value)
        {
            dict[colorKey] = value;
            dict[brushKey] = new System.Windows.Media.SolidColorBrush(value);
        }

        Put(AccentPrimaryColorKey,       AccentPrimaryBrushKey,       accent);
        Put(AccentAccentColorKey,        AccentAccentBrushKey,        accent);
        Put(AccentSidebarActiveColorKey, AccentSidebarActiveBrushKey, accent);
        Put(AccentPrimaryDarkColorKey,   AccentPrimaryDarkBrushKey,   primaryDark);
        Put(AccentTextOnColorColorKey,   AccentTextOnColorBrushKey,   textOnColor);

        if (Current.TryFindResource(AccentSurfaceColorKey) is System.Windows.Media.Color surface)
        {
            var accentLight = Mix(surface, accent, AccentLightMixRatio);
            Put(AccentPrimaryLightColorKey, AccentPrimaryLightBrushKey, accentLight);
            Put(AccentAccentLightColorKey,  AccentAccentLightBrushKey,  accentLight);
        }

        _accentOverlay = dict;
        // MergedDictionaries は「後から追加した辞書が優先」。テーマ辞書の同名キーを上書きするため末尾に追加する
        Current.Resources.MergedDictionaries.Add(dict);
    }

    /// <summary>"#RRGGBB" / "RRGGBB"（前後空白許容）を不透明色として解釈する。</summary>
    private static bool TryParseHex(string? text, out System.Windows.Media.Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var body = text.Trim();
        if (body.StartsWith("#")) body = body[1..];
        if (body.Length != 6) return false;

        const System.Globalization.NumberStyles hexStyle = System.Globalization.NumberStyles.HexNumber;
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        if (!byte.TryParse(body.AsSpan(0, 2), hexStyle, culture, out var r)) return false;
        if (!byte.TryParse(body.AsSpan(2, 2), hexStyle, culture, out var g)) return false;
        if (!byte.TryParse(body.AsSpan(4, 2), hexStyle, culture, out var b)) return false;

        color = System.Windows.Media.Color.FromRgb(r, g, b);
        return true;
    }

    /// <summary>各チャンネルに係数を乗算し 0–255 でクランプした不透明色を返す。</summary>
    private static System.Windows.Media.Color Scale(System.Windows.Media.Color c, double f)
    {
        static byte ScaleChannel(byte channel, double factor)
        {
            var scaled = Math.Round(channel * factor);
            if (scaled < 0)   scaled = 0;
            if (scaled > 255) scaled = 255;
            return (byte)scaled;
        }
        return System.Windows.Media.Color.FromRgb(
            ScaleChannel(c.R, f), ScaleChannel(c.G, f), ScaleChannel(c.B, f));
    }

    /// <summary>sRGB 単純線形補間（a 側 1-t）で不透明色を返す。</summary>
    private static System.Windows.Media.Color Mix(System.Windows.Media.Color a, System.Windows.Media.Color b, double t)
    {
        static byte MixChannel(byte from, byte to, double ratio)
            => (byte)Math.Round(from * (1 - ratio) + to * ratio);
        return System.Windows.Media.Color.FromRgb(
            MixChannel(a.R, b.R, t), MixChannel(a.G, b.G, t), MixChannel(a.B, b.B, t));
    }

    /// <summary>相対輝度 (0.299R + 0.587G + 0.114B) / 255 を返す。</summary>
    private static double RelativeLuminance(System.Windows.Media.Color c)
        => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

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
        FlushAndBackup();
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        if (_isDuplicateInstance) return;
        FlushAndBackup();
    }

    private void FlushAndBackup()
    {
        if (Interlocked.Exchange(ref _flushed, 1) != 0) return;
        try { SettingsService.Instance.FlushAll(); } catch { }
        try { SettingsService.Instance.SaveAutoBackupIfDirty(); } catch { }
        try { LoggerService.Instance.CloseDebugDb(); } catch { }
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
        if (_isDuplicateInstance)
        {
            _mutex?.Dispose();
            base.OnExit(e);
            return;
        }
        MonitorService.Instance.Stop();
        PluginBridge.Instance.Stop();
        FlushAndBackup();
        _trayIconService?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
