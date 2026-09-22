using System.IO;
using Newtonsoft.Json;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// チャンネル一覧とカテゴリ一覧（通常・休止）の保管庫。
/// 三者は1つのロック（_persistLock）で守られており分離できないため、同じクラスで保持する。
/// </summary>
public sealed class ChannelStore
{
    private readonly SettingsFileStore _fileStore;
    private readonly SettingsService _core;

    private List<ChannelInfo> _channels = new();
    public List<CategoryInfo> Categories { get; private set; } = new();
    public List<CategoryInfo> DormantCategories { get; private set; } = new();

    // Channels/Categories への並行アクセスを直列化するロック
    private readonly object _persistLock = new();

    internal ChannelStore(SettingsFileStore fileStore, SettingsService core)
    {
        _fileStore = fileStore;
        _core      = core;
    }

    // ===== 他ストアからロックを保持したまま読み取るための入口 =====

    /// <summary>チャンネル一覧のロックを保持したまま読み取り処理を実行し、その結果を返す</summary>
    internal TResult ReadChannelsLocked<TResult>(Func<List<ChannelInfo>, TResult> reader)
    {
        lock (_persistLock) return reader(_channels);
    }

    /// <summary>チャンネル一覧のロックを保持したまま読み取り処理を実行する</summary>
    internal void ReadChannelsLocked(Action<List<ChannelInfo>> reader)
    {
        lock (_persistLock) reader(_channels);
    }

    /// <summary>ロックを取らずにチャンネル一覧へ直接アクセスする（読み込み処理の内部専用）</summary>
    internal List<ChannelInfo> ChannelList => _channels;

    /// <summary>チャンネル一覧と待機リストのロックを保持したまま期限切れ猶予エントリの掃除を行う</summary>
    internal bool CleanupExpiredGraceEntriesLocked()
    {
        lock (_persistLock)
        {
            return MonitorService.RunUnderPendingListLock(() => ChannelMigrator.CleanupExpiredGraceEntries(_channels));
        }
    }

    // ===== チャンネルの読み込み・保存 =====

    internal void LoadChannels()
    {
        try
        {
            if (File.Exists(_fileStore.ChannelsPath))
            {
                var json = File.ReadAllText(_fileStore.ChannelsPath);
                _channels = JsonConvert.DeserializeObject<List<ChannelInfo>>(json) ?? new List<ChannelInfo>();
            }
        }
        catch (Exception ex)
        {
            _fileStore.WriteSaveError("LoadChannels", ex.Message);
            _channels = new List<ChannelInfo>();
        }

        bool migrated = ChannelMigrator.MigrateChannelsToFocusSlots(_channels);
        migrated |= ChannelMigrator.MigrateNotifyUpcomingToNullable(_channels);
        migrated |= ChannelMigrator.MigrateUpcomingNotifyMode(_channels, _core.Settings, _core.SaveSettings);
        migrated |= ChannelMigrator.MigrateUpcomingNotifySplit(_channels, _core.Settings, _core.SaveSettings);
        migrated |= CleanupExpiredGraceEntriesLocked();
        if (migrated) { SaveChannelsSilent(); _core.MarkDirty(); }
    }

    /// <summary>通常の保存（ダーティフラグを立てる）</summary>
    public void SaveChannels()
    {
        SaveChannelsInternal();
        _core.MarkDirty();
    }

    /// <summary>VideoID更新など監視系の保存（ダーティフラグを立てない）</summary>
    public void SaveChannelsSilent() => SaveChannelsInternal();

    internal void SaveChannelsInternal()
    {
        try
        {
            string json;
            lock (_persistLock)
                json = JsonConvert.SerializeObject(_channels, Formatting.Indented);
            SettingsFileStore.WriteAtomic(_fileStore.ChannelsPath, json);
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveChannels", ex.Message); }
    }

    // ===== チャンネルの追加・削除・更新 =====

    public void AddChannel(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            if (_channels.Any(c => c.ChannelId == channel.ChannelId)) return;
            _channels.Add(channel);
        }
        SaveChannels();
    }

    public void RemoveChannel(string channelId)
    {
        lock (_persistLock)
        {
            var ch = _channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (ch == null) return;
            _channels.Remove(ch);
        }
        SaveChannels();
    }

