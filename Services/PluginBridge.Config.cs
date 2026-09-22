using System.IO;
using System.Text;
using Newtonsoft.Json;
using YTNotifier.Plugin;

namespace YTNotifier.Services;

// 部分クラス: 設定画面向けの読み書き・conf\plugins.json の読み書き
public partial class PluginBridge
{
    /// <summary>プラグインごとの有効・優先順の記録ファイル（<c>conf\</c> 直下・バックアップ対象外）</summary>
    private const string PluginConfigFileName = "plugins.json";

    /// <summary>有効・無効切り替えログ（PluginEnabledChanged の {1}）の表記。XAML の CheckBox Content="有効" に合わせる。</summary>
    private const string EnabledLabelOn  = "有効";
    private const string EnabledLabelOff = "無効";

    /// <summary>
    /// 監視の判断に口を出す仕事の名前。ここに載る仕事を持つプラグインは、
    /// 新規発見時の既定が「無効」になる（設定画面で明示的に有効化するまで動かない）。
    /// 第2弾時点で該当する仕事は無い（機構のみ整備。要約 summary は「結果を見せるだけ」側）。
    /// </summary>
    private static readonly HashSet<string> MonitorInfluencingJobs = new(StringComparer.Ordinal);

    /// <summary>
    /// プラグインが有効か。<c>conf\plugins.json</c> に記録が無ければ <c>true</c>
    /// （記録なし＝有効の既存解決ルールと一致）。
    /// </summary>
    public bool IsPluginEnabled(string folder)
    {
        lock (_sync)
        {
            var config = LoadPluginConfig();
            return !config.Enabled.TryGetValue(folder, out var isEnabled) || isEnabled;
        }
    }

    /// <summary>
    /// プラグイン全体の表示・優先順（フォルダ名の配列）のコピー。記録が無ければ空リスト。
    /// 仕事の呼び出し順・画面のボタン表示順の両方にこの1つの順序を使う。
    /// </summary>
    public IReadOnlyList<string> GetPluginOrder()
    {
        lock (_sync)
        {
            var config = LoadPluginConfig();
            return new List<string>(config.Order);
        }
    }

    /// <summary>プラグインの有効・無効を切り替えて <c>conf\plugins.json</c> へ保存する。</summary>
    public void SetPluginEnabled(string folder, bool enabled)
    {
        lock (_sync)
        {
            var config = LoadPluginConfig();
            config.Enabled[folder] = enabled;
            SavePluginConfig(config);

            var displayName = _plugins.FirstOrDefault(p => string.Equals(p.Folder, folder, StringComparison.Ordinal))?.Name
                              ?? folder;
            AppLogger.Log(LogMsg.PluginEnabledChanged, null, displayName, enabled ? EnabledLabelOn : EnabledLabelOff);
        }
    }

    /// <summary>プラグイン全体の表示・優先順を差し替えて <c>conf\plugins.json</c> へ保存する。ログ出力は呼び出し側で行う。</summary>
    public void SetPluginOrder(IReadOnlyList<string> order)
    {
        lock (_sync)
        {
            var config = LoadPluginConfig();
            config.Order = new List<string>(order);
            SavePluginConfig(config);
        }
    }

    /// <summary>呼び出し側で <see cref="_sync"/> を保持していること。</summary>
    private List<PluginDescriptor> ResolveOrderedEnabled(string job)
    {
        var matching = _plugins
            .Where(p => p.Jobs.Contains(job, StringComparer.Ordinal))
            .ToList();
        if (matching.Count == 0) return matching;

        var config = LoadPluginConfig();

        var enabled = matching
            .Where(p => !config.Enabled.TryGetValue(p.Folder, out var isEnabled) || isEnabled)
            .ToList();

        if (config.Order.Count == 0) return enabled;

        var rankByFolder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var orderIndex = 0; orderIndex < config.Order.Count; orderIndex++)
            if (!rankByFolder.ContainsKey(config.Order[orderIndex])) rankByFolder[config.Order[orderIndex]] = orderIndex;

