using System.IO;
using Newtonsoft.Json;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// API使用量・Gemini呼び出し回数・レポート履歴（report_history.json）を担当するストア。
/// 日次カウンタの実体は実行状態ストアのアプリ状態オブジェクト側にあり、中核経由で参照する。
/// </summary>
public sealed class UsageStatsStore
{
    private readonly SettingsFileStore _fileStore;
    private readonly SettingsService _core;

    // AddApiUnits の並行呼び出しを直列化するロック
    private readonly object _apiUnitsLock = new();
    // AddGeminiRequest の並行呼び出しを直列化するロック（YouTube側のユニット計上とは別変数）
    private readonly object _geminiRequestLock = new();
    // レポート履歴（_reportHistory）への並行アクセスを直列化するロック。
    // ロックの入れ子は「_apiUnitsLock → _reportHistoryLock」の一方向のみ（逆順は作らない）
    private readonly object _reportHistoryLock = new();

    // レポート履歴（report_history.json）。バックアップ対象外で、読み込みはプロセス起動後の1回だけ行う
    private ReportHistory _reportHistory = new();
    private bool _reportHistoryLoaded = false;

    // AddApiUnits の計上カテゴリを非同期フロー単位で受け渡す（並列チャンネルチェックでも各フローに独立コピーされる）
    private static readonly System.Threading.AsyncLocal<Models.ApiUnitCategory> _apiUnitCategory = new();

    /// <summary>現在の非同期フローにおける API ユニット計上カテゴリ。未設定時は Normal。</summary>
    public static Models.ApiUnitCategory CurrentApiUnitCategory
    {
        get => _apiUnitCategory.Value;
        set => _apiUnitCategory.Value = value;
    }

    internal UsageStatsStore(SettingsFileStore fileStore, SettingsService core)
    {
        _fileStore = fileStore;
        _core      = core;
    }

    /// <summary>API使用ユニットを加算（日付をまたいだらリセット）</summary>
    public void AddApiUnits(int units)
    {
        lock (_apiUnitsLock)
        {
            var appState = _core.MonitorState.AppState;
            var quotaKey = AppConstants.GetQuotaDayKey();
            if (appState.TodayApiDate != quotaKey)
            {
                // リセットする前の前日分を履歴へ残す
                if (!string.IsNullOrEmpty(appState.TodayApiDate))
                {
                    var previousPending    = appState.TodayApiUnitsPendingTrack;
                    var previousLiveStatus = appState.TodayApiUnitsLiveStatus;
                    AddDailyApiUsageRecord(new DailyApiUsageRecord
                    {
                        Date            = appState.TodayApiDate,
                        NormalUnits     = Math.Max(0, appState.TodayApiUnits - previousPending - previousLiveStatus),
                        PendingUnits    = previousPending,
                        LiveStatusUnits = previousLiveStatus,
                    });
                }

                appState.TodayApiUnits            = 0;
                appState.TodayApiUnitsPendingTrack = 0;
                appState.TodayApiUnitsLiveStatus   = 0;
                appState.TodayApiDate             = quotaKey;
            }
            appState.TodayApiUnits += units;
            switch (CurrentApiUnitCategory)
            {
                case Models.ApiUnitCategory.PendingTrack: appState.TodayApiUnitsPendingTrack += units; break;
                case Models.ApiUnitCategory.LiveStatus:   appState.TodayApiUnitsLiveStatus   += units; break;
            }
        }
        MonitorService.Instance.NotifyQuotaUpdated();
    }

    /// <summary>Geminiリクエスト数を加算（日付をまたいだらリセット）</summary>
    public void AddGeminiRequest()
    {
        lock (_geminiRequestLock)
        {
            var appState = _core.MonitorState.AppState;
            var quotaKey = AppConstants.GetQuotaDayKey();
            if (appState.TodayGeminiRequestDate != quotaKey)
            {
                appState.TodayGeminiRequests    = 0;
                appState.TodayGeminiRequestDate = quotaKey;
            }
            appState.TodayGeminiRequests += 1;
        }
        MonitorService.Instance.NotifyQuotaUpdated();
    }

    // ===== レポート履歴 =====

    /// <summary>前日分などの確定したAPI使用量を履歴へ追加する（同じ日付の記録があれば置き換える）</summary>
    private void AddDailyApiUsageRecord(DailyApiUsageRecord record)
    {
        lock (_reportHistoryLock)
        {
            _reportHistory.ApiUsage.RemoveAll(r => r.Date == record.Date);
            _reportHistory.ApiUsage.Add(record);
            PruneReportHistoryLocked();
        }
    }

