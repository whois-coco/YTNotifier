using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using YTNotifier.Constants;
using YTNotifier.Services;

namespace YTNotifier.Services;

/// <summary>
/// システムトレイアイコンの初期化・管理を担当するサービス
/// </summary>
public class TrayIconService : IDisposable
{
    private const string KeySurface     = "SurfaceColor";
    private const string KeyTextPrimary = "TextPrimaryColor";
    private const string KeyBorder      = "BorderColor";
    private const string KeyHover       = "HoverColor";

    private static System.Drawing.Color ColorBackground = System.Drawing.Color.FromArgb(0x1E, 0x29, 0x3B);
    private static System.Drawing.Color ColorForeground = System.Drawing.Color.FromArgb(0xF1, 0xF5, 0xF9);
    private static System.Drawing.Color ColorBorder     = System.Drawing.Color.FromArgb(0x33, 0x41, 0x55);
    private static System.Drawing.Color ColorHover      = System.Drawing.Color.FromArgb(0x1E, 0x3A, 0x5F);

    private NotifyIcon?      _notifyIcon;
    private ContextMenuStrip? _menu;
    private ToolStripItem?   _checkMenuItem;
    private readonly Action  _showMainWindow;
    private readonly Action  _exitApp;

    private static void LoadThemeColors()
    {
        ColorBackground = ResolveThemeColor(KeySurface,     ColorBackground);
        ColorForeground = ResolveThemeColor(KeyTextPrimary, ColorForeground);
        ColorBorder     = ResolveThemeColor(KeyBorder,      ColorBorder);
        ColorHover      = ResolveThemeColor(KeyHover,       ColorHover);
    }

