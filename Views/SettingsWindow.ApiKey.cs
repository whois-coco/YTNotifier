using System;
using System.Windows;
using System.Windows.Controls;
using YTNotifier.Services;
using Button  = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;

namespace YTNotifier.Views;

// 部分クラス: APIキー入力・保存・スロット切り替え
public partial class SettingsWindow : Window
{
    private int    _selectedApiKeySlotIndex = 0;
    private string _actualApiKey            = "";

    private const string ApiKeyButtonEditLabel = "変更";
    private const string ApiKeyButtonSaveLabel = "保存";

    /// <summary>APIキー欄が今マスク表示（保存済み）中かどうか。true＝マスク表示中（＝「変更」ボタン）、false＝編集中（＝「保存」ボタン）。</summary>
    private bool _apiKeySaved;

    private void SaveApiKey_Click(object sender, RoutedEventArgs e)
    {
        var isSummaryKeySlot = _selectedApiKeySlotIndex == 1;

        if (_apiKeySaved)
        {
            AppLogger.Log(isSummaryKeySlot ? LogMsg.GeminiApiKeyEditStarted : LogMsg.ApiKeyEditStarted);
            UpdateApiKeyState(false);
            ApiKeyBox.Text = "";
            return;
        }

        var key = ApiKeyBox.IsReadOnly ? _actualApiKey : ApiKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            if (!string.IsNullOrEmpty(_actualApiKey))
            {
                // 既存キーがある状態で未入力のまま保存 → 変更なしとしてマスク表示に戻す
                AppLogger.Log(isSummaryKeySlot ? LogMsg.GeminiApiKeyUnchanged : LogMsg.ApiKeyUnchanged);
                UpdateApiKeyState(true);
                return;
            }
            // 未設定のまま保存 → 空文字として保存処理に続行
        }
        _actualApiKey = key;

        if (isSummaryKeySlot)
        {
            GeminiApiKeyService.Save(SettingsService.Instance.ConfDir, key);
            AppLogger.Log(string.IsNullOrEmpty(key) ? LogMsg.GeminiApiKeyChanged : LogMsg.GeminiApiKeySaved);
        }
        else
        {
            SettingsService.Instance.Settings.ApiKey = key;
            ApiKeyService.Save(SettingsService.Instance.ConfDir, key);
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(string.IsNullOrEmpty(key) ? LogMsg.ApiKeyChanged : LogMsg.ApiKeySaved);
        }
        UpdateApiKeyState(true);
    }

    private void UpdateApiKeyState(bool saved)
    {
        _apiKeySaved = saved;
        if (saved)
        {
            ApiKeyBox.Text          = new string('●', Math.Min(_actualApiKey.Length, 32));
            ApiKeyBox.IsReadOnly    = true;
            ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Center;
            MainWindow.SetDynamicBrush(ApiKeyBox, TextBox.BackgroundProperty, "SurfaceAltBrush");
            MainWindow.SetDynamicBrush(ApiKeyBox, TextBox.ForegroundProperty, "TextMutedBrush");
        }
        else
        {
            ApiKeyBox.Text          = "";
            ApiKeyBox.IsReadOnly    = false;
            ApiKeyBox.TextAlignment = System.Windows.TextAlignment.Left;
            MainWindow.SetDynamicBrush(ApiKeyBox, TextBox.BackgroundProperty, "SurfaceBrush");
            MainWindow.SetDynamicBrush(ApiKeyBox, TextBox.ForegroundProperty, "TextPrimaryBrush");
            ApiKeyBox.Focus();
        }
        SaveApiKeyButton.Content = saved ? ApiKeyButtonEditLabel : ApiKeyButtonSaveLabel;
        MainWindow.SetDynamicBrush(SaveApiKeyButton, Button.BackgroundProperty, saved ? "SurfaceElevatedBrush" : "PrimaryBrush");
        if (saved) MainWindow.SetDynamicBrush(SaveApiKeyButton, Button.ForegroundProperty, "TextPrimaryBrush");
        else       MainWindow.SetDynamicBrush(SaveApiKeyButton, Button.ForegroundProperty, "TextOnColorBrush");
    }

    private void ApiKeyLink_Click(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        BrowserLaunchHelper.OpenUrl(e.Uri.AbsoluteUri); e.Handled = true;
    }

    // ===== APIキースロット管理 =====

    private void InitApiKeySlotComboBox()
    {
        _loadingSettings = true;
        ApiKeySlotComboBox.Items.Clear();
        ApiKeySlotComboBox.Items.Add("チェック用キー");
        ApiKeySlotComboBox.Items.Add("要約キー");
        _selectedApiKeySlotIndex = 0;
        ApiKeySlotComboBox.SelectedIndex = 0;
        _loadingSettings = false;
    }

    private void ApiKeySlotComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingSettings) return;
        var idx = ApiKeySlotComboBox.SelectedIndex;
        if (idx < 0) return;
        _selectedApiKeySlotIndex = idx;

        if (idx == 1)
        {
            _actualApiKey = GeminiApiKeyService.Load(SettingsService.Instance.ConfDir) ?? string.Empty;
        }
        else
        {
            _actualApiKey = SettingsService.Instance.Settings.ApiKey;
        }
        UpdateApiKeyState(!string.IsNullOrEmpty(_actualApiKey));
    }
}