    /// <summary>表示した通知を1件加算する（ローカル日付の記録へ）</summary>
    public void AddNotificationCount()
    {
        lock (_reportHistoryLock)
        {
            GetOrCreateTodayActivityRecordLocked().NotificationCount += 1;
            PruneReportHistoryLocked();
        }
    }

    /// <summary>起動時間（秒）を加算する（ローカル日付の記録へ）</summary>
    public void AddUptimeSeconds(int seconds)
    {
        lock (_reportHistoryLock)
        {
            GetOrCreateTodayActivityRecordLocked().UptimeSeconds += seconds;
            PruneReportHistoryLocked();
        }
    }

    /// <summary>レポート履歴のコピーを返す（ロック内でコピーするため、呼び出し側は自由に読める）</summary>
    public ReportHistory GetReportHistorySnapshot()
    {
        lock (_reportHistoryLock)
        {
            return new ReportHistory
            {
                ApiUsage = _reportHistory.ApiUsage.Select(r => new DailyApiUsageRecord
                {
                    Date            = r.Date,
                    NormalUnits     = r.NormalUnits,
                    PendingUnits    = r.PendingUnits,
                    LiveStatusUnits = r.LiveStatusUnits,
                }).ToList(),
                Activity = _reportHistory.Activity.Select(r => new DailyActivityRecord
                {
                    Date              = r.Date,
                    NotificationCount = r.NotificationCount,
                    UptimeSeconds     = r.UptimeSeconds,
                }).ToList(),
            };
        }
    }

    /// <summary>ローカル日付の今日の活動記録を返す。なければ作る（_reportHistoryLock 内で呼ぶこと）</summary>
    private DailyActivityRecord GetOrCreateTodayActivityRecordLocked()
    {
        var todayKey = ReportStatsHelper.ToDateKey(DateTime.Now);
        var record   = _reportHistory.Activity.FirstOrDefault(r => r.Date == todayKey);
        if (record == null)
        {
            record = new DailyActivityRecord { Date = todayKey };
            _reportHistory.Activity.Add(record);
        }
        return record;
    }

    /// <summary>今日から数えて ReportDays 日より前の記録を削除する（_reportHistoryLock 内で呼ぶこと）</summary>
    private void PruneReportHistoryLocked()
    {
        var apiCutoffKey = ReportStatsHelper.ToDateKey(ReportStatsHelper.GetQuotaToday().AddDays(-AppConstants.ReportDays));
        _reportHistory.ApiUsage.RemoveAll(r => string.CompareOrdinal(r.Date, apiCutoffKey) < 0);

        var activityCutoffKey = ReportStatsHelper.ToDateKey(DateTime.Now.Date.AddDays(-AppConstants.ReportDays));
        _reportHistory.Activity.RemoveAll(r => string.CompareOrdinal(r.Date, activityCutoffKey) < 0);
    }

    /// <summary>
    /// レポート履歴を report_history.json へ保存する。バックアップ対象外のファイルとして扱う。
    /// </summary>
    internal void SaveReportHistoryInternal()
    {
        try
        {
            string json;
            lock (_reportHistoryLock)
                json = JsonConvert.SerializeObject(_reportHistory, Formatting.Indented);
            SettingsFileStore.WriteAtomic(_fileStore.ReportHistoryPath, json);
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveReportHistory", ex.Message); }
    }

    /// <summary>
    /// report_history.json を読み込む。ファイル欠落・破損・空は空の履歴で開始する。
    /// 履歴はバックアップ対象外のため、読み込みはプロセス起動後の1回だけ行う
    /// （データ管理のインポートで Load() が再度呼ばれても、メモリ上の履歴をディスクの内容で上書きしない）。
    /// </summary>
    internal void LoadReportHistory()
    {
        if (_reportHistoryLoaded) return;
        _reportHistoryLoaded = true;

        try
        {
            if (!File.Exists(_fileStore.ReportHistoryPath))
                return;

            var json = File.ReadAllText(_fileStore.ReportHistoryPath);
            if (string.IsNullOrWhiteSpace(json))
                return;

            var loaded = JsonConvert.DeserializeObject<ReportHistory>(json);
            if (loaded == null)
                return;

            loaded.ApiUsage ??= new();
            loaded.Activity ??= new();
            loaded.ApiUsage.RemoveAll(r => r == null || string.IsNullOrEmpty(r.Date));
            loaded.Activity.RemoveAll(r => r == null || string.IsNullOrEmpty(r.Date));

            lock (_reportHistoryLock)
                _reportHistory = loaded;
        }
        catch (Exception ex) { _fileStore.WriteSaveError("LoadReportHistory", ex.Message); }
    }
}
