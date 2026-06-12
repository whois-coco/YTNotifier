using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;

namespace YTNotifier.Services;

public class LoggerService
{
    private static LoggerService? _instance;
    public static LoggerService Instance => _instance ??= new LoggerService();

    public ObservableCollection<LogEntry> Entries { get; } = new();
    private readonly string _logDir;
    private readonly object _fileLock = new();
    private const int MaxUiEntries = 200;

    private LoggerService()
    {
        _logDir = Path.Combine(SettingsService.Instance.AppDataDir, "logs");
        Directory.CreateDirectory(_logDir);
    }

    public void Log(string message, LogLevel level = LogLevel.Info, string? channelName = null,
                    LogCategory category = LogCategory.System)
    {
        var entry = new LogEntry
        {
            Timestamp   = DateTime.Now,
            Level       = level,
            Message     = message,
            ChannelName = channelName
        };

        // フィルター判定（システムメッセージは常に出力）
        bool show = true;
        if (category != LogCategory.System)
        {
            var s = SettingsService.Instance.Settings;
            show = category switch
            {
                LogCategory.NoNew      => s.LogShowNoNew,
                LogCategory.NewFound   => s.LogShowNewFound,
                LogCategory.CheckError => s.LogShowCheckError,
                LogCategory.Notify     => s.LogShowNotify,
                _                      => true
            };
        }

        // フィルターOFF → UIにも出力しない、ファイルにも出力しない
        if (!show) return;

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(channelName))
            {
                var toRemove = Entries.Where(e => e.ChannelName == channelName).ToList();
                foreach (var e in toRemove) Entries.Remove(e);
            }
            else
            {
                var toRemove = Entries.Where(e => string.IsNullOrEmpty(e.ChannelName) && e.Message == message).ToList();
                foreach (var e in toRemove) Entries.Remove(e);
            }

            Entries.Add(entry);

            while (Entries.Count > MaxUiEntries)
                Entries.RemoveAt(0);
        });

        WriteToFile(entry);
    }

    public void Info(string msg, string? ch = null, LogCategory cat = LogCategory.System)
        => Log(msg, LogLevel.Info, ch, cat);
    public void Success(string msg, string? ch = null, LogCategory cat = LogCategory.System)
        => Log(msg, LogLevel.Success, ch, cat);
    public void Warning(string msg, string? ch = null, LogCategory cat = LogCategory.System)
        => Log(msg, LogLevel.Warning, ch, cat);
    public void Error(string msg, string? ch = null, LogCategory cat = LogCategory.System)
        => Log(msg, LogLevel.Error, ch, cat);

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            var fileName = $"{entry.Timestamp:yyyy-MM-dd}.log";
            var path     = Path.Combine(_logDir, fileName);
            var line     = $"[{entry.Timestamp:HH:mm:ss}] [{entry.LevelText,-7}]";
            if (entry.ChannelName != null) line += $" [{entry.ChannelName}]";
            line += $" {entry.Message}";
            lock (_fileLock)
                File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }

    public void ClearUiLog()
    {
        Application.Current?.Dispatcher.Invoke(() => Entries.Clear());
    }

    // ===== ログメンテナンス =====
    /// <summary>指定日数より古いログファイルを削除する</summary>
    public (int deleted, long freedBytes) CleanOldLogs(int retentionDays)
    {
        int deleted = 0;
        long freed  = 0;
        try
        {
            // -1 = 無制限（削除しない）
            if (retentionDays < 0) return (0, 0);

            // 0日 = 当日のみ残す（昨日以前を削除）
            // N日 = N日前より古いものを削除
            var today  = DateTime.Today;
            var cutoff = retentionDays == 0
                ? today  // 今日より前（昨日以前）を削除
                : DateTime.Now.AddDays(-retentionDays);

            foreach (var file in Directory.GetFiles(_logDir, "*.log"))
            {
                var info = new FileInfo(file);
                var fileDate = info.LastWriteTime.Date;

                var shouldDelete = retentionDays == 0
                    ? fileDate < today          // 当日以外を削除
                    : info.LastWriteTime < cutoff;

                if (shouldDelete)
                {
                    freed += info.Length;
                    File.Delete(file);
                    deleted++;
                }
            }
        }
        catch { }
        return (deleted, freed);
    }

    /// <summary>ログフォルダの統計情報を取得する</summary>
    public (int fileCount, long totalBytes, DateTime? oldest, DateTime? newest) GetLogStats()
    {
        try
        {
            var files = Directory.GetFiles(_logDir, "*.log")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTime)
                .ToList();
            if (files.Count == 0) return (0, 0, null, null);
            return (
                files.Count,
                files.Sum(f => f.Length),
                files.First().LastWriteTime,
                files.Last().LastWriteTime
            );
        }
        catch { return (0, 0, null, null); }
    }

}