        return enabled
            .OrderBy(p => rankByFolder.TryGetValue(p.Folder, out var rank) ? rank : int.MaxValue)
            .ToList();
    }

    private static string PluginConfigPath =>
        Path.Combine(SettingsService.Instance.ConfDir, PluginConfigFileName);

    private static PluginConfig LoadPluginConfig()
    {
        try
        {
            var path = PluginConfigPath;
            if (!File.Exists(path)) return new PluginConfig();
            var text = File.ReadAllText(path);

            try
            {
                return JsonConvert.DeserializeObject<PluginConfig>(text) ?? new PluginConfig();
            }
            catch (JsonException)
            {
                // 旧形式（order/regionOrder が「仕事名・リージョンID → フォルダ配列」のDictionaryだった版）。
                // 有効・無効の記録だけ引き継ぎ、順序は検出順（空リスト）へリセットする。
                var legacy = JsonConvert.DeserializeObject<LegacyPluginConfig>(text) ?? new LegacyPluginConfig();
                return new PluginConfig { Enabled = legacy.Enabled };
            }
        }
        catch
        {
            return new PluginConfig();
        }
    }

    /// <summary>
    /// 起動時に一度だけ呼ぶ。旧形式（仕事ごと・リージョンごとの Dictionary 順序）の
    /// <c>conf\plugins.json</c> を検出したら、有効・無効の記録だけを引き継いだ新形式で上書き保存する。
    /// 新形式として読める場合・ファイルが無い場合は何もしない。
    /// </summary>
    private static void MigrateLegacyPluginConfigIfNeeded()
    {
        var path = PluginConfigPath;
        if (!File.Exists(path)) return;

        string text;
        try { text = File.ReadAllText(path); }
        catch { return; }

        try
        {
            JsonConvert.DeserializeObject<PluginConfig>(text);
            return; // 新形式として読めたので移行不要
        }
        catch (JsonException)
        {
            // 旧形式。下で変換して保存する。
        }
        catch
        {
            return; // 想定外の読み込み失敗。移行は行わない
        }

        try
        {
            var legacy = JsonConvert.DeserializeObject<LegacyPluginConfig>(text) ?? new LegacyPluginConfig();
            SavePluginConfig(new PluginConfig { Enabled = legacy.Enabled });
        }
        catch { /* 移行に失敗しても起動は継続する */ }
    }

    private static void SavePluginConfig(PluginConfig config)
    {
        try
        {
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(PluginConfigPath, json, new UTF8Encoding(false));
        }
        catch { /* 記録に失敗しても動作は続行する */ }
    }

    /// <summary>
    /// 記録に無いプラグインを <c>enabled=true</c>・順序の末尾として書き足す。
    /// 記録済みの内容（存在しないフォルダ名を含む）は消さない。
    /// </summary>
    private void RecordDiscoveredPlugins(IReadOnlyList<PluginDescriptor> discovered)
    {
        var config  = LoadPluginConfig();
        var changed = false;

        foreach (var descriptor in discovered)
        {
            if (!config.Enabled.ContainsKey(descriptor.Folder))
            {
                config.Enabled[descriptor.Folder] = !IsMonitorInfluencing(descriptor);
                changed = true;
            }

            if (!config.Order.Contains(descriptor.Folder))
            {
                config.Order.Add(descriptor.Folder);
                changed = true;
            }
        }

        if (changed) SavePluginConfig(config);
    }

    /// <summary>この仕事群のいずれかが「監視に口を出す」種類かどうか。</summary>
    private static bool IsMonitorInfluencing(PluginDescriptor descriptor)
        => descriptor.Jobs.Any(MonitorInfluencingJobs.Contains);

    private sealed class PluginConfig
    {
        /// <summary>プラグイン全体の表示・優先順（フォルダ名の配列）。仕事の呼び出し順・画面のボタン表示順の両方にこの1つを使う。</summary>
        public List<string> Order { get; set; } = new();

        /// <summary>フォルダ名 → 有効かどうか</summary>
        public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// 旧形式（仕事ごと・リージョンごとに別々の優先順位を持っていた版）の <c>plugins.json</c> を読むための型。
    /// <see cref="LoadPluginConfig"/> が新形式でのデシリアライズに失敗したときだけ使う。
    /// </summary>
    private sealed class LegacyPluginConfig
    {
        public Dictionary<string, List<string>> Order { get; set; } = new(StringComparer.Ordinal);
        public Dictionary<string, bool> Enabled { get; set; } = new(StringComparer.Ordinal);
        [JsonProperty("regionOrder")]
        public Dictionary<string, List<string>> RegionOrder { get; set; } = new(StringComparer.Ordinal);
    }
}
