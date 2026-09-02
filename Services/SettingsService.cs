using System.IO;
using System.IO.Compression;
using Newtonsoft.Json;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

public class SettingsService
{
    private const string DirConf        = "conf";
    private const string DirBackup      = "bkup";
    private const string FileConfig     = "config.json";
    private const string FileChannels   = "channels.json";
    private const string FileCategories = "categories.json";
    private const string FileState      = "state.json";
    private const string FileAutoBackup = "auto_backup.ytbk";
    private const string FileDormantCategories = "dormant_categories.json";
    private const string FileRecentUploads = "recent_uploads.json";
    private const string SoundsZipPrefix = "Sounds/";
    /// <summary>バックアップ対象とする通知音ファイルの拡張子</summary>
    private const string SoundFileExtension = ".wav";
    private const int    StateWriteIntervalMs = 60 * 60 * 1000; // 1時間

    private static readonly Lazy<SettingsService> _lazy = new(() => new SettingsService());
    public static SettingsService Instance => _lazy.Value;

    private readonly string _appDataDir;
    private readonly string _configPath;
    private readonly string _channelsPath;
    private readonly string _categoriesPath;
    private readonly string _dormantCategoriesPath;
    private readonly string _statePath;
    private readonly string _recentUploadsPath;
    private readonly string _apiKeyPath;
    private readonly string _geminiApiKeyPath;
    private readonly string _confDir;

    public string ConfDir => _confDir;

    public AppSettings Settings { get; private set; } = new();
    public List<ChannelInfo> Channels { get; private set; } = new();
    public List<CategoryInfo> Categories { get; private set; } = new();
    public List<CategoryInfo> DormantCategories { get; private set; } = new();
    public AppState AppState { get; private set; } = new();

    // Channels/Categories への並行アクセスを直列化するロック
    private readonly object _persistLock = new();
    // AddApiUnits の並行呼び出しを直列化するロック
    private readonly object _apiUnitsLock = new();

    private System.Threading.Timer _stateTimer;

    /// <summary>保存系エラーをクラッシュログへ直接書き込む（LoggerService に依存しない）</summary>
    private void WriteSaveError(string context, string message)
    {
        try
        {
            var logDir = Path.Combine(_appDataDir, AppConstants.DirLogs);
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
    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content, System.Text.Encoding.UTF8);
        File.Move(tmp, path, overwrite: true);
    }

    private SettingsService()
    {
        _appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            AppConstants.AppName);
        var confDir  = Path.Combine(_appDataDir, DirConf);
        var bkupDir  = Path.Combine(_appDataDir, DirBackup);
        Directory.CreateDirectory(confDir);
        Directory.CreateDirectory(bkupDir);
        Directory.CreateDirectory(Path.Combine(_appDataDir, AppConstants.DirLogs));
        _configPath             = Path.Combine(confDir, FileConfig);
        _channelsPath           = Path.Combine(confDir, FileChannels);
        _categoriesPath         = Path.Combine(confDir, FileCategories);
        _dormantCategoriesPath  = Path.Combine(confDir, FileDormantCategories);
        _statePath              = Path.Combine(confDir, FileState);
        _recentUploadsPath      = Path.Combine(confDir, FileRecentUploads);
        _apiKeyPath             = Path.Combine(confDir, AppConstants.FileApiKey);
        _geminiApiKeyPath       = Path.Combine(confDir, AppConstants.FileGeminiApiKey);
        _confDir                = confDir;

        // 旧パス（フラット構造）からの移行
        foreach (var fname in new[] { FileConfig, FileChannels, FileCategories, AppConstants.FileApiKey })
        {
            var oldPath = Path.Combine(_appDataDir, fname);
            var newPath = Path.Combine(confDir, fname);
            if (File.Exists(oldPath) && !File.Exists(newPath))
                File.Move(oldPath, newPath);
        }

