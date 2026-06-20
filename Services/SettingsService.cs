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
    private const string FileAutoBackup = "auto_backup.ytbk";
    private const string SoundsZipPrefix = "Sounds/";

    private static readonly Lazy<SettingsService> _lazy = new(() => new SettingsService());
    public static SettingsService Instance => _lazy.Value;

    private readonly string _appDataDir;
    private readonly string _configPath;
    private readonly string _channelsPath;
    private readonly string _categoriesPath;
    private readonly string _apiKeyPath;
    private readonly string _confDir;

    public AppSettings Settings { get; private set; } = new();
    public List<ChannelInfo> Channels { get; private set; } = new();
    public List<CategoryInfo> Categories { get; private set; } = new();

    // Channels/Categories への並行アクセスを直列化するロック
    private readonly object _persistLock = new();
    // AddApiUnits の並行呼び出しを直列化するロック
    private readonly object _apiUnitsLock = new();

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
        Directory.CreateDirectory(Path.Combine(_appDataDir, AppConstants.DirIcons));
        _configPath      = Path.Combine(confDir, FileConfig);
        _channelsPath    = Path.Combine(confDir, FileChannels);
        _categoriesPath  = Path.Combine(confDir, FileCategories);
        _apiKeyPath      = Path.Combine(confDir, AppConstants.FileApiKey);
        _confDir         = confDir;

        // 旧パス（フラット構造）からの移行
        foreach (var fname in new[] { FileConfig, FileChannels, FileCategories, AppConstants.FileApiKey })
        {
            var oldPath = Path.Combine(_appDataDir, fname);
            var newPath = Path.Combine(confDir, fname);
            if (File.Exists(oldPath) && !File.Exists(newPath))
                File.Move(oldPath, newPath);
        }
    }

    public string AppDataDir => _appDataDir;

    // ===== バックアップ / インポート =====

    /// <summary>Sounds フォルダのパス（exe と同じディレクトリ）</summary>
    private static readonly string SoundsDir =
        Path.Combine(
            Path.GetDirectoryName(Environment.ProcessPath
                ?? System.Reflection.Assembly.GetExecutingAssembly().Location) ?? "",
            AppConstants.DirSounds);

    public string ExportBackup(string destPath)
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
            foreach (var file in new[] { _configPath, _channelsPath, _categoriesPath, _apiKeyPath })
                if (File.Exists(file)) zip.CreateEntryFromFile(file, Path.GetFileName(file));

            // Sounds フォルダ（フォルダごと・全ファイル再帰）
            var soundsDir = SoundsDir;
            if (Directory.Exists(soundsDir))
                foreach (var file in Directory.GetFiles(soundsDir, "*", SearchOption.AllDirectories))
                {
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

            var allowedFiles = new[] { FileConfig, FileChannels, FileCategories, AppConstants.FileApiKey };

            foreach (var entry in zip.Entries)
            {
                var entryDir = Path.GetDirectoryName(entry.FullName)?.Replace('/', '\\') ?? "";

                // 設定ファイル（ルート直下）
                if (allowedFiles.Contains(entry.Name) &&
                    string.IsNullOrEmpty(entryDir))
                {
                    entry.ExtractToFile(Path.Combine(_appDataDir, DirConf, entry.Name), overwrite: true);
                    continue;
                }

                // Sounds フォルダ（フォルダ構造ごと exe ディレクトリに復元）
                var fullNameFwd = entry.FullName.Replace('\\', '/');
                if (fullNameFwd.StartsWith(SoundsZipPrefix, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(entry.Name))
                {
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
                    var allowedRoot = Path.GetFullPath(exeDir) + Path.DirectorySeparatorChar;
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
        try
        {
            lock (_apiUnitsLock)
            {
                var quotaKey = AppConstants.GetQuotaDayKey();
                if (Settings.TodayApiDate != quotaKey)
                {
                    Settings.TodayApiUnits = 0;
                    Settings.TodayApiDate  = quotaKey;
                }
                Settings.TodayApiUnits += units;
                var json = JsonConvert.SerializeObject(Settings, Formatting.Indented);
                ApiKeyService.Save(_confDir, Settings.ApiKey);
                WriteAtomic(_configPath, json);
            }
        }
        catch (Exception ex) { WriteSaveError("AddApiUnits", ex.Message); }
        MonitorService.Instance.NotifyQuotaUpdated();
    }

    public void SaveCategories()
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

    public CategoryInfo AddCategory(string name)
    {
        CategoryInfo cat;
        lock (_persistLock)
        {
            cat = new CategoryInfo
            {
                CategoryId   = Guid.NewGuid().ToString(),
                CategoryName = name,
                SortOrder    = Categories.Count
            };
            Categories.Add(cat);
        }
        MarkDirty();
        SaveCategories();
        return cat;
    }

    public List<ChannelInfo> GetEnabledChannelsSnapshot()
    {
        lock (_persistLock) return Channels.Where(c => c.IsEnabled).ToList();
    }

    public void RemoveCategory(string categoryId)
    {
        lock (_persistLock)
        {
            // カテゴリ削除時は所属チャンネルを未分類に
            foreach (var ch in Channels.Where(c => c.CategoryId == categoryId))
                ch.CategoryId = null;
            Categories.RemoveAll(c => c.CategoryId == categoryId);
        }
        MarkDirty();
        SaveCategories();
        SaveChannels();
    }

    public void RenameCategory(string categoryId, string newName)
    {
        var cat = Categories.FirstOrDefault(c => c.CategoryId == categoryId);
        if (cat == null) return;
        cat.CategoryName = newName;
        MarkDirty();
        SaveCategories();
    }

    public void SetChannelCategory(string channelId, string? categoryId)
    {
        var ch = Channels.FirstOrDefault(c => c.ChannelId == channelId);
        if (ch == null) return;
        ch.CategoryId = categoryId;
        SaveChannels();
    }

    public void Load()
    {
        LoadSettings();
        LoadChannels();
        LoadCategories();
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
                    ApiKeyService.Save(_confDir, legacyKey);
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

        if (MigrateChannelsToFocusSlots()) { SaveChannelsSilent(); MarkDirty(); }
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
                slot.Days = 0b1111111;
                migrated = true;
            }
        }

        foreach (var ch in Channels)
        {
            if (ch.FocusSlots.Count > 0) continue;

            List<FocusSlot> baseSlots = ch.MonitorMode switch
            {
                MonitorMode.LowFreq => new List<FocusSlot>
                {
                    new FocusSlot
                    {
                        SlotMode = MonitorMode.LowFreq,
                        SlotLowFreqIntervalMinutes = ch.LowFreqIntervalMinutes,
                        IsEnabled = true
                    }
                },
                MonitorMode.Focus => new List<FocusSlot>
                {
                    new FocusSlot
                    {
                        SlotMode        = MonitorMode.Focus,
                        NotifyKind      = VideoKind.Video,
                        Days            = ch.FocusDays,
                        Hour            = ch.FocusHour,
                        Minute          = ch.FocusMinute,
                        WindowMinutes   = ch.FocusWindowMinutes,
                        IntervalMinutes = ch.FocusIntervalMinutes,
                        IsEnabled       = true
                    }
                },
                _ => new List<FocusSlot>
                {
                    new FocusSlot { SlotMode = MonitorMode.Normal, IsEnabled = true }
                }
            };

            bool[] kindEnabled = { ch.NotifyVideo, ch.NotifyShort, ch.NotifyLive };
            var slots = new List<FocusSlot>();
            for (int i = 0; i < 3; i++)
            {
                var slot = i < baseSlots.Count ? baseSlots[i] : new FocusSlot { NotifyKind = defaultKinds[i] };
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
            ApiKeyService.Save(_confDir, Settings.ApiKey);
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
        SaveChannels(); // SaveChannels内でMarkDirty
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

    public void UpdateChannel(ChannelInfo channel)
    {
        ReplaceChannel(channel);
        SaveChannels();
    }

    /// <summary>監視系の状態更新（LastCheckedAt/VideoId/NextCheckAt等）。ダーティフラグを立てない</summary>
    public void UpdateChannelSilent(ChannelInfo channel)
    {
        try
        {
            lock (_persistLock)
            {
                var idx = Channels.FindIndex(c => c.ChannelId == channel.ChannelId);
                if (idx < 0) return;
                Channels[idx] = channel;
                var json = JsonConvert.SerializeObject(Channels, Formatting.Indented);
                WriteAtomic(_channelsPath, json);
            }
        }
        catch (Exception ex) { WriteSaveError("UpdateChannelSilent", ex.Message); }
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

    public void MoveChannelUp(string channelId)
    {
        lock (_persistLock)
        {
            var idx = Channels.FindIndex(c => c.ChannelId == channelId);
            if (idx <= 0) return;
            (Channels[idx], Channels[idx - 1]) = (Channels[idx - 1], Channels[idx]);
        }
        SaveChannels();
    }

    public void MoveChannelDown(string channelId)
    {
        lock (_persistLock)
        {
            var idx = Channels.FindIndex(c => c.ChannelId == channelId);
            if (idx < 0 || idx >= Channels.Count - 1) return;
            (Channels[idx], Channels[idx + 1]) = (Channels[idx + 1], Channels[idx]);
        }
        SaveChannels();
    }
}
