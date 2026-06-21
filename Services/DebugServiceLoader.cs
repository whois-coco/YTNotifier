using System.IO;
using System.Reflection;

namespace YTNotifier.Services;

/// <summary>
/// YTNotifier.Debug.dll を遅延ロードし IDebugChannelService 実装を返すシングルトン。
/// DLL が存在しない場合は null を返す。Debug ビルドのみ有効。
/// </summary>
public static class DebugServiceLoader
{
#if DEBUG
    private static IDebugChannelService? _service;
    private static bool _loaded;
    private static readonly string _dllPath = Path.Combine(
        Path.GetDirectoryName(Environment.ProcessPath
            ?? Assembly.GetExecutingAssembly().Location) ?? "",
        "YTNotifier.Debug.dll");

    public static IDebugChannelService? GetService()
    {
        if (_loaded) return _service;
        _loaded = true;

        if (!File.Exists(_dllPath)) return null;

        try
        {
            var asm  = Assembly.LoadFrom(_dllPath);
            var type = asm.GetTypes().FirstOrDefault(t =>
                typeof(IDebugChannelService).IsAssignableFrom(t)
                && !t.IsInterface && !t.IsAbstract);

            _service = type != null
                ? (IDebugChannelService?)Activator.CreateInstance(type)
                : null;
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.DebugDllFailed, null, ex.Message);
        }

        return _service;
    }
#else
    public static IDebugChannelService? GetService() => null;
#endif
}
