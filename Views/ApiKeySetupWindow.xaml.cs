using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using KeyEventArgs  = System.Windows.Input.KeyEventArgs;
using Application   = System.Windows.Application;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ApiKeySetupWindow : Window
{
    public bool ApiKeySaved { get; private set; } = false;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    public ApiKeySetupWindow()
    {
        InitializeComponent();
        ApiKeyBox.Focus();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            SendMessage(new WindowInteropHelper(this).Handle, WM_NCLBUTTONDOWN, new IntPtr(HTCAPTION), IntPtr.Zero);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        // ✕ボタンはアプリ終了
        Application.Current.Shutdown();
    }

    private void ApiKeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) SaveButton_Click(sender, e);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            ApiKeyBox.Focus();
            return;
        }
        SettingsService.Instance.Settings.ApiKey = key;
        SettingsService.Instance.SaveSettings();
        ApiKeySaved = true;
        Close();
    }

    private void ApiKeyLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title      = "バックアップファイルを選択",
            Filter     = "YTNotifierバックアップ (*.ytbk)|*.ytbk|ZIPファイル (*.zip)|*.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;

        var (success, message) = Services.SettingsService.Instance.ImportBackup(dlg.FileName);
        if (success)
        {
            var apiKey = Services.SettingsService.Instance.Settings.ApiKey;
            if (!string.IsNullOrEmpty(apiKey))
            {
                ApiKeySaved = true;
                Close();
            }
            else
            {
                System.Windows.MessageBox.Show("復元しましたが APIキーが含まれていませんでした。\n手動で入力してください。");
            }
        }
        else
        {
            System.Windows.MessageBox.Show(message);
        }
    }
}
