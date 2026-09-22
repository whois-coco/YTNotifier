using System.IO;
using Newtonsoft.Json;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 監視の実行時状態（state.json）の読み書きを担当するストア。
/// </summary>
public sealed class MonitorStateStore
{
    private readonly SettingsFileStore _fileStore;
    private readonly SettingsService _core;

    public AppState AppState { get; private set; } = new();

    internal MonitorStateStore(SettingsFileStore fileStore, SettingsService core)
    {
        _fileStore = fileStore;
        _core      = core;
    }

    // ===== 読み込み =====

    internal void LoadState()
    {
        if (!File.Exists(_fileStore.StatePath))
        {
            _core.Channels.CleanupExpiredGraceEntriesLocked();
            SaveStateInternal();
            return;
        }
        try
        {
            var json = File.ReadAllText(_fileStore.StatePath);
            if (string.IsNullOrWhiteSpace(json))
                throw new Exception();
            AppState = JsonConvert.DeserializeObject<AppState>(json) ?? throw new Exception();
        }
        catch
        {
            TryRestoreStateFromBackup();
            try
            {
                var json = File.ReadAllText(_fileStore.StatePath);
                AppState = JsonConvert.DeserializeObject<AppState>(json) ?? new AppState();
            }
            catch { AppState = new AppState(); }
        }
        var validIds = new HashSet<string>(_core.Channels.ChannelList.Select(c => c.ChannelId));
        foreach (var id in AppState.Channels.Keys.Where(id => !validIds.Contains(id)).ToList())
            AppState.Channels.Remove(id);
        foreach (var ch in _core.Channels.ChannelList)
        {
            if (AppState.Channels.TryGetValue(ch.ChannelId, out var state))
                ch.State = state;
        }
        _core.Channels.CleanupExpiredGraceEntriesLocked();
    }

    // ===== 書き出し =====

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
            // 各チャンネルの実行状態をそのまま書き出すため、コピーは作らない。
            // 書き出し中に他スレッドが待機リストを書き換えて例外になるのを防ぐため、
            // 待機リスト用ロックを保持したまま文字列化まで行う（ロック順序：チャンネル一覧 → 待機リスト用）。
            var json = _core.Channels.ReadChannelsLocked(channels =>
                MonitorService.RunUnderPendingListLock(() =>
                {
                    foreach (var ch in channels)
                        AppState.Channels[ch.ChannelId] = ch.State;
                    return JsonConvert.SerializeObject(AppState, _stateSerializerSettings);
                }));
            SettingsFileStore.WriteAtomic(_fileStore.StatePath, json);
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveState", ex.Message); }
    }

    /// <summary>クォータ超過による監視停止の再開予定時刻を state.json へ即時保存する
    /// （再起動をまたいで保持するため）。解除時は null を渡す。</summary>
    public void PersistQuotaSuspension(DateTime? suspendedUntil)
    {
        AppState.QuotaSuspendedUntil = suspendedUntil;
        SaveStateInternal();
        _core.ClearProgressDirty();
    }

    // ===== state.json の退避・復元 =====

    internal void SaveStateToBackup()
    {
        try
        {
            if (File.Exists(_fileStore.StatePath))
                File.Copy(_fileStore.StatePath, Path.Combine(_fileStore.AppDataDir, AppConstants.DirBackup, AppConstants.FileState), overwrite: true);
        }
        catch (Exception ex) { AppLogger.Log(LogMsg.StateBackupSaveFailed, null, ex.Message); }
    }

    private void TryRestoreStateFromBackup()
    {
        var backupStatePath = Path.Combine(_fileStore.AppDataDir, AppConstants.DirBackup, AppConstants.FileState);
        if (!File.Exists(backupStatePath)) return;
        try { File.Copy(backupStatePath, _fileStore.StatePath, overwrite: true); }
        catch (Exception ex) { AppLogger.Log(LogMsg.StateBackupRestoreFailed, null, ex.Message); }
    }
}
