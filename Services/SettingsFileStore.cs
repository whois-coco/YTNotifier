using System.IO;
using YTNotifier.Constants;

namespace YTNotifier.Services;

/// <summary>
/// 保存系サービス共通の土台。保存先フォルダ・各ファイルの絶対パスと、
/// アトミック書き込み・保存エラーの記録手段だけを持つ道具役（状態を持たない）。
/// </summary>
internal sealed class SettingsFileStore
{
    public string AppDataDir { get; }
    public string ConfDir { get; }
    public string ConfigPath { get; }
    public string ChannelsPath { get; }
    public string CategoriesPath { get; }
    public string DormantCategoriesPath { get; }
    public string StatePath { get; }
    public string RecentUploadsPath { get; }
    public string ReportHistoryPath { get; }
    public string ApiKeyPath { get; }
    public string GeminiApiKeyPath { get; }

    public SettingsFileStore()
    {
        AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppConstants.AppName);
        var confDir  = Path.Combine(AppDataDir, AppConstants.DirConf);
        var bkupDir  = Path.Combine(AppDataDir, AppConstants.DirBackup);
        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(bkupDir);
        Directory.CreateDirectory(Path.Combine(AppDataDir, AppConstants.DirLogs));
        ConfigPath             = Path.Combine(confDir, AppConstants.FileConfig);
        ChannelsPath           = Path.Combine(confDir, AppConstants.FileChannels);
        CategoriesPath         = Path.Combine(confDir, AppConstants.FileCategories);
        DormantCategoriesPath  = Path.Combine(confDir, AppConstants.FileDormantCategories);
        StatePath              = Path.Combine(confDir, AppConstants.FileState);
        RecentUploadsPath      = Path.Combine(confDir, AppConstants.FileRecentUploads);
        ReportHistoryPath      = Path.Combine(confDir, AppConstants.FileReportHistory);
        ApiKeyPath             = Path.Combine(confDir, AppConstants.FileApiKey);
        GeminiApiKeyPath       = Path.Combine(confDir, AppConstants.FileGeminiApiKey);
        ConfDir                = confDir;

        // 旧パス（フラット構造）からの移行
        foreach (var fname in new[] { AppConstants.FileConfig, AppConstants.FileChannels, AppConstants.FileCategories, AppConstants.FileApiKey })
        {
            var oldPath = Path.Combine(AppDataDir, fname);
            var newPath = Path.Combine(confDir, fname);
            if (File.Exists(oldPath) && !File.Exists(newPath))
                File.Move(oldPath, newPath);
        }
    }

    /// <summary>保存系エラーをクラッシュログへ直接書き込む（LoggerService に依存しない）</summary>
    public void WriteSaveError(string context, string message)
    {
        try
        {
            var logDir = Path.Combine(AppDataDir, AppConstants.DirLogs);
            File.AppendAllText(
                Path.Combine(logDir, $"{DateTime.Now:yyyy-MM-dd}_crash.log"),
                $"[{DateTime.Now:HH:mm:ss}] [SaveError:{context}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    /// <summary>
    /// 一時ファイルに書き込んでからリネームするアトミック書き込み。
    /// WriteAllText の途中でプロセスが終了しても元ファイルが破損しない。
    /// </summary>
    public static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, System.Text.Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    public static void WriteAtomic(string path, byte[] content)
    {
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}
