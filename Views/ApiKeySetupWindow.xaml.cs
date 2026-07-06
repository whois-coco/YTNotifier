using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using KeyEventArgs  = System.Windows.Input.KeyEventArgs;
using Application   = System.Windows.Application;
using YTNotifier.Constants;
using YTNotifier.Services;

namespace YTNotifier.Views;

public partial class ApiKeySetupWindow : Window
{
    public bool ApiKeySaved { get; private set; } = false;

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    private const int WM_NCLBUTTONDOWN = 0xA1;
    private const int HTCAPTION        = 2;

    private const int IntroStepIndex      = 0;
    private const int FirstImageStepIndex = 1;
    private const int KeyInputStepIndex   = AppConstants.ApiKeySetupStepCount - 1;

    private static readonly string[] _stepDescriptions =
    {
        "Google Cloud Consoleにアクセスします。下のボタンをクリックしてブラウザでページを開いてください。多要素認証が未設定のGoogleアカウントをお使いの場合、「Google Cloudへのアクセスがブロックされました」という画面が表示されることがあります。表示された場合は「MFAを有効にする」をクリックし、次のステップに進んでください。（設定完了後はこのページに戻り、ブラウザを更新してください）",
        "別ウィンドウでGoogleアカウントのログイン画面が表示されます。パスワードを入力し、「次へ」をクリックしてください。",
        "「2段階認証プロセス」の設定画面が表示されます。案内に従って設定し、「2段階認証プロセスを有効にする」をクリックしてください。完了したら、先ほどの画面に戻ってブラウザを更新（再読み込み）してください。反映まで60秒ほどかかることがあります。",
        "Google Cloudの利用規約同意画面が表示されます。国を選択し、利用規約のチェックボックスをオンにして、「同意して続行」をクリックしてください。",
        "画面左上の「プロジェクトの選択」をクリックしてください。",
        "「新しいプロジェクト」画面が表示されます。プロジェクト名にはデフォルトの名前があらかじめ入力されています。そのままで問題なければ「作成」をクリックしてください（変更したい場合は入力し直してください）。",
        "プロジェクトが作成されると通知が表示されます。「プロジェクトを選択」をクリックしてください。",
        "画面左側のナビゲーションメニューから「APIとサービス」→「有効なAPIとサービス」の順にクリックしてください。",
        "「APIとサービス」画面が表示されます。「＋ APIとサービスを有効にする」をクリックしてください。",
        "検索結果の一覧から「YouTube Data API v3」をクリックして選択してください。",
        "YouTube Data API v3の詳細ページが表示されます。「API が有効です」と表示されていることを確認し、「管理」ボタンをクリックしてください。",
        "ページ上部の「認証情報」タブをクリックしてください。",
        "画面右側に表示される「＋ 認証情報を作成」をクリックしてください。",
        "表示されたメニューから「APIキー」を選択してください。",
        "「APIキーの作成」画面が表示されます。「APIの制限の選択」をクリックしてください。",
        "一覧から「YouTube Data API v3」にチェックを入れ、「OK」をクリックしてください。",
        "「アプリケーションの制限」で「なし」を選択し、「作成」をクリックしてください。",
        "APIキーが発行されます。表示された文字列をコピーボタンをクリックし、コピーしておいてください。次の画面でこのキーを入力します。",
    };

    private static readonly string[] _stepTitles =
    {
        "Google Cloud Consoleにアクセス",
        "Googleアカウントにログイン",
        "2段階認証を設定",
        "利用規約に同意",
        "プロジェクトを選択",
        "新しいプロジェクトを作成",
        "作成したプロジェクトを選択",
        "APIとサービスを開く",
        "APIとサービスを有効にする",
        "YouTube Data API v3を検索",
        "APIの有効化を確認",
        "認証情報タブを開く",
        "認証情報を作成",
        "APIキーを選択",
        "APIの制限を選択",
        "YouTube Data API v3を制限に追加",
        "アプリケーションの制限を設定",
        "APIキーの取得完了",
    };

    private static readonly System.Net.Http.HttpClient _http =
        new() { Timeout = TimeSpan.FromSeconds(10) };

    private int _currentStep = IntroStepIndex;
    private readonly BitmapImage?[] _stepImages = new BitmapImage?[AppConstants.StepByStepImageCount];