    private static System.Drawing.Color ResolveThemeColor(string key, System.Drawing.Color fallback)
    {
        if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.Color c)
            return System.Drawing.Color.FromArgb(c.A, c.R, c.G, c.B);
        return fallback;
    }

    public TrayIconService(Action showMainWindow, Action exitApp)
    {
        _showMainWindow = showMainWindow;
        _exitApp        = exitApp;
    }

    public void Initialize()
    {
        try
        {
            var icon = LoadIcon("app.ico");
            _notifyIcon = new NotifyIcon
            {
                Icon    = icon,
                Text    = "YTNotifier - YouTube通知",
                Visible = true
            };

            LoadThemeColors();

            var menu = new ContextMenuStrip();
            ApplyTrayMenuTheme(menu);
            menu.Items.Add("🖥  ウィンドウを開く",  null, (_, _) => { AppLogger.Log(LogMsg.TrayWindowOpened); _showMainWindow(); });
            _checkMenuItem = menu.Items.Add("🔄  今すぐチェック",    null, async (_, _) =>
            {
                AppLogger.Log(LogMsg.TrayManualCheckTriggered);
                try { await MonitorService.Instance.ManualCheckAsync(); }
                catch (Exception ex) { AppLogger.Log(LogMsg.CheckFailed, null, ex.Message); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("▶  監視開始",           null, (_, _) => { AppLogger.Log(LogMsg.TrayMonitorStarted); MonitorService.Instance.Start(); });
            menu.Items.Add("⏸  監視停止",           null, (_, _) => { AppLogger.Log(LogMsg.TrayMonitorStopped); MonitorService.Instance.Stop(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("✖  終了",               null, (_, _) => _exitApp());

            foreach (ToolStripItem item in menu.Items)
                ApplyTrayMenuItemTheme(item);

            _notifyIcon.ContextMenuStrip = menu;
            _menu = menu;
            _notifyIcon.DoubleClick     += (_, _) => _showMainWindow();

            MonitorService.Instance.StatusChanged  += OnMonitorStatusChanged;
            MonitorService.Instance.QuotaUpdated   += OnQuotaUpdated;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.TrayIconInitFailed, null, ex.Message);
        }
    }

    /// <summary>選択テーマ変更時にトレイメニューの配色を追従させる（App.ApplyTheme から呼ばれる）</summary>
    public void RefreshTheme()
    {
        LoadThemeColors();
        if (_menu == null) return;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            _menu.BackColor = ColorBackground;
            _menu.ForeColor = ColorForeground;
            foreach (ToolStripItem item in _menu.Items)
                ApplyTrayMenuItemTheme(item);
            _menu.Renderer = new TrayMenuRenderer();
        });
    }

    private void OnQuotaUpdated()
    {
        if (_checkMenuItem == null) return;
        var appState    = SettingsService.Instance.AppState;
        var quotaKey    = AppConstants.GetQuotaDayKey();
        var actualUnits = appState.TodayApiDate == quotaKey ? appState.TodayApiUnits : 0;
        var actualPct   = actualUnits * 100.0 / ApiQuotaHelper.DailyLimit;
        _checkMenuItem.Enabled = actualPct <= ApiQuotaHelper.QuotaDisableThresholdPct;
    }

    private void OnMonitorStatusChanged(bool isRunning)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_notifyIcon == null) return;
            _notifyIcon.Text = isRunning ? "YTNotifier - 監視中" : "YTNotifier - 停止中";
            try
            {
                var oldIcon = _notifyIcon.Icon;
                _notifyIcon.Icon = LoadIcon(isRunning ? "app.ico" : "app_warn.ico");
                if (oldIcon != SystemIcons.Application) oldIcon?.Dispose();
            }
            catch { }
        });
    }

    private static void ApplyTrayMenuTheme(ContextMenuStrip menu)
    {
        menu.BackColor       = ColorBackground;
        menu.ForeColor       = ColorForeground;
        menu.Font            = new System.Drawing.Font("Yu Gothic UI", 9.5f);
        menu.ShowImageMargin = false;
        menu.ShowCheckMargin = false;
        menu.Padding         = new Padding(4, 4, 4, 4);
        menu.Renderer        = new TrayMenuRenderer();
        menu.Opening        += (_, _) =>
        {
            foreach (ToolStripItem item in menu.Items)
                ApplyTrayMenuItemTheme(item);
        };
    }

    private static void ApplyTrayMenuItemTheme(ToolStripItem item)
    {
        if (item is ToolStripMenuItem mi)
        {
            mi.BackColor = ColorBackground;
            mi.ForeColor = ColorForeground;
            mi.Padding   = new Padding(8, 4, 8, 4);
        }
        else if (item is ToolStripSeparator sep)
        {
            sep.BackColor = ColorBackground;
            sep.ForeColor = ColorBorder;
        }
    }

    private static Icon LoadIcon(string fileName)
    {
        var uri = new Uri($"pack://application:,,,/Resources/{fileName}");
        var sri = System.Windows.Application.GetResourceStream(uri);
        if (sri != null) return new Icon(sri.Stream);

        var exeDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? "";
        var path   = Path.Combine(exeDir, "Resources", fileName);
        if (File.Exists(path)) return new Icon(path);

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        MonitorService.Instance.StatusChanged  -= OnMonitorStatusChanged;
        MonitorService.Instance.QuotaUpdated   -= OnQuotaUpdated;
        _notifyIcon?.Dispose();
    }

    // ── カスタムレンダラー ────────────────────────────────────────

    private class TrayMenuRenderer : ToolStripProfessionalRenderer
    {
        public TrayMenuRenderer() : base(new TrayMenuColorTable()) { }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            var g = e.Graphics;
            var y = e.Item.Height / 2;
            using var pen = new System.Drawing.Pen(ColorBorder);
            g.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (e.Item.Selected)
            {
                using var brush = new System.Drawing.SolidBrush(ColorHover);
                var rc = new System.Drawing.Rectangle(2, 1, e.Item.Width - 4, e.Item.Height - 2);
                e.Graphics.FillRectangle(brush, rc);
            }
            else
            {
                using var brush = new System.Drawing.SolidBrush(ColorBackground);
                e.Graphics.FillRectangle(brush, e.Item.Bounds);
            }
        }
    }

    private class TrayMenuColorTable : ProfessionalColorTable
    {
        public override System.Drawing.Color MenuBorder
            => ColorBorder;
        public override System.Drawing.Color ToolStripDropDownBackground
            => ColorBackground;
        public override System.Drawing.Color ImageMarginGradientBegin
            => ColorBackground;
        public override System.Drawing.Color ImageMarginGradientMiddle
            => ColorBackground;
        public override System.Drawing.Color ImageMarginGradientEnd
            => ColorBackground;
    }
}
