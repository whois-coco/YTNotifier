using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Forms;
using YTNotifier.Services;

namespace YTNotifier.Services;

/// <summary>
/// システムトレイアイコンの初期化・管理を担当するサービス
/// </summary>
public class TrayIconService : IDisposable
{
    private NotifyIcon? _notifyIcon;
    private readonly Action _showMainWindow;
    private readonly Action _exitApp;

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

            var menu = new ContextMenuStrip();
            ApplyTrayMenuTheme(menu);
            menu.Items.Add("🖥  ウィンドウを開く",  null, (_, _) => _showMainWindow());
            menu.Items.Add("🔄  今すぐチェック",    null, async (_, _) =>
            {
                try { await MonitorService.Instance.ManualCheckAsync(); }
                catch (Exception ex) { AppLogger.Log(LogMsg.CheckFailed, null, ex.Message); }
            });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("▶  監視開始",           null, (_, _) => MonitorService.Instance.Start());
            menu.Items.Add("⏸  監視停止",           null, (_, _) => MonitorService.Instance.Stop());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("✖  終了",               null, (_, _) => _exitApp());

            foreach (ToolStripItem item in menu.Items)
                ApplyTrayMenuItemTheme(item);

            _notifyIcon.ContextMenuStrip = menu;
            _notifyIcon.DoubleClick     += (_, _) => _showMainWindow();

            MonitorService.Instance.StatusChanged += OnMonitorStatusChanged;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.TrayIconInitFailed, null, ex.Message);
        }
    }

    private void OnMonitorStatusChanged(bool isRunning)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (_notifyIcon == null) return;
            _notifyIcon.Text = isRunning ? "YTNotifier - 監視中" : "YTNotifier - 停止中";
            try { _notifyIcon.Icon = LoadIcon(isRunning ? "app.ico" : "app_warn.ico"); }
            catch { }
        });
    }

    private static void ApplyTrayMenuTheme(ContextMenuStrip menu)
    {
        menu.BackColor       = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
        menu.ForeColor       = System.Drawing.Color.FromArgb(0xE2, 0xE8, 0xF0);
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
            mi.BackColor = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
            mi.ForeColor = System.Drawing.Color.FromArgb(0xE2, 0xE8, 0xF0);
            mi.Padding   = new Padding(8, 4, 8, 4);
        }
        else if (item is ToolStripSeparator sep)
        {
            sep.BackColor = System.Drawing.Color.FromArgb(0x1E, 0x21, 0x30);
            sep.ForeColor = System.Drawing.Color.FromArgb(0x2D, 0x34, 0x4F);
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
        MonitorService.Instance.StatusChanged -= OnMonitorStatusChanged;
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
            using var pen = new System.Drawing.Pen(
                System.Drawing.Color.FromArgb(0x2D, 0x34, 0x4F));
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
}