    public ApiKeySetupWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            WindowCornerHelper.Apply(this);
            RenderStep();
            _ = LoadStepImagesAsync();
        };
    }

    private async Task LoadStepImagesAsync()
    {
        var tasks = Enumerable.Range(0, AppConstants.StepByStepImageCount)
            .Select(LoadSingleStepImageAsync);
        await Task.WhenAll(tasks);
    }

    private async Task LoadSingleStepImageAsync(int index)
    {
        try
        {
            var url   = $"{AppConstants.StepByStepImageBaseUrl}step{index + 1}.png";
            var bytes = await _http.GetByteArrayAsync(url);
            var bmp   = new BitmapImage();
            using (var stream = new MemoryStream(bytes))
            {
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.CacheOption  = BitmapCacheOption.OnLoad;
                bmp.EndInit();
            }
            bmp.Freeze();
            _stepImages[index] = bmp;
        }
        catch
        {
            _stepImages[index] = null;
        }

        if (_currentStep == FirstImageStepIndex + index)
            RenderStep();
    }

    private void RenderStep()
    {
        IntroPanel.Visibility    = _currentStep == IntroStepIndex    ? Visibility.Visible : Visibility.Collapsed;
        KeyInputPanel.Visibility = _currentStep == KeyInputStepIndex ? Visibility.Visible : Visibility.Collapsed;

        IntroTitleText.Text    = $"step:{IntroStepIndex + 1} はじめに";
        KeyInputTitleText.Text = $"step:{KeyInputStepIndex + 1} APIキーを入力";

        bool isImageStep = _currentStep is >= FirstImageStepIndex and < KeyInputStepIndex;
        StepImagePanel.Visibility = isImageStep ? Visibility.Visible : Visibility.Collapsed;

        if (isImageStep)
        {
            var imageIndex = _currentStep - FirstImageStepIndex;
            StepImage.Source            = _stepImages[imageIndex];
            StepTitleText.Text          = $"step:{_currentStep + 1} {_stepTitles[imageIndex]}";
            StepDescriptionText.Text    = _stepDescriptions[imageIndex];
            OpenConsoleButton.Visibility = imageIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        BackButton.Visibility = _currentStep == IntroStepIndex    ? Visibility.Collapsed : Visibility.Visible;
        NextButton.Visibility = _currentStep == KeyInputStepIndex ? Visibility.Collapsed : Visibility.Visible;
        ProgressText.Text = $"{_currentStep + 1}/{AppConstants.ApiKeySetupStepCount}";

        if (_currentStep == KeyInputStepIndex) ApiKeyBox.Focus();
    }

    private void NextButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep < AppConstants.ApiKeySetupStepCount - 1)
        {
            _currentStep++;
            RenderStep();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_currentStep > IntroStepIndex)
        {
            _currentStep--;
            RenderStep();
        }
    }

    private void SkipLink_Click(object sender, RoutedEventArgs e)
    {
        _currentStep = KeyInputStepIndex;
        RenderStep();
    }

    private void OpenConsoleButton_Click(object sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo(AppConstants.GoogleCloudConsoleUrl) { UseShellExecute = true });
    }

    private void DiscardStepImages()
    {
        Array.Clear(_stepImages, 0, _stepImages.Length);
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

    private async void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var key = ApiKeyBox.Text.Trim();
        if (string.IsNullOrEmpty(key))
        {
            ApiKeyBox.Focus();
            return;
        }

        SaveButton.IsEnabled = false;
        try
        {
            var result = await new YouTubeApiClient().TestApiKeyAsync(key, YouTubeConstants.ApiKeyTestChannelId);
            if (result == ApiKeyTestResult.Invalid)
            {
                System.Windows.MessageBox.Show("入力されたAPIキーは無効です。キーをご確認のうえ、再度入力してください。");
                ApiKeyBox.Focus();
                return;
            }
            if (result == ApiKeyTestResult.NetworkError)
            {
                System.Windows.MessageBox.Show("APIキーの有効性を確認できませんでした。ネットワーク接続をご確認のうえ、再度お試しください。");
                return;
            }

            var keys = SettingsService.Instance.Settings.ApiKeys;
            if (keys.Count == 0) keys.Add(key);
            else keys[0] = key;
            ApiKeyService.Save(SettingsService.Instance.ConfDir, keys);
            SettingsService.Instance.SaveSettings();
            AppLogger.Log(LogMsg.ApiKeyChanged);
            DiscardStepImages();
            ApiKeySaved = true;
            Close();
        }
        finally
        {
            SaveButton.IsEnabled = true;
        }
    }

    private void RestoreButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title      = "バックアップファイルを選択",
            Filter     = AppConstants.BackupFileFilter,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)
        };
        if (dlg.ShowDialog() != true) return;

        var (success, message) = Services.SettingsService.Instance.ImportBackup(dlg.FileName);
        if (success)
        {
            AppLogger.Log(LogMsg.SettingBackupImported, null, System.IO.Path.GetFileName(dlg.FileName));
            var apiKeys = Services.SettingsService.Instance.Settings.ApiKeys;
            var apiKey = apiKeys.Count > 0 ? apiKeys[0] : string.Empty;
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
