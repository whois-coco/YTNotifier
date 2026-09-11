using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using YTNotifier.Constants;
using YTNotifier.Plugin;

namespace YTNotifier.Services;

/// <summary>
/// 本体側で唯一プラグインを知る場所。プラグインは本体では読み込まず、別プロセス
/// <c>PluginHost.exe</c> に任せる。本体は名前付きパイプのサーバーとなり、通信（HTTP）と
/// ログ出力を代行する。要約用APIキーはこのプロセスから外へ出ない。
/// </summary>
public sealed class PluginBridge
{
    private static readonly Lazy<PluginBridge> _lazy = new(() => new PluginBridge());
    public static PluginBridge Instance => _lazy.Value;

    private const string PluginsDirName        = "Plugins";
    private const string HostFileName          = "PluginHost.exe";
    private const string ManifestFileName      = "plugin.json";
    private const string PipeNamePrefix        = "YTNotifier.Plugin.";
    private const int    MaxRestartCount       = 3;
    private const int    ShutdownWaitSeconds   = 5;
    private const int    SummaryTimeoutMinutes = 5;
    private const int    SummaryIdleTimeoutSeconds = 30;

    /// <summary>プラグインごとの有効・優先順の記録ファイル（<c>conf\</c> 直下・バックアップ対象外）</summary>
    private const string PluginConfigFileName = "plugins.json";

    /// <summary>待ち時間の上限の判定結果をログへ載せるときの表記（PluginJobTimeout の {1}）</summary>
    private const string TimeoutKindOverall = "全体";
    private const string TimeoutKindIdle    = "無音";

    /// <summary>有効・無効切り替えログ（PluginEnabledChanged の {1}）の表記。XAML の CheckBox Content="有効" に合わせる。</summary>
    private const string EnabledLabelOn  = "有効";
    private const string EnabledLabelOff = "無効";

    private const string NewLineForPipe = "\n";

