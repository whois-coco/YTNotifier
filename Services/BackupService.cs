using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 設定一式のバックアップ（.ytbk）の書き出し・取り込みと、
/// 終了時の自動バックアップ・起動時の自動復元を担当するサービス。
/// </summary>
public sealed class BackupService
{
    private const string SoundsZipPrefix = "Sounds/";
    /// <summary>バックアップ対象とする通知音ファイルの拡張子</summary>
    private const string SoundFileExtension = ".wav";
    /// <summary>カテゴリファイルの破損を検知して自動復元するときの復元理由</summary>
    private const string RestoreReasonCategoryCorrupted = "カテゴリデータが破損していました";

    private readonly SettingsFileStore _fileStore;
    private readonly SettingsService _core;

    internal BackupService(SettingsFileStore fileStore, SettingsService core)
    {
        _fileStore = fileStore;
        _core      = core;
    }

    /// <summary>Sounds フォルダのパス（exe と同じディレクトリ）</summary>
    private static readonly string SoundsDir =
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            AppConstants.DirSounds);

    /// <summary>終了時に自動保存するバックアップの保存先</summary>
    private string BkupPath => Path.Combine(_fileStore.AppDataDir, AppConstants.DirBackup, AppConstants.FileAutoBackup);

    /// <summary>
    /// ファイル名の拡張子が .wav と完全一致するか判定する。
    /// Directory.GetFiles の検索パターン（"*.wav"）は拡張子がちょうど3文字の場合に
    /// 前方一致となり、余分なファイル（例: file.wavx）まで拾うため、検索パターンには頼らず
    /// 明示的に比較する。
    /// </summary>
    private static bool IsWavFile(string fileName)
        => string.Equals(Path.GetExtension(fileName), SoundFileExtension,
                         StringComparison.OrdinalIgnoreCase);

    public string ExportBackup(string destPath, bool includeState = false)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");

        // 拡張子を .ytbk に強制
        var ytbkPath = string.IsNullOrEmpty(destPath)
            ? Path.Combine(_fileStore.AppDataDir, AppConstants.DirBackup, $"backup_{timestamp}{BackupCryptoService.Extension}")
            : Path.ChangeExtension(destPath, BackupCryptoService.Extension);

        // まずメモリ上にZIPを作成
        using var zipMs = new System.IO.MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipMs,
            System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            // 設定ファイル群
            foreach (var file in new[] { _fileStore.ConfigPath, _fileStore.ChannelsPath, _fileStore.CategoriesPath, _fileStore.DormantCategoriesPath, _fileStore.ApiKeyPath, _fileStore.GeminiApiKeyPath })
                if (File.Exists(file)) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            if (includeState && File.Exists(_fileStore.StatePath))
                zip.CreateEntryFromFile(_fileStore.StatePath, Path.GetFileName(_fileStore.StatePath));
            if (includeState && File.Exists(_fileStore.RecentUploadsPath))
                zip.CreateEntryFromFile(_fileStore.RecentUploadsPath, Path.GetFileName(_fileStore.RecentUploadsPath));

            // Sounds フォルダ（フォルダごと再帰。.wav のみを対象とする）
            var soundsDir = SoundsDir;
            if (Directory.Exists(soundsDir))
                foreach (var file in Directory.GetFiles(soundsDir, "*", SearchOption.AllDirectories))
                {
                    if (!IsWavFile(file)) continue;

                    // Sounds\ からの相対パスで ZIPエントリ名を構築
                    var relativePath = file.Substring(soundsDir.Length).TrimStart(Path.DirectorySeparatorChar);
                    var entryName    = SoundsZipPrefix + relativePath.Replace(Path.DirectorySeparatorChar, '/');
                    zip.CreateEntryFromFile(file, entryName);
                }
        }

        // ZIPバイナリを AES-256 で暗号化して .ytbk として出力
        BackupCryptoService.Encrypt(zipMs.ToArray(), ytbkPath);
        return ytbkPath;
    }

    public (bool success, string message) ImportBackup(string path)
    {
        try
        {
            byte[]? zipBytes = null;

            if (path.EndsWith(BackupCryptoService.Extension, StringComparison.OrdinalIgnoreCase)
                || BackupCryptoService.IsYtbk(path))
            {
                // .ytbk: 復号してZIPバイナリを取得
                zipBytes = BackupCryptoService.Decrypt(path);
                if (zipBytes == null)
                    return (false, "バックアップファイルの復号に失敗しました。ファイルが破損しているか、対応していない形式です。");
            }
            else
            {
                return (false, "対応していないファイル形式です（.ytbk）。");
            }

            // ZIPバイナリを展開
            using var zipMs = new System.IO.MemoryStream(zipBytes);
            using var zip   = new System.IO.Compression.ZipArchive(zipMs,
                System.IO.Compression.ZipArchiveMode.Read);

            var allowedFiles = new[] { AppConstants.FileConfig, AppConstants.FileChannels, AppConstants.FileCategories, AppConstants.FileDormantCategories, AppConstants.FileApiKey, AppConstants.FileGeminiApiKey, AppConstants.FileState, AppConstants.FileRecentUploads };

            foreach (var entry in zip.Entries)
            {
                var entryDir = Path.GetDirectoryName(entry.FullName)?.Replace('/', '\\') ?? "";

                // 設定ファイル（ルート直下）
                if (allowedFiles.Contains(entry.Name) &&
                    string.IsNullOrEmpty(entryDir))
                {
                    var destPath    = Path.Combine(_fileStore.AppDataDir, AppConstants.DirConf, entry.Name);
                    var fullDest    = Path.GetFullPath(destPath);
                    var allowedRoot = Path.GetFullPath(Path.Combine(_fileStore.AppDataDir, AppConstants.DirConf)) + Path.DirectorySeparatorChar;
                    if (!fullDest.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                        continue;
                    entry.ExtractToFile(destPath, overwrite: true);
                    continue;
                }

                // Sounds フォルダ（フォルダ構造ごと exe ディレクトリに復元。.wav のみ）
                var fullNameFwd = entry.FullName.Replace('\\', '/');
                if (fullNameFwd.StartsWith(SoundsZipPrefix, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(entry.Name))
                {
                    // .wav 以外は展開しない（旧バージョンで作成されたバックアップ・
                    // 細工されたバックアップに .wav 以外が含まれている場合の防御）
                    if (!IsWavFile(entry.Name)) continue;

                    var exeDir = Path.GetDirectoryName(
                                     Environment.ProcessPath)
                              ?? Path.GetDirectoryName(
                                     System.Reflection.Assembly.GetExecutingAssembly().Location)
                              ?? AppDomain.CurrentDomain.BaseDirectory;
                    exeDir = exeDir.TrimEnd(Path.DirectorySeparatorChar);
                    if (string.IsNullOrEmpty(exeDir)) continue;

                    var destPath = Path.Combine(exeDir,
                        fullNameFwd.Replace('/', Path.DirectorySeparatorChar));

                    // Zip Slip 防止：展開先が想定ディレクトリ配下であることを確認
                    var fullDest    = Path.GetFullPath(destPath);
                    var allowedRoot = Path.GetFullPath(Path.Combine(exeDir, AppConstants.DirSounds)) + Path.DirectorySeparatorChar;
                    if (!fullDest.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                    entry.ExtractToFile(destPath, overwrite: true);
                }
            }

            _core.Load();
            return (true, "インポートが完了しました。再起動が不要な設定はすぐに反映されます。");
        }
        catch (Exception ex)
        {
            return (false, $"インポートに失敗しました: {ex.Message}");
        }
    }

    /// <summary>アプリ終了時にdirtyなら bkup/auto_backup.ytbk へ保存</summary>
    public void SaveAutoBackupIfDirty()
    {
        if (!_core.IsDirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BkupPath)!);
            ExportBackup(BkupPath);
            _core.ClearDirty();
            AppLogger.Log(LogMsg.BackupSaved);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.BackupFailed, null, ex.Message);
        }
    }

    /// <summary>
    /// 起動時にデータ破損・消失・0バイト・チャンネル0件を検知し、
    /// auto_backup.ytbk が存在すればサイレント復元する。
    /// 復元した場合は復元理由を、しなかった場合は null を返す。
    /// </summary>
    public string? TryAutoRestore()
    {
        if (!File.Exists(BkupPath)) return null;

        bool needRestore = false;

        // 1. 必須ファイルが存在しない
        string restoreReason = "";
        if (!File.Exists(_fileStore.ConfigPath) || !File.Exists(_fileStore.ChannelsPath))
        { needRestore = true; restoreReason = "設定ファイルが見つかりませんでした"; }

        // 2. ファイルサイズが0バイト
        if (!needRestore)
        {
            foreach (var requiredFilePath in new[] { _fileStore.ConfigPath, _fileStore.ChannelsPath })
                if (new FileInfo(requiredFilePath).Length == 0)
                { needRestore = true; restoreReason = "設定ファイルが0バイトでした"; break; }
        }

        // 3. JSONパース失敗
        if (!needRestore)
        {
            try { Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(_fileStore.ChannelsPath)); }
            catch { needRestore = true; restoreReason = "設定ファイルが破損していました"; }
        }
        if (!needRestore)
        {
            try { Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(_fileStore.ConfigPath)); }
            catch { needRestore = true; restoreReason = "設定ファイルが破損していました"; }
        }

        // 3b. カテゴリファイルの破損（ファイルが存在する場合のみ判定。0件は正常な状態のため復元条件にしない）
        if (!needRestore)
        {
            foreach (var categoryFilePath in new[] { _fileStore.CategoriesPath, _fileStore.DormantCategoriesPath })
            {
                if (!File.Exists(categoryFilePath)) continue;
                try { JsonConvert.DeserializeObject<List<CategoryInfo>>(File.ReadAllText(categoryFilePath)); }
                catch { needRestore = true; restoreReason = RestoreReasonCategoryCorrupted; break; }
            }
        }

        // 4. チャンネルが0件（バックアップに1件以上ある場合のみ）
        if (!needRestore)
        {
            try
            {
                var channels = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.ChannelInfo>>(
                    File.ReadAllText(_fileStore.ChannelsPath));
                if (channels == null || channels.Count == 0)
                {
                    var bkupChannels = PeekChannelCountFromBackup(BkupPath);
                    // bkupChannels > 0: バックアップにチャンネルあり
                    // bkupChannels == -1: バックアップ読み取り不能（念のため復元試行）
                    if (bkupChannels != 0)
                    { needRestore = true; restoreReason = "チャンネルが0件でした"; }
                }
            }
            catch { needRestore = true; restoreReason = "チャンネルデータが破損していました"; }
        }

        if (!needRestore) return null;

        // サイレント復元実行
        try
        {
            var (success, message) = ImportBackup(BkupPath);
            if (!success)
            {
                AppLogger.Log(LogMsg.AutoRestoreFailed, null, message);
                return null;
            }
            return restoreReason;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.AutoRestoreFailed, null, ex.Message);
            return null;
        }
    }

    /// <summary>バックアップ内のチャンネル数を取得（復元せず確認）</summary>
    private static int PeekChannelCountFromBackup(string backupPath)
    {
        try
        {
            byte[]? zipBytes;
            if (BackupCryptoService.IsYtbk(backupPath))
                zipBytes = BackupCryptoService.Decrypt(backupPath);
            else
                zipBytes = File.ReadAllBytes(backupPath);

            if (zipBytes == null) return 0;
            using var zipMs  = new MemoryStream(zipBytes);
            using var zip    = new System.IO.Compression.ZipArchive(zipMs, System.IO.Compression.ZipArchiveMode.Read);
            var entry        = zip.GetEntry(AppConstants.FileChannels);
            if (entry == null) return 0;
            using var reader = new System.IO.StreamReader(entry.Open());
            var channels     = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.ChannelInfo>>(reader.ReadToEnd());
            return channels?.Count ?? 0;
        }
        catch { return -1; }  // -1 = 読み取り不能（0チャンネルとは区別する）
    }
}
