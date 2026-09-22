using System.IO;
using Newtonsoft.Json;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 保存系サービスの中核。設定値（config.json）の読み書きと、
/// 各ストア（チャンネル／実行状態／表示キャッシュ／集計／バックアップ）の保持・読み込みの指揮を担当する。
/// </summary>
public class SettingsService
{
    private const int StateWriteIntervalMs = 60 * 60 * 1000; // 1時間（スナップショット2種の定期保存）
    private const int ProgressWriteIntervalMs = 30 * 1000;   // 30秒（監視進捗 state.json のまとめ書き）

    private static readonly Lazy<SettingsService> _lazy = new(() => new SettingsService());
    public static SettingsService Instance => _lazy.Value;

    private readonly SettingsFileStore _fileStore;

    public string AppDataDir => _fileStore.AppDataDir;
    public string ConfDir    => _fileStore.ConfDir;

    public AppSettings Settings { get; private set; } = new();

    /// <summary>チャンネル一覧・カテゴリ一覧（通常／休止）の保管庫</summary>
    public ChannelStore Channels { get; }

    /// <summary>監視の実行時状態（state.json）のストア</summary>
    public MonitorStateStore MonitorState { get; }

    /// <summary>最新動画スナップショット（recent_uploads.json.gz）のストア</summary>
    public RecentUploadStore RecentUploads { get; }

    /// <summary>API使用量・Gemini呼び出し回数・レポート履歴のストア</summary>
    public UsageStatsStore UsageStats { get; }

    /// <summary>バックアップの書き出し・取り込み・自動復元のサービス</summary>
    public BackupService Backup { get; }

    private readonly System.Threading.Timer _stateTimer;
    private readonly System.Threading.Timer _progressTimer;

    // 自動バックアップ用ダーティフラグ
    private bool _dirty = false;

    // 監視進捗（state.json）に未保存の変更があることを示す。
    // 自動バックアップ用の _dirty とは用途が異なるため別変数として持つ
    private volatile bool _progressDirty = false;

    private SettingsService()
    {
        _fileStore = new SettingsFileStore();

        Channels      = new ChannelStore(_fileStore, this);
        MonitorState  = new MonitorStateStore(_fileStore, this);
        RecentUploads = new RecentUploadStore(_fileStore, this);
        UsageStats    = new UsageStatsStore(_fileStore, this);
        Backup        = new BackupService(_fileStore, this);

        _stateTimer = new System.Threading.Timer(
            _ => SaveSnapshotsInternal(), null, StateWriteIntervalMs, StateWriteIntervalMs);
        _progressTimer = new System.Threading.Timer(
            _ => SaveProgressIfDirty(), null, ProgressWriteIntervalMs, ProgressWriteIntervalMs);
    }

    // ===== ダーティフラグ =====

    /// <summary>チャンネル/カテゴリ/設定の変更時に呼ぶ（VideoIDなど監視系は除く）</summary>
    public void MarkDirty() => _dirty = true;

    /// <summary>自動バックアップ用ダーティフラグが立っているか（バックアップサービスから参照する）</summary>
    internal bool IsDirty => _dirty;

    /// <summary>自動バックアップ用ダーティフラグを下ろす（バックアップサービスから呼ぶ）</summary>
    internal void ClearDirty() => _dirty = false;

    /// <summary>監視進捗（state.json）に未保存の変更があることを記録する</summary>
    internal void MarkProgressDirty() => _progressDirty = true;

    /// <summary>監視進捗（state.json）の未保存フラグを下ろす</summary>
    internal void ClearProgressDirty() => _progressDirty = false;

    // ===== 読み込みの指揮 =====

    public void Load()
    {
        LoadSettings();
        Channels.LoadChannels();
        Channels.LoadCategories();
        Channels.LoadDormantCategories();
        MonitorState.LoadState();

        // 表示キャッシュ（recent_uploads.json.gz）を読み込む。
        // ファイルが無い場合は内部で何もせず戻り、次回の巡回で再生成される。
        RecentUploads.LoadRecentUploads();

        UsageStats.LoadReportHistory();
    }

    // ===== 設定値（config.json）=====

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_fileStore.ConfigPath))
            {
                var json = File.ReadAllText(_fileStore.ConfigPath);

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
        catch (Exception ex)
        {
            _fileStore.WriteSaveError("LoadSettings", ex.Message);
            Settings = new AppSettings();
        }

        // 旧バージョン移行: isDarkMode → theme（一度だけ）
        if (!Settings.ThemeMigrated)
        {
            Settings.Theme = Settings.IsDarkMode ? AppTheme.Dark : AppTheme.Light;
            Settings.ThemeMigrated = true;
            MarkDirty();
        }

        // APIキーは別ファイルから復号して読み込む
        Settings.ApiKey = ApiKeyService.Load(_fileStore.ConfDir);
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
            SettingsFileStore.WriteAtomic(_fileStore.ConfigPath, json);
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveSettings", ex.Message); }
    }

    // ===== 終了時の一括保存・定期保存 =====

    /// <summary>アプリ終了時に呼ぶ。全ファイルを保存し、監視状態も保存する</summary>
    public void FlushAll()
    {
        _stateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        _progressTimer.Change(Timeout.Infinite, Timeout.Infinite);
        SaveSettingsInternal();
        Channels.SaveChannelsInternal();
        Channels.SaveCategoriesInternal();
        Channels.SaveDormantCategoriesInternal(markDirty: false);
        MonitorState.SaveStateInternal();
        RecentUploads.SaveRecentUploadsInternal();
        UsageStats.SaveReportHistoryInternal();
        MonitorState.SaveStateToBackup();
    }

    /// <summary>recent_uploads.json.gz・report_history.json をまとめて保存する（スナップショット用タイマー）</summary>
    private void SaveSnapshotsInternal()
    {
        RecentUploads.SaveRecentUploadsInternal();
        UsageStats.SaveReportHistoryInternal();
    }

    /// <summary>
    /// 監視進捗に変更がある場合だけ state.json を書き出す（進捗用タイマー）。
    /// 書き出し中に起きた変更を取りこぼさないよう、フラグは保存より先に落とす。
    /// </summary>
    private void SaveProgressIfDirty()
    {
        if (!_progressDirty) return;
        _progressDirty = false;
        MonitorState.SaveStateInternal();
    }
}