    private static readonly JsonSerializerSettings MessageSettings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy(processDictionaryKeys: false, overrideSpecifiedNames: true),
        },
        NullValueHandling = NullValueHandling.Ignore,
        Formatting = Formatting.None,
    };

    /// <summary>
    /// 監視の判断に口を出す仕事の名前。ここに載る仕事を持つプラグインは、
    /// 新規発見時の既定が「無効」になる（設定画面で明示的に有効化するまで動かない）。
    /// 第2弾時点で該当する仕事は無い（機構のみ整備。要約 summary は「結果を見せるだけ」側）。
    /// </summary>
    private static readonly HashSet<string> MonitorInfluencingJobs = new(StringComparer.Ordinal);

    private static readonly string ExeDir =
        Path.GetDirectoryName(Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location) ?? string.Empty;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<int, InvokeContext> _activeInvokes = new();

    private NamedPipeServerStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _hostProcess;

    /// <summary>ホストを閉じ込めている資源の枠。閉じると残っているホストのプロセスも終了する。</summary>
    private SafeHandle? _jobHandle;

    private CancellationTokenSource? _cts;

    private bool _started;
    private bool _connected;
    private bool _disabled;
    private bool _stopping;
    private int  _restartCount;
    private int  _nextInvokeId;

    private IReadOnlyList<PluginDescriptor> _plugins = Array.Empty<PluginDescriptor>();

    private PluginBridge() { }

    /// <summary>見つかったプラグインの一覧（第2弾の設定画面が参照する）。</summary>
    public IReadOnlyList<PluginDescriptor> Plugins
    {
        get { lock (_sync) return _plugins; }
    }

    // ── 起動・停止 ──────────────────────────────────────────────────────────

    /// <summary>
    /// <c>PluginHost.exe</c> が実行ファイルと同じフォルダにあり、かつ <c>Plugins\</c> に
    /// プラグインフォルダが1つ以上あるとき、パイプを用意してホストを起動する。
    /// どちらか欠ければ何もしない。
    /// </summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_started) return;

            MigrateLegacyPluginConfigIfNeeded();

            var hostPath   = Path.Combine(ExeDir, HostFileName);
            var pluginsDir  = Path.Combine(ExeDir, PluginsDirName);
            if (!File.Exists(hostPath) || !HasAnyPluginFolder(pluginsDir)) return;

            _started = true;
            StartHost(hostPath, pluginsDir);
        }
    }

    /// <summary>
    /// <c>shutdown</c> を送り、一定時間内に終了しなければプロセスを強制終了する。
    /// </summary>
    public void Stop()
    {
        Process? hostProcess;
        CancellationTokenSource? cts;
        lock (_sync)
        {
            if (!_started) return;
            _stopping    = true;
            hostProcess  = _hostProcess;
            cts          = _cts;
        }

        try { Task.Run(() => SendAsync(new ShutdownMessage())).Wait(TimeSpan.FromSeconds(ShutdownWaitSeconds)); }
        catch { /* 送信できなくても下で強制終了する */ }

        try
        {
            if (hostProcess is { HasExited: false } &&
                !hostProcess.WaitForExit((int)TimeSpan.FromSeconds(ShutdownWaitSeconds).TotalMilliseconds))
            {
                hostProcess.Kill(entireProcessTree: true);
            }
        }
        catch { /* すでに終了している場合など */ }

        try { cts?.Cancel(); } catch { }
        lock (_sync) { Cleanup(); }
    }

    /// <summary>ホストが動いていて、その仕事に対応する有効なプラグインが1つ以上あるか。</summary>
    public bool IsJobAvailable(string job)
    {
        lock (_sync)
        {
            if (!_connected || _disabled) return false;
            return ResolveOrderedEnabled(job).Count > 0;
        }
    }

    /// <summary>
    /// 優先順に従ってプラグインへ仕事を依頼し、結果の JSON 文字列を返す。
    /// 全て失敗した場合は <c>null</c> を返す（呼び出し元が「フォールバックすべきサイン」として扱う）。
    /// 例外は外に投げない。
    /// </summary>
    public async Task<string?> InvokeAsync(string job, string inputJson)
    {
        try
        {
            List<PluginDescriptor> candidates;
            bool ready;
            lock (_sync)
            {
                ready = _connected && !_disabled;
                candidates = ready ? ResolveOrderedEnabled(job) : new List<PluginDescriptor>();
            }
            if (!ready || candidates.Count == 0) return null;

            var apiKey = GeminiApiKeyService.Load(SettingsService.Instance.ConfDir) ?? string.Empty;
            var overallTimeout = TimeSpan.FromMinutes(SummaryTimeoutMinutes);
            var idleTimeout    = TimeSpan.FromSeconds(SummaryIdleTimeoutSeconds);

            foreach (var plugin in candidates)
            {
                var invokeId = Interlocked.Increment(ref _nextInvokeId);
                var context  = new InvokeContext(invokeId, plugin.Folder, apiKey);
                _activeInvokes[invokeId] = context;
                try
                {
                    AppLogger.Log(LogMsg.PluginJobRequested, null, job, plugin.Folder);

                    await SendAsync(new InvokeMessage
                    {
                        Id            = invokeId,
                        Folder        = plugin.Folder,
                        Job           = job,
                        Input         = inputJson,
                        TimeoutMs     = (int)overallTimeout.TotalMilliseconds,
                        IdleTimeoutMs = (int)idleTimeout.TotalMilliseconds,
                    }).ConfigureAwait(false);

                    var outcome = await AwaitResultAsync(context, overallTimeout, idleTimeout).ConfigureAwait(false);

                    if (outcome.TimedOut)
                    {
                        AppLogger.Log(LogMsg.PluginJobTimeout, null, plugin.Folder, outcome.TimeoutKind);
                        continue;
                    }

                    var result = outcome.Result!;
                    if (!result.Ok || string.IsNullOrWhiteSpace(result.Output))
                    {
                        var reason = string.IsNullOrEmpty(result.Error) ? "結果が空でした" : result.Error;
                        AppLogger.Log(LogMsg.PluginJobFailed, null, job, reason);
                        continue;
                    }

                    return result.Output;
                }
                finally
                {
                    _activeInvokes.TryRemove(invokeId, out _);
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.PluginInvokeError, null, job, ex.Message);
            return null;
        }
    }

    // ── 設定画面向けの読み書き（conf\plugins.json） ───────────────────────

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

    // ── パイプとホストプロセス ─────────────────────────────────────────────

    private static bool HasAnyPluginFolder(string pluginsDir)
    {
        if (!Directory.Exists(pluginsDir)) return false;
        foreach (var folder in Directory.GetDirectories(pluginsDir))
            if (File.Exists(Path.Combine(folder, ManifestFileName))) return true;
        return false;
    }

    /// <summary>呼び出し側で <see cref="_sync"/> を保持していること。</summary>
    private void StartHost(string hostPath, string pluginsDir)
    {
        try
        {
            var pipeName = PipeNamePrefix + Environment.ProcessId;
            _cts    = new CancellationTokenSource();
            _pipe   = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            _reader = new StreamReader(_pipe, encoding);
            _writer = new StreamWriter(_pipe, encoding) { AutoFlush = false, NewLine = NewLineForPipe };

            var sandboxedHost = PluginSandbox.Start(hostPath, pipeName, pluginsDir, ExeDir);
            _hostProcess = sandboxedHost.Process;
            _jobHandle   = sandboxedHost.JobHandle;

            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token));
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.PluginHostStartFailed, null, ex.Message);
            Cleanup();
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        NamedPipeServerStream? pipe;
        StreamReader? reader;
        lock (_sync)
        {
            pipe   = _pipe;
            reader = _reader;
        }
        if (pipe == null || reader == null) return;

        try
        {
            await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
        }
        catch
        {
            HandleDisconnect();
            return;
        }

        while (!token.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            if (line == null) break;

            try { Dispatch(line); }
            catch { /* 1メッセージの処理失敗で受信ループを止めない */ }
        }

        HandleDisconnect();
    }

    private void HandleDisconnect()
    {
        lock (_sync)
        {
            Cleanup();
            FailActiveInvokes();

            if (_stopping || _disabled) return;

            _restartCount++;
            AppLogger.Log(LogMsg.PluginHostExited, null, _restartCount, MaxRestartCount);
            if (_restartCount > MaxRestartCount) return;

            var hostPath   = Path.Combine(ExeDir, HostFileName);
            var pluginsDir  = Path.Combine(ExeDir, PluginsDirName);
            if (!File.Exists(hostPath) || !HasAnyPluginFolder(pluginsDir)) return;

            _connected = false;
            StartHost(hostPath, pluginsDir);
        }
    }

    /// <summary>呼び出し側で <see cref="_sync"/> を保持していること。</summary>
    private void Cleanup()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        // 枠を閉じると、残っているホストのプロセスも道連れに終了する
        try { _jobHandle?.Dispose(); } catch { }
        _writer    = null;
        _reader    = null;
        _pipe      = null;
        _jobHandle = null;
    }

    private void FailActiveInvokes()
    {
        foreach (var context in _activeInvokes.Values) context.Fail();
    }

    // ── メッセージの処理 ───────────────────────────────────────────────────

    private void Dispatch(string line)
    {
        var messageType = JObject.Parse(line).Value<string>(PluginProtocol.TypeField) ?? string.Empty;

        switch (messageType)
        {
            case PluginProtocol.MessageTypes.Hello:
                OnHello(Deserialize<HelloMessage>(line));
                break;
            case PluginProtocol.MessageTypes.Plugins:
                OnPlugins(Deserialize<PluginsMessage>(line));
                break;
            case PluginProtocol.MessageTypes.Progress:
            {
                var progress = Deserialize<ProgressMessage>(line);
                if (_activeInvokes.TryGetValue(progress.Id, out var context)) context.MarkActivity();
                break;
            }
            case PluginProtocol.MessageTypes.Result:
            {
                var result = Deserialize<ResultMessage>(line);
                if (_activeInvokes.TryGetValue(result.Id, out var context)) context.Complete(result);
                break;
            }
            case PluginProtocol.MessageTypes.Call:
            {
                var call = Deserialize<CallMessage>(line);
                _ = Task.Run(() => OnCallAsync(call));
                break;
            }
        }
    }

    private void OnHello(HelloMessage hello)
    {
        if (hello.ProtocolVersion != PluginProtocol.ProtocolVersion)
        {
            lock (_sync) { _disabled = true; _stopping = true; }
            AppLogger.Log(LogMsg.PluginHostVersionMismatch, null, AppConstants.AppVersion, hello.HostVersion);
            _ = SendAsync(new WelcomeMessage
            {
                ProtocolVersion = PluginProtocol.ProtocolVersion,
                Accepted        = false,
                Reason          = "やりとりの版が一致しません",
            });
            return;
        }

        _ = SendAsync(new WelcomeMessage
        {
            ProtocolVersion = PluginProtocol.ProtocolVersion,
            Accepted        = true,
        });
    }

    private void OnPlugins(PluginsMessage message)
    {
        foreach (var error in message.Errors)
            AppLogger.Log(LogMsg.PluginManifestInvalid, null, error.Folder, error.Reason);

        lock (_sync)
        {
            _plugins   = message.Items;
            _connected = true;
        }

        foreach (var descriptor in message.Items)
            AppLogger.Log(LogMsg.PluginDetected, null, descriptor.Name, descriptor.Folder);

        RecordDiscoveredPlugins(message.Items);

        AppLogger.Log(LogMsg.PluginHostStarted, null, message.Items.Count);
    }

    private async Task OnCallAsync(CallMessage call)
    {
        var output = string.Empty;

        if (_activeInvokes.TryGetValue(call.Id, out var context))
        {
            context.MarkActivity();
            try
            {
                if (call.Api == PluginProtocol.Apis.Http)
                {
                    var input = Deserialize<HttpCallInput>(call.Input);
                    output = context.HttpGateway.HttpRequest(input.Method, input.Url, input.HeadersJson, input.BodyText);
                }
                else if (call.Api == PluginProtocol.Apis.Log)
                {
                    var input = Deserialize<LogCallInput>(call.Input);
                    context.LogGateway.Log(input.Message);
                }
            }
            catch { /* 窓口の失敗は握りつぶし、下で空応答を返す */ }
            context.MarkActivity();
        }

        await SendAsync(new CallResultMessage { Id = call.Id, Output = output }).ConfigureAwait(false);
    }

    private async Task SendAsync(object message)
    {
        StreamWriter? writer;
        lock (_sync) writer = _writer;
        if (writer == null) return;

        var line = JsonConvert.SerializeObject(message, MessageSettings);

        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
            await writer.FlushAsync().ConfigureAwait(false);
        }
        catch { /* パイプ断は受信ループ側で検知して後始末する */ }
        finally
        {
            _writeLock.Release();
        }
    }

    private static T Deserialize<T>(string line) where T : new()
        => JsonConvert.DeserializeObject<T>(line, MessageSettings) ?? new T();

    // ── 待ち時間の上限 ─────────────────────────────────────────────────────

    private static async Task<InvokeOutcome> AwaitResultAsync(InvokeContext context,
        TimeSpan overallTimeout, TimeSpan idleTimeout)
    {
        var overallDeadline = DateTime.UtcNow + overallTimeout;

        while (true)
        {
            var now = DateTime.UtcNow;
            if (now >= overallDeadline) return InvokeOutcome.Timeout(TimeoutKindOverall);

            var idleDeadline = context.LastActivityUtc + idleTimeout;
            if (now >= idleDeadline) return InvokeOutcome.Timeout(TimeoutKindIdle);

            var wait = overallDeadline - now < idleDeadline - now ? overallDeadline - now : idleDeadline - now;

            var finished = await Task.WhenAny(context.ResultTask, Task.Delay(wait)).ConfigureAwait(false);
            if (finished != context.ResultTask) continue;

            try { return InvokeOutcome.Completed(await context.ResultTask.ConfigureAwait(false)); }
            catch { return InvokeOutcome.Timeout(TimeoutKindOverall); }
        }
    }

    // ── conf\plugins.json の読み書き ───────────────────────────────────────

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
        for (var i = 0; i < config.Order.Count; i++)
            if (!rankByFolder.ContainsKey(config.Order[i])) rankByFolder[config.Order[i]] = i;

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

    // ── 内部型 ────────────────────────────────────────────────────────────

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

    private sealed class InvokeContext
    {
        private readonly TaskCompletionSource<ResultMessage> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private long _lastActivityTicks;

        public InvokeContext(int id, string folderName, string apiKey)
        {
            Id          = id;
            HttpGateway = new PluginHttpGateway(apiKey);
            LogGateway  = new PluginLogGateway(folderName);
            MarkActivity();
        }

        public int Id { get; }
        public PluginHttpGateway HttpGateway { get; }
        public PluginLogGateway  LogGateway  { get; }
        public Task<ResultMessage> ResultTask => _completion.Task;

        public DateTime LastActivityUtc =>
            new(Interlocked.Read(ref _lastActivityTicks), DateTimeKind.Utc);

        public void MarkActivity() => Interlocked.Exchange(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
        public void Complete(ResultMessage result) => _completion.TrySetResult(result);
        public void Fail() => _completion.TrySetCanceled();
    }

    private readonly struct InvokeOutcome
    {
        private InvokeOutcome(bool timedOut, string timeoutKind, ResultMessage? result)
        {
            TimedOut    = timedOut;
            TimeoutKind = timeoutKind;
            Result      = result;
        }

        public bool TimedOut { get; }
        public string TimeoutKind { get; }
        public ResultMessage? Result { get; }

        public static InvokeOutcome Timeout(string kind)      => new(true, kind, null);
        public static InvokeOutcome Completed(ResultMessage r) => new(false, string.Empty, r);
    }
}