        _stateTimer = new System.Threading.Timer(
            _ => SaveStateAndSnapshotsInternal(), null, StateWriteIntervalMs, StateWriteIntervalMs);
    }

    public string AppDataDir => _appDataDir;

    // ===== バックアップ / インポート =====

    /// <summary>Sounds フォルダのパス（exe と同じディレクトリ）</summary>
    private static readonly string SoundsDir =
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            AppConstants.DirSounds);

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
            ? Path.Combine(_appDataDir, DirBackup, $"backup_{timestamp}{BackupCryptoService.Extension}")
            : Path.ChangeExtension(destPath, BackupCryptoService.Extension);

        // まずメモリ上にZIPを作成
        using var zipMs = new System.IO.MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(zipMs,
            System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            // 設定ファイル群
            foreach (var file in new[] { _configPath, _channelsPath, _categoriesPath, _dormantCategoriesPath, _apiKeyPath, _geminiApiKeyPath })
                if (File.Exists(file)) zip.CreateEntryFromFile(file, Path.GetFileName(file));
            if (includeState && File.Exists(_statePath))
                zip.CreateEntryFromFile(_statePath, Path.GetFileName(_statePath));

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
            else if (path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // 旧形式 .zip をそのまま読み込み（後方互換）
                zipBytes = File.ReadAllBytes(path);
            }
            else
            {
                return (false, "対応していないファイル形式です（.ytbk または .zip）。");
            }

            // ZIPバイナリを展開
            using var zipMs = new System.IO.MemoryStream(zipBytes);
            using var zip   = new System.IO.Compression.ZipArchive(zipMs,
                System.IO.Compression.ZipArchiveMode.Read);

            var allowedFiles = new[] { FileConfig, FileChannels, FileCategories, FileDormantCategories, AppConstants.FileApiKey, AppConstants.FileGeminiApiKey, FileState };

            foreach (var entry in zip.Entries)
            {
                var entryDir = Path.GetDirectoryName(entry.FullName)?.Replace('/', '\\') ?? "";

                // 設定ファイル（ルート直下）
                if (allowedFiles.Contains(entry.Name) &&
                    string.IsNullOrEmpty(entryDir))
                {
                    var destPath    = Path.Combine(_appDataDir, DirConf, entry.Name);
                    var fullDest    = Path.GetFullPath(destPath);
                    var allowedRoot = Path.GetFullPath(Path.Combine(_appDataDir, DirConf)) + Path.DirectorySeparatorChar;
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

            Load();
            return (true, "インポートが完了しました。再起動が不要な設定はすぐに反映されます。");
        }
        catch (Exception ex)
        {
            return (false, $"インポートに失敗しました: {ex.Message}");
        }
    }

    private void LoadCategories()
    {
        try
        {
            if (File.Exists(_categoriesPath))
            {
                var json = File.ReadAllText(_categoriesPath);
                Categories = JsonConvert.DeserializeObject<List<CategoryInfo>>(json) ?? new();
            }
        }
        catch { Categories = new(); }
    }

    private void LoadDormantCategories()
    {
        try
        {
            if (File.Exists(_dormantCategoriesPath))
            {
                var json = File.ReadAllText(_dormantCategoriesPath);
                DormantCategories = JsonConvert.DeserializeObject<List<CategoryInfo>>(json) ?? new();
            }
        }
        catch { DormantCategories = new(); }
    }

    public void SaveDormantCategories() => SaveDormantCategoriesInternal(markDirty: true);

    private void SaveDormantCategoriesInternal(bool markDirty)
    {
        try
        {
            string json;
            lock (_persistLock)
                json = JsonConvert.SerializeObject(DormantCategories, Formatting.Indented);
            WriteAtomic(_dormantCategoriesPath, json);
            if (markDirty) MarkDirty();
        }
        catch (Exception ex) { WriteSaveError("SaveDormantCategories", ex.Message); }
    }

    // ===== 自動バックアップ用ダーティフラグ =====
    private bool _dirty = false;
    private string BkupPath => Path.Combine(_appDataDir, DirBackup, FileAutoBackup);
    public  string AutoBackupPath => BkupPath;

    /// <summary>チャンネル/カテゴリ/設定の変更時に呼ぶ（VideoIDなど監視系は除く）</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>アプリ終了時にdirtyなら bkup/auto_backup.ytbk へ保存</summary>
    public void SaveAutoBackupIfDirty()
    {
        if (!_dirty) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(BkupPath)!);
            ExportBackup(BkupPath);
            _dirty = false;
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
        if (!File.Exists(_configPath) || !File.Exists(_channelsPath))
        { needRestore = true; restoreReason = "設定ファイルが見つかりませんでした"; }

        // 2. ファイルサイズが0バイト
        if (!needRestore)
        {
            foreach (var f in new[] { _configPath, _channelsPath })
                if (new FileInfo(f).Length == 0)
                { needRestore = true; restoreReason = "設定ファイルが0バイトでした"; break; }
        }

        // 3. JSONパース失敗
        if (!needRestore)
        {
            try { Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(_channelsPath)); }
            catch { needRestore = true; restoreReason = "設定ファイルが破損していました"; }
        }
        if (!needRestore)
        {
            try { Newtonsoft.Json.JsonConvert.DeserializeObject(File.ReadAllText(_configPath)); }
            catch { needRestore = true; restoreReason = "設定ファイルが破損していました"; }
        }

        // 4. チャンネルが0件（バックアップに1件以上ある場合のみ）
        if (!needRestore)
        {
            try
            {
                var channels = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.ChannelInfo>>(
                    File.ReadAllText(_channelsPath));
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
            var entry        = zip.GetEntry(FileChannels);
            if (entry == null) return 0;
            using var reader = new System.IO.StreamReader(entry.Open());
            var channels     = Newtonsoft.Json.JsonConvert.DeserializeObject<List<Models.ChannelInfo>>(reader.ReadToEnd());
            return channels?.Count ?? 0;
        }
        catch { return -1; }  // -1 = 読み取り不能（0チャンネルとは区別する）
    }

    /// <summary>API使用ユニットを加算（日付をまたいだらリセット）</summary>
    public void AddApiUnits(int units)
    {
        lock (_apiUnitsLock)
        {
            var quotaKey = AppConstants.GetQuotaDayKey();
            if (AppState.TodayApiDate != quotaKey)
            {
                AppState.TodayApiUnits = 0;
                AppState.TodayApiDate  = quotaKey;
            }
            AppState.TodayApiUnits += units;
        }
        MonitorService.Instance.NotifyQuotaUpdated();
    }

    public void SaveCategories() => SaveCategoriesInternal();

    private void SaveCategoriesInternal()
    {
        try
        {
            string json;
            lock (_persistLock)
                json = JsonConvert.SerializeObject(Categories, Formatting.Indented);
            WriteAtomic(_categoriesPath, json);
        }
        catch (Exception ex) { WriteSaveError("SaveCategories", ex.Message); }
    }

    // ===== カテゴリ操作の共通処理（監視カテゴリ・休眠カテゴリで共用） =====
    // save には SaveCategories / SaveDormantCategories を渡す
    //（ダーティフラグの扱いの差は各保存メソッド側で維持される）

    // カテゴリ名の重複判定で共通使用（大文字小文字を無視）
    private static bool CategoryNameEquals(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

    private CategoryInfo AddCategoryCore(List<CategoryInfo> categories, List<CategoryInfo> otherCategories, Action save, string name)
    {
        CategoryInfo? cat;
        lock (_persistLock)
        {
            // 自リスト内に同名カテゴリが既にあればそれを返す（重複作成しない）
            cat = categories.FirstOrDefault(c => CategoryNameEquals(c.CategoryName, name));
            if (cat != null) return cat;

            // 相手リストに同名カテゴリがあればカテゴリIDを引き継ぐ
            var linked = otherCategories.FirstOrDefault(c => CategoryNameEquals(c.CategoryName, name));
            cat = new CategoryInfo
            {
                CategoryId   = linked?.CategoryId ?? Guid.NewGuid().ToString(),
                CategoryName = name,
                SortOrder    = categories.Count
            };
            categories.Add(cat);
        }
        save();
        return cat;
    }

    private string EnsureCategoryCore(List<CategoryInfo> categories, Action save, string categoryId, string categoryName)
    {
        lock (_persistLock)
        {
            // 移動元と同じIDが既に対象リストにあればそのまま使う
            if (categories.Any(c => c.CategoryId == categoryId)) return categoryId;

            // 同名カテゴリが対象リストに既にあれば新規作成せず合流する
            var byName = categories.FirstOrDefault(c => CategoryNameEquals(c.CategoryName, categoryName));
            if (byName != null) return byName.CategoryId;

            // どちらにも該当が無ければ移動元と同じIDでペアを作成し最後尾に追加する
            categories.Add(new CategoryInfo
            {
                CategoryId   = categoryId,
                CategoryName = categoryName,
                SortOrder    = categories.Count
            });
        }
        save();
        return categoryId;
    }

    private void RemoveCategoryCore(List<CategoryInfo> categories, Action save, string categoryId, bool isDormant)
    {
        lock (_persistLock)
        {
            // カテゴリ削除時は所属チャンネルを未分類に（同じページのチャンネルのみ対象）
            foreach (var ch in Channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                ch.CategoryId = null;
            categories.RemoveAll(c => c.CategoryId == categoryId);
        }
        save();
        SaveChannels();
    }

    private void RenameCategoryCore(List<CategoryInfo> categories, List<CategoryInfo> otherCategories, Action save, string categoryId, bool isDormant, string newName)
    {
        lock (_persistLock)
        {
            var cat = categories.FirstOrDefault(c => c.CategoryId == categoryId);
            if (cat == null) return;
            if (CategoryNameEquals(cat.CategoryName, newName)) return;

            var isPaired = otherCategories.Any(c => c.CategoryId == categoryId);
            var ownDup   = categories.FirstOrDefault(c => c.CategoryId != categoryId && CategoryNameEquals(c.CategoryName, newName));

            if (!isPaired)
            {
                if (ownDup == null)
                {
                    // 単純リネーム
                    cat.CategoryName = newName;
                }
                else
                {
                    // 自リスト内の既存カテゴリへ合流
                    foreach (var ch in Channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                        ch.CategoryId = ownDup.CategoryId;
                    categories.Remove(cat);
                }
            }
            else if (ownDup != null)
            {
                // 相手リストとの共有を切り離し、自リスト内の既存カテゴリへ合流。相手リストの元カテゴリ（categoryId）は無変更のまま残る
                foreach (var ch in Channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                    ch.CategoryId = ownDup.CategoryId;
                categories.Remove(cat);
            }
            else
            {
                // 相手リストとの共有を切り離す。相手リストに新名称と同名のカテゴリがあればそのIDを引き継ぎ、無ければ新規ID発行
                var otherMatch = otherCategories.FirstOrDefault(c => c.CategoryId != categoryId && CategoryNameEquals(c.CategoryName, newName));
                var newId      = otherMatch?.CategoryId ?? Guid.NewGuid().ToString();

                foreach (var ch in Channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                    ch.CategoryId = newId;
                cat.CategoryId   = newId;
                cat.CategoryName = newName;
            }
        }
        save();
        SaveChannels();
    }

    public CategoryInfo AddCategory(string name)        => AddCategoryCore(Categories,        DormantCategories, SaveCategories,        name);

    public CategoryInfo AddDormantCategory(string name) => AddCategoryCore(DormantCategories, Categories,        SaveDormantCategories, name);

    /// <summary>指定した categoryId の監視カテゴリがあればそのIDを、無ければ同名カテゴリに合流するか新規作成して使用すべきカテゴリIDを返す</summary>
    public string EnsureCategory(string categoryId, string categoryName)
        => EnsureCategoryCore(Categories, SaveCategories, categoryId, categoryName);

    /// <summary>指定した categoryId の休眠カテゴリがあればそのIDを、無ければ同名カテゴリに合流するか新規作成して使用すべきカテゴリIDを返す</summary>
    public string EnsureDormantCategory(string categoryId, string categoryName)
        => EnsureCategoryCore(DormantCategories, SaveDormantCategories, categoryId, categoryName);

    public void SetDormantChannelCategory(string channelId, string? categoryId)
        => SetChannelCategory(channelId, categoryId);

    public void RemoveDormantCategory(string categoryId)
        => RemoveCategoryCore(DormantCategories, SaveDormantCategories, categoryId, isDormant: true);

    public void RenameDormantCategory(string categoryId, string newName)
        => RenameCategoryCore(DormantCategories, Categories, SaveDormantCategories, categoryId, isDormant: true, newName);

    public List<ChannelInfo> GetEnabledChannelsSnapshot()
    {
        lock (_persistLock) return Channels.Where(c => c.IsEnabled && !c.IsDormant).ToList();
    }

    public List<ChannelInfo> GetChannelsSnapshot()
    {
        lock (_persistLock) return Channels.ToList();
    }

    public void RemoveCategory(string categoryId)
        => RemoveCategoryCore(Categories, SaveCategories, categoryId, isDormant: false);

    public void RenameCategory(string categoryId, string newName)
        => RenameCategoryCore(Categories, DormantCategories, SaveCategories, categoryId, isDormant: false, newName);

    public void SetChannelCategory(string channelId, string? categoryId)
    {
        lock (_persistLock)
        {
            var ch = Channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (ch == null) return;
            ch.CategoryId = categoryId;
        }
        MarkDirty();
    }

    public void Load()
    {
        LoadSettings();
        LoadChannels();
        LoadCategories();
        LoadDormantCategories();
        LoadState();

        // recent_uploads.json があればそれを正とする。
        // なければ（＝更新後の初回起動）LoadState が旧 state.json から移行した
        // ch.RecentUploads の内容をそのまま新ファイルへ書き出す（1バージョンだけの読み取り移行）。
        if (File.Exists(_recentUploadsPath))
            LoadRecentUploads();
        else
            SaveRecentUploadsInternal();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_configPath))
            {
                var json = File.ReadAllText(_configPath);

                // toastStyle が数値で保存されている場合（旧バージョン互換）を文字列に変換
                var jobj = Newtonsoft.Json.JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(json);
                if (jobj?["toastStyle"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer)
                {
                    var numVal = jobj["toastStyle"]!.ToObject<int>();
                    var strVal = numVal == 1 ? "Thumbnail" : "Standard";
                    jobj["toastStyle"] = strVal;
                    json = jobj.ToString(Newtonsoft.Json.Formatting.None);
                }

                Settings = JsonConvert.DeserializeObject<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch { Settings = new AppSettings(); }

        // APIキーは別ファイルから復号して読み込む
        Settings.ApiKey = ApiKeyService.Load(_confDir);

        // 旧バージョン移行: config.json に平文 ApiKey が残っていれば api_key.dat に移行して除去
        try
        {
            if (File.Exists(_configPath) && string.IsNullOrEmpty(Settings.ApiKey))
            {
                var raw = File.ReadAllText(_configPath);
                var legacy = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JObject>(raw);
                var legacyKey = legacy?["ApiKey"]?.ToString();
                if (!string.IsNullOrEmpty(legacyKey))
                {
                    Settings.ApiKey = legacyKey;
                    ApiKeyService.Save(_confDir, Settings.ApiKey);
                    // config.json から ApiKey キーを除去して上書き
                    legacy!.Remove("ApiKey");
                    WriteAtomic(_configPath, legacy.ToString(Newtonsoft.Json.Formatting.Indented));
                    AppLogger.Log(LogMsg.ApiKeyMigrated);
                }
            }
        }
        catch { }
    }

    private void LoadChannels()
    {
        try
        {
            if (File.Exists(_channelsPath))
            {
                var json = File.ReadAllText(_channelsPath);
                Channels = JsonConvert.DeserializeObject<List<ChannelInfo>>(json) ?? new List<ChannelInfo>();
            }
        }
        catch { Channels = new List<ChannelInfo>(); }

        bool migrated = MigrateChannelsToFocusSlots();
        migrated |= MigrateNotifyUpcomingToNullable();
        migrated |= MigrateUpcomingNotifyMode();
        migrated |= MigrateUpcomingNotifySplit();
        migrated |= CleanupExpiredGraceEntries();
        if (migrated) { SaveChannelsSilent(); MarkDirty(); }
    }

    private bool CleanupExpiredGraceEntries()
    {
        bool cleaned = false;
        lock (_persistLock)
        {
            lock (MonitorService._pendingListLock)
            {
                foreach (var ch in Channels)
                {
                    if (ch.PendingLives.RemoveAll(p => p.GraceRemaining == -1) > 0) cleaned = true;
                    if (ch.PendingPremieres.RemoveAll(p => p.GraceRemaining == -1) > 0) cleaned = true;
                }
            }
        }
        return cleaned;
    }

    /// <summary>
    /// NotifyUpcoming が false（旧デフォルト）のチャンネルを null（グローバルに従う）に変換する。
    /// </summary>
    private bool MigrateNotifyUpcomingToNullable()
    {
        bool migrated = false;
        foreach (var ch in Channels.Where(c => c.NotifyUpcoming == false))
        {
            ch.NotifyUpcoming = null;
            migrated = true;
        }
        return migrated;
    }

    /// <summary>
    /// NotifyUpcoming（bool?）→ UpcomingNotifyMode への移行。
    /// Settings.UpcomingMigrated が false の場合のみ実行する。
    /// </summary>
    private bool MigrateUpcomingNotifyMode()
    {
        if (Settings.UpcomingMigrated) return false;

        bool globalUpcoming = Settings.GlobalNotifyUpcoming;
        foreach (var ch in Channels)
        {
            var effective = ch.NotifyUpcoming ?? globalUpcoming;
            ch.UpcomingNotifyMode       = effective
                ? Models.UpcomingNotifyMode.WaitingRoomOnly
                : Models.UpcomingNotifyMode.LiveStartOnly;
            ch.UpcomingNotifyLeadMinutes = 0;
            ch.NotifyUpcoming            = null;
        }
        Settings.UpcomingMigrated = true;
        SaveSettings();
        return true;
    }

    /// <summary>
    /// 共通 UpcomingNotifyMode / UpcomingNotifyLeadMinutes を
    /// Premiere 用・Live 用の別フィールドへ複製する。
    /// Settings.UpcomingSplitMigrated が false の場合のみ実行する。
    /// </summary>
    private bool MigrateUpcomingNotifySplit()
    {
        if (Settings.UpcomingSplitMigrated) return false;

        foreach (var ch in Channels)
        {
            ch.PremiereUpcomingNotifyMode        = ch.UpcomingNotifyMode;
            ch.PremiereUpcomingNotifyLeadMinutes = ch.UpcomingNotifyLeadMinutes;
            ch.LiveUpcomingNotifyMode            = ch.UpcomingNotifyMode;
            ch.LiveUpcomingNotifyLeadMinutes     = ch.UpcomingNotifyLeadMinutes;
        }
        Settings.UpcomingSplitMigrated = true;
        SaveSettings();
        return true;
    }

    /// <summary>
    /// 旧来の単一 MonitorMode（Normal/LowFreq/Focus 単一スロット）を
    /// 動画/Short/ライブ別の3スロット形式（FocusSlots）に一括移行する（初回のみ）。
    /// ChannelDetailWindow コンストラクタの変換ロジックと同じ規則を使用。
    /// </summary>
    private bool MigrateChannelsToFocusSlots()
    {
        bool migrated = false;
        VideoKind[] defaultKinds = { VideoKind.Video, VideoKind.Short, VideoKind.Live };

        // Days == 0（旧・全曜日）を 127（全ビット）に変換
        foreach (var ch in Channels)
        {
            foreach (var slot in ch.FocusSlots.Where(s => s.SlotMode == MonitorMode.Focus && s.Days == 0))
            {
                slot.Days = AppConstants.AllDaysMask;
                migrated = true;
            }
        }

        foreach (var ch in Channels)
        {
            if (ch.FocusSlots.Count > 0) continue;

            bool[] kindEnabled = { ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive };
            var slots = new List<FocusSlot>();
            for (int i = 0; i < 3; i++)
            {
                var slot = ch.CreateDefaultFocusSlot(defaultKinds[i]);
                slot.IsEnabled = kindEnabled[i];
                slots.Add(slot);
            }

            ch.FocusSlots  = slots;
            ch.MonitorMode = MonitorMode.Focus;
            migrated = true;
        }

        return migrated;
    }

    /// <summary>通常の設定保存（ダーティフラグを立てる）</summary>
    public void SaveSettings()
    {
        SaveSettingsInternal();
        MarkDirty();
    }

    /// <summary>ウィンドウ位置など揮発的な設定の保存（ダーティフラグを立てない）</summary>
    public void SaveSettingsSilent() => SaveSettingsInternal();

    private void SaveSettingsInternal()
    {
        try
        {
            var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
            WriteAtomic(_configPath, json);
        }
        catch (Exception ex) { WriteSaveError("SaveSettings", ex.Message); }
    }

    /// <summary>通常の保存（ダーティフラグを立てる）</summary>
    public void SaveChannels()
    {
        SaveChannelsInternal();
        MarkDirty();
    }

    /// <summary>VideoID更新など監視系の保存（ダーティフラグを立てない）</summary>
    public void SaveChannelsSilent() => SaveChannelsInternal();

    private void SaveChannelsInternal()
    {
        try
        {
            string json;
            lock (_persistLock)
                json = JsonConvert.SerializeObject(Channels, Formatting.Indented);
            WriteAtomic(_channelsPath, json);
        }
        catch (Exception ex) { WriteSaveError("SaveChannels", ex.Message); }
    }

    public void AddChannel(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            if (Channels.Any(c => c.ChannelId == channel.ChannelId)) return;
            Channels.Add(channel);
        }
        SaveChannels();
    }

    public void RemoveChannel(string channelId)
    {
        lock (_persistLock)
        {
            var ch = Channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (ch == null) return;
            Channels.Remove(ch);
        }
        SaveChannels();
    }

    /// <summary>チャンネル詳細設定の保存ボタンなど即時保存が必要な場合に使う</summary>
    public void UpdateChannel(ChannelInfo channel)
    {
        ReplaceChannel(channel);
        SaveChannels();
    }

    /// <summary>監視系の状態更新（LastCheckedAt/VideoId/NextCheckAt等）。終了時のみ保存</summary>
    public void UpdateChannelSilent(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            var idx = Channels.FindIndex(c => c.ChannelId == channel.ChannelId);
            if (idx >= 0) Channels[idx] = channel;
        }
    }

    private void ReplaceChannel(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            var idx = Channels.FindIndex(c => c.ChannelId == channel.ChannelId);
            if (idx < 0) return;
            Channels[idx] = channel;
        }
    }

    /// <summary>アプリ終了時に呼ぶ。全ファイルを保存し、監視状態も保存する</summary>
    public void FlushAll()
    {
        _stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveSettingsInternal();
        SaveChannelsInternal();
        SaveCategoriesInternal();
        SaveDormantCategoriesInternal(markDirty: false);
        SaveStateInternal();
        SaveRecentUploadsInternal();
        SaveStateToBackup();
    }

    // ===== state.json 管理 =====

    private static ChannelState ExtractStateFromChannel(ChannelInfo ch)
    {
        lock (MonitorService._pendingListLock)
        {
            return new()
            {
                LastCheckedVideoId     = ch.LastCheckedVideoId,
                LastCheckedVideoPublishedAt = ch.LastCheckedVideoPublishedAt,
                NextCheckAt            = ch.NextCheckAt,
                LastCheckedAt          = ch.LastCheckedAt,
                UploadsPlaylistId      = ch.UploadsPlaylistId,
                PendingLives           = ch.PendingLives.ToList(),
                PendingPremieres       = ch.PendingPremieres.ToList(),
                ActiveLives            = ch.ActiveLives.ToList(),
                ActivePremieres        = ch.ActivePremieres.ToList(),
                LastLiveNotifiedId     = ch.LastLiveNotifiedId,
                LastPremiereNotifiedId = ch.LastPremiereNotifiedId,
                LastLiveId             = ch.LastLiveId,
                LastPremiereId         = ch.LastPremiereId,
                NextLiveCheckAt        = ch.NextLiveCheckAt,
                NextPremiereCheckAt    = ch.NextPremiereCheckAt,
                LiveGraceRemaining     = ch.LiveGraceRemaining,
                LastVideoTitle         = ch.LastVideoTitle,
                LastVideoNotifiedAt    = ch.LastVideoNotifiedAt,
                LastShortNotifiedId    = ch.LastShortNotifiedId,
                LastShortTitle         = ch.LastShortTitle,
                LastShortNotifiedAt    = ch.LastShortNotifiedAt,
                LastLiveNotifiedTitle  = ch.LastLiveNotifiedTitle,
                LastLiveNotifiedAt     = ch.LastLiveNotifiedAt,
                LastPremiereNotifiedTitle = ch.LastPremiereNotifiedTitle,
                LastPremiereNotifiedAt = ch.LastPremiereNotifiedAt,
                LatestTitle            = ch.LatestTitle,
                LatestKind             = ch.LatestKind,
                LatestVideoId          = ch.LatestVideoId,
                LatestDuration         = ch.LatestDuration,
                LatestThumbnailUrl     = ch.LatestThumbnailUrl,
                IsBanned               = ch.IsBanned,
                LatestVideoDeleted     = ch.LatestVideoDeleted,
                NoVideosFound          = ch.NoVideosFound,
            };
        }
    }

    private static void ApplyStateToChannel(ChannelInfo ch, ChannelState state)
    {
        ch.LastCheckedVideoId     = state.LastCheckedVideoId;
        ch.LastCheckedVideoPublishedAt = state.LastCheckedVideoPublishedAt;
        ch.NextCheckAt            = state.NextCheckAt;
        ch.LastCheckedAt          = state.LastCheckedAt;
        ch.UploadsPlaylistId      = state.UploadsPlaylistId;
        ch.PendingLives           = state.PendingLives;
        ch.PendingPremieres       = state.PendingPremieres;
        ch.ActiveLives            = state.ActiveLives ?? new();
        ch.ActivePremieres        = state.ActivePremieres ?? new();
        ch.LastLiveNotifiedId     = state.LastLiveNotifiedId;
        ch.LastPremiereNotifiedId = state.LastPremiereNotifiedId;
        ch.LastLiveId             = state.LastLiveId;
        ch.LastPremiereId         = state.LastPremiereId;
        ch.NextLiveCheckAt        = state.NextLiveCheckAt;
        ch.NextPremiereCheckAt    = state.NextPremiereCheckAt;
        ch.LiveGraceRemaining     = state.LiveGraceRemaining;
        ch.LastVideoTitle         = state.LastVideoTitle;
        ch.LastVideoNotifiedAt    = state.LastVideoNotifiedAt;
        ch.LastShortNotifiedId    = state.LastShortNotifiedId;
        ch.LastShortTitle         = state.LastShortTitle;
        ch.LastShortNotifiedAt    = state.LastShortNotifiedAt;
        ch.LastLiveNotifiedTitle  = state.LastLiveNotifiedTitle;
        ch.LastLiveNotifiedAt     = state.LastLiveNotifiedAt;
        ch.LastPremiereNotifiedTitle = state.LastPremiereNotifiedTitle;
        ch.LastPremiereNotifiedAt = state.LastPremiereNotifiedAt;
        ch.LatestTitle            = state.LatestTitle;
        ch.LatestKind             = state.LatestKind;
        ch.LatestVideoId          = state.LatestVideoId;
        ch.LatestDuration         = state.LatestDuration;
        ch.LatestThumbnailUrl     = state.LatestThumbnailUrl;
        ch.RecentUploads          = state.RecentUploads ?? new();
        ch.IsBanned               = state.IsBanned;
        ch.LatestVideoDeleted     = state.LatestVideoDeleted;
        ch.NoVideosFound          = state.NoVideosFound;
    }

    private void LoadState()
    {
        if (!File.Exists(_statePath))
        {
            MigrateStateFromChannels();
            return;
        }
        try
        {
            var json = File.ReadAllText(_statePath);
            if (string.IsNullOrWhiteSpace(json))
                throw new Exception();
            AppState = JsonConvert.DeserializeObject<AppState>(json) ?? throw new Exception();
        }
        catch
        {
            TryRestoreStateFromBackup();
            try
            {
                var json = File.ReadAllText(_statePath);
                AppState = JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
            }
            catch { AppState = new AppState(); }
        }
        var validIds = new HashSet<string>(Channels.Select(c => c.ChannelId));
        foreach (var id in AppState.Channels.Keys.Where(id => !validIds.Contains(id)).ToList())
            AppState.Channels.Remove(id);
        foreach (var ch in Channels)
        {
            if (AppState.Channels.TryGetValue(ch.ChannelId, out var state))
                ApplyStateToChannel(ch, state);
        }
        CleanupExpiredGraceEntries();
    }

    private void MigrateStateFromChannels()
    {
        AppState = new AppState
        {
            TodayApiUnits = Settings.TodayApiUnits,
            TodayApiDate  = Settings.TodayApiDate,
        };
        try
        {
            if (File.Exists(_channelsPath))
            {
                var arr = JsonConvert.DeserializeObject<Newtonsoft.Json.Linq.JArray>(
                    File.ReadAllText(_channelsPath));
                if (arr != null)
                {
                    foreach (var item in arr)
                    {
                        var channelId = item["channelId"]?.ToString();
                        if (string.IsNullOrEmpty(channelId)) continue;
                        AppState.Channels[channelId] = new ChannelState
                        {
                            LastCheckedVideoId     = item["lastCheckedVideoId"]?.ToString() ?? "",
                            NextCheckAt            = item["nextCheckAt"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                            LastCheckedAt          = item["lastCheckedAt"]?.ToObject<DateTime>() ?? DateTime.MinValue,
                            UploadsPlaylistId      = item["uploadsPlaylistId"]?.ToString() ?? "",
                            PendingLives           = item["pendingLives"]?.ToObject<List<PendingVideoEntry>>() ?? new(),
                            PendingPremieres       = item["pendingPremieres"]?.ToObject<List<PendingVideoEntry>>() ?? new(),
                            LastLiveNotifiedId     = item["lastLiveNotifiedId"]?.ToString() ?? "",
                            LastPremiereNotifiedId = item["lastPremiereNotifiedId"]?.ToString() ?? "",
                            LastLiveId             = item["lastLiveId"]?.ToString() ?? "",
                            LastPremiereId         = item["lastPremiereId"]?.ToString() ?? "",
                            NextLiveCheckAt        = item["nextLiveCheckAt"]?.ToObject<DateTime?>(),
                            NextPremiereCheckAt    = item["nextPremiereCheckAt"]?.ToObject<DateTime?>(),
                            LiveGraceRemaining     = item["liveGraceRemaining"]?.ToObject<int>() ?? 0,
                        };
                    }
                }
            }
        }
        catch { }
        foreach (var ch in Channels)
        {
            if (AppState.Channels.TryGetValue(ch.ChannelId, out var state))
                ApplyStateToChannel(ch, state);
        }
        CleanupExpiredGraceEntries();
        SaveStateInternal();
        SaveRecentUploadsInternal();
    }

    /// <summary>state.json と recent_uploads.json をまとめて保存する（定期保存タイマー用）</summary>
    private void SaveStateAndSnapshotsInternal()
    {
        SaveStateInternal();
        SaveRecentUploadsInternal();
    }

    /// <summary>
    /// state.json のシリアライズ設定。
    /// 既定値・null・空文字・空コレクションのプロパティを出力しないことでファイルサイズを抑える。
    /// 本体は可読性のため Indented のまま維持する。
    /// </summary>
    private static readonly JsonSerializerSettings _stateSerializerSettings = new()
    {
        ContractResolver     = StateOmitEmptyContractResolver.Instance,
        Formatting           = Formatting.Indented,
        NullValueHandling    = NullValueHandling.Ignore,
        DefaultValueHandling = DefaultValueHandling.Ignore,
    };

    /// <summary>
    /// recent_uploads.json のシリアライズ設定。
    /// 人間が編集しない純粋な表示キャッシュのため、詰めて（Formatting.None）書き出す。
    /// </summary>
    private static readonly JsonSerializerSettings _recentUploadsSerializerSettings = new()
    {
        Formatting        = Formatting.None,
        NullValueHandling = NullValueHandling.Ignore,
    };

    /// <summary>
    /// state.json 書き出し用のコントラクトリゾルバ。
    /// - すべてのプロパティで既定値・null を省略する
    /// - string プロパティは null／空文字なら出力しない
    /// - string 以外の IEnumerable プロパティは null／要素0件なら出力しない
    /// Json.NET はコントラクトをリゾルバインスタンス単位でキャッシュするため、インスタンスは1つだけ保持して再利用する。
    /// </summary>
    private sealed class StateOmitEmptyContractResolver : Newtonsoft.Json.Serialization.DefaultContractResolver
    {
        public static readonly StateOmitEmptyContractResolver Instance = new();

        protected override Newtonsoft.Json.Serialization.JsonProperty CreateProperty(
            System.Reflection.MemberInfo member,
            Newtonsoft.Json.MemberSerialization memberSerialization)
        {
            var property = base.CreateProperty(member, memberSerialization);

            property.DefaultValueHandling = DefaultValueHandling.Ignore;
            property.NullValueHandling    = NullValueHandling.Ignore;

            var propertyType = property.PropertyType;
            if (propertyType == null)
                return property;

            if (propertyType == typeof(string))
            {
                var valueProvider = property.ValueProvider;
                property.ShouldSerialize = target =>
                    !string.IsNullOrEmpty(valueProvider?.GetValue(target) as string);
            }
            else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(propertyType))
            {
                var valueProvider = property.ValueProvider;
                property.ShouldSerialize = target =>
                    HasAnyElement(valueProvider?.GetValue(target) as System.Collections.IEnumerable);
            }

            return property;
        }

        private static bool HasAnyElement(System.Collections.IEnumerable? source)
        {
            if (source == null)
                return false;

            var enumerator = source.GetEnumerator();
            try
            {
                return enumerator.MoveNext();
            }
            finally
            {
                (enumerator as IDisposable)?.Dispose();
            }
        }
    }

    public void SaveStateInternal()
    {
        try
        {
            lock (_persistLock)
            {
                foreach (var ch in Channels)
                    AppState.Channels[ch.ChannelId] = ExtractStateFromChannel(ch);
            }
            var json = JsonConvert.SerializeObject(AppState, _stateSerializerSettings);
            WriteAtomic(_statePath, json);
        }
        catch (Exception ex) { WriteSaveError("SaveState", ex.Message); }
    }

    /// <summary>
    /// 最新動画スナップショット（表示用キャッシュ）を recent_uploads.json へ保存する。
    /// state.json とは切り離し、バックアップ対象外の再生成可能ファイルとして扱う。
    /// </summary>
    private void SaveRecentUploadsInternal()
    {
        try
        {
            string json;
            lock (_persistLock)
            {
                var snapshot = new Dictionary<string, List<RecentUploadEntry>>();
                foreach (var ch in Channels)
                {
                    if (ch.RecentUploads.Count > 0)
                        snapshot[ch.ChannelId] = ch.RecentUploads.ToList();
                }
                json = JsonConvert.SerializeObject(snapshot, _recentUploadsSerializerSettings);
            }
            WriteAtomic(_recentUploadsPath, json);
        }
        catch (Exception ex) { WriteSaveError("SaveRecentUploads", ex.Message); }
    }

    /// <summary>
    /// recent_uploads.json を読み込み、各チャンネルの表示用スナップショットへ反映する。
    /// 破損・空・null の場合はバックアップからの復元は行わず、そのまま戻る（次回チェックで自己修復するため）。
    /// </summary>
    private void LoadRecentUploads()
    {
        try
        {
            if (!File.Exists(_recentUploadsPath))
                return;

            var json = File.ReadAllText(_recentUploadsPath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var snapshot = JsonConvert.DeserializeObject<Dictionary<string, List<RecentUploadEntry>>>(json);
            if (snapshot == null || snapshot.Count == 0)
                return;

            lock (_persistLock)
            {
                foreach (var ch in Channels)
                {
                    ch.RecentUploads = snapshot.TryGetValue(ch.ChannelId, out var uploads) && uploads != null
                        ? uploads
                        : new();
                }
            }
        }
        catch (Exception ex) { WriteSaveError("LoadRecentUploads", ex.Message); }
    }

    private void SaveStateToBackup()
    {
        try
        {
            if (File.Exists(_statePath))
                File.Copy(_statePath, Path.Combine(_appDataDir, DirBackup, FileState), overwrite: true);
        }
        catch { }
    }

    private void TryRestoreStateFromBackup()
    {
        var backupStatePath = Path.Combine(_appDataDir, DirBackup, FileState);
        if (!File.Exists(backupStatePath)) return;
        try { File.Copy(backupStatePath, _statePath, overwrite: true); }
        catch { }
    }
}