    /// <summary>チャンネル詳細設定の保存ボタンなど即時保存が必要な場合に使う</summary>
    public void UpdateChannel(ChannelInfo channel)
    {
        ReplaceChannel(channel);
        SaveChannels();
    }

    /// <summary>監視系の状態更新（LastCheckedAt/VideoId/NextCheckAt等）。監視進捗を未保存として印を立てるだけで、ファイルへの書き出しは30秒周期のタイマーが行う</summary>
    public void UpdateChannelSilent(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            var idx = _channels.FindIndex(c => c.ChannelId == channel.ChannelId);
            if (idx >= 0) _channels[idx] = channel;
        }
        _core.MarkProgressDirty();
    }

    private void ReplaceChannel(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            var idx = _channels.FindIndex(c => c.ChannelId == channel.ChannelId);
            if (idx < 0) return;
            _channels[idx] = channel;
        }
    }

    // ===== チャンネル一覧のスナップショット・並べ替え =====

    public List<ChannelInfo> GetEnabledChannelsSnapshot()
    {
        lock (_persistLock) return _channels.Where(c => c.IsEnabled && !c.IsDormant).ToList();
    }

    public List<ChannelInfo> GetChannelsSnapshot()
    {
        lock (_persistLock) return _channels.ToList();
    }

    /// <summary>指定チャンネルを、同じ IsDormant・CategoryId を持つチャンネル群の最後尾（該当が無ければ全体の最後尾）へ再配置する。保存・MarkDirty は呼び出し側で行う</summary>
    public void MoveChannelToCategoryEnd(ChannelInfo channel)
    {
        lock (_persistLock)
        {
            _channels.Remove(channel);
            var lastIndex = _channels.FindLastIndex(c => c.IsDormant == channel.IsDormant && c.CategoryId == channel.CategoryId);
            _channels.Insert(lastIndex < 0 ? _channels.Count : lastIndex + 1, channel);
        }
    }

    /// <summary>
    /// 同じグループ（isSameGroup が true を返すチャンネル群）の中で、移動元のチャンネルを移動先の位置へ並べ替える。保存・MarkDirty・再描画は呼び出し側で行う。
    /// sourceGroupIndex / destGroupIndex はグループ内のインデックス。移動元の位置に movingChannel が居ない場合は何もしない
    /// </summary>
    public void ReorderChannelInGroup(ChannelInfo movingChannel, Func<ChannelInfo, bool> isSameGroup, int sourceGroupIndex, int destGroupIndex)
    {
        lock (_persistLock)
        {
            var sameGroup = _channels
                .Select((channel, index) => (channel, index))
                .Where(entry => isSameGroup(entry.channel))
                .ToList();

            if (sourceGroupIndex < 0 || sourceGroupIndex >= sameGroup.Count
                || sameGroup[sourceGroupIndex].channel.ChannelId != movingChannel.ChannelId) return;

            var (moving, movingGlobal) = sameGroup[sourceGroupIndex];
            _channels.RemoveAt(movingGlobal);

            // RemoveAt後にインデックスを再取得
            var updated = _channels
                .Select((channel, index) => (channel, index))
                .Where(entry => isSameGroup(entry.channel))
                .ToList();

            // destGroupIndex は「移動先の行インデックス」（CalcSwapIndex が返す最近傍行）
            // srcより下に移動する場合: destの後ろに挿入（Remove後インデックスは1つずれる）
            // srcより上に移動する場合: destの前に挿入
            int insertIndex;
            if (destGroupIndex > sourceGroupIndex)
            {
                // 下移動: destGroupIndex行の後ろ → Remove後は destGroupIndex-1 の後ろ
                var afterIndex = destGroupIndex - 1;
                insertIndex = afterIndex < updated.Count ? updated[afterIndex].index + 1 : _channels.Count;
            }
            else
            {
                // 上移動: destGroupIndex行の前
                insertIndex = destGroupIndex < updated.Count ? updated[destGroupIndex].index : _channels.Count;
            }
            _channels.Insert(Math.Clamp(insertIndex, 0, _channels.Count), moving);
        }
    }

    // ===== カテゴリの読み込み・保存 =====

    internal void LoadCategories()
    {
        try
        {
            if (File.Exists(_fileStore.CategoriesPath))
            {
                var json = File.ReadAllText(_fileStore.CategoriesPath);
                Categories = JsonConvert.DeserializeObject<List<CategoryInfo>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            _fileStore.WriteSaveError("LoadCategories", ex.Message);
            Categories = new();
        }
    }

    internal void LoadDormantCategories()
    {
        try
        {
            if (File.Exists(_fileStore.DormantCategoriesPath))
            {
                var json = File.ReadAllText(_fileStore.DormantCategoriesPath);
                DormantCategories = JsonConvert.DeserializeObject<List<CategoryInfo>>(json) ?? new();
            }
        }
        catch (Exception ex)
        {
            _fileStore.WriteSaveError("LoadDormantCategories", ex.Message);
            DormantCategories = new();
        }
    }

    public void SaveDormantCategories() => SaveDormantCategoriesInternal(markDirty: true);

    internal void SaveDormantCategoriesInternal(bool markDirty)
    {
        try
        {
            string json;
            lock (_persistLock)
                json = JsonConvert.SerializeObject(DormantCategories, Formatting.Indented);
            SettingsFileStore.WriteAtomic(_fileStore.DormantCategoriesPath, json);
            if (markDirty) _core.MarkDirty();
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveDormantCategories", ex.Message); }
    }

    public void SaveCategories() => SaveCategoriesInternal();

    internal void SaveCategoriesInternal()
    {
        try
        {
            string json;
            lock (_persistLock)
                json = JsonConvert.SerializeObject(Categories, Formatting.Indented);
            SettingsFileStore.WriteAtomic(_fileStore.CategoriesPath, json);
        }
        catch (Exception ex) { _fileStore.WriteSaveError("SaveCategories", ex.Message); }
    }

    // ===== カテゴリ操作の共通処理（監視カテゴリ・休眠カテゴリで共用） =====
    // save には SaveCategories / SaveDormantCategories を渡す
    //（ダーティフラグの扱いの差は各保存メソッド側で維持される）

    // カテゴリ名の重複判定で共通使用（大文字小文字を無視）
    private static bool CategoryNameEquals(string firstName, string secondName) => firstName.Equals(secondName, StringComparison.OrdinalIgnoreCase);

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
            foreach (var ch in _channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
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
                    foreach (var ch in _channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                        ch.CategoryId = ownDup.CategoryId;
                    categories.Remove(cat);
                }
            }
            else if (ownDup != null)
            {
                // 相手リストとの共有を切り離し、自リスト内の既存カテゴリへ合流。相手リストの元カテゴリ（categoryId）は無変更のまま残る
                foreach (var ch in _channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                    ch.CategoryId = ownDup.CategoryId;
                categories.Remove(cat);
            }
            else
            {
                // 相手リストとの共有を切り離す。相手リストに新名称と同名のカテゴリがあればそのIDを引き継ぎ、無ければ新規ID発行
                var otherMatch = otherCategories.FirstOrDefault(c => c.CategoryId != categoryId && CategoryNameEquals(c.CategoryName, newName));
                var newId      = otherMatch?.CategoryId ?? Guid.NewGuid().ToString();

                foreach (var ch in _channels.Where(c => c.CategoryId == categoryId && c.IsDormant == isDormant))
                    ch.CategoryId = newId;
                cat.CategoryId   = newId;
                cat.CategoryName = newName;
            }
        }
        save();
        SaveChannels();
    }

    // ===== カテゴリ操作の入口 =====

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

    public void RemoveCategory(string categoryId)
        => RemoveCategoryCore(Categories, SaveCategories, categoryId, isDormant: false);

    public void RenameCategory(string categoryId, string newName)
        => RenameCategoryCore(Categories, DormantCategories, SaveCategories, categoryId, isDormant: false, newName);

    public void SetChannelCategory(string channelId, string? categoryId)
    {
        lock (_persistLock)
        {
            var ch = _channels.FirstOrDefault(c => c.ChannelId == channelId);
            if (ch == null) return;
            ch.CategoryId = categoryId;
        }
        _core.MarkDirty();
    }
}
