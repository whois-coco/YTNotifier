namespace YTNotifier.Services;

/// <summary>
/// YTNotifier.Debug.dll が提供するテストチャンネルサービスのインターフェース。
/// MonitorService は DLL が存在する場合のみ反射経由でこの実装を取得する。
/// </summary>
public interface IDebugChannelService
{
    (List<VideoInfo> videos, List<VideoInfo> pendingTransitioned)
        GetNextCheckResult(ChannelInfo channel);
}